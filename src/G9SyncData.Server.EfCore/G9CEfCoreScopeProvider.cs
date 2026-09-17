using System.Data.Common;
using System.Linq.Expressions;
using G9SyncData.Server.Model;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Server.EfCore;

/// <summary>
///     Visibility as an EF Core query (v2 A-02, ADR-0029): which rows of a table a subscription holds is an
///     <see cref="IQueryable{T}" /> over the application's own <see cref="DbContext" />, so the rules the application already
///     applies — global and named query filters, navigations, value converters, shared predicates — decide what a device sees.
///     <para>
///         <b>The query is translated, never executed.</b> Per request the provider asks EF for the SQL of
///         <c>query.Select(e =&gt; e.Key)</c> and hands that text to the storage provider, which embeds it in its own
///         statements (<c>EXISTS (SELECT 1 FROM (&lt;keys&gt;) AS g9s(g9k) WHERE g9s.g9k = row.key)</c>). Membership is
///         therefore evaluated inside the SAME snapshot as the rows it gates — an EF round trip would have read it in another
///         transaction, and a row could have been sent under one visibility and gated by another.
///     </para>
///     <para>
///         EF caches translations by expression shape, so after the first request this is the cost of building a DbContext
///         (cheap with <c>AddDbContextPool</c> / <see cref="IDbContextFactory{TContext}" />) plus one cache lookup.
///     </para>
/// </summary>
/// <typeparam name="TContext">The application's DbContext.</typeparam>
/// <typeparam name="TEntity">The entity mapped to the included table.</typeparam>
public sealed class G9CEfCoreScopeProvider<TContext, TEntity> : IG9SyncScopeProvider
    where TContext : DbContext
    where TEntity : class
{
    private readonly Func<TContext, G9CSyncScopeContext, IQueryable<TEntity>> _query;

    /// <param name="query">Builds the visible rows for one request. Must not execute anything.</param>
    /// <param name="version">
    ///     Part of <see cref="Fingerprint" />. A delegate cannot be compared between deployments, so bump this whenever the
    ///     query's MEANING changes — every device then reconciles once and gets exactly the difference.
    /// </param>
    public G9CEfCoreScopeProvider(Func<TContext, G9CSyncScopeContext, IQueryable<TEntity>> query, string version = "1")
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        _query = query;
        Fingerprint = $"efcore:{typeof(TContext).FullName}/{typeof(TEntity).FullName}@{version}";
    }

    public string Fingerprint { get; }

    public async ValueTask<G9CSyncScopeFragment> CreateAsync(G9CSyncScopeContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A factory is preferred: it is the registration EF recommends for work outside a web request, and it never hands
        // out a context another component of the same scope is using.
        if (context.Services.GetService<IDbContextFactory<TContext>>() is { } factory)
        {
            await using var owned = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            return Translate(owned, context);
        }

        if (context.Services.GetService<IServiceScopeFactory>() is not { } scopes)
        {
            throw new G9CSyncException(G9SyncErrorCodes.ServerError,
                $"The scope of {context.Table.LogicalName} in '{context.Subscription.Name}' is an EF Core query, but no service provider was "
                + $"given to the store. Register the server through AddG9SyncDataServer, or register IDbContextFactory<{typeof(TContext).Name}>.");
        }

        // A scope of our own: the store is a singleton, and a scoped DbContext resolved from the root provider would live
        // (and be shared across threads) for the life of the process.
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetService<TContext>()
                 ?? throw new G9CSyncException(G9SyncErrorCodes.ServerError,
                     $"The scope of {context.Table.LogicalName} in '{context.Subscription.Name}' needs {typeof(TContext).Name}, which is not registered.");
        return Translate(db, context);
    }

    private G9CSyncScopeFragment Translate(TContext db, G9CSyncScopeContext context)
    {
        var key = ValidateEntity(db.Model, context);
        var query = _query(db, context)
                    ?? throw new G9CSyncException(G9SyncErrorCodes.ServerError,
                        $"The scope query of {context.Table.LogicalName} in '{context.Subscription.Name}' returned null.");

        // query.Select(e => EF.Property<TKey>(e, "Key")) built by hand: the key's CLR type is known only at run time.
        var row = Expression.Parameter(typeof(TEntity), "e");
        var selector = Expression.Lambda(
            Expression.Call(typeof(EF), nameof(EF.Property), [key.ClrType], row, Expression.Constant(key.Name)),
            row);
        var keys = query.Provider.CreateQuery(Expression.Call(
            typeof(Queryable), nameof(Queryable.Select), [typeof(TEntity), key.ClrType], query.Expression, Expression.Quote(selector)));

        using var command = keys.CreateDbCommand();
        var parameters = new DbParameter[command.Parameters.Count];
        for (var i = 0; i < parameters.Length; i++) parameters[i] = command.Parameters[i];

        // Detached from the command, so the store may clone them into its own statements after this context is gone.
        command.Parameters.Clear();
        return new G9CSyncScopeFragment(command.CommandText, parameters);
    }

    /// <summary>
    ///     The entity must be the included table, keyed by the column the sync model keys it by. Checked on every call because
    ///     it is a lookup, and a mismatch would otherwise compare one table's keys with another's — silently wrong rows.
    /// </summary>
    private static IProperty ValidateEntity(IModel model, G9CSyncScopeContext context)
    {
        var table = context.Table;
        var entityType = model.FindEntityType(typeof(TEntity))
                         ?? throw Mismatch(context, $"'{typeof(TEntity).Name}' is not part of {typeof(TContext).Name}'s model");

        var tableName = entityType.GetTableName();
        var schema = entityType.GetSchema() ?? model.GetDefaultSchema() ?? "public";
        if (!string.Equals(tableName, table.Name, StringComparison.Ordinal) || !string.Equals(schema, table.Schema, StringComparison.Ordinal))
        {
            throw Mismatch(context, $"'{typeof(TEntity).Name}' maps to {schema}.{tableName}, not {table.Schema}.{table.Name}");
        }

        var primaryKey = entityType.FindPrimaryKey();
        if (primaryKey is not { Properties.Count: 1 })
        {
            throw Mismatch(context, $"'{typeof(TEntity).Name}' needs a single-column primary key");
        }

        var key = primaryKey.Properties[0];
        var column = key.GetColumnName(StoreObjectIdentifier.Table(tableName!, entityType.GetSchema() ?? model.GetDefaultSchema())) ?? key.Name;
        if (!string.Equals(column, table.KeyColumn, StringComparison.Ordinal))
        {
            throw Mismatch(context, $"'{typeof(TEntity).Name}' is keyed by \"{column}\", the sync model by \"{table.KeyColumn}\"");
        }

        return key;
    }

    private static G9CSyncException Mismatch(G9CSyncScopeContext context, string detail) =>
        new(G9SyncErrorCodes.ModelInvalid, $"The EF Core scope of {context.Table.LogicalName} in '{context.Subscription.Name}' is invalid: {detail}.");
}

