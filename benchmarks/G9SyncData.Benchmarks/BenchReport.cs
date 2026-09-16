using System.Globalization;
using System.Text;

namespace G9SyncData.Benchmarks;

/// <summary>Collects measurements, echoes them to the console and renders a markdown report.</summary>
internal sealed class BenchReport(int rows)
{
    private readonly List<(string Name, string Value)> _environment = [];
    private readonly List<(string Section, string Metric, string Value)> _lines = [];

    public void Environment(string name, string value) => _environment.Add((name, value));

    public void Add(string section, string metric, string value)
    {
        _lines.Add((section, metric, value));
        Console.WriteLine($"  {section,-4} {metric,-62} {value}");
    }

    public static string Ms(TimeSpan elapsed) => elapsed.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";

    public static string Rate(long count, TimeSpan elapsed) =>
        (count / Math.Max(0.001, elapsed.TotalSeconds)).ToString("N0", CultureInfo.InvariantCulture) + " rows/s";

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => (bytes / 1024d / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (bytes / 1024d).ToString("N1", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };

    public string ToMarkdown()
    {
        var text = new StringBuilder()
            .AppendLine("# G9SyncData benchmark")
            .AppendLine()
            .AppendLine($"Rows: {rows.ToString("N0", CultureInfo.InvariantCulture)} · run {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC")
            .AppendLine();
        foreach (var (name, value) in _environment) text.AppendLine($"- **{name}:** {value}");
        text.AppendLine();
        foreach (var section in _lines.GroupBy(l => l.Section))
        {
            text.AppendLine($"## {section.Key}").AppendLine().AppendLine("| Metric | Value |").AppendLine("|---|---|");
            foreach (var (_, metric, value) in section) text.AppendLine($"| {metric} | {value} |");
            text.AppendLine();
        }

        return text.ToString();
    }
}
