using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;

namespace G9SyncData.Server.Engine;

/// <summary>Validates a subscription reference: definition, authorisation, typed client parameters and server parameters.</summary>
internal static class G9CSubscriptionResolver
{
    public static bool TryResolve(G9CSyncServerModel model, G9DtSubscriptionRef reference, G9CSyncAuthContext auth,
        [NotNullWhen(true)] out G9CResolvedSubscription? resolved, [NotNullWhen(false)] out G9DtSubscriptionState? failure)
    {
        resolved = null;
        failure = null;
        var name = reference.Name ?? string.Empty;

        // Failures carry the client's own subscription key, so the client can mark exactly that subscription failed.
        var key = KeyOf(reference);
        var definition = string.IsNullOrWhiteSpace(name) ? null : model.FindSubscription(name);
        if (definition is null)
        {
            failure = Fail(key, G9ESubscriptionAction.Unknown, $"Subscription '{name}' is not defined on this server.");
            return false;
        }

        if (!auth.IsSubscriptionAllowed(definition.Name))
        {
            failure = Fail(key, G9ESubscriptionAction.Forbidden, $"The caller may not use subscription '{name}'.");
            return false;
        }

        var supplied = reference.Parameters ?? new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var suppliedName in supplied.Keys)
        {
            if (definition.ClientParameters.All(p => !string.Equals(p.Name, suppliedName, StringComparison.Ordinal)))
            {
                failure = Fail(key, G9ESubscriptionAction.InvalidParameters, $"Unknown parameter '{suppliedName}'.");
                return false;
            }
        }

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var canonical = new G9CSyncParameters();
        foreach (var parameter in definition.ClientParameters)
        {
            if (!supplied.TryGetValue(parameter.Name, out var text))
            {
                if (parameter.Required)
                {
                    failure = Fail(key, G9ESubscriptionAction.InvalidParameters, $"Missing required parameter '{parameter.Name}'.");
                    return false;
                }

                values[parameter.Name] = null;
                continue;
            }

            if (!TryParseClientValue(parameter, text, out var value, canonical))
            {
                failure = Fail(key, G9ESubscriptionAction.InvalidParameters,
                    $"Parameter '{parameter.Name}' is not a valid {(parameter.IsList ? "list of " : string.Empty)}{parameter.Type}.");
                return false;
            }

            values[parameter.Name] = value;
        }

        var authText = new StringBuilder();
        foreach (var parameter in definition.ServerParameters.OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            auth.ServerParameters.TryGetValue(parameter.Name, out var raw);
            if (!TryNormalizeServerValue(parameter, raw, out var value, out var canonicalText))
            {
                throw new G9CSyncException(G9SyncErrorCodes.ServerError,
                    $"The auth context supplied an unsupported value type for server parameter '{parameter.Name}' of subscription '{definition.Name}'.");
            }

            values[parameter.Name] = value;
            authText.Append(parameter.Name).Append('=').Append(canonicalText).Append(';');
        }

