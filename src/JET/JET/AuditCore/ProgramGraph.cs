namespace JET.AuditCore;

/// <summary>
/// 六步驟 mainline action 的 review descriptor。Milestone 只供既有自動 step
/// 推進路徑使用；本型別不參與 ActionDispatcher runtime dispatch。
/// </summary>
internal sealed record ProgramNode(
    string ActionName,
    int Milestone,
    IReadOnlyList<ProcedureDefinition> Procedures);

/// <summary>
/// JET audit mainline 的 internal 程式審查目錄。程序敘述直接引用
/// <see cref="JetAuditProgram.Procedures"/>，不複製 display name、purpose 或 SQL。
/// </summary>
internal sealed class ProgramGraph
{
    private ProgramGraph(IReadOnlyList<ProgramNode> nodes)
    {
        Nodes = nodes;
    }

    internal static ProgramGraph Current { get; } = new(BuildNodes());

    internal IReadOnlyList<ProgramNode> Nodes { get; }

    internal ProgramNode RequireNode(string actionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionName);

        return Nodes.SingleOrDefault(node =>
                string.Equals(node.ActionName, actionName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"ProgramGraph 未登錄 mainline action '{actionName}'。");
    }

    private static IReadOnlyList<ProgramNode> BuildNodes()
    {
        var nodes = new[]
        {
            Node("project.create", 1, Spine("project_create")),
            Node("import.gl.fromFile", 2, Spine("data_import")),
            Node("mapping.commit.gl", 3, Spine("field_mapping")),
            Node("import.tb.fromFile", 2, Spine("data_import")),
            Node("mapping.commit.tb", 3, Spine("field_mapping")),
            Node("import.accountMapping.fromFile", 3, Spine("field_mapping")),
            Node("import.authorizedPreparer.fromFile", 3, Spine("field_mapping")),
            Node("import.holiday", 3, Spine("field_mapping")),
            Node("import.makeupDay", 3, Spine("field_mapping")),
            Node("import.holiday.fromFile", 3, Spine("field_mapping")),
            Node("import.makeupDay.fromFile", 3, Spine("field_mapping")),
            Node("calendar.setNonWorkingDays", 3, Spine("field_mapping")),
            Node("validate.run", 4, Family("validate.run")),
            Node("prescreen.run", 4, Family("prescreen.run")),
            Node("filter.preview", 4, Spine("advanced_filter")),
            Node("filter.commit", 5, Spine("advanced_filter")),
            Node("export.validationArtifacts", 4, Spine("validation_and_testing")),
            Node("export.accountMappingTemplate", 4, Spine("validation_and_testing")),
            Node("export.prescreenReport", 4, Spine("validation_and_testing")),
            Node("export.criteriaSelectionReport", 5, Spine("advanced_filter")),
            Node("export.workpaperStream", 5, Spine("workpaper_export"))
        };

        return Array.AsReadOnly(nodes);
    }

    private static ProgramNode Node(
        string actionName,
        int milestone,
        IReadOnlyList<ProcedureDefinition> procedures) =>
        new(actionName, milestone, procedures);

    private static IReadOnlyList<ProcedureDefinition> Spine(string slug) =>
        Array.AsReadOnly(
        [
            JetAuditProgram.Procedures.Single(definition =>
                string.Equals(definition.Slug, slug, StringComparison.Ordinal))
        ]);

    private static IReadOnlyList<ProcedureDefinition> Family(string actionName) =>
        Array.AsReadOnly(
            JetAuditProgram.Procedures
                .Where(definition =>
                    string.Equals(definition.ActionName, actionName, StringComparison.Ordinal))
                .ToArray());
}
