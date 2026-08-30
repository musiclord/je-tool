using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace JET.Infrastructure;

/// <summary>
/// 包裝 DuckDB.NET 的 <see cref="DbConnection"/>，讓它發出的每個命令都經
/// <see cref="DuckDbCommandAdapter"/>（spec §3 的引擎縫參數轉接器）。共用 <c>Local*</c> repository
/// 家族的 SQL 文本原為 SQLite 形（<c>@name</c> 具名參數），轉接器在執行前把記號改寫為 DuckDB 的
/// <c>$name</c> 並把參數名剝為裸名——repo 與 SQL 文本因此零改動。其餘成員全數委派給內層連線。
/// </summary>
internal sealed class DuckDbConnectionAdapter(DbConnection inner) : DbConnection
{
    /// <summary>內層真連線（DuckDB.NET 的 DuckDBConnection）。dev 檢視等需要具體型別時可取。</summary>
    public DbConnection Inner => inner;

    [AllowNull]
    public override string ConnectionString
    {
        get => inner.ConnectionString;
        set => inner.ConnectionString = value!;
    }

    public override string Database => inner.Database;

    public override string DataSource => inner.DataSource;

    public override string ServerVersion => inner.ServerVersion;

    public override ConnectionState State => inner.State;

    public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

    public override void Close() => inner.Close();

    public override void Open() => inner.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => inner.OpenAsync(cancellationToken);

    protected override DbCommand CreateDbCommand() => new DuckDbCommandAdapter(inner.CreateCommand(), this);

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        inner.BeginTransaction(isolationLevel);

    protected override async ValueTask<DbTransaction> BeginDbTransactionAsync(
        IsolationLevel isolationLevel, CancellationToken cancellationToken) =>
        await inner.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 包裝 DuckDB.NET 的 <see cref="DbCommand"/>：在每次執行（含 async 路徑）與 <see cref="Prepare"/> 之前，
/// (a) <see cref="CommandText"/> 設定時即把 <c>@ident</c> 改寫為 <c>$ident</c>（單引號常值感知；見
/// <see cref="DuckDbParameterMarkerRewriter"/>）；(b) 執行前把每個參數 <see cref="DbParameter.ParameterName"/>
/// 的前綴 <c>@</c>／<c>$</c> 暫時剝為裸名（DuckDB 綁定要求裸名），執行後**還原**——不改呼叫端持有的
/// 參數物件語意（採「暫時改名後還原」，非複製）。其餘成員全數委派內層命令。
/// </summary>
internal sealed class DuckDbCommandAdapter(DbCommand inner, DuckDbConnectionAdapter? owner) : DbCommand
{
    private string _commandText = string.Empty;

    /// <summary>對外回呼叫端寫入的原文（<c>@name</c> 形）；內層持改寫後（<c>$name</c> 形）。</summary>
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;
            inner.CommandText = DuckDbParameterMarkerRewriter.Rewrite(_commandText);
        }
    }

    public override int CommandTimeout
    {
        get => inner.CommandTimeout;
        set => inner.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => inner.CommandType;
        set => inner.CommandType = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => inner.UpdatedRowSource;
        set => inner.UpdatedRowSource = value;
    }

    public override bool DesignTimeVisible
    {
        get => inner.DesignTimeVisible;
        set => inner.DesignTimeVisible = value;
    }

    protected override DbConnection? DbConnection
    {
        // 對外呈現包裝連線；內層命令已於 CreateCommand 綁妥內層連線。呼叫端重設連線時解包委派。
        get => owner ?? inner.Connection as DbConnection;
        set => inner.Connection = value is DuckDbConnectionAdapter adapter ? adapter.Inner : value;
    }

    protected override DbParameterCollection DbParameterCollection => inner.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => inner.Transaction;
        set => inner.Transaction = value;
    }

    public override void Cancel() => inner.Cancel();

    protected override DbParameter CreateDbParameter() => inner.CreateParameter();

    public override void Prepare()
    {
        using (StripParameterPrefixes())
        {
            inner.Prepare();
        }
    }

    public override async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        using (StripParameterPrefixes())
        {
            await inner.PrepareAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override int ExecuteNonQuery()
    {
        using (StripParameterPrefixes())
        {
            return inner.ExecuteNonQuery();
        }
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        using (StripParameterPrefixes())
        {
            return await inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public override object? ExecuteScalar()
    {
        using (StripParameterPrefixes())
        {
            return inner.ExecuteScalar();
        }
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        using (StripParameterPrefixes())
        {
            return await inner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        using (StripParameterPrefixes())
        {
            return inner.ExecuteReader(behavior);
        }
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior, CancellationToken cancellationToken)
    {
        using (StripParameterPrefixes())
        {
            return await inner.ExecuteReaderAsync(behavior, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// 執行/Prepare 前把每個參數名前綴 <c>@</c>／<c>$</c> 暫時剝為裸名（DuckDB 綁定要求裸名），
    /// 回傳的 <see cref="IDisposable"/> 於離開時**還原**原名——呼叫端持有的參數物件語意不變。
    /// </summary>
    private ParameterNameScope StripParameterPrefixes() => new(inner.Parameters);

    private readonly struct ParameterNameScope : IDisposable
    {
        private readonly DbParameterCollection _parameters;
        private readonly string?[] _originalNames;

        public ParameterNameScope(DbParameterCollection parameters)
        {
            _parameters = parameters;
            _originalNames = new string?[parameters.Count];
            for (var i = 0; i < parameters.Count; i++)
            {
                var parameter = parameters[i];
                var name = parameter.ParameterName;
                _originalNames[i] = name;
                if (!string.IsNullOrEmpty(name) && (name[0] == '@' || name[0] == '$'))
                {
                    parameter.ParameterName = name[1..];
                }
            }
        }

        public void Dispose()
        {
            for (var i = 0; i < _parameters.Count && i < _originalNames.Length; i++)
            {
                _parameters[i].ParameterName = _originalNames[i];
            }
        }
    }
}