        resolved = new G9CResolvedSubscription
        {
            Definition = definition,
            Key = G9CSyncParameters.BuildSubscriptionKey(definition.Name, canonical),
            Parameters = canonical,
            Values = values,
            AuthHash = authText.Length == 0 ? string.Empty : G9CHash.Sha256Hex(authText.ToString()),
            Auth = auth
        };
        return true;
    }

    public static string FailureCode(G9DtSubscriptionState failure) => failure.Action switch
    {
        G9ESubscriptionAction.Unknown => G9SyncErrorCodes.UnknownSubscription,
        G9ESubscriptionAction.Forbidden => G9SyncErrorCodes.SubscriptionForbidden,
        _ => G9SyncErrorCodes.InvalidParameters
    };

    /// <summary>The key the client stores for this reference (its parameters as sent), or the bare name when they are unusable.</summary>
    private static string KeyOf(G9DtSubscriptionRef reference)
    {
        if (string.IsNullOrWhiteSpace(reference.Name)) return string.Empty;
        try
        {
            return G9CSyncParameters.BuildSubscriptionKey(reference.Name, G9CSyncParameters.FromValues(reference.Parameters));
        }
        catch (ArgumentException)
        {
            return reference.Name;
        }
    }

    private static G9DtSubscriptionState Fail(string key, G9ESubscriptionAction action, string message) =>
        new() { Key = key, Action = action, Message = message };

    private static bool TryParseClientValue(G9CSyncParameterDefinition parameter, string text, out object? value, G9CSyncParameters canonical)
    {
        value = null;
        if (parameter.IsList)
        {
            var parts = text.Length == 0 ? [] : text.Split(',');
            switch (parameter.Type)
            {
                case G9ESyncColumnType.Guid:
                {
                    var list = new Guid[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (!Guid.TryParse(parts[i], out list[i])) return false;
                    }

                    canonical.SetList(parameter.Name, list);
                    value = list;
                    return true;
                }
                case G9ESyncColumnType.Int32:
                {
                    var list = new int[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (!int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out list[i])) return false;
                    }

                    canonical.SetList(parameter.Name, list.Select(v => (long)v));
                    value = list;
                    return true;
                }
                case G9ESyncColumnType.Int64:
                {
                    var list = new long[parts.Length];
                    for (var i = 0; i < parts.Length; i++)
                    {
                        if (!long.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out list[i])) return false;
                    }

                    canonical.SetList(parameter.Name, list);
                    value = list;
                    return true;
                }
                case G9ESyncColumnType.String:
                    canonical.SetList(parameter.Name, parts);
                    value = parts;
                    return true;
                default:
                    return false;
            }
        }

        switch (parameter.Type)
        {
            case G9ESyncColumnType.Guid when Guid.TryParse(text, out var g):
                canonical.Set(parameter.Name, g);
                value = g;
                return true;
            case G9ESyncColumnType.Int32 when int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i32):
                canonical.Set(parameter.Name, i32);
                value = i32;
                return true;
            case G9ESyncColumnType.Int64 when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var i64):
                canonical.Set(parameter.Name, i64);
                value = i64;
                return true;
            case G9ESyncColumnType.Boolean when bool.TryParse(text, out var b):
                canonical.Set(parameter.Name, b);
                value = b;
                return true;
            case G9ESyncColumnType.String:
                canonical.Set(parameter.Name, text);
                value = text;
                return true;
            default:
                return false;
        }
    }

    private static bool TryNormalizeServerValue(G9CSyncParameterDefinition parameter, object? raw, out object? value, out string canonical)
    {
        value = null;
        canonical = "∅";
        if (raw is null) return true;

        if (parameter.IsList)
        {
            switch (parameter.Type)
            {
                case G9ESyncColumnType.Guid:
                {
                    Guid[]? list = raw switch
                    {
                        Guid[] a => a,
                        IEnumerable<Guid> e => e.ToArray(),
                        IEnumerable<string> s => s.Select(Guid.Parse).ToArray(),
                        _ => null
                    };
                    if (list is null) return false;
                    value = list;
                    canonical = string.Join(',', list.Select(G9CSyncKey.FromGuid).Order(StringComparer.Ordinal));
                    return true;
                }
                case G9ESyncColumnType.Int32:
                {
                    int[]? list = raw switch
                    {
                        int[] a => a,
                        IEnumerable<int> e => e.ToArray(),
                        IEnumerable<long> l => l.Select(v => checked((int)v)).ToArray(),
                        _ => null
                    };
                    if (list is null) return false;
                    value = list;
                    canonical = string.Join(',', list.Order().Select(v => v.ToString(CultureInfo.InvariantCulture)));
                    return true;
                }
                case G9ESyncColumnType.Int64:
                {
                    long[]? list = raw switch
                    {
                        long[] a => a,
                        IEnumerable<long> e => e.ToArray(),
                        IEnumerable<int> i => i.Select(v => (long)v).ToArray(),
                        _ => null
                    };
                    if (list is null) return false;
                    value = list;
                    canonical = string.Join(',', list.Order().Select(v => v.ToString(CultureInfo.InvariantCulture)));
                    return true;
                }
                case G9ESyncColumnType.String:
                {
                    var list = raw switch
                    {
                        string[] a => a,
                        IEnumerable<string> e => e.ToArray(),
                        _ => null
                    };
                    if (list is null) return false;
                    value = list;
                    canonical = string.Join((char)0x1F, list.Order(StringComparer.Ordinal));
                    return true;
                }
                default:
                    return false;
            }
        }

        switch (parameter.Type)
        {
            case G9ESyncColumnType.Guid:
                if (raw is Guid g || (raw is string s && Guid.TryParse(s, out g)))
                {
                    value = g;
                    canonical = G9CSyncKey.FromGuid(g);
                    return true;
                }

                return false;
            case G9ESyncColumnType.Int32:
                if (raw is int i32 || (raw is long l32 && l32 is >= int.MinValue and <= int.MaxValue && (i32 = (int)l32) == l32))
                {
                    value = i32;
                    canonical = i32.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            case G9ESyncColumnType.Int64:
                if (raw is long i64 || (raw is int small && (i64 = small) == small))
                {
                    value = i64;
                    canonical = i64.ToString(CultureInfo.InvariantCulture);
                    return true;
                }

                return false;
            case G9ESyncColumnType.Boolean when raw is bool b:
                value = b;
                canonical = b ? "1" : "0";
                return true;
            case G9ESyncColumnType.String when raw is string text:
                value = text;
                canonical = text;
                return true;
            default:
                return false;
        }
    }
}
