using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Engine;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;

namespace G9SyncData.Tests.Server;

/// <summary>
///     What a device asks to subscribe to is untrusted input: every way a reference can be wrong must end in the right refusal,
///     with the client's own key, and never in an exception. Server parameters come from the host's auth provider: a value of the
///     wrong shape is a host bug and must say which parameter it was.
/// </summary>
public sealed class SubscriptionResolverTests
{
    private static readonly G9CSyncServerModel Model = BuildModel();

    private static G9CSyncServerModel BuildModel()
    {
        var builder = new G9CSyncModelBuilder();
        builder.Table("farm", "Pot", t => t.Key("Id").Column("SiteId", G9ESyncColumnType.Guid).Column("Code", G9ESyncColumnType.String));
        builder.Subscription("by-site", s => s.Parameter("SiteId", G9ESyncColumnType.Guid).Include("farm.Pot", "{row}.\"SiteId\" = @SiteId"));
        builder.Subscription("typed", s => s
            .Parameter("I", G9ESyncColumnType.Int32)
            .Parameter("L", G9ESyncColumnType.Int64)
            .Parameter("B", G9ESyncColumnType.Boolean)
            .Parameter("S", G9ESyncColumnType.String)
            .Parameter("Optional", G9ESyncColumnType.Guid, required: false)
            .Include("farm.Pot", "@I = @I AND @L = @L AND @B = @B AND @S = @S AND (@Optional IS NULL OR true)"));
        builder.Subscription("lists", s => s
            .Parameter("G", G9ESyncColumnType.Guid, isList: true)
            .Parameter("I", G9ESyncColumnType.Int32, isList: true)
            .Parameter("L", G9ESyncColumnType.Int64, isList: true)
            .Parameter("S", G9ESyncColumnType.String, isList: true)
            .Include("farm.Pot", "{row}.\"SiteId\" = ANY(@G) AND cardinality(@I) >= 0 AND cardinality(@L) >= 0 AND cardinality(@S) >= 0"));
        builder.Subscription("granted", s => s
            .ServerParameter("Sites", G9ESyncColumnType.Guid, isList: true)
            .ServerParameter("Level", G9ESyncColumnType.Int32)
            .ServerParameter("Codes", G9ESyncColumnType.Int32, isList: true)
            .Include("farm.Pot", "{row}.\"SiteId\" = ANY(@Sites) AND @Level >= 0 AND cardinality(@Codes) >= 0"));
        return builder.Build();
    }

    private static G9DtSubscriptionRef Ref(string name, params (string Name, string Value)[] parameters)
    {
        var reference = new G9DtSubscriptionRef { Name = name };
        foreach (var (key, value) in parameters) reference.Parameters[key] = value;
        return reference;
    }

    private static G9DtSubscriptionState Refused(G9DtSubscriptionRef reference, G9CSyncAuthContext? auth = null)
    {
        Assert.False(G9CSubscriptionResolver.TryResolve(Model, reference, auth ?? G9CSyncAuthContext.Anonymous, out var resolved, out var failure));
        Assert.Null(resolved);
        return failure!;
    }

    private static G9CResolvedSubscription Resolved(G9DtSubscriptionRef reference, G9CSyncAuthContext? auth = null)
    {
        Assert.True(G9CSubscriptionResolver.TryResolve(Model, reference, auth ?? G9CSyncAuthContext.Anonymous, out var resolved, out var failure),
            failure?.Message);
        return resolved!;
    }

    // ---------------------------------------------------------------- the definition and the caller

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-such-subscription")]
    public void An_unknown_name_is_refused_as_unknown(string name)
    {
        var failure = Refused(Ref(name));
        Assert.Equal(G9ESubscriptionAction.Unknown, failure.Action);
        Assert.Equal(G9SyncErrorCodes.UnknownSubscription, G9CSubscriptionResolver.FailureCode(failure));
        Assert.Equal(string.IsNullOrWhiteSpace(name) ? string.Empty : name, failure.Key);
    }

