namespace JET.Application.Commands.FilterScenario.Rules
{
    /// <summary>
    /// Maps logical mapping keys (used by the UI scenario payload) to physical columns
    /// of the <c>target_gl_entry</c> table. Centralised so every evaluator agrees on the
    /// translation; do not duplicate this in repository SQL.
    /// </summary>
    internal static class GlColumnResolver
    {
        private static readonly IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["docNum"] = "doc_num",
            ["lineID"] = "line_id",
            ["lineId"] = "line_id",
            ["postDate"] = "post_date",
            ["docDate"] = "doc_date",
            ["accNum"] = "acc_num",
            ["accName"] = "acc_name",
            ["description"] = "description",
            ["jeSource"] = "je_source",
            ["createBy"] = "create_by",
            ["approveBy"] = "approve_by",
            ["manual"] = "manual",
            ["amount"] = "amount",
            ["debitAmount"] = "dr_amount",
            ["creditAmount"] = "cr_amount",
        };

        public static string? Resolve(string logicalKey)
        {
            if (string.IsNullOrWhiteSpace(logicalKey)) return null;
            return _map.TryGetValue(logicalKey, out var col) ? col : null;
        }
    }
}
