using System.Collections.Concurrent;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 單庫「存在且引擎非 Express（就緒）」的 <b>process 級</b> 就緒快取（控制面第四輪 §4，master 依賴最小化）。
/// 由 <see cref="SqlServerProjectRegistry"/> 與 <see cref="SqlServerProjectDatabase"/> 共用：任一元件首次確認某
/// 單庫目標就緒後標記,其後全 app 生命週期內所有 SqlServer 元件對<b>同一目標</b>的存在性檢查一律跳過 master 探測
/// ——把原本的實例級快取（<c>_engineVerified</c>／<c>_tablesEnsured</c>）提升到 process 級,master 至多探一次。
/// <para>
/// 就緒以「連線目標」（伺服器 DataSource ＋ 單庫名）為鍵,而非單一全域旗標:app 全程只對一個目標操作,故一支條目
/// ＝「process 級單一旗標」的原設計語意;但測試 process 可能同時碰多個目標（真 JET_Test 伺服器與 LocalDB Express
/// 守衛的不同 instance）——若用單一全域旗標,先確認的目標會讓另一目標錯誤跳過 Express 引擎檢查。按目標分鍵消除此
/// 跨目標污染,是嚴格更正確的設計,對 app 行為零差異。
/// </para>
/// thread-safe：以 <see cref="ConcurrentDictionary{TKey,TValue}"/> 承載已確認目標的集合。
/// </summary>
internal static class SqlServerSingleDatabaseReadiness
{
    private static readonly ConcurrentDictionary<string, bool> Confirmed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>此目標是否已確認就緒（就緒後對該目標跳過 master 探測）。</summary>
    public static bool IsConfirmed(string targetKey) => Confirmed.ContainsKey(targetKey);

    /// <summary>標記此目標已就緒（存在＋非 Express）。冪等——重複標記無害。</summary>
    public static void MarkConfirmed(string targetKey) => Confirmed[targetKey] = true;

    /// <summary>
    /// 由連線設定衍生穩定的就緒鍵：伺服器 DataSource ＋ 單庫名（以換行相接——伺服器名／庫名皆不含換行,杜絕串接歧義）。
    /// BaseConnectionString 不可解析時退回原字串,null/空白時只以單庫名成鍵（此情形存在性檢查本就會因未設定連線而失敗）。
    /// </summary>
    public static string KeyFor(SqlServerConnectionOptions options)
    {
        var dataSource = string.Empty;
        if (!string.IsNullOrWhiteSpace(options.BaseConnectionString))
        {
            try
            {
                dataSource = new SqlConnectionStringBuilder(options.BaseConnectionString).DataSource;
            }
            catch (Exception)
            {
                dataSource = options.BaseConnectionString;
            }
        }

        return dataSource + "\n" + options.SingleDatabaseName;
    }
}
