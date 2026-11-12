namespace JET.Tests.TestInfrastructure;

internal static class TestProfileTraits
{
    internal const string Key = "TestProfile";
    internal const string CapabilityKey = "Capability";


    // 讀方案資料夾（JET.slnx 所在的資料夾）以外檔案的測試：範本原檔、舊版腳本、Git 忽略規則與追蹤清單、
    // 桌面情境程式與 action 契約文件。在開發儲存庫照常執行；只有方案資料夾時，
    // 執行測試程式加上 --filter-not-trait "TestProfile=DevelopmentRepository" 排除。
    internal const string DevelopmentRepository = "DevelopmentRepository";

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> Scale =
        [new(Key, "Scale")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> PrivateCase =
        [new(Key, "PrivateCase")];

    internal static readonly IReadOnlyCollection<KeyValuePair<string, string>> FileSystemLinks =
        [new(CapabilityKey, "FileSystemLinks")];

}
