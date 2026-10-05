using Xunit;

namespace JET.Tests;

/// <summary>
/// 斷言實際經過時間上限的測試。測試平行執行時機器滿載，這些上限可能被排程延遲誤判，
/// 所以放在不參與平行的集合，等其他測試跑完後單獨執行。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingSensitiveCollection
{
    public const string Name = "TimingSensitive";
}
