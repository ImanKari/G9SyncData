using G9SyncData.Client.Storage;

namespace G9SyncData.Tests.Client;

/// <summary>The revocation policy's decision order and defaults (ADR-0026).</summary>
public sealed class RevocationPolicyUnitTests
{
    [Fact]
    public void Without_rules_clean_rows_are_deleted_and_rows_with_pending_changes_are_quarantined()
    {
        Assert.Equal(G9ERevocationDisposition.Delete, G9CRevocationPolicy.Default.Decide(Row("Pot", pending: false), new Editor()).Disposition);
        Assert.Equal(G9ERevocationDisposition.Quarantine, G9CRevocationPolicy.Default.Decide(Row("Pot", pending: true), new Editor()).Disposition);
    }

    [Fact]
    public void The_first_matching_rule_decides_and_is_named()
    {
        var policy = G9CRevocationPolicy.Create(rules => rules
            .ForTable("tree-pending", "Tree", r => r.HasPendingChanges).MarkRevoked()
            .ForTable("trees", "Tree").Delete()
            .When("everything-else", _ => true).Quarantine());

        var (rule, disposition) = policy.Decide(Row("Tree", pending: true), new Editor());
        Assert.Equal(("tree-pending", G9ERevocationDisposition.MarkRevoked), (rule!.Name, disposition));
        Assert.Equal("trees", policy.Decide(Row("tree", pending: false), new Editor()).Rule!.Name);   // table names are case-insensitive
        Assert.Equal(G9ERevocationDisposition.Quarantine, policy.Decide(Row("Pot", pending: false), new Editor()).Disposition);
    }

    [Fact]
    public void A_rule_name_may_be_declared_only_once()
    {
        Assert.Throws<ArgumentException>(() => G9CRevocationPolicy.Create(rules => rules.When("a", _ => true).Delete().When("a", _ => false).Delete()));
    }

    private static G9CRevokedRow Row(string table, bool pending) => new()
    {
        LocalTable = table, Key = "k", Reason = G9ERevocationReason.LeftScope, HasPendingChanges = pending, IsRejected = false,
        Values = new Dictionary<string, object?>()
    };

    private sealed class Editor : IG9RevokedRowEditor
    {
        public void Set(string column, object? value)
        {
        }
    }
}
