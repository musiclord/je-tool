namespace JET.Domain;

/// <summary>科目配對匯入與遷移共用；先認領確切標頭，再用關鍵字，最後補未認領欄位。</summary>
internal static class AccountMappingColumnMatcher
{
    private static readonly string[][] ExactNames =
    [
        ["GL_Number"], ["GL_Name"], ["Standardized Account Name*", "STANDARDIZED_ACCOUNT_NAME"]
    ];

    private static readonly string[][] Keywords =
    [
        ["科目代號", "科目編號", "account code", "code", "gl_number"],
        ["科目名稱", "account name", "gl_name", "name"],
        ["分類", "category", "standardized"]
    ];

    internal static AccountMappingColumnResolver.Resolution Resolve(IReadOnlyList<string> columns) => ResolveDetailed(columns).Columns;

    internal static AccountMappingColumnResolver.DetailedResolution ResolveDetailed(IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count < 3)
            throw new JetActionException(JetErrorCodes.ProjectionFailed, "科目配對檔需含科目編號、科目名稱、科目分類三欄。");

        var chosen = new int?[3];
        var methods = new AccountMappingColumnResolver.MatchMethod[3];
        var used = new HashSet<int>();
        // 三種確切欄名都先認領，避免分類的 Account Name 被名稱的寬鬆關鍵字占用。
        for (var role = 0; role < 3; role++)
            Find(role, ExactNames[role], exact: true);
        foreach (var role in new[] { 2, 0, 1 })
            if (chosen[role] is null) Find(role, Keywords[role], exact: false);
        for (var role = 0; role < 3; role++)
        {
            if (chosen[role] is not null) continue;
            var column = Enumerable.Range(0, columns.Count).First(i => !used.Contains(i));
            chosen[role] = column;
            methods[role] = AccountMappingColumnResolver.MatchMethod.Position;
            used.Add(column);
        }
        return new(Match(0), Match(1), Match(2));

        AccountMappingColumnResolver.ColumnMatch Match(int role) => new(columns[chosen[role]!.Value], methods[role]);

        void Find(int role, string[] candidates, bool exact)
        {
            foreach (var candidate in candidates)
            for (var index = 0; index < columns.Count; index++)
            {
                if (used.Contains(index)) continue;
                var name = columns[index].Trim();
                if (exact ? name.Equals(candidate, StringComparison.OrdinalIgnoreCase)
                    : name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    chosen[role] = index;
                    methods[role] = exact ? AccountMappingColumnResolver.MatchMethod.ExactName : AccountMappingColumnResolver.MatchMethod.Keyword;
                    used.Add(index);
                    return;
                }
            }
        }
    }
}