    [Fact]
    public void A_subscription_the_caller_may_not_use_is_forbidden()
    {
        var auth = new G9CSyncAuthContext { SubscriptionFilter = name => name != "by-site" };
        var failure = Refused(Ref("by-site", ("SiteId", Guid.NewGuid().ToString())), auth);
        Assert.Equal(G9ESubscriptionAction.Forbidden, failure.Action);
        Assert.Equal(G9SyncErrorCodes.SubscriptionForbidden, G9CSubscriptionResolver.FailureCode(failure));
    }

    // ---------------------------------------------------------------- client parameters

    [Fact]
    public void A_parameter_the_definition_does_not_declare_is_refused_and_named()
    {
        var failure = Refused(Ref("by-site", ("SiteId", Guid.NewGuid().ToString()), ("Injected", "1 OR 1=1")));
        Assert.Equal(G9ESubscriptionAction.InvalidParameters, failure.Action);
        Assert.Equal(G9SyncErrorCodes.InvalidParameters, G9CSubscriptionResolver.FailureCode(failure));
        Assert.Contains("'Injected'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_required_parameter_is_refused_and_a_missing_optional_one_is_null()
    {
        Assert.Contains("Missing required parameter 'SiteId'", Refused(Ref("by-site")).Message, StringComparison.Ordinal);

        var resolved = Resolved(Ref("typed", ("I", "1"), ("L", "2"), ("B", "true"), ("S", "x")));
        Assert.True(resolved.Values.ContainsKey("Optional"));
        Assert.Null(resolved.Values["Optional"]);
    }

    [Theory]
    [InlineData("I", "1.5")]
    [InlineData("I", "99999999999")]
    [InlineData("I", "")]
    [InlineData("L", "abc")]
    [InlineData("L", "99999999999999999999")]
    [InlineData("B", "yes")]
    [InlineData("Optional", "not-a-guid")]
    public void A_value_of_the_wrong_type_is_refused_naming_the_parameter(string name, string value)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal) { ["I"] = "1", ["L"] = "2", ["B"] = "true", ["S"] = "x", [name] = value };
        var failure = Refused(Ref("typed", parameters.Select(kv => (kv.Key, kv.Value)).ToArray()));
        Assert.Equal(G9ESubscriptionAction.InvalidParameters, failure.Action);
        Assert.Contains($"'{name}'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_values_are_parsed_invariantly_and_a_string_is_taken_as_sent()
    {
        var resolved = Resolved(Ref("typed", ("I", "-42"), ("L", "9000000000"), ("B", "False"), ("S", "ردیف ۱, 'quoted'")));
        Assert.Equal(-42, resolved.Values["I"]);
        Assert.Equal(9_000_000_000L, resolved.Values["L"]);
        Assert.Equal(false, resolved.Values["B"]);
        Assert.Equal("ردیف ۱, 'quoted'", resolved.Values["S"]);
    }

    [Fact]
    public void Lists_parse_every_element_and_one_bad_element_refuses_the_list()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var resolved = Resolved(Ref("lists", ("G", $"{a},{b}"), ("I", "1,-2"), ("L", "3,9000000000"), ("S", "x,y")));
        Assert.Equal(new[] { a, b }, resolved.Values["G"]);
        Assert.Equal(new[] { 1, -2 }, resolved.Values["I"]);
        Assert.Equal(new[] { 3L, 9_000_000_000L }, resolved.Values["L"]);
        Assert.Equal(new[] { "x", "y" }, resolved.Values["S"]);

        Assert.Contains("'G'", Refused(Ref("lists", ("G", $"{a},nope"), ("I", "1"), ("L", "1"), ("S", "x"))).Message, StringComparison.Ordinal);
        Assert.Contains("'I'", Refused(Ref("lists", ("G", $"{a}"), ("I", "1,x"), ("L", "1"), ("S", "x"))).Message, StringComparison.Ordinal);
        Assert.Contains("'L'", Refused(Ref("lists", ("G", $"{a}"), ("I", "1"), ("L", "1,"), ("S", "x"))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_list_is_an_empty_list_not_one_empty_element()
    {
        var resolved = Resolved(Ref("lists", ("G", ""), ("I", ""), ("L", ""), ("S", "")));
        Assert.Empty((Guid[])resolved.Values["G"]!);
        Assert.Empty((int[])resolved.Values["I"]!);
        Assert.Empty((string[])resolved.Values["S"]!);
    }

    [Fact]
    public void The_same_subscription_spelled_differently_has_one_key()
    {
        // A device that upper-cases its GUIDs must not end up with two subscriptions of the same rows.
        var site = Guid.NewGuid();
        var lower = Resolved(Ref("by-site", ("SiteId", site.ToString("D"))));
        var upper = Resolved(Ref("by-site", ("SiteId", site.ToString("D").ToUpperInvariant())));
        var braces = Resolved(Ref("by-site", ("SiteId", site.ToString("B"))));
        Assert.Equal(lower.Key, upper.Key);
        Assert.Equal(lower.Key, braces.Key);
    }

    // ---------------------------------------------------------------- server parameters (from the host's auth provider)

    [Fact]
    public void Server_parameters_accept_the_shapes_an_auth_provider_naturally_has()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var auth = new G9CSyncAuthContext
        {
            ServerParameters = new Dictionary<string, object?>
            {
                ["Sites"] = new List<string> { a.ToString(), b.ToString() },
                ["Level"] = 3L,
                ["Codes"] = new List<long> { 5, 7 }
            }
        };
        var resolved = Resolved(Ref("granted"), auth);
        Assert.Equal(new[] { a, b }, resolved.Values["Sites"]);
        Assert.Equal(3, resolved.Values["Level"]);
        Assert.Equal(new[] { 5, 7 }, resolved.Values["Codes"]);
        Assert.NotEqual(string.Empty, resolved.AuthHash);
    }

    [Fact]
    public void The_auth_hash_follows_the_values_and_not_their_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        G9CSyncAuthContext Auth(object sites, int level) => new()
        {
            ServerParameters = new Dictionary<string, object?> { ["Sites"] = sites, ["Level"] = level, ["Codes"] = new[] { 1 } }
        };

        var one = Resolved(Ref("granted"), Auth(new[] { a, b }, 1)).AuthHash;
        Assert.Equal(one, Resolved(Ref("granted"), Auth(new[] { b, a }, 1)).AuthHash);              // same grant, other order
        Assert.NotEqual(one, Resolved(Ref("granted"), Auth(new[] { a }, 1)).AuthHash);              // a site taken away
        Assert.NotEqual(one, Resolved(Ref("granted"), Auth(new[] { a, b }, 2)).AuthHash);           // a level changed
    }

    [Fact]
    public void A_missing_server_parameter_is_null_and_still_hashed()
    {
        var resolved = Resolved(Ref("granted"));
        Assert.Null(resolved.Values["Sites"]);
        Assert.NotEqual(string.Empty, resolved.AuthHash);
    }

    [Theory]
    [MemberData(nameof(WrongServerValues))]
    public void A_server_value_of_the_wrong_shape_fails_naming_the_parameter(string parameter, object value)
    {
        var values = new Dictionary<string, object?> { ["Sites"] = new[] { Guid.NewGuid() }, ["Level"] = 1, ["Codes"] = new[] { 1 } };
        values[parameter] = value;
        var exception = Assert.Throws<G9CSyncException>(() =>
            G9CSubscriptionResolver.TryResolve(Model, Ref("granted"), new G9CSyncAuthContext { ServerParameters = values }, out _, out _));
        Assert.Equal(G9SyncErrorCodes.ServerError, exception.Code);
        Assert.Contains($"'{parameter}'", exception.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, object> WrongServerValues() => new()
    {
        { "Sites", new List<string> { Guid.NewGuid().ToString(), "not-a-guid" } },   // Guid.Parse threw FormatException until 1.5.7
        { "Sites", 42 },
        { "Level", "three" },
        { "Level", 1L + int.MaxValue },
        { "Codes", new List<long> { 1, 1L + int.MaxValue } },                        // checked() threw OverflowException until 1.5.7
        { "Codes", new[] { "1" } }
    };
}
