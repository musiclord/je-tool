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
        Assert.Equal("Synthetic private case entity", manifest.Project.EntityName);
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
        document["gl"]!["mapping"]!.AsObject().Remove(GlMappingKeys.DocNum);
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
            WriteStub("reports/workpaper.xlsx");
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
                ["entityName"] = "Synthetic private case entity",
                ["operatorId"] = "synthetic-private-operator",
                ["periodStart"] = "2025-01-01",
                ["periodEnd"] = "2025-12-31",
                ["lastPeriodStart"] = "2025-12-31",
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
                    (GlMappingKeys.DocNum, "private-header-doc"),
                    (GlMappingKeys.PostDate, "private-header-date"),
                    (GlMappingKeys.AccNum, "private-header-account"),
                    (GlMappingKeys.AccName, "private-header-name"),
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
                    (TbMappingKeys.AccNum, "private-header-account"),
                    (TbMappingKeys.AccName, "private-header-name"),
                    (TbMappingKeys.Amount, "private-header-change")),
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
                    ["workingPaper"] = "reports/workpaper.xlsx",
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
