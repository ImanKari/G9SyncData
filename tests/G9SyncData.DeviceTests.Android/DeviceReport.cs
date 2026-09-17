using System.Globalization;
using System.Text;
using System.Text.Json;

namespace G9SyncData.DeviceTests;

/// <summary>
///     A flat, ordered report: top-level values plus named lists of records. Written with <see cref="Utf8JsonWriter" /> (no reflection)
///     so the trimmed Release build serialises exactly what was recorded.
/// </summary>
public sealed class DeviceReport
{
    private readonly List<KeyValuePair<string, object?>> _values = [];
    private readonly Dictionary<string, List<List<KeyValuePair<string, object?>>>> _lists = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    public void Set(string name, object? value)
    {
        lock (_lock)
        {
            var index = _values.FindIndex(v => v.Key == name);
            if (index >= 0) _values[index] = new(name, value);
            else _values.Add(new(name, value));
        }
    }

    public void Add(string list, params (string Name, object? Value)[] fields)
    {
        lock (_lock)
        {
            if (!_lists.TryGetValue(list, out var records)) _lists[list] = records = [];
            records.Add(fields.Select(f => new KeyValuePair<string, object?>(f.Name, f.Value)).ToList());
        }
    }

    public string ToJson()
    {
        lock (_lock)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var (name, value) in _values) Write(writer, name, value);
                foreach (var (list, records) in _lists)
                {
                    writer.WriteStartArray(list);
                    foreach (var record in records)
                    {
                        writer.WriteStartObject();
                        foreach (var (name, value) in record) Write(writer, name, value);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private static void Write(Utf8JsonWriter writer, string name, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNull(name); break;
            case bool b: writer.WriteBoolean(name, b); break;
            case int i: writer.WriteNumber(name, i); break;
            case long l: writer.WriteNumber(name, l); break;
            case double d: writer.WriteNumber(name, Math.Round(d, 3)); break;
            case TimeSpan t: writer.WriteNumber(name, Math.Round(t.TotalMilliseconds, 1)); break;
            case IFormattable f: writer.WriteString(name, f.ToString(null, CultureInfo.InvariantCulture)); break;
            default: writer.WriteString(name, value.ToString()); break;
        }
    }
}
