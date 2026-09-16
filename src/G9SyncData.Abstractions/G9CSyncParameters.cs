using System.Collections;
using System.Globalization;
using System.Text;

namespace G9SyncData;

/// <summary>
///     Typed, canonicalised subscription parameters. Values are normalised on the way in (GUIDs lower-case,
///     lists sorted and de-duplicated) so two spellings of the same parameters always produce the same
///     subscription key — the direct fix for Dotmim's duplicate-scope trap (P-10).
/// </summary>
/// <remarks>AOT-safe by construction: no anonymous objects, no reflection.</remarks>
public sealed class G9CSyncParameters : IEnumerable<KeyValuePair<string, string>>
{
    private readonly SortedDictionary<string, string> _values = new(StringComparer.Ordinal);

    /// <summary>Canonical name → value pairs.</summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    public int Count => _values.Count;

    public G9CSyncParameters Set(string name, Guid value) => SetCore(name, G9CSyncKey.FromGuid(value));

    public G9CSyncParameters Set(string name, long value) => SetCore(name, value.ToString(CultureInfo.InvariantCulture));

    public G9CSyncParameters Set(string name, int value) => SetCore(name, value.ToString(CultureInfo.InvariantCulture));

    public G9CSyncParameters Set(string name, bool value) => SetCore(name, value ? "true" : "false");

    public G9CSyncParameters Set(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SetCore(name, value);
    }

    public G9CSyncParameters SetList(string name, IEnumerable<Guid> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SetCore(name, string.Join(',', values.Select(G9CSyncKey.FromGuid).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)));
    }

    public G9CSyncParameters SetList(string name, IEnumerable<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return SetCore(name, string.Join(',', values.Distinct().Order().Select(v => v.ToString(CultureInfo.InvariantCulture))));
    }

    public G9CSyncParameters SetList(string name, IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var list = values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (list.Any(v => v.Contains(',', StringComparison.Ordinal)))
        {
            throw new ArgumentException("String list values must not contain ','.", nameof(values));
        }

        return SetCore(name, string.Join(',', list));
    }

    /// <summary>Builds from wire values. Values are trusted to be canonical only after server validation.</summary>
    public static G9CSyncParameters FromValues(IEnumerable<KeyValuePair<string, string>>? values)
    {
        var result = new G9CSyncParameters();
        if (values is null) return result;
        foreach (var pair in values)
        {
            result.SetCore(pair.Key, pair.Value);
        }

        return result;
    }

    /// <summary>Stable text form: <c>a=1&amp;b=x</c>, names ordinal-sorted, values URI-escaped.</summary>
    public string ToCanonicalString()
    {
        var builder = new StringBuilder();
        foreach (var (name, value) in _values)
        {
            if (builder.Length > 0) builder.Append('&');
            builder.Append(Uri.EscapeDataString(name)).Append('=').Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    /// <summary>The subscription key: <c>name</c> or <c>name?canonical-parameters</c>.</summary>
    public static string BuildSubscriptionKey(string subscriptionName, G9CSyncParameters? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionName);
        return parameters is null || parameters.Count == 0
            ? subscriptionName
            : subscriptionName + "?" + parameters.ToCanonicalString();
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private G9CSyncParameters SetCore(string name, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _values[name] = value;
        return this;
    }
}