/// <summary>EF Core flavours of <see cref="G9CSyncSubscriptionBuilder.Include(string, IG9SyncScopeProvider, string?, bool)" />.</summary>
public static class G9CEfCoreSubscriptionBuilderExtensions
{
    /// <summary>
    ///     Includes <paramref name="table" /> with the rows <paramref name="query" /> returns for the caller.
    ///     <para>
    ///         Keep ROW-STATE conditions (soft delete, archived, inactive) in <paramref name="whenSql" />, not in the query: a
    ///         row that fails <c>when</c> is sent as a delete by the next delta, while a row that leaves the query is only
    ///         noticed by reconciliation. Use the query for WHO may see a row; use <c>when</c> for whether it still exists.
    ///     </para>
    ///     <para>
    ///         ⚠ When the query depends on the caller's permissions, declare those permissions as a server parameter (a
    ///         permission stamp is enough): the changed auth hash is what makes each device reconcile and drop what it lost.
    ///     </para>
    /// </summary>
    /// <param name="builder">The subscription.</param>
    /// <param name="table">Logical name (<c>schema.table</c>) of the table <typeparamref name="TEntity" /> maps to.</param>
    /// <param name="query">The rows visible to the caller. Translated per request, never executed by the provider.</param>
    /// <param name="whenSql">Row-state filter over <c>{row}</c>; rows failing it are sent as deletes.</param>
    /// <param name="allowUpload">Whether the query also authorises uploads to the table.</param>
    /// <param name="version">Bump when the query's meaning changes (see <see cref="G9CEfCoreScopeProvider{TContext, TEntity}" />).</param>
    public static G9CSyncSubscriptionBuilder IncludeQuery<TContext, TEntity>(this G9CSyncSubscriptionBuilder builder, string table,
        Func<TContext, G9CSyncScopeContext, IQueryable<TEntity>> query, string? whenSql = null, bool allowUpload = true, string version = "1")
        where TContext : DbContext
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.Include(table, new G9CEfCoreScopeProvider<TContext, TEntity>(query, version), whenSql, allowUpload);
    }
}
