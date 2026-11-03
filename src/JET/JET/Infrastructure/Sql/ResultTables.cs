using JET.Domain;

namespace JET.Infrastructure;

/// <summary>驗證結果與預篩選結果各存一張表；兩種結果對到哪一張表只在這裡決定。</summary>
internal static class ResultTables
{
    internal const string Validation = "validation_result";
    internal const string Prescreen = "prescreen_result";

    internal static string For(ResultKind kind) => kind switch
    {
        ResultKind.Validation => Validation,
        ResultKind.Prescreen => Prescreen,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
