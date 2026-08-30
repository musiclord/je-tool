namespace JET.Domain;

public enum DatasetKind
{
    Gl,
    Tb,
    AccountMapping
}

public static class DatasetKindNames
{
    public static string ToStorageName(this DatasetKind kind) => kind switch
    {
        DatasetKind.Gl => "gl",
        DatasetKind.Tb => "tb",
        DatasetKind.AccountMapping => "account_mapping",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
