using JET.AuditCore;
using Xunit;

namespace JET.Tests.AuditCore;

public sealed class ProgramGraphTests
{
    [Fact]
    public void Current_ContainsOnlyTheTwentyOneIncludedNonQueryActions()
    {
        var expected = new[]
        {
            "project.create",
            "import.gl.fromFile",
            "mapping.commit.gl",
            "import.tb.fromFile",
            "mapping.commit.tb",
            "import.accountMapping.fromFile",
            "import.authorizedPreparer.fromFile",
            "import.holiday",
            "import.makeupDay",
            "import.holiday.fromFile",
            "import.makeupDay.fromFile",
            "calendar.setNonWorkingDays",
            "validate.run",
            "prescreen.run",
            "filter.preview",
            "filter.commit",
            "export.validationArtifacts",
            "export.accountMappingTemplate",
            "export.prescreenReport",
            "export.criteriaSelectionReport",
            "export.workpaperStream"
        };

        Assert.Equal(expected, ProgramGraph.Current.Nodes.Select(node => node.ActionName));
        Assert.DoesNotContain(
            ProgramGraph.Current.Nodes,
            node => node.ActionName.StartsWith("query.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("project.create")]
    [InlineData("import.gl.fromFile")]
    [InlineData("mapping.commit.gl")]
    [InlineData("import.tb.fromFile")]
    [InlineData("mapping.commit.tb")]
    [InlineData("import.accountMapping.fromFile")]
    [InlineData("import.authorizedPreparer.fromFile")]
    [InlineData("import.holiday")]
    [InlineData("import.makeupDay")]
    [InlineData("import.holiday.fromFile")]
    [InlineData("import.makeupDay.fromFile")]
    [InlineData("calendar.setNonWorkingDays")]
    public void IntakeMappingNode_RemainsInReviewCatalog(string action)
    {
        Assert.Equal(action, ProgramGraph.Current.RequireNode(action).ActionName);
    }

    [Theory]
    [InlineData("project.create", 1)]
    [InlineData("import.gl.fromFile", 2)]
    [InlineData("mapping.commit.gl", 3)]
    [InlineData("mapping.commit.tb", 3)]
    [InlineData("validate.run", 4)]
    [InlineData("prescreen.run", 4)]
    [InlineData("filter.commit", 5)]
    public void MainlineNode_MilestoneMatchesExistingAutomaticProgress(
        string action,
        int expectedMilestone)
    {
        Assert.Equal(expectedMilestone, ProgramGraph.Current.RequireNode(action).Milestone);
    }

    [Fact]
    public void QueryActions_AreOutOfScopeAndNotRegistered()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ProgramGraph.Current.RequireNode("query.dataPreview"));

        Assert.Contains("未登錄", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MigrationStateType_IsRemovedAfterFinalGate()
    {
        var assembly = typeof(JetAuditProgram).Assembly;

        Assert.Null(assembly.GetType("JET.AuditCore.ProgramMigrationState"));
    }

    [Fact]
    public void ValidationNode_DerivesProceduresFromJetAuditProgramCatalog()
    {
        var expected = JetAuditProgram.Procedures
            .Where(item => item.ActionName == "validate.run")
            .ToArray();
        var actual = ProgramGraph.Current.RequireNode("validate.run").Procedures;

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PrescreenNode_DerivesProceduresFromJetAuditProgramCatalog()
    {
        var expected = JetAuditProgram.Procedures
            .Where(item => item.ActionName == "prescreen.run")
            .ToArray();
        var actual = ProgramGraph.Current.RequireNode("prescreen.run").Procedures;

        Assert.Equal(expected, actual);
    }
}
