using System.Data.Common;
using JET.Infrastructure;
using Microsoft.Data.Sqlite;

namespace JET.Tests.Application;

/// <summary>
/// 量測試裡資料庫的工作量，結果只看資料與查詢，不受機器快慢影響。
/// 每條語句執行前，把那條連線的 SQLite 進度回呼換成這條語句所屬類別的計數器，每執行 16 個虛擬機指令加 1；
/// 讀取器之後逐列讀資料的工作量也算在同一類，直到同一條連線執行下一條語句。
/// <paramref name="classify"/> 依 SQL 文字回傳類別名稱；回傳 null 的語句不算進任何具名類別，只算進 <see cref="TotalSteps"/>。
/// </summary>
internal sealed class SqliteStatementWorkMeter(Func<string, string?> classify) : ISqlStatementObserver
{
    private const string Unclassified = "";

    private readonly object _gate = new();
    private readonly Dictionary<string, Category> _categories = new(StringComparer.Ordinal);

    /// <summary>到目前為止這個類別的語句執行了幾次。</summary>
    public int Statements(string category)
    {
        lock (_gate)
        {
            return _categories.TryGetValue(category, out var item) ? item.Statements : 0;
        }
    }

    /// <summary>到目前為止這個類別的語句用掉的工作量。</summary>
    public long Steps(string category)
    {
        lock (_gate)
        {
            return _categories.TryGetValue(category, out var item) ? Interlocked.Read(ref item.Steps) : 0;
        }
    }

    /// <summary>到目前為止全部語句用掉的工作量，含沒有歸類的語句。</summary>
    public long TotalSteps
    {
        get
        {
            lock (_gate)
            {
                return _categories.Values.Sum(item => Interlocked.Read(ref item.Steps));
            }
        }
    }

    public SqlStatementWatch? Begin(DbCommand command)
    {
        var name = classify(command.CommandText ?? string.Empty) ?? Unclassified;
        Category category;
        lock (_gate)
        {
            if (!_categories.TryGetValue(name, out category!))
            {
                category = new Category();
                _categories.Add(name, category);
            }

            category.Statements++;
        }

        if (command.Connection is SqliteConnection { Handle: { } handle })
        {
            SQLitePCL.raw.sqlite3_progress_handler(handle, 16, category.Tick, null);
        }

        return null;
    }

    private sealed class Category
    {
        public int Statements;
        public long Steps;

        public Category() => Tick = _ =>
        {
            Interlocked.Increment(ref Steps);
            return 0;
        };

        // 進度回呼要一直有人參考，否則 SQLite 呼叫時委派可能已被回收。
        public SQLitePCL.delegate_progress Tick { get; }
    }
}
