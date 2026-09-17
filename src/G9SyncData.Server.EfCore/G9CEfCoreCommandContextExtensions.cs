using System.Data.Common;
using G9SyncData.Server.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.Server.EfCore;

/// <summary>A <see cref="DbContext" /> bound to a push transaction. Dispose it; the transaction itself belongs to the push.</summary>
public sealed class G9CEfCoreBoundContext<TContext> : IAsyncDisposable
    where TContext : DbContext
{
    private readonly AsyncServiceScope? _scope;

    private G9CEfCoreBoundContext(TContext db, AsyncServiceScope? scope)
    {
        Db = db;
        _scope = scope;
    }

    public TContext Db { get; }

    /// <summary>
    ///     A context from <see cref="IDbContextFactory{TContext}" /> when registered, else from a fresh DI scope, joined to
    ///     <paramref name="connection" /> and <paramref name="transaction" /> so its SaveChanges is part of the push.
    /// </summary>
    internal static async ValueTask<G9CEfCoreBoundContext<TContext>> OpenAsync(IServiceProvider services, DbConnection? connection,
        DbTransaction? transaction, CancellationToken cancellationToken)
    {
        G9CEfCoreBoundContext<TContext> bound;
        if (services.GetService<IDbContextFactory<TContext>>() is { } factory)
        {
            bound = new G9CEfCoreBoundContext<TContext>(await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false), null);
        }
        else
        {
            var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
            bound = new G9CEfCoreBoundContext<TContext>(scope.ServiceProvider.GetRequiredService<TContext>(), scope);
        }

        if (connection is null || transaction is null) return bound;
        try
        {
            bound.Db.Database.SetDbConnection(connection);
            await bound.Db.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            return bound;
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("execution strategy", StringComparison.OrdinalIgnoreCase))
        {
            await bound.DisposeAsync().ConfigureAwait(false);
            throw new G9CSyncException(G9SyncErrorCodes.ServerError,
                $"{typeof(TContext).Name} is configured with a retrying execution strategy, which cannot join the push transaction. " +
                "Register a context for G9SyncData without EnableRetryOnFailure.", exception);
        }
        catch
        {
            await bound.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_scope is { } scope) await scope.DisposeAsync().ConfigureAwait(false);
        else await Db.DisposeAsync().ConfigureAwait(false);
    }
}

public static class G9CEfCoreCommandContextExtensions
{
    /// <summary>
    ///     The application's DbContext joined to the push transaction, for a command handler (ADR-0025):
    ///     <code>
    ///     await using var bound = await context.OpenDbContextAsync&lt;AgricultureDbContext&gt;(ct);
    ///     await placements.ReplantAsync(bound.Db, cmd.PotId, cmd.TreeId, ct);   // your existing service and rules
    ///     await bound.Db.SaveChangesAsync(ct);
    ///     </code>
    /// </summary>
    public static ValueTask<G9CEfCoreBoundContext<TContext>> OpenDbContextAsync<TContext>(this G9CCommandContext context, CancellationToken cancellationToken = default)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(context);
        return G9CEfCoreBoundContext<TContext>.OpenAsync(context.Services, context.Connection, context.Transaction, cancellationToken);
    }
}
