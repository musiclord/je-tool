using System.Text.Json;
using ClosedXML.Excel;
using JET.Domain;
using Xunit;

namespace JET.Tests.Infrastructure;

public sealed class LegacyAuditParityProfileTests
{
    [Fact]
    public async Task BuildAsync_SyntheticReports_ProducesAliasOnlyExecutableProfile()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory);

        var fixture = new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory);
        var outputPath = Path.Combine(workspace.Path, "profile.json");

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            fixture,
            outputPath,
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("case-A", profile.ToString());
        Assert.Equal(new DateOnly(2031, 1, 1), profile.PeriodStart);
        Assert.Equal(new DateOnly(2031, 12, 31), profile.PeriodEnd);
        Assert.Equal(new DateOnly(2031, 12, 1), profile.LastPeriodStart);
        Assert.Equal(2, profile.GlSources.Count);
        Assert.Single(profile.TbSources);
        Assert.All(profile.GlSources, source => Assert.True(source.DataRowCount > 0));
        Assert.Equal(2, profile.GlSources.Sum(source => source.DataRowCount));
        Assert.Equal(1, profile.TbSources[0].DataRowCount);
        Assert.Equal(GlAmountMode.SignedAmount, profile.GlAmountMode);
        Assert.Equal(TbChangeMode.DirectChange, profile.TbChangeMode);
        Assert.Equal("gl-doc", profile.GlMapping[GlMappingKeys.DocNum]);
        Assert.Equal("tb-change", profile.TbMapping[TbMappingKeys.Amount]);
        AssertMappingsEqual(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GlMappingKeys.DocNum] = "gl-doc",
                [GlMappingKeys.PostDate] = "gl-post",
                [GlMappingKeys.AccNum] = "gl-account",
                [GlMappingKeys.AccName] = "gl-account-name",
                [GlMappingKeys.Description] = "gl-description",
                [GlMappingKeys.Amount] = "gl-amount",
            },
            profile.GlMapping,
            "Synthetic GL mapping values changed from the stage-3 fixture baseline.");
        AssertMappingsEqual(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [TbMappingKeys.AccNum] = "tb-code",
                [TbMappingKeys.AccName] = "tb-name",
                [TbMappingKeys.Amount] = "tb-change",
            },
            profile.TbMapping,
            "Synthetic TB mapping values changed from the stage-3 fixture baseline.");
        AssertResolutionSourcesMatchMappings(
            profile.GlMapping,
            profile.GlMappingResolutionSources);
        AssertResolutionSourcesMatchMappings(
            profile.TbMapping,
            profile.TbMappingResolutionSources);
        Assert.All(
            profile.GlMappingResolutionSources.Values,
            source => Assert.Equal(
                LegacyMappingResolutionSource.WorkingPaperExplicitMapping,
                source));
        Assert.All(
            profile.TbMappingResolutionSources.Values,
            source => Assert.Equal(
                LegacyMappingResolutionSource.WorkingPaperExplicitMapping,
                source));
        Assert.True(profile.FieldInfoRowCount > 0);
        Assert.Single(profile.HolidayDates);
        Assert.Single(profile.MakeupDates);
        Assert.True(profile.CalendarSettingRowCount > 0);
        Assert.Equal(1, profile.AccountMapping.RowCount);
        Assert.Equal("自動化工具-科目配對資訊", profile.AccountMapping.SourceSheet);
        Assert.True(File.Exists(profile.AccountMapping.ImportPath));
        Assert.NotEqual(
            profile.LegacyReports[LegacyReportKind.AccountMapping],
            profile.AccountMapping.ImportPath);
        using (var extracted = new XLWorkbook(profile.AccountMapping.ImportPath))
        {
            var sheet = extracted.Worksheet(1);
            Assert.Equal("GL_NUMBER", sheet.Cell(1, 1).GetString());
            Assert.Equal("synthetic-account", sheet.Cell(2, 1).GetString());
            Assert.Equal(AccountMappingCategories.Cash, sheet.Cell(2, 3).GetString());
        }
        Assert.Single(profile.Scenarios);
        Assert.Single(profile.LegacyScenarioCounts);
        Assert.Equal(6, profile.LegacyReports.Count);
        Assert.Empty(profile.PendingFieldIds);
        var decidedNotProvided = Assert.Single(profile.DecidedNotProvidedInputs);
        Assert.Equal(
            "authorized-preparer-allowlist-not-provided-by-decision",
            LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId);
        Assert.Equal(
            LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId,
            decidedNotProvided.FieldId);
        Assert.Equal(
            LegacyAuditParityProfileBuilder.NonAuthorizedPreparerRuleSlug,
            decidedNotProvided.NotApplicableRuleSlug);
        Assert.True(File.Exists(outputPath));
        using var serializedProfile = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
        AssertSerializedResolutionSourcesAreStrings(
            serializedProfile.RootElement.GetProperty(nameof(profile.GlMappingResolutionSources)),
            profile.GlMapping.Count);
        AssertSerializedResolutionSourcesAreStrings(
            serializedProfile.RootElement.GetProperty(nameof(profile.TbMappingResolutionSources)),
            profile.TbMapping.Count);
        Assert.All(
            serializedProfile.RootElement.GetProperty(nameof(profile.GlSources)).EnumerateArray(),
            source => Assert.Equal(
                JsonValueKind.Number,
                source.GetProperty(nameof(LegacyParityTabularSource.DataRowCount)).ValueKind));
        Assert.Equal(
            1,
            serializedProfile.RootElement
                .GetProperty(nameof(profile.TbSources))[0]
                .GetProperty(nameof(LegacyParityTabularSource.DataRowCount))
                .GetInt64());
    }

    [Fact]
    public async Task BuildAsync_DecidedNotProvidedAuthorizedPreparer_MarksNonAuthorizedPreparerNotApplicable()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-decision-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        var decidedNotProvided = Assert.Single(profile.DecidedNotProvidedInputs);
        Assert.Equal(
            LegacyAuditParityProfileBuilder.AuthorizedPreparerDecidedNotProvidedId,
            decidedNotProvided.FieldId);
        Assert.Equal(
            LegacyAuditParityProfileBuilder.NonAuthorizedPreparerRuleSlug,
            decidedNotProvided.NotApplicableRuleSlug);
        Assert.DoesNotContain(decidedNotProvided.FieldId, profile.PendingFieldIds);
        Assert.True(profile.IsRuleNotApplicableByDecision(
            LegacyAuditParityProfileBuilder.NonAuthorizedPreparerRuleSlug));
    }

    [Fact]
    public async Task BuildAsync_CanonicalDisplayFallback_UsesUniqueSemanticHeaderEvidence()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-ordinal-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, canonicalDisplayFallback: true);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("gl-description", profile.GlMapping[GlMappingKeys.Description]);
        Assert.Equal(
            LegacyMappingResolutionSource.BilateralRoleAgreement,
            profile.GlMappingResolutionSources[GlMappingKeys.Description]);
        Assert.Equal(2, profile.GlSources.Count);
    }

    [Fact]
    public async Task BuildAsync_NormalizedHeaderIdentity_PreservesResolvedMappingAndMarksSource()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-normalized-identity-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.NormalizedIdentity);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("gl-account", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.NormalizedHeaderIdentity,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
        AssertResolutionSourcesMatchMappings(
            profile.GlMapping,
            profile.GlMappingResolutionSources);
    }

    [Fact]
    public async Task BuildAsync_ReorderedGlSource_DoesNotUseWorkingPaperOrdinal()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-reordered-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.ReorderedDirect);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("gl-doc", profile.GlMapping[GlMappingKeys.DocNum]);
        Assert.Equal("gl-post", profile.GlMapping[GlMappingKeys.PostDate]);
        Assert.Equal("gl-description", profile.GlMapping[GlMappingKeys.Description]);
        Assert.Equal("gl-amount", profile.GlMapping[GlMappingKeys.Amount]);
    }

    [Fact]
    public async Task BuildAsync_DirectAmountHeaderContainingFormulaText_RemainsDirect()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-direct-formula-name-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.DirectFormulaLikeName);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal(GlAmountMode.SignedAmount, profile.GlAmountMode);
        Assert.Equal(
            "gl-amount: gl-debit-gl-credit",
            profile.GlMapping[GlMappingKeys.Amount]);
        Assert.DoesNotContain(GlMappingKeys.DebitAmount, profile.GlMapping.Keys);
        Assert.DoesNotContain(GlMappingKeys.CreditAmount, profile.GlMapping.Keys);
    }

    [Fact]
    public async Task BuildAsync_AnchoredGeneratedDualAmount_UsesDeclaredOperands()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-dual-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.GeneratedDual);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal(GlAmountMode.DualAmount, profile.GlAmountMode);
        Assert.Equal("gl-debit", profile.GlMapping[GlMappingKeys.DebitAmount]);
        Assert.Equal("gl-credit", profile.GlMapping[GlMappingKeys.CreditAmount]);
        Assert.Equal(
            LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
            profile.GlMappingResolutionSources[GlMappingKeys.DebitAmount]);
        Assert.Equal(
            LegacyMappingResolutionSource.WorkingPaperGeneratedExpression,
            profile.GlMappingResolutionSources[GlMappingKeys.CreditAmount]);
        Assert.DoesNotContain("gl-amount-mode", profile.PendingFieldIds);
    }

    [Fact]
    public async Task BuildAsync_MalformedGeneratedAmount_FailsClosedWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-malformed-amount-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.MalformedGeneratedDual);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-source-compatibility", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-source-compatibility",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ReorderedPartitionNamedHeaders_AreCompatibleIgnoringOrder()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-reordered-partition-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.ReorderedPartition);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal(2, profile.GlSources.Count);
        Assert.Equal("gl-doc", profile.GlMapping[GlMappingKeys.DocNum]);
        Assert.Equal("gl-amount", profile.GlMapping[GlMappingKeys.Amount]);
    }

    [Fact]
    public async Task BuildAsync_PartitionPlaceholderDifference_IsCompatible()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-placeholder-partition-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.PlaceholderColumnPartition);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal(2, profile.GlSources.Count);
        Assert.Equal("gl-account", profile.GlMapping[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_PartitionPopulatedPlaceholderDifference_FailsClosedWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-populated-placeholder-partition-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.PopulatedPlaceholderColumnPartition);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-ambiguous-source-schema", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-ambiguous-source-schema",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_PartitionLazyOutOfRangeColumn_FailsClosedWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-out-of-range-partition-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.OutOfRangeDataColumnPartition);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-ambiguous-source-schema", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-ambiguous-source-schema",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_HeaderOnlyPartition_IsExcludedFromImportSources()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-header-only-partition-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.HeaderOnlyPartition);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Single(profile.GlSources);
        Assert.Equal(1, profile.GlSources[0].DataRowCount);
        Assert.Equal("gl-account", profile.GlMapping[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_PartitionMissingNamedHeader_FailsClosedWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-missing-partition-column-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.MissingNamedColumnPartition);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-ambiguous-source-schema", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-ambiguous-source-schema",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_ExactTargetlessIdentity_OutranksLooseTargetNormalization()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-targetless-reservation-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.TargetlessNormalizedConflict);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-source-compatibility", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-source-compatibility",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_TargetAndHeaderRoleAgreement_OutranksNormalizedTargetlessVariant()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-qualified-target-role-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.QualifiedTargetRoleConflict);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("gl-account", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.BilateralRoleAgreement,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_TargetlessHeaderCannotBeReusedBySemanticRoleFallback()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-targetless-role-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.TargetlessRoleConflict);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-source-compatibility", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-source-compatibility",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_RequiredMappedTarget_UsesSoleRoleAfterExactTargetlessReservations()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-normalized-targetless-role-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.TargetlessNormalizedRoleConflict);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("gl-account", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.BipartiteUniqueSolution,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_CaseBAccountFallback_SelectsUniqueExactZeroFilledReconciliation()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseB,
            $"profile-account-reconciliation-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.CaseBAccountReconciliationAlternateUnique);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseB, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("correct-account-code", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.CrossDatasetAccountTotalReconciliation,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
        Assert.Equal(2, profile.GlSources.Count);
    }

    [Fact]
    public async Task BuildAsync_CaseBAccountFallback_CanRetainInitiallySelectedUniqueReconciliation()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseB,
            $"profile-account-reconciliation-current-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.CaseBAccountReconciliationCurrentUnique);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseB, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("correct-account-code", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.CrossDatasetAccountTotalReconciliation,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_CaseBAccountFallback_NonUniqueReconciliationFailsClosed()
    {
        foreach (var glShape in new[]
                 {
                     SyntheticGlShape.CaseBAccountReconciliationAmbiguous,
                     SyntheticGlShape.CaseBAccountReconciliationNoMatch,
                 })
        {
            using var workspace = LegacyParityWorkspace.CreateTemporary(
                LegacyParityCase.CaseB,
                $"profile-account-reconciliation-fail-{Guid.NewGuid():N}");
            var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
            Directory.CreateDirectory(caseDirectory);
            WriteSyntheticCase(caseDirectory, glShape: glShape);

            var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
                LegacyAuditParityProfileBuilder.BuildAsync(
                    new LegacyParityCaseFixture(LegacyParityCase.CaseB, caseDirectory),
                    Path.Combine(workspace.Path, "profile.json"),
                    workspace.Path,
                    CancellationToken.None));

            Assert.Equal("gl-account-tb-reconciliation", exception.FieldId);
            Assert.Equal(
                "legacy-audit-parity-profile:gl-account-tb-reconciliation",
                exception.Message);
        }
    }

    [Fact]
    public async Task BuildAsync_OverlappingRequiredRoles_WithUniqueInjectiveAssignment_ResolveGlobally()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-unique-role-matching-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.OverlappingRequiredRolesUnique);

        var profile = await LegacyAuditParityProfileBuilder.BuildAsync(
            new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
            Path.Combine(workspace.Path, "profile.json"),
            workspace.Path,
            CancellationToken.None);

        Assert.Equal("document-code", profile.GlMapping[GlMappingKeys.DocNum]);
        Assert.Equal("document-account-code", profile.GlMapping[GlMappingKeys.AccNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.BipartiteUniqueSolution,
            profile.GlMappingResolutionSources[GlMappingKeys.DocNum]);
        Assert.Equal(
            LegacyMappingResolutionSource.BipartiteUniqueSolution,
            profile.GlMappingResolutionSources[GlMappingKeys.AccNum]);
    }

    [Fact]
    public async Task BuildAsync_OverlappingRequiredRoles_WithTwoAssignments_FailClosed()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-ambiguous-role-matching-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.OverlappingRequiredRolesAmbiguous);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-required-document-identifier", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-required-document-identifier",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_DuplicateRequiredTargets_FailClosedBeforeRoleAssignmentSearch()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-duplicate-required-targets-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(
            caseDirectory,
            glShape: SyntheticGlShape.DuplicateRequiredDocumentTargets);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-required-document-identifier", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-required-document-identifier",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_GenericNameHeader_DoesNotGuessAccountName()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-generic-name-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory, glShape: SyntheticGlShape.GenericAccountNameFallback);

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-source-compatibility", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-source-compatibility",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_DistinctLongestSourceSchemas_FailClosedWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-schema-{Guid.NewGuid():N}");
        var caseDirectory = Path.Combine(workspace.Path, "synthetic-case");
        Directory.CreateDirectory(caseDirectory);
        WriteSyntheticCase(caseDirectory);
        WriteGlSource(
            Path.Combine(caseDirectory, "source-ledger-alternate.xlsx"),
            descriptionHeader: "alternate-description");

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                new LegacyParityCaseFixture(LegacyParityCase.CaseA, caseDirectory),
                Path.Combine(workspace.Path, "profile.json"),
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("gl-mapping-ambiguous-source-schema", exception.FieldId);
        Assert.Equal(
            "legacy-audit-parity-profile:gl-mapping-ambiguous-source-schema",
            exception.Message);
    }

    [Fact]
    public async Task BuildAsync_OutputOutsideIgnoredWorkspace_FailsWithFixedFieldId()
    {
        using var workspace = LegacyParityWorkspace.CreateTemporary(
            LegacyParityCase.CaseA,
            $"profile-path-{Guid.NewGuid():N}");
        var fixture = new LegacyParityCaseFixture(LegacyParityCase.CaseA, workspace.Path);
        var outsidePath = Path.Combine(Path.GetTempPath(), $"profile-{Guid.NewGuid():N}.json");

        var exception = await Assert.ThrowsAsync<LegacyAuditParityProfileException>(() =>
            LegacyAuditParityProfileBuilder.BuildAsync(
                fixture,
                outsidePath,
                workspace.Path,
                CancellationToken.None));

        Assert.Equal("profile-output-path", exception.FieldId);
        Assert.Equal("legacy-audit-parity-profile:profile-output-path", exception.Message);
    }

    [Fact]
    public void CriteriaLog_ReversibleClauses_UseOneAndGroup()
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var amount = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAmount).LegacyFieldName!;
        var log = $"#1. 文字欄位【{description}】值為 - alpha,beta "
            + "#2. 僅考量借方傳票 "
            + "#3. 分錄摘要出現特定描述 "
            + $"#4. 數字欄位【{amount}】值介於 10 和 20 ";

        var result = LegacyCriteriaLogParser.TryParse(
            log,
            "legacy-scenario-01",
            "synthetic rationale",
            "scenario-01-criteria-log");

        Assert.Null(result.PendingFieldId);
        var scenario = AssertScenario(result);
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal("AND", group.GetProperty("join").GetString());
        var rules = group.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(4, rules.Length);
        Assert.All(rules, rule => Assert.Equal("AND", rule.GetProperty("join").GetString()));
        Assert.Equal("text", rules[0].GetProperty("type").GetString());
        Assert.Equal("drCrOnly", rules[1].GetProperty("type").GetString());
        Assert.Equal("prescreen", rules[2].GetProperty("type").GetString());
        Assert.Equal("numRange", rules[3].GetProperty("type").GetString());
    }

    [Fact]
    public void CriteriaLog_AccountPair_UsesExactAccountPairRule()
    {
        var log = $"#1. 設定的特定借貸組合為：借方 : {AccountMappingCategories.Cash} "
            + $"和 貸方 : {AccountMappingCategories.Revenue}";

        var result = LegacyCriteriaLogParser.TryParse(
            log,
            "legacy-scenario-01",
            "synthetic rationale",
            "scenario-01-criteria-log");

        var scenario = AssertScenario(result);
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        var rule = Assert.Single(group.GetProperty("rules").EnumerateArray());
        Assert.Equal("accountPair", rule.GetProperty("type").GetString());
        Assert.Equal(AccountPairModes.Exact, rule.GetProperty("pairMode").GetString());
    }

    // 2026-10-02 刪除單選分類欄位後，舊底稿的分類名稱改成內建分類身分陣列；PrivateCase 不經過這段轉換，由這兩個測試鎖住。
    [Fact]
    public void CriteriaLog_AccountPair_EmitsBuiltInCategoryIdArrays()
    {
        var log = $"#1. 設定的特定借貸組合為：借方 : {AccountMappingCategories.Cash} "
            + $"和 貸方 : {AccountMappingCategories.Revenue}";

        var result = LegacyCriteriaLogParser.TryParse(
            log,
            "legacy-scenario-01",
            "synthetic rationale",
            "scenario-01-criteria-log");

        var group = Assert.Single(AssertScenario(result).GetProperty("groups").EnumerateArray());
        var rule = Assert.Single(group.GetProperty("rules").EnumerateArray());
        Assert.Equal(new[] { AccountTaxonomyBuiltIns.CashId }, CategoryIds(rule, "debitCategoryIds"));
        Assert.Equal(new[] { AccountTaxonomyBuiltIns.RevenueId }, CategoryIds(rule, "creditCategoryIds"));
        Assert.False(rule.TryGetProperty("debitCategory", out _));
        Assert.False(rule.TryGetProperty("creditCategory", out _));
    }

    [Fact]
    public void CriteriaLog_SpecialAccountCategoryPair_EmitsBuiltInCategoryIdArrays()
    {
        var log = $"#1. 新增科目配對篩選條件為 借方 - {AccountMappingCategories.Cash}、"
            + $"貸方 - 非 {AccountMappingCategories.Revenue}";

        var result = LegacyCriteriaLogParser.TryParse(
            log,
            "legacy-scenario-01",
            "synthetic rationale",
            "scenario-01-criteria-log");

        var group = Assert.Single(AssertScenario(result).GetProperty("groups").EnumerateArray());
        var rule = Assert.Single(group.GetProperty("rules").EnumerateArray());
        Assert.Equal("specialAccountCategoryPair", rule.GetProperty("type").GetString());
        Assert.Equal(SpecialAccountCategoryPairModes.DrNotCr, rule.GetProperty("pairMode").GetString());
        Assert.Equal(new[] { AccountTaxonomyBuiltIns.CashId }, CategoryIds(rule, "debitCategoryIds"));
        Assert.Equal(new[] { AccountTaxonomyBuiltIns.RevenueId }, CategoryIds(rule, "creditCategoryIds"));
        Assert.False(rule.TryGetProperty("debitCategory", out _));
        Assert.False(rule.TryGetProperty("creditCategory", out _));
    }

    private static string[] CategoryIds(JsonElement rule, string property) =>
        rule.GetProperty(property).EnumerateArray().Select(static item => item.GetString() ?? string.Empty).ToArray();

    [Fact]
    public void CriteriaLog_RegexMetacharacter_FailsClosedWithoutEchoingToken()
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 文字欄位【{description}】值為 - alpha.*",
            "legacy-scenario-01",
            "synthetic rationale",
            "scenario-01-criteria-log");

        Assert.Null(result.Scenario);
        Assert.Equal("scenario-01-criteria-log", result.PendingFieldId);
    }

    [Fact]
    public void CriteriaLog_CaseASecondPosition_NormalizesNegativeLiteralSetIntoTypedTextRule()
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 文字欄位【{description}】值不包含 - alpha one,beta .*",
            "legacy-scenario-02",
            "synthetic rationale",
            "scenario-02-criteria-log",
            LegacyParityCase.CaseA,
            2);

        var scenario = AssertScenario(result);
        Assert.Equal("legacy-scenario-02", scenario.GetProperty("name").GetString());
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal("row", group.GetProperty("matchScope").GetString());
        var rule = Assert.Single(group.GetProperty("rules").EnumerateArray());
        Assert.Equal("text", rule.GetProperty("type").GetString());
        Assert.Equal("notContains", rule.GetProperty("mode").GetString());
        Assert.Equal("alphaone,beta.*", rule.GetProperty("keywords").GetString());
        Assert.False(rule.TryGetProperty("values", out _));
        Assert.False(rule.TryGetProperty("normalization", out _));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void CriteriaLog_SameVoucherTextSet_PreservesAnchorOrderAndOriginalPosition(
        int legacyPosition)
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 僅考量貸方傳票 "
                + $"#2. 文字欄位【{description}】值為 - alpha one,beta.* "
                + $"#3. 文字欄位【{description}】值包含 - gamma",
            $"legacy-scenario-{legacyPosition:D2}",
            "synthetic rationale",
            $"scenario-{legacyPosition:D2}-criteria-log",
            LegacyParityCase.CaseA,
            legacyPosition);

        var scenario = AssertScenario(result);
        Assert.Equal($"legacy-scenario-{legacyPosition:D2}", scenario.GetProperty("name").GetString());
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal("sameVoucher", group.GetProperty("matchScope").GetString());
        var rules = group.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(3, rules.Length);
        Assert.Equal("drCrOnly", rules[0].GetProperty("type").GetString());
        Assert.Equal("textSet", rules[1].GetProperty("type").GetString());
        Assert.Equal("preserve", rules[1].GetProperty("normalization").GetString());
        Assert.Equal("textSet", rules[2].GetProperty("type").GetString());
        Assert.Equal("preserve", rules[2].GetProperty("normalization").GetString());
    }

    [Fact]
    public void CriteriaLog_CaseAFourthPosition_ConvertsMultipleLiteralListsToTextSets()
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var accountName = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAccName).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 文字欄位【{description}】值為 - alpha #2. 文字欄位【{accountName}】值為 - beta,gamma",
            "legacy-scenario-04",
            "synthetic rationale",
            "scenario-04-criteria-log",
            LegacyParityCase.CaseA,
            4);

        var scenario = AssertScenario(result);
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal("sameVoucher", group.GetProperty("matchScope").GetString());
        var rules = group.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.All(rules, rule => Assert.Equal("textSet", rule.GetProperty("type").GetString()));
        Assert.All(rules, rule => Assert.Equal("preserve", rule.GetProperty("normalization").GetString()));
    }

    [Theory]
    [InlineData((int)LegacyParityCase.CaseA, 2, "row", "removeAsciiSpaces")]
    [InlineData((int)LegacyParityCase.CaseA, 3, "sameVoucher", "preserve")]
    public void CriteriaLog_CustomDescriptionList_UsesClosedTextSetPolicy(
        int caseValue,
        int legacyPosition,
        string matchScope,
        string normalization)
    {
        var result = LegacyCriteriaLogParser.TryParse(
            "#1. 人工分錄 #2. 新增的特定描述為：alpha one,beta.*",
            $"legacy-scenario-{legacyPosition:D2}",
            "synthetic rationale",
            $"scenario-{legacyPosition:D2}-criteria-log",
            (LegacyParityCase)caseValue,
            legacyPosition);

        var scenario = AssertScenario(result);
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal(matchScope, group.GetProperty("matchScope").GetString());
        var rule = Assert.Single(
            group.GetProperty("rules").EnumerateArray(),
            candidate => candidate.GetProperty("type").GetString() == "textSet");
        Assert.Equal(GlMappingKeys.Description, rule.GetProperty("field").GetString());
        Assert.Equal(normalization, rule.GetProperty("normalization").GetString());
    }

    [Fact]
    public void CriteriaLog_CaseBSecondPosition_CombinesNumericAnchorAndLiteralTextSet()
    {
        var amount = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlAmount).LegacyFieldName!;
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 數字欄位【{amount}】值大於(含) 10 #2. 文字欄位【{description}】值包含 - alpha,beta",
            "legacy-scenario-02",
            "synthetic rationale",
            "scenario-02-criteria-log",
            LegacyParityCase.CaseB,
            2);

        var scenario = AssertScenario(result);
        var group = Assert.Single(scenario.GetProperty("groups").EnumerateArray());
        Assert.Equal("sameVoucher", group.GetProperty("matchScope").GetString());
        var rules = group.GetProperty("rules").EnumerateArray().ToArray();
        Assert.Equal(2, rules.Length);
        Assert.Equal("numRange", rules[0].GetProperty("type").GetString());
        Assert.Equal("textSet", rules[1].GetProperty("type").GetString());
    }

    [Fact]
    public void CriteriaLog_TextFieldWithUnknownConnector_FailsWithTypedUnknownRule()
    {
        var description = JetFieldCatalog.GlFields.Single(
            static field => field.SemanticIdentity == JetFieldCatalog.GlDescription).LegacyFieldName!;
        var result = LegacyCriteriaLogParser.TryParse(
            $"#1. 文字欄位【{description}】任意連接詞 - alpha",
            "legacy-scenario-02",
            "synthetic rationale",
            "scenario-02-criteria-log",
            LegacyParityCase.CaseA,
            2);

        Assert.Null(result.Scenario);
        Assert.Equal("scenario-02-criteria-log", result.PendingFieldId);
        Assert.Equal(LegacyCriteriaParseFailure.UnknownRule, result.Failure);
        Assert.Equal("unknown-rule", result.Failure.FixedId());
    }

    [Fact]
    public void CriteriaLog_SameVoucherTargetWithOnlyAnchor_FailsClosed()
    {
        var result = LegacyCriteriaLogParser.TryParse(
            "#1. 人工分錄",
            "legacy-scenario-03",
            "synthetic rationale",
            "scenario-03-criteria-log",
            LegacyParityCase.CaseA,
            3);

        Assert.Null(result.Scenario);
        Assert.Equal("scenario-03-criteria-log", result.PendingFieldId);
    }

    private static JsonElement AssertScenario(LegacyCriteriaParseResult result)
    {
        Assert.Null(result.PendingFieldId);
        Assert.True(result.Scenario.HasValue);
        return result.Scenario.Value;
    }

    private static void AssertResolutionSourcesMatchMappings(
        IReadOnlyDictionary<string, string> mappings,
        IReadOnlyDictionary<string, LegacyMappingResolutionSource> resolutionSources)
    {
        Assert.True(
            mappings.Count == resolutionSources.Count
            && mappings.Keys.ToHashSet(StringComparer.Ordinal)
                .SetEquals(resolutionSources.Keys),
            "Mapping provenance keys differed from mapping keys; values suppressed.");
        Assert.All(
            resolutionSources.Values,
            source => Assert.True(Enum.IsDefined(source)));
    }

    private static void AssertMappingsEqual(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual,
        string message) =>
        Assert.True(
            expected.Count == actual.Count
            && expected.All(pair => actual.TryGetValue(pair.Key, out var value)
                && value.Equals(pair.Value, StringComparison.Ordinal)),
            message);

    private static void AssertSerializedResolutionSourcesAreStrings(
        JsonElement serializedSources,
        int expectedCount)
    {
        var properties = serializedSources.EnumerateObject().ToArray();
        Assert.Equal(expectedCount, properties.Length);
        Assert.All(
            properties,
            property =>
            {
                Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
                Assert.True(Enum.TryParse<LegacyMappingResolutionSource>(
                    property.Value.GetString(),
                    out _));
            });
    }

    private enum SyntheticGlShape
    {
        Direct,
        NormalizedIdentity,
        ReorderedDirect,
        ReorderedPartition,
        PlaceholderColumnPartition,
        PopulatedPlaceholderColumnPartition,
        OutOfRangeDataColumnPartition,
        HeaderOnlyPartition,
        MissingNamedColumnPartition,
        TargetlessNormalizedConflict,
        QualifiedTargetRoleConflict,
        TargetlessRoleConflict,
        TargetlessNormalizedRoleConflict,
        CaseBAccountReconciliationAlternateUnique,
        CaseBAccountReconciliationCurrentUnique,
        CaseBAccountReconciliationAmbiguous,
        CaseBAccountReconciliationNoMatch,
        OverlappingRequiredRolesUnique,
        OverlappingRequiredRolesAmbiguous,
        DuplicateRequiredDocumentTargets,
        GenericAccountNameFallback,
        DirectFormulaLikeName,
        GeneratedDual,
        MalformedGeneratedDual,
    }

    private static void WriteSyntheticCase(
        string directory,
        bool canonicalDisplayFallback = false,
        SyntheticGlShape glShape = SyntheticGlShape.Direct)
    {
        const string stamp = "fixture_20310102030405";
        WriteWorkbook(Path.Combine(directory, $"{stamp}_ValidationReport.xlsx"), workbook =>
            workbook.AddWorksheet("ValidationReport").Cell("A1").Value = "synthetic");
        WriteWorkbook(Path.Combine(directory, $"{stamp}_AccountMapping.xlsx"), workbook =>
        {
            var sheet = workbook.AddWorksheet("AccountMapping");
            sheet.Cell("A1").Value = "code";
            sheet.Cell("B1").Value = "name";
            sheet.Cell("C1").Value = "category";
        });
        WriteWorkbook(Path.Combine(directory, $"{stamp}_INFReport.xlsx"), workbook =>
            workbook.AddWorksheet("INF").Cell("A1").Value = "synthetic");
        WriteWorkbook(Path.Combine(directory, $"{stamp}_PrescreeningReport.xlsx"), workbook =>
            workbook.AddWorksheet("Pre-screening_Report").Cell("A1").Value = "synthetic");
        WriteCriteriaReport(Path.Combine(directory, $"{stamp}_CriteriaSelectionReport.xlsx"));
        WriteWorkingPaper(
            Path.Combine(directory, $"{stamp}_WorkingPaper.xlsx"),
            canonicalDisplayFallback,
            glShape);
        WriteGlSource(Path.Combine(directory, "source-ledger.xlsx"), glShape: glShape);
        var reconciliationShape = glShape is
            SyntheticGlShape.CaseBAccountReconciliationAlternateUnique or
            SyntheticGlShape.CaseBAccountReconciliationCurrentUnique or
            SyntheticGlShape.CaseBAccountReconciliationAmbiguous or
            SyntheticGlShape.CaseBAccountReconciliationNoMatch;
        File.WriteAllText(
            Path.Combine(directory, "source-trial-balance.txt"),
            $"tb-code,tb-name,tb-change\nT-1,synthetic,{(reconciliationShape ? "10" : "0")}\n");
    }

    private static void WriteCriteriaReport(string path)
    {
        WriteWorkbook(path, workbook =>
        {
            var sheet = workbook.AddWorksheet("Summary Inforamtion");
            sheet.Cell("A5").Value = "Criteria Selection 1";
            sheet.Cell("B5").Value = "#1. 僅考量借方傳票";
            sheet.Cell("C5").Value = 2;
            sheet.Cell("D5").Value = 3;
        });
    }

    private static void WriteWorkingPaper(
        string path,
        bool canonicalDisplayFallback,
        SyntheticGlShape glShape)
    {
        WriteWorkbook(path, workbook =>
        {
            var period = workbook.AddWorksheet("step1 synthetic");
            period.Cell("A2").Value = "測試資料期間 : 2031/01/01 ~ 2031/12/31";
            period.Cell("A3").Value = "財務報表準備期間 - 開始日 : 2031/12/01";

            var fieldInfo = workbook.AddWorksheet("自動化工具-檔案欄位資訊");
            fieldInfo.Cell("A2").Value = "TB檔案配對前後欄位對照表";
            fieldInfo.Cell("A3").Value = "配對前欄位名稱";
            fieldInfo.Cell("E3").Value = "配對後欄位名稱";
            AddFieldInfoRow(fieldInfo, 4, "tb-code", LegacyName(DatasetKind.Tb, JetFieldCatalog.TbAccNum));
            AddFieldInfoRow(fieldInfo, 5, "tb-name", LegacyName(DatasetKind.Tb, JetFieldCatalog.TbAccName));
            AddFieldInfoRow(fieldInfo, 6, "tb-change", LegacyName(DatasetKind.Tb, JetFieldCatalog.TbChangeAmount));

            fieldInfo.Cell("A8").Value = "GL檔案配對前後欄位對照表";
            fieldInfo.Cell("A9").Value = "配對前欄位名稱";
            fieldInfo.Cell("E9").Value = "配對後欄位名稱";
            AddFieldInfoRow(
                fieldInfo,
                10,
                glShape is SyntheticGlShape.OverlappingRequiredRolesUnique
                    or SyntheticGlShape.OverlappingRequiredRolesAmbiguous
                        ? "mapped-required-a"
                        : "gl-doc",
                LegacyName(DatasetKind.Gl, JetFieldCatalog.GlDocNum));
            AddFieldInfoRow(fieldInfo, 11, "gl-post", LegacyName(DatasetKind.Gl, JetFieldCatalog.GlPostDate));
            AddFieldInfoRow(
                fieldInfo,
                12,
                glShape switch
                {
                    SyntheticGlShape.NormalizedIdentity => "gl account",
                    SyntheticGlShape.TargetlessNormalizedConflict => "gl account",
                    SyntheticGlShape.QualifiedTargetRoleConflict => "legacy account identifier",
                    SyntheticGlShape.OverlappingRequiredRolesUnique or
                    SyntheticGlShape.OverlappingRequiredRolesAmbiguous => "mapped-required-b",
                    SyntheticGlShape.TargetlessRoleConflict or
                    SyntheticGlShape.TargetlessNormalizedRoleConflict or
                    SyntheticGlShape.CaseBAccountReconciliationAlternateUnique or
                    SyntheticGlShape.CaseBAccountReconciliationCurrentUnique or
                    SyntheticGlShape.CaseBAccountReconciliationAmbiguous or
                    SyntheticGlShape.CaseBAccountReconciliationNoMatch =>
                        "unrelated-required-display",
                    _ => "gl-account",
                },
                LegacyName(DatasetKind.Gl, JetFieldCatalog.GlAccNum));
            AddFieldInfoRow(
                fieldInfo,
                13,
                glShape == SyntheticGlShape.GenericAccountNameFallback
                    ? "legacy-account-name-display"
                    : "gl-account-name",
                LegacyName(DatasetKind.Gl, JetFieldCatalog.GlAccName));
            AddFieldInfoRow(
                fieldInfo,
                14,
                canonicalDisplayFallback
                    ? LegacyName(DatasetKind.Gl, JetFieldCatalog.GlDescription)
                    : "gl-description",
                LegacyName(DatasetKind.Gl, JetFieldCatalog.GlDescription));
            AddGlAmountFieldInfoRows(fieldInfo, glShape);
            if (glShape == SyntheticGlShape.DuplicateRequiredDocumentTargets)
            {
                for (var row = 16; row <= 27; row++)
                {
                    AddFieldInfoRow(
                        fieldInfo,
                        row,
                        $"duplicate-document-target-{row - 15:00}",
                        LegacyName(DatasetKind.Gl, JetFieldCatalog.GlDocNum));
                }
            }
            if (glShape == SyntheticGlShape.MissingNamedColumnPartition)
            {
                AddFieldInfoRow(fieldInfo, 16, "gl-unmapped", string.Empty);
            }
            else if (glShape is SyntheticGlShape.PlaceholderColumnPartition
                or SyntheticGlShape.PopulatedPlaceholderColumnPartition)
            {
                AddFieldInfoRow(fieldInfo, 16, "COL_7", string.Empty);
            }
            else if (glShape is SyntheticGlShape.TargetlessNormalizedConflict
                or SyntheticGlShape.QualifiedTargetRoleConflict
                or SyntheticGlShape.TargetlessRoleConflict
                or SyntheticGlShape.TargetlessNormalizedRoleConflict
                or SyntheticGlShape.CaseBAccountReconciliationAlternateUnique
                or SyntheticGlShape.CaseBAccountReconciliationCurrentUnique
                or SyntheticGlShape.CaseBAccountReconciliationAmbiguous
                or SyntheticGlShape.CaseBAccountReconciliationNoMatch)
            {
                AddFieldInfoRow(
                    fieldInfo,
                    16,
                    glShape switch
                    {
                        SyntheticGlShape.TargetlessNormalizedRoleConflict => "gl account",
                        SyntheticGlShape.QualifiedTargetRoleConflict => "gl account",
                        SyntheticGlShape.CaseBAccountReconciliationCurrentUnique =>
                            "wrong-account-code",
                        SyntheticGlShape.CaseBAccountReconciliationAlternateUnique or
                        SyntheticGlShape.CaseBAccountReconciliationAmbiguous or
                        SyntheticGlShape.CaseBAccountReconciliationNoMatch =>
                            "correct-account-code",
                        _ => "gl-account",
                    },
                    string.Empty);
            }

            var calendar = workbook.AddWorksheet("自動化工具-假期假日資訊");
            calendar.Cell("A3").Value = "Date_of_Holiday";
            calendar.Cell("B3").Value = "Holiday_Name";
            calendar.Cell("C3").Value = "IS_Holiday";
            calendar.Cell("A4").Value = "2031/02/03";
            calendar.Cell("C4").Value = "Y";
            calendar.Cell("A5").Value = "2031/02/04";
            calendar.Cell("C5").Value = "N";
            calendar.Cell("A8").Value = "Date_of_MakeUpDay";
            calendar.Cell("A9").Value = "2031/02/08";

            var accountMapping = workbook.AddWorksheet("自動化工具-科目配對資訊");
            accountMapping.Cell(1, 1).Value = "GL_NUMBER";
            accountMapping.Cell(1, 2).Value = "GL_NAME";
            accountMapping.Cell(1, 3).Value = "STANDARDIZED_ACCOUNT_NAME";
            accountMapping.Cell(2, 1).Value = "synthetic-account";
            accountMapping.Cell(2, 2).Value = "synthetic account name";
            accountMapping.Cell(2, 3).Value = AccountMappingCategories.Cash;

            var rationale = workbook.AddWorksheet("step3 synthetic");
            rationale.Cell("C19").Value = "#1. 僅考量借方傳票";
            rationale.Cell("D19").Value = "synthetic rationale";
        });
    }

    private static void WriteGlSource(
        string path,
        string descriptionHeader = "gl-description",
        SyntheticGlShape glShape = SyntheticGlShape.Direct)
    {
        WriteWorkbook(path, workbook =>
        {
            switch (glShape)
            {
                case SyntheticGlShape.ReorderedPartition:
                    AddGlSheet(
                        workbook.AddWorksheet("Part-1"),
                        descriptionHeader,
                        SyntheticGlShape.Direct);
                    AddGlSheet(
                        workbook.AddWorksheet("Part-2"),
                        descriptionHeader,
                        SyntheticGlShape.ReorderedDirect);
                    break;
                case SyntheticGlShape.PlaceholderColumnPartition:
                case SyntheticGlShape.PopulatedPlaceholderColumnPartition:
                case SyntheticGlShape.OutOfRangeDataColumnPartition:
                case SyntheticGlShape.HeaderOnlyPartition:
                case SyntheticGlShape.MissingNamedColumnPartition:
                    AddGlSheet(workbook.AddWorksheet("Part-1"), descriptionHeader, glShape);
                    AddGlSheet(
                        workbook.AddWorksheet("Part-2"),
                        descriptionHeader,
                        SyntheticGlShape.Direct);
                    break;
                default:
                    AddGlSheet(workbook.AddWorksheet("Part-1"), descriptionHeader, glShape);
                    AddGlSheet(workbook.AddWorksheet("Part-2"), descriptionHeader, glShape);
                    break;
            }
        });
    }

    private static void AddGlAmountFieldInfoRows(
        IXLWorksheet fieldInfo,
        SyntheticGlShape glShape)
    {
        var amountLegacyName = LegacyName(DatasetKind.Gl, JetFieldCatalog.GlAmount);
        switch (glShape)
        {
            case SyntheticGlShape.Direct:
            case SyntheticGlShape.NormalizedIdentity:
            case SyntheticGlShape.ReorderedDirect:
            case SyntheticGlShape.ReorderedPartition:
            case SyntheticGlShape.PlaceholderColumnPartition:
            case SyntheticGlShape.PopulatedPlaceholderColumnPartition:
            case SyntheticGlShape.OutOfRangeDataColumnPartition:
            case SyntheticGlShape.HeaderOnlyPartition:
            case SyntheticGlShape.MissingNamedColumnPartition:
            case SyntheticGlShape.TargetlessNormalizedConflict:
            case SyntheticGlShape.QualifiedTargetRoleConflict:
            case SyntheticGlShape.TargetlessRoleConflict:
            case SyntheticGlShape.TargetlessNormalizedRoleConflict:
            case SyntheticGlShape.CaseBAccountReconciliationAlternateUnique:
            case SyntheticGlShape.CaseBAccountReconciliationCurrentUnique:
            case SyntheticGlShape.CaseBAccountReconciliationAmbiguous:
            case SyntheticGlShape.CaseBAccountReconciliationNoMatch:
            case SyntheticGlShape.OverlappingRequiredRolesUnique:
            case SyntheticGlShape.OverlappingRequiredRolesAmbiguous:
            case SyntheticGlShape.DuplicateRequiredDocumentTargets:
            case SyntheticGlShape.GenericAccountNameFallback:
                AddFieldInfoRow(fieldInfo, 15, "gl-amount", amountLegacyName);
                break;
            case SyntheticGlShape.DirectFormulaLikeName:
                AddFieldInfoRow(fieldInfo, 15, "gl-debit", string.Empty);
                AddFieldInfoRow(fieldInfo, 16, "gl-credit", string.Empty);
                AddFieldInfoRow(
                    fieldInfo,
                    17,
                    "gl-amount: gl-debit-gl-credit",
                    amountLegacyName);
                break;
            case SyntheticGlShape.GeneratedDual:
            case SyntheticGlShape.MalformedGeneratedDual:
                AddFieldInfoRow(fieldInfo, 15, "gl-debit", string.Empty);
                AddFieldInfoRow(fieldInfo, 16, "gl-credit", string.Empty);
                var suffix = glShape == SyntheticGlShape.MalformedGeneratedDual
                    ? " extra"
                    : string.Empty;
                AddFieldInfoRow(
                    fieldInfo,
                    17,
                    $"{amountLegacyName} 由系統產生 : gl-debit - gl-credit{suffix}",
                    amountLegacyName);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(glShape), glShape, null);
        }
    }

    private static void AddGlSheet(
        IXLWorksheet sheet,
        string descriptionHeader,
        SyntheticGlShape glShape)
    {
        var headers = GlHeaders(descriptionHeader, glShape);
        for (var column = 0; column < headers.Length; column++)
        {
            var header = headers[column];
            sheet.Cell(1, column + 1).Value = header;
            if (glShape == SyntheticGlShape.HeaderOnlyPartition)
            {
                continue;
            }
            if (TabularHeaderNormalizer.IsPlaceholder(header))
            {
                if (glShape == SyntheticGlShape.PopulatedPlaceholderColumnPartition)
                {
                    sheet.Cell(2, column + 1).Value = "0";
                }

                continue;
            }

            if (glShape is SyntheticGlShape.CaseBAccountReconciliationAlternateUnique
                or SyntheticGlShape.CaseBAccountReconciliationCurrentUnique
                or SyntheticGlShape.CaseBAccountReconciliationAmbiguous
                or SyntheticGlShape.CaseBAccountReconciliationNoMatch)
            {
                sheet.Cell(2, column + 1).Value = header switch
                {
                    "gl-post" => "2031-01-01",
                    "wrong-account-code" =>
                        glShape == SyntheticGlShape.CaseBAccountReconciliationAmbiguous
                            ? "T-1"
                            : "W-1",
                    "correct-account-code" =>
                        glShape == SyntheticGlShape.CaseBAccountReconciliationNoMatch
                            ? "W-2"
                            : "T-1",
                    "gl-amount" => "5",
                    _ => "synthetic",
                };
                continue;
            }

            sheet.Cell(2, column + 1).Value = header.Contains("amount", StringComparison.OrdinalIgnoreCase)
                || header.Contains("debit", StringComparison.OrdinalIgnoreCase)
                || header.Contains("credit", StringComparison.OrdinalIgnoreCase)
                    ? "0"
                    : "synthetic";
        }

        if (glShape == SyntheticGlShape.OutOfRangeDataColumnPartition)
        {
            sheet.Cell(2, headers.Length + 1).Value = "0";
        }
    }

    private static string[] GlHeaders(string descriptionHeader, SyntheticGlShape glShape) =>
        glShape switch
        {
            SyntheticGlShape.Direct or
            SyntheticGlShape.NormalizedIdentity or
            SyntheticGlShape.ReorderedPartition or
            SyntheticGlShape.OutOfRangeDataColumnPartition or
            SyntheticGlShape.HeaderOnlyPartition or
            SyntheticGlShape.TargetlessNormalizedConflict or
            SyntheticGlShape.QualifiedTargetRoleConflict or
            SyntheticGlShape.TargetlessRoleConflict or
            SyntheticGlShape.TargetlessNormalizedRoleConflict =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.CaseBAccountReconciliationAlternateUnique or
            SyntheticGlShape.CaseBAccountReconciliationCurrentUnique or
            SyntheticGlShape.CaseBAccountReconciliationAmbiguous or
            SyntheticGlShape.CaseBAccountReconciliationNoMatch =>
            [
                "gl-doc",
                "gl-post",
                "wrong-account-code",
                "correct-account-code",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.OverlappingRequiredRolesUnique =>
            [
                "document-account-code",
                "document-code",
                "gl-post",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.OverlappingRequiredRolesAmbiguous =>
            [
                "document-account-code",
                "journal-account-id",
                "gl-post",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.DuplicateRequiredDocumentTargets =>
            [
                "document-code-01",
                "document-code-02",
                "document-code-03",
                "document-code-04",
                "document-code-05",
                "document-code-06",
                "document-code-07",
                "document-code-08",
                "document-code-09",
                "document-code-10",
                "document-code-11",
                "document-code-12",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.MissingNamedColumnPartition =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
                "gl-unmapped",
            ],
            SyntheticGlShape.PlaceholderColumnPartition or
            SyntheticGlShape.PopulatedPlaceholderColumnPartition =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-amount",
                "COL_7",
            ],
            SyntheticGlShape.GenericAccountNameFallback =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "name",
                descriptionHeader,
                "gl-amount",
            ],
            SyntheticGlShape.ReorderedDirect =>
            [
                "gl-amount",
                descriptionHeader,
                "gl-account-name",
                "gl-doc",
                "gl-account",
                "gl-post",
            ],
            SyntheticGlShape.DirectFormulaLikeName =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-debit",
                "gl-credit",
                "gl-amount: gl-debit-gl-credit",
            ],
            SyntheticGlShape.GeneratedDual or SyntheticGlShape.MalformedGeneratedDual =>
            [
                "gl-doc",
                "gl-post",
                "gl-account",
                "gl-account-name",
                descriptionHeader,
                "gl-debit",
                "gl-credit",
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(glShape), glShape, null),
        };

    private static string LegacyName(DatasetKind dataset, string semanticIdentity) =>
        (dataset == DatasetKind.Gl ? JetFieldCatalog.GlFields : JetFieldCatalog.TbFields)
        .Single(field => field.SemanticIdentity == semanticIdentity)
        .LegacyFieldName!;

    private static void AddFieldInfoRow(
        IXLWorksheet sheet,
        int row,
        string source,
        string target)
    {
        sheet.Cell(row, 1).Value = source;
        sheet.Cell(row, 2).Value = "文字型態";
        if (target.Length > 0)
        {
            sheet.Cell(row, 5).Value = target;
        }
    }

    private static void WriteWorkbook(string path, Action<XLWorkbook> build)
    {
        using var workbook = new XLWorkbook();
        build(workbook);
        workbook.SaveAs(path);
    }
}
