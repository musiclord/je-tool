using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

/// <summary>
/// 中央三層表名登錄(<see cref="JetSchemaCatalog"/>)的結構不變量與逐表對照。
/// 純 AuditCore 單元測試(無 I/O):鎖「身分」(實體名 / 正準名唯一)+「值」(逐表 canonical / layer /
/// audience),並守住「每筆都有層 + 曝光」「DataView/StructureOnly 必有非空正準名」等不變量。
/// 實體表是否「漏登錄」由 Infrastructure 的漂移守門測試對真 SQLite 把關(本檔不碰資料庫)。
/// </summary>
public sealed class JetSchemaCatalogTests
{
    /// <summary>實體表名是查表的鍵,必須唯一(序數)。</summary>
    [Fact]
    public void All_PhysicalNames_AreUnique()
    {
        var names = JetSchemaCatalog.All.Select(e => e.PhysicalName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>正準審計名是對外身分,必須唯一(序數)——兩張實體表不得映到同一審計名。</summary>
    [Fact]
    public void All_CanonicalNames_AreUnique()
    {
        var names = JetSchemaCatalog.All.Select(e => e.CanonicalName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>每筆都必須是已定義的 layer + audience 列舉值(防 default(0) 漏填)。</summary>
    [Fact]
    public void All_Entries_HaveDefinedLayerAndAudience()
    {
        Assert.All(JetSchemaCatalog.All, e =>
        {
            Assert.True(Enum.IsDefined(e.Layer), $"{e.PhysicalName} 的 Layer 非法。");
            Assert.True(Enum.IsDefined(e.Audience), $"{e.PhysicalName} 的 Audience 非法。");
        });
    }

    /// <summary>每筆都必須有非空白的實體名、正準名、說明(目錄不得有半填條目)。</summary>
    [Fact]
    public void All_Entries_HaveNonEmptyNamesAndDescription()
    {
        Assert.All(JetSchemaCatalog.All, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.PhysicalName));
            Assert.False(string.IsNullOrWhiteSpace(e.CanonicalName), $"{e.PhysicalName} 的正準名為空。");
            Assert.False(string.IsNullOrWhiteSpace(e.Description), $"{e.PhysicalName} 的說明為空。");
        });
    }

    /// <summary>
    /// 曝光於資料預覽 / 結構總覽者(DataView / StructureOnly)必有非空正準名——
    /// 下一個任務的預覽要拿這個名字顯示,不得是空字串。
    /// </summary>
    [Fact]
    public void ExposedEntries_HaveNonEmptyCanonicalName()
    {
        var exposed = JetSchemaCatalog.All
            .Where(e => e.Audience is SchemaAudience.DataView or SchemaAudience.StructureOnly);

        Assert.All(exposed, e =>
            Assert.False(string.IsNullOrWhiteSpace(e.CanonicalName),
                $"曝光表「{e.PhysicalName}」缺正準名。"));
    }

    /// <summary>恰好 7 張 DataView 表(任務鎖定;多 / 少一張即紅,逼明確決策)。</summary>
    [Fact]
    public void DataView_HasExactlySevenTables()
    {
        Assert.Equal(7, JetSchemaCatalog.ByAudience(SchemaAudience.DataView).Count());
    }

    /// <summary>DataView 集合的實體名鎖定(等價於下一任務的可瀏覽白名單)。</summary>
    [Fact]
    public void DataView_PhysicalNames_AreLocked()
    {
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "staging_gl_raw_row",
            "staging_tb_raw_row",
            "target_gl_entry",
            "target_tb_balance",
            "target_account_mapping",
            "target_authorized_preparer",
            "staging_calendar_raw_day"
        };

        var actual = JetSchemaCatalog
            .ByAudience(SchemaAudience.DataView)
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// 逐表決策對照(physical → canonical / layer / audience)。值＋身分同鎖:
    /// 改正準名、改層、改曝光任一即紅。涵蓋全部 24 張登錄表。
    /// </summary>
    [Theory]
    // Source / DataView
    [InlineData("staging_gl_raw_row", "JE_PBC", SchemaLayer.Source, SchemaAudience.DataView)]
    [InlineData("staging_tb_raw_row", "TB_PBC", SchemaLayer.Source, SchemaAudience.DataView)]
    [InlineData("target_account_mapping", "ACCOUNT_MAPPING", SchemaLayer.Source, SchemaAudience.DataView)]
    [InlineData("target_authorized_preparer", "AUTHORIZED_PREPARER", SchemaLayer.Source, SchemaAudience.DataView)]
    [InlineData("staging_calendar_raw_day", "DATE_DIMENSION", SchemaLayer.Source, SchemaAudience.DataView)]
    // Staging / DataView
    [InlineData("target_gl_entry", "JE", SchemaLayer.Staging, SchemaAudience.DataView)]
    [InlineData("target_tb_balance", "TB", SchemaLayer.Staging, SchemaAudience.DataView)]
    // Target / StructureOnly
    [InlineData("result_rule_run", "VALIDATION_OVERVIEW", SchemaLayer.Target, SchemaAudience.StructureOnly)]
    [InlineData("result_filter_run", "FILTER_HITS", SchemaLayer.Target, SchemaAudience.StructureOnly)]
    [InlineData("result_inf_sampling_test_sample", "INF_SAMPLE", SchemaLayer.Target, SchemaAudience.StructureOnly)]
    // System / StructureOnly
    [InlineData("config_field_mapping", "FIELD_MAPPING_INFO", SchemaLayer.System, SchemaAudience.StructureOnly)]
    [InlineData("config_field_mapping_previous", "FIELD_MAPPING_PREVIOUS", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("config_account_taxonomy", "ACCOUNT_TAXONOMY", SchemaLayer.System, SchemaAudience.StructureOnly)]
    [InlineData("config_account_taxonomy_path", "ACCOUNT_TAXONOMY_PATH", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("config_filter_scenario", "FILTER_CRITERIA", SchemaLayer.System, SchemaAudience.StructureOnly)]
    [InlineData("import_batch", "IMPORT_BATCH", SchemaLayer.System, SchemaAudience.StructureOnly)]
    [InlineData("import_batch_source", "IMPORT_BATCH_SOURCE", SchemaLayer.System, SchemaAudience.StructureOnly)]
    // System / Hidden
    [InlineData("import_field_definition", "IMPORT_FIELD_DEFINITION", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("config_gl_rde_field", "GL_RDE_FIELD", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("gl_control_total", "GL_CONTROL_TOTAL", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("config_result_stale_state", "RESULT_STALE_STATE", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("app_message_log", "APP_MESSAGE_LOG", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("audit_event_log", "PROJECT_AUDIT_LOG", SchemaLayer.System, SchemaAudience.Hidden)]
    [InlineData("schema_info", "SCHEMA_INFO", SchemaLayer.System, SchemaAudience.Hidden)]
    // Staging / Hidden (ETL scratch)
    [InlineData("staging_account_mapping_raw_row", "ACCOUNT_MAPPING_PBC", SchemaLayer.Staging, SchemaAudience.Hidden)]
    [InlineData("staging_authorized_preparer_raw_row", "AUTHORIZED_PREPARER_PBC", SchemaLayer.Staging, SchemaAudience.Hidden)]
    [InlineData("target_gl_rde_value", "GL_RDE_VALUE", SchemaLayer.Staging, SchemaAudience.Hidden)]
    public void Entry_Physical_MapsTo_CanonicalLayerAudience(
        string physical, string expectedCanonical, object expectedLayer, object expectedAudience)
    {
        Assert.True(JetSchemaCatalog.TryGet(physical, out var entry),
            $"登錄表缺少實體表「{physical}」。");
        Assert.Equal(expectedCanonical, entry.CanonicalName);
        Assert.Equal(Assert.IsType<SchemaLayer>(expectedLayer), entry.Layer);
        Assert.Equal(Assert.IsType<SchemaAudience>(expectedAudience), entry.Audience);
    }

    /// <summary>決策表的列數必須等於目錄總數(逐表對照不得漏列任何登錄條目)。</summary>
    [Fact]
    public void DecisionTable_Covers_EveryEntry()
    {
        // 上面 [Theory] 列舉的 physical 名集合,必須與 All 完全相同(雙向涵蓋)。
        // 2026-10-03 O3 新增 config_field_mapping_previous(第一次失敗:收據 20261003-065529947-9b08f413915748fe812bdead0a6d6d89)。
        var decisionTablePhysicals = new HashSet<string>(StringComparer.Ordinal)
        {
            "staging_gl_raw_row", "staging_tb_raw_row", "target_account_mapping",
            "target_authorized_preparer", "staging_calendar_raw_day", "target_gl_entry",
            "target_tb_balance", "result_rule_run", "result_filter_run",
            "result_inf_sampling_test_sample", "config_field_mapping", "config_field_mapping_previous",
            "config_account_taxonomy", "config_account_taxonomy_path",
            "config_filter_scenario", "config_gl_rde_field", "config_result_stale_state",
            "import_batch", "import_batch_source", "import_field_definition", "gl_control_total", "app_message_log",
            "audit_event_log",
            "schema_info", "staging_account_mapping_raw_row", "staging_authorized_preparer_raw_row",
            "target_gl_rde_value"
        };

        var catalogPhysicals = JetSchemaCatalog.All
            .Select(e => e.PhysicalName)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(catalogPhysicals, decisionTablePhysicals);
    }

    /// <summary>ResolveCanonical:已登錄回正準名;未登錄(臆造名)回 null(不臆造 fallback)。</summary>
    [Fact]
    public void ResolveCanonical_KnownAndUnknown()
    {
        Assert.Equal("JE", JetSchemaCatalog.ResolveCanonical("target_gl_entry"));
        Assert.Null(JetSchemaCatalog.ResolveCanonical("no_such_table"));
    }

    /// <summary>正準名不得殘留 V/R/A 流水代號字樣(審計詞彙,非工程代號)。</summary>
    [Fact]
    public void All_CanonicalNames_AreCodeFree()
    {
        Assert.All(JetSchemaCatalog.All, e =>
            Assert.DoesNotMatch("[VRA][0-9]", e.CanonicalName));
    }
}
