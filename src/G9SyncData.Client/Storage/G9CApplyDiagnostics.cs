using System.Globalization;
using System.Text;

namespace G9SyncData.Client.Storage;

/// <summary>
///     How an apply ran, as opposed to what it changed (ADR-0058). The large-download optimisations are all conditional and all
///     silent when they do not engage: one trigger the library did not create keeps trigger execution on for every apply, a table that
///     already holds a row keeps its indexes live, and a sliced apply does without index deferral altogether. Measured on 500,000
///     rows, an apply that gets none of them is 5.3 times slower than one that gets all of them - and until this type existed nothing
///     said which of the two a device had just run.
/// </summary>
public sealed class G9CApplyDiagnostics
{
    /// <summary>Why a table large enough for index deferral kept its indexes live: it already held more rows than the download would add (<c>RebuildIndexesMinimumGrowth</c>, ADR-0058).</summary>
    public const string ReasonTableNotEmpty = "table-not-empty";

    /// <summary>Most foreign trigger names kept in <see cref="ForeignTriggers" />.</summary>
    public const int MaxForeignTriggerNames = 10;

    /// <summary>Apply transactions covered: one per committed slice, one for an atomic apply.</summary>
    public int Transactions { get; set; }

    /// <summary>Of <see cref="Transactions" />, how many ran with trigger execution switched off on the store's connection.</summary>
    public int TransactionsWithTriggersSkipped { get; set; }

    /// <summary>Triggers the library did not create, which kept trigger execution on (at most <see cref="MaxForeignTriggerNames" /> names).</summary>
    public List<string> ForeignTriggers { get; } = [];

    /// <summary>Plain indexes dropped for the load and rebuilt after it, per local table.</summary>
    public Dictionary<string, int> DeferredIndexes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tables whose snapshot was large enough for index deferral but which kept live indexes, with the reason.</summary>
    public Dictionary<string, string> LiveIndexReasons { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Rows applied per local table.</summary>
    public Dictionary<string, long> RowsByTable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Milliseconds spent applying rows per local table (row statements only; index rebuilds are in <see cref="IndexRebuildMs" />).</summary>
    public Dictionary<string, long> MsByTable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Milliseconds spent rebuilding deferred indexes.</summary>
    public long IndexRebuildMs { get; set; }

    /// <summary>Milliseconds from opening the first apply transaction to committing the last.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>
    ///     True when every transaction ran with trigger execution off, false when any ran with it on, null when nothing was applied.
    /// </summary>
    public bool? TriggersSkipped => Transactions == 0 ? null : TransactionsWithTriggersSkipped == Transactions;

    /// <summary>Indexes dropped and rebuilt, over all tables.</summary>
    public int DeferredIndexCount => DeferredIndexes.Values.Sum();

    public void Merge(G9CApplyDiagnostics other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Transactions += other.Transactions;
        TransactionsWithTriggersSkipped += other.TransactionsWithTriggersSkipped;
        IndexRebuildMs += other.IndexRebuildMs;
        ElapsedMs += other.ElapsedMs;
        foreach (var name in other.ForeignTriggers)
        {
            if (ForeignTriggers.Count >= MaxForeignTriggerNames) break;
            if (!ForeignTriggers.Contains(name, StringComparer.OrdinalIgnoreCase)) ForeignTriggers.Add(name);
        }

        foreach (var (table, count) in other.DeferredIndexes) DeferredIndexes[table] = DeferredIndexes.GetValueOrDefault(table) + count;
        foreach (var (table, reason) in other.LiveIndexReasons) LiveIndexReasons[table] = reason;
        foreach (var (table, rows) in other.RowsByTable) RowsByTable[table] = RowsByTable.GetValueOrDefault(table) + rows;
        foreach (var (table, ms) in other.MsByTable) MsByTable[table] = MsByTable.GetValueOrDefault(table) + ms;
    }

    /// <summary>One line for a log: what engaged, what did not and why, and the tables that took the time.</summary>
    public override string ToString()
    {
        if (Transactions == 0) return "nothing applied";
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Transactions} transaction(s) in {ElapsedMs} ms; triggers ");
        text.Append(TriggersSkipped == true ? "skipped" : TransactionsWithTriggersSkipped == 0 ? "LIVE" : $"skipped in {TransactionsWithTriggersSkipped} of {Transactions}");
        if (ForeignTriggers.Count > 0) text.Append(" (foreign: ").AppendJoin(", ", ForeignTriggers).Append(')');
        if (DeferredIndexes.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"; {DeferredIndexCount} index(es) rebuilt after the load in {IndexRebuildMs} ms (")
                .AppendJoin(", ", DeferredIndexes.Select(pair => pair.Key + ":" + pair.Value.ToString(CultureInfo.InvariantCulture))).Append(')');
        }

        if (LiveIndexReasons.Count > 0) text.Append("; live indexes on ").AppendJoin(", ", LiveIndexReasons.Select(pair => pair.Key + " (" + pair.Value + ")"));

        var slowest = MsByTable.OrderByDescending(pair => pair.Value).Take(4).ToList();
        if (slowest.Count > 0)
        {
            text.Append("; ").AppendJoin(", ", slowest.Select(pair =>
            {
                var rows = RowsByTable.GetValueOrDefault(pair.Key);
                var rate = pair.Value > 0 ? rows * 1000 / pair.Value : rows;
                return string.Create(CultureInfo.InvariantCulture, $"{pair.Key} {rows} rows in {pair.Value} ms ({rate}/s)");
            }));
        }

        return text.ToString();
    }
}
