using JET.Domain;
using Xunit;

namespace JET.Tests.Domain;

public sealed class GlFieldWhitelistTests
{
    [Fact]
    public void TryResolve_DocDate_MapsToApprovalDateColumn()
    {
        Assert.True(GlFieldWhitelist.TryResolve("docDate", out var column));
        Assert.Equal(new GlFieldColumn("approval_date", GlFieldKind.Date), column);
    }

    [Fact]
    public void TryResolve_VoucherDate_MapsToVoucherDateColumn()
    {
        Assert.True(GlFieldWhitelist.TryResolve("voucherDate", out var column));
        Assert.Equal(new GlFieldColumn("voucher_date", GlFieldKind.Date), column);
    }

    [Fact]
    public void TryResolve_Amount_IsAmountKind()
    {
        Assert.True(GlFieldWhitelist.TryResolve("amount", out var column));
        Assert.Equal(GlFieldKind.Amount, column.Kind);
    }

    [Fact]
    public void TryResolve_Description_MapsToDocumentDescription()
    {
        Assert.True(GlFieldWhitelist.TryResolve("description", out var column));
        Assert.Equal(new GlFieldColumn("document_description", GlFieldKind.Text), column);
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
            ["docNum"] = new("document_number", GlFieldKind.Text),
            ["lineID"] = new("line_item", GlFieldKind.Text),
            ["postDate"] = new("post_date", GlFieldKind.Date),
            ["docDate"] = new("approval_date", GlFieldKind.Date),
            ["voucherDate"] = new("voucher_date", GlFieldKind.Date),
            ["accNum"] = new("account_code", GlFieldKind.Text),
            ["accName"] = new("account_name", GlFieldKind.Text),
            ["description"] = new("document_description", GlFieldKind.Text),
            ["jeSource"] = new("source_module", GlFieldKind.Text),
            ["createBy"] = new("created_by", GlFieldKind.Text),
            ["approveBy"] = new("approved_by", GlFieldKind.Text),
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
        Assert.False(GlFieldWhitelist.TryResolve("manual", out _));
        Assert.False(GlFieldWhitelist.TryResolve("debitAmount", out _));
        Assert.False(GlFieldWhitelist.TryResolve("creditAmount", out _));
        Assert.False(GlFieldWhitelist.TryResolve("dcField", out _));
        Assert.False(GlFieldWhitelist.TryResolve("dcDebitCode", out _));
        Assert.False(GlFieldWhitelist.TryResolve("postingStatus", out _));
    }
}
