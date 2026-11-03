using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class PrivateCaseManifestTests
{
    [Fact]
    public void Load_CompleteLocalManifest_BindsExplicitFilesAndRedactsPrivateValues()
    {
        using var root = new TemporaryManifestRoot();
        root.WriteCompleteManifest();

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("local-case-01", manifest.CaseAlias);
        Assert.Equal(1, manifest.GlSourceCount);
        Assert.Equal(1, manifest.TbSourceCount);
        Assert.Equal(1, manifest.ScenarioCount);
        Assert.Equal(6, manifest.LegacyReportCount);
        Assert.True(manifest.UsesGeneratedLineItem);
        Assert.Equal(
            PrivateCaseAuthorizedPreparerMode.NotProvided,
            manifest.AuthorizedPreparerMode);
        Assert.Equal(GlAmountMode.DualAmount, manifest.Gl.AmountMode);
        Assert.Equal(TbChangeMode.DirectChange, manifest.Tb.ChangeMode);
        Assert.Equal("SYNTHETIC-PRIVATE-CASE", manifest.Project.ProjectCode);
        Assert.Equal("Synthetic private case entity", manifest.Project.ClientName);
        Assert.Equal("synthetic-private-operator", manifest.Project.OperatorId);

        var serialized = JsonSerializer.Serialize(manifest);
        Assert.DoesNotContain("private-header", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sheet", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-filter-value", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("JE.xlsx", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE-CASE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic private case entity", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-private-operator", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private-header", manifest.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Load_OldSameVoucherGroup_IsReadInTheCurrentShape()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["scenarios"] = new JsonArray
        {
            new JsonObject
            {
                ["name"] = "synthetic",
                ["groups"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["join"] = "AND",
                        ["matchScope"] = "sameVoucher",
                        ["rules"] = new JsonArray
                        {
                            new JsonObject { ["join"] = "AND", ["type"] = "drCrOnly", ["drCr"] = "credit" },
                            new JsonObject { ["join"] = "AND", ["type"] = "prescreen", ["prescreenKey"] = PrescreenRuleKeys.BlankDescription },
                        },
                    },
                },
            },
        };
        root.WriteManifest(document);

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        var expected = JsonDocument.Parse(
            $$"""
            [{"join":"AND","matchScope":"row","conditions":[
              {"join":"AND","type":"drCrOnly","drCr":"credit"},
              {"join":"AND","type":"voucher","quantifier":"any","conditions":[{"join":"AND","type":"prescreen","prescreenKey":"{{PrescreenRuleKeys.BlankDescription}}"}]}]}]
            """).RootElement;
        Assert.True(JsonElement.DeepEquals(expected, manifest.Scenarios[0].GetProperty("groups")));
        Assert.Equal("synthetic", manifest.Scenarios[0].GetProperty("name").GetString());
    }

    // 情境裡的條件陣列 2026-10-30 從 rules 改名為 conditions；私人案件清單仍寫 rules，
    // 讀進來時條件組與組合條件（group、voucher）裡的子條件都要換成 conditions，條件內容不變。
    [Fact]
    public void Load_ManifestWritingRules_IsReadAsConditions()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["scenarios"] = JsonNode.Parse(
            $$"""
            [{"name":"synthetic","groups":[
              {"join":"AND","matchScope":"row","rules":[
                {"join":"AND","type":"drCrOnly","drCr":"debit"},
                {"join":"OR","type":"group","rules":[
                  {"join":"AND","type":"prescreen","prescreenKey":"{{PrescreenRuleKeys.BlankDescription}}"},
                  {"join":"AND","type":"voucher","quantifier":"none","rules":[{"join":"AND","type":"drCrOnly","drCr":"credit"}]}]}]},
              {"join":"OR","rules":[{"join":"AND","type":"manualAuto","isManual":true}]}]}]
            """)!.AsArray();
        root.WriteManifest(document);

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        var expected = JsonDocument.Parse(
            $$"""
            [{"join":"AND","matchScope":"row","conditions":[
                {"join":"AND","type":"drCrOnly","drCr":"debit"},
                {"join":"OR","type":"group","conditions":[
                  {"join":"AND","type":"prescreen","prescreenKey":"{{PrescreenRuleKeys.BlankDescription}}"},
                  {"join":"AND","type":"voucher","quantifier":"none","conditions":[{"join":"AND","type":"drCrOnly","drCr":"credit"}]}]}]},
              {"join":"OR","conditions":[{"join":"AND","type":"manualAuto","isManual":true}]}]
            """).RootElement;
        Assert.True(JsonElement.DeepEquals(expected, manifest.Scenarios[0].GetProperty("groups")));
        Assert.DoesNotContain("\"rules\"", manifest.Scenarios[0].GetRawText(), StringComparison.Ordinal);
    }

    // 專案紀錄收斂計畫第四階段改名時，案件設定的兩個欄名、GL 與 TB 的欄位代號和一個預篩選代號都換了名稱。
    // 私人案件清單在儲存庫外，照裁定 2026-10-16-14 清單檔本身不改，仍是改名前的寫法；讀進來時要換成現在的名稱。
    // 舊名稱逐字照改名前的 JetFieldCatalog 與 PrescreenRuleKeys 寫，新舊對應見名詞表「不再使用的名稱」。
    // 本機私人案件檢查因此在讀清單時就失敗：PrivateCase 收據 20261102-232931191-d62be73f0156411685b9ed22d7f8a70d（SQLite）。
    [Fact]
    public void Load_ManifestWritingPreRenameNames_IsReadWithCurrentNames()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        var project = document["project"]!.AsObject();
        project.Remove("clientName");
        project.Remove("prepStartDate");
        project["entityName"] = "Synthetic private case entity";
        project["lastPeriodStart"] = "2025-12-20";
        document["gl"]!["mapping"] = JsonNode.Parse(
            """
            {"docNum":"private-header-doc","lineID":"private-header-line","postDate":"private-header-date",
             "docDate":"private-header-approval","accNum":"private-header-account","accName":"private-header-name",
             "description":"private-header-description","jeSource":"private-header-source","createBy":"private-header-preparer",
             "approveBy":"private-header-approver","manual":"private-header-manual",
             "debitAmount":"private-header-debit","creditAmount":"private-header-credit"}
            """);
        document["tb"]!["mapping"] = JsonNode.Parse(
            """{"accNum":"private-header-account","accName":"private-header-name","amount":"private-header-change"}""");
        document["scenarios"] = JsonNode.Parse(
            """
            [{"name":"synthetic","groups":[
              {"join":"AND","matchScope":"row","rules":[
                {"join":"AND","type":"fieldValue","field":"createBy","operator":"in","values":["private-filter-value"],"includeBlank":false},
                {"join":"AND","type":"prescreen","prescreenKey":"postPeriodApproval"},
                {"join":"AND","type":"entityFrequency","field":"accNum","countUnit":"entries","countOperator":"lte","countFrom":"10"},
                {"join":"AND","type":"voucher","quantifier":"any","rules":[
                  {"join":"AND","type":"fieldValue","field":"postDate","operator":"isNonBusinessDay","includeBlank":false}]}]}]}]
            """)!.AsArray();
        root.WriteManifest(document);

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        Assert.Equal("Synthetic private case entity", manifest.Project.ClientName);
        Assert.Equal(new DateOnly(2025, 12, 20), manifest.Project.PrepStartDate);
        Assert.Equal(
            new[]
            {
                GlMappingKeys.AccountName, GlMappingKeys.AccountNumber, GlMappingKeys.ApprovalDate, GlMappingKeys.ApprovedBy,
                GlMappingKeys.CreatedBy, GlMappingKeys.CreditAmount, GlMappingKeys.DebitAmount, GlMappingKeys.Description,
                GlMappingKeys.GlDate, GlMappingKeys.IsManual, GlMappingKeys.LineItem, GlMappingKeys.SourceModule,
                GlMappingKeys.VoucherNumber,
            },
            manifest.Gl.Mapping.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("private-header-doc", manifest.Gl.Mapping[GlMappingKeys.VoucherNumber]);
        Assert.False(manifest.UsesGeneratedLineItem);
        Assert.Equal(
            new[] { TbMappingKeys.AccountName, TbMappingKeys.AccountNumber, TbMappingKeys.ChangeAmount },
            manifest.Tb.Mapping.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("private-header-change", manifest.Tb.Mapping[TbMappingKeys.ChangeAmount]);

        var expected = JsonDocument.Parse(
            $$"""
            [{"join":"AND","matchScope":"row","conditions":[
                {"join":"AND","type":"fieldValue","field":"{{GlMappingKeys.CreatedBy}}","operator":"in","values":["private-filter-value"],"includeBlank":false},
                {"join":"AND","type":"prescreen","prescreenKey":"{{PrescreenRuleKeys.PrepPeriodApproval}}"},
                {"join":"AND","type":"entityFrequency","field":"{{GlMappingKeys.AccountNumber}}","countUnit":"entries","countOperator":"lte","countFrom":"10"},
                {"join":"AND","type":"voucher","quantifier":"any","conditions":[
                  {"join":"AND","type":"fieldValue","field":"{{GlMappingKeys.GlDate}}","operator":"isNonBusinessDay","includeBlank":false}]}]}]
            """).RootElement;
        Assert.True(JsonElement.DeepEquals(expected, manifest.Scenarios[0].GetProperty("groups")));
    }

    [Fact]
    public void Load_UnknownManifestProperty_FailsClosed()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["unexpected"] = true;
        root.WriteManifest(document);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.InvalidJson, exception.Failure);
    }

    [Fact]
    public void Load_ExportedVoucherRowBasis_BindsExplicitly()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["legacy"]!["scenarioCounts"]![0]!["rowCountBasis"] =
            "exportedVoucherRows";
        root.WriteManifest(document);

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        Assert.Equal(
            PrivateCaseScenarioRowCountBasis.ExportedVoucherRows,
            Assert.Single(manifest.Legacy.ScenarioCounts).RowCountBasis);
    }

    [Fact]
    public void Load_UnknownScenarioRowCountBasis_FailsClosed()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["legacy"]!["scenarioCounts"]![0]!["rowCountBasis"] = "unknown";
        root.WriteManifest(document);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.InvalidLegacyEvidence, exception.Failure);
    }

    [Fact]
    public void Load_SupportedContentDecision_BindsExplicitTarget()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["legacy"]!["contentDecisions"] = new JsonArray
        {
            new JsonObject
            {
                ["scope"] = "validation-v5",
                ["dimension"] = "rowValues",
                ["decision"] = PrivateCaseReportDifferencePolicy.ApprovedCompletenessTotals,
            },
        };
        root.WriteManifest(document);

        var manifest = PrivateCaseManifestLoader.Load(root.Path, "private-case.json");

        var decision = Assert.Single(manifest.Legacy.ContentDecisions);
        Assert.Equal("validation-v5", decision.Scope);
        Assert.Equal(LegacyAuditParityContentDimension.RowValues, decision.Dimension);
        Assert.Equal(
            PrivateCaseReportDifferencePolicy.ApprovedCompletenessTotals,
            decision.DecisionId);
    }

    [Fact]
    public void Load_UnsupportedOrDuplicateContentDecision_FailsClosed()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        var decision = new JsonObject
        {
            ["scope"] = "validation-v5",
            ["dimension"] = "rowValues",
            ["decision"] = "unreviewed-decision",
        };
        document["legacy"]!["contentDecisions"] = new JsonArray(decision);
        root.WriteManifest(document);

        var unsupported = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));
        Assert.Equal(PrivateCaseManifestFailure.InvalidLegacyEvidence, unsupported.Failure);

        decision["decision"] = PrivateCaseReportDifferencePolicy.ApprovedCompletenessTotals;
        document["legacy"]!["contentDecisions"] = new JsonArray(
            decision.DeepClone(),
            decision.DeepClone());
        root.WriteManifest(document);

        var duplicate = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));
        Assert.Equal(PrivateCaseManifestFailure.InvalidLegacyEvidence, duplicate.Failure);
    }

    [Fact]
    public void Load_MissingRequiredMapping_FailsBeforeExecution()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["gl"]!["mapping"]!.AsObject().Remove(GlMappingKeys.VoucherNumber);
        root.WriteManifest(document);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.InvalidMapping, exception.Failure);
    }

    [Fact]
    public void Load_ImportLogBindingMismatch_FailsWithoutEchoingPrivateFields()
    {
        using var root = new TemporaryManifestRoot();
        root.WriteCompleteManifest();
        root.WriteImportLog("logs/gl.txt", "JE.xlsx", "different-sheet");

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.ImportLogMismatch, exception.Failure);
        Assert.DoesNotContain("different-sheet", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(root.Path, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_DuplicateFileRole_FailsClosed()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["referenceData"]!["accountMappingRelativePath"] =
            "reports/account-mapping.xlsx";
        root.WriteManifest(document);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.DuplicateFile, exception.Failure);
    }

    [Fact]
    public void Load_ManifestOverBoundedSize_FailsClosed()
    {
        using var root = new TemporaryManifestRoot();
        File.WriteAllText(
            System.IO.Path.Combine(root.Path, "private-case.json"),
            new string('x', PrivateCaseManifestLoader.MaximumManifestBytes + 1),
            Encoding.UTF8);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.ManifestTooLarge, exception.Failure);
    }

    [Fact]
    public void Load_AuthorizedPreparerFileMode_RequiresAnExplicitUniqueFile()
    {
        using var root = new TemporaryManifestRoot();
        var document = root.CompleteManifestDocument();
        document["referenceData"]!["authorizedPreparer"] = new JsonObject
        {
            ["mode"] = "file",
            ["relativePath"] = null,
        };
        root.WriteManifest(document);

        var exception = Assert.Throws<PrivateCaseManifestException>(() =>
            PrivateCaseManifestLoader.Load(root.Path, "private-case.json"));

        Assert.Equal(PrivateCaseManifestFailure.InvalidReferenceData, exception.Failure);
    }

    private sealed class TemporaryManifestRoot : IDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions =
            new(JsonSerializerDefaults.Web) { WriteIndented = true };

        internal TemporaryManifestRoot()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"jet-private-manifest-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);

            WriteStub("JE.xlsx");
            WriteStub("TB.xlsx");
            WriteStub("inputs/account-mapping.xlsx");
            WriteStub("reports/validation.xlsx");
            WriteStub("reports/account-mapping.xlsx");
            WriteStub("reports/inf.xlsx");
            WriteStub("reports/prescreen.xlsx");
            WriteStub("reports/criteria.xlsx");
            WriteStub("reports/workingPaper.xlsx");
            WriteImportLog("logs/gl.txt", "JE.xlsx", "private-sheet-gl");
            WriteImportLog("logs/tb.txt", "TB.xlsx", "private-sheet-tb");
        }

        internal string Path { get; }

        internal void WriteCompleteManifest() => WriteManifest(CompleteManifestDocument());

        internal JsonObject CompleteManifestDocument() => new()
        {
            ["schemaVersion"] = 1,
            ["caseAlias"] = "local-case-01",
            ["project"] = new JsonObject
            {
                ["projectCode"] = "SYNTHETIC-PRIVATE-CASE",
                ["clientName"] = "Synthetic private case entity",
                ["operatorId"] = "synthetic-private-operator",
                ["periodStart"] = "2025-01-01",
                ["periodEnd"] = "2025-12-31",
                ["prepStartDate"] = "2025-12-31",
                ["sampleSeed"] = 1_600_001,
            },
            ["gl"] = new JsonObject
            {
                ["sources"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["relativePath"] = "JE.xlsx",
                        ["worksheetName"] = "private-sheet-gl",
                        ["firstRowIsFieldNames"] = true,
                    },
                },
                ["importMode"] = "replace",
                ["mapping"] = Mapping(
                    (GlMappingKeys.VoucherNumber, "private-header-doc"),
                    (GlMappingKeys.GlDate, "private-header-date"),
                    (GlMappingKeys.AccountNumber, "private-header-account"),
                    (GlMappingKeys.AccountName, "private-header-name"),
                    (GlMappingKeys.Description, "private-header-description"),
                    (GlMappingKeys.DebitAmount, "private-header-debit"),
                    (GlMappingKeys.CreditAmount, "private-header-credit")),
                ["amountMode"] = GlAmountModeNames.Dual,
            },
            ["tb"] = new JsonObject
            {
                ["sources"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["relativePath"] = "TB.xlsx",
                        ["worksheetName"] = "private-sheet-tb",
                        ["firstRowIsFieldNames"] = true,
                    },
                },
                ["importMode"] = "replace",
                ["mapping"] = Mapping(
                    (TbMappingKeys.AccountNumber, "private-header-account"),
                    (TbMappingKeys.AccountName, "private-header-name"),
                    (TbMappingKeys.ChangeAmount, "private-header-change")),
                ["changeMode"] = TbChangeModeNames.Direct,
            },
            ["referenceData"] = new JsonObject
            {
                ["accountMappingRelativePath"] = "inputs/account-mapping.xlsx",
                ["authorizedPreparer"] = new JsonObject
                {
                    ["mode"] = "notProvided",
                    ["relativePath"] = null,
                },
                ["holidayDates"] = new JsonArray("2025-01-01"),
                ["makeupDates"] = new JsonArray("2025-02-08"),
            },
            ["scenarios"] = new JsonArray
            {
                new JsonObject
                {
                    ["position"] = 1,
                    ["value"] = "private-filter-value",
                },
            },
            ["legacy"] = new JsonObject
            {
                ["importLogs"] = new JsonObject
                {
                    ["gl"] = "logs/gl.txt",
                    ["tb"] = "logs/tb.txt",
                },
                ["reports"] = new JsonObject
                {
                    ["validationReport"] = "reports/validation.xlsx",
                    ["accountMapping"] = "reports/account-mapping.xlsx",
                    ["infReport"] = "reports/inf.xlsx",
                    ["prescreenReport"] = "reports/prescreen.xlsx",
                    ["criteriaSelectionReport"] = "reports/criteria.xlsx",
                    ["workingPaper"] = "reports/workingPaper.xlsx",
                },
                ["scenarioCounts"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["position"] = 1,
                        ["voucherCount"] = 1,
                        ["rowCount"] = 2,
                    },
                },
            },
        };

        internal void WriteManifest(JsonObject document) => File.WriteAllText(
            System.IO.Path.Combine(Path, "private-case.json"),
            document.ToJsonString(JsonOptions),
            Encoding.UTF8);

        internal void WriteImportLog(
            string relativePath,
            string sourceFile,
            string worksheet)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var encoding = Encoding.GetEncoding(
                950,
                EncoderFallback.ExceptionFallback,
                DecoderFallback.ExceptionFallback);
            var content = string.Join(
                "\r\n",
                "task.begin=import_excel",
                $"source.file={sourceFile}",
                $"source.sheet={worksheet}",
                "source.first_row_is_field_names=true",
                "field.count=1",
                "field.1.name=field",
                "field.1.type=character",
                "task.end=success");
            Write(relativePath, encoding.GetBytes(content));
        }

        public void Dispose()
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            var root = System.IO.Path.TrimEndingDirectorySeparator(
                System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
            var candidate = System.IO.Path.GetFullPath(Path);
            var prefix = root + System.IO.Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(candidate).StartsWith(
                    "jet-private-manifest-tests-",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Synthetic manifest cleanup was refused.");
            }

            Directory.Delete(candidate, recursive: true);
        }

        private static JsonObject Mapping(params (string Key, string Value)[] pairs)
        {
            var mapping = new JsonObject();
            foreach (var (key, value) in pairs)
            {
                mapping[key] = value;
            }
            return mapping;
        }

        private void WriteStub(string relativePath) =>
            Write(relativePath, "synthetic"u8.ToArray());

        private void Write(string relativePath, byte[] content)
        {
            var path = System.IO.Path.Combine(Path, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }
    }
}
