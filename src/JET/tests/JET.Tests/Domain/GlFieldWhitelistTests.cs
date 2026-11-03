using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlFieldWhitelistTests
{
    [Fact]
    public void TryResolve_ApprovalDate_MapsToApprovalDateColumn()
    {
        Assert.True(GlFieldWhitelist.TryResolve("approvalDate", out var column));
        Assert.Equal(new GlFieldColumn("approval_date", GlFieldKind.Date), column);
    }

    [Fact]
    public void TryResolve_Amount_IsAmountKind()
    {
        Assert.True(GlFieldWhitelist.TryResolve("amount", out var column));
        Assert.Equal(GlFieldKind.Amount, column.Kind);
    }

    [Fact]
    public void TryResolve_Description_MapsToDescription()
    {
        Assert.True(GlFieldWhitelist.TryResolve("description", out var column));
        Assert.Equal(new GlFieldColumn("description", GlFieldKind.Text), column);
    }

    [Fact]
    public void TryResolve_PhysicalColumnName_ReturnsFalse()
    {
        // 白名單只接受邏輯 id；實體欄名（或任何未知字串）不得直通 SQL。
        Assert.False(GlFieldWhitelist.TryResolve("amount_scaled", out _));
    }

    [Fact]
    public void TryResolve_AllCurrentLogicalKeys_PreservesExactSqlTargetAndKind()
    {
        var expected = new Dictionary<string, GlFieldColumn>(StringComparer.Ordinal)
        {
            ["voucherNumber"] = new("voucher_number", GlFieldKind.Text),
            ["lineItem"] = new("line_item", GlFieldKind.Text),
            ["glDate"] = new("gl_date", GlFieldKind.Date),
            ["approvalDate"] = new("approval_date", GlFieldKind.Date),
            ["accountNumber"] = new("account_number", GlFieldKind.Text),
            ["accountName"] = new("account_name", GlFieldKind.Text),
            ["description"] = new("description", GlFieldKind.Text),
            ["sourceModule"] = new("source_module", GlFieldKind.Text),
            ["createdBy"] = new("created_by", GlFieldKind.Text),
            ["approvedBy"] = new("approved_by", GlFieldKind.Text),
            ["amount"] = new("amount_scaled", GlFieldKind.Amount)
        };

        var resolved = new Dictionary<string, GlFieldColumn>(StringComparer.Ordinal);
        foreach (var key in GlMappingKeys.All)
        {
            if (GlFieldWhitelist.TryResolve(key, out var column))
            {
                resolved[key] = column;
            }
        }

        Assert.Equal(expected, resolved);
        Assert.False(GlFieldWhitelist.TryResolve("isManual", out _));
        Assert.False(GlFieldWhitelist.TryResolve("debitAmount", out _));
        Assert.False(GlFieldWhitelist.TryResolve("creditAmount", out _));
        Assert.False(GlFieldWhitelist.TryResolve("drCr", out _));
        Assert.False(GlFieldWhitelist.TryResolve("drCrDebitCode", out _));
        Assert.False(GlFieldWhitelist.TryResolve("postingStatus", out _));
    }
}
