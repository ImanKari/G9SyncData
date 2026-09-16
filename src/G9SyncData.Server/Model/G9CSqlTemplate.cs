using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace G9SyncData.Server.Model;

/// <summary>
///     Binds the SQL templates of the model: <c>{row}</c> becomes a table alias, <c>{value}</c> a staged value, and every
///     DECLARED parameter <c>@Name</c> is renamed with a prefix so two subscriptions with the same parameter name (two farms
///     in one upload) can live in one statement. Undeclared <c>@x</c> tokens are left alone and fail boot validation.
/// </summary>
public static partial class G9CSqlTemplate
{
    public static string Bind(string template, string rowAlias, string parameterPrefix, IReadOnlySet<string> parameterNames)
    {
        ArgumentNullException.ThrowIfNull(template);
        var sql = template.Replace("{row}", rowAlias, StringComparison.Ordinal);
        return ParameterRegex().Replace(sql, match =>
            parameterNames.Contains(match.Groups[1].Value) ? "@" + parameterPrefix + match.Groups[1].Value : match.Value);
    }

    public static string BindValue(string template, string valueExpression)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Replace("{value}", valueExpression, StringComparison.Ordinal);
    }

    [GeneratedRegex("(?<![@A-Za-z0-9_])@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterRegex();
}

internal static class G9CHash
{
    public static string Sha256Hex(string text, int bytes = 16) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)).AsSpan(0, bytes));
}
