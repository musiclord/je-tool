using System.Collections;
using System.Data.Common;
using System.Threading.Channels;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>channel 內待寫入 staging 的一列(batch_id/source_no 為整批常數,不入 channel)。</summary>
internal readonly record struct StagingBulkRecord(long RowNumber, int SourceRowNumber, string RowJson);

/// <summary>
/// 把 producer 餵入 channel 的列以 staging 五欄串流給 <see cref="SqlBulkCopy"/>。
/// SqlBulkCopy 的 async 路徑以 ReadAsync 推進列,故覆寫 ReadAsync 走 channel(不在 Read() 阻塞 async);
/// 其餘成員對齊 <see cref="GlProjectionDataReader"/>。
/// </summary>
internal sealed class StagingBulkCopyDataReader(ChannelReader<StagingBulkRecord> reader, string batchId, int sourceNo)
    : DbDataReader
{
    public static readonly string[] ColumnNames =
        ["batch_id", "row_number", "source_no", "source_row_number", "row_json"];

    private static readonly Type[] ColumnTypes =
        [typeof(string), typeof(long), typeof(int), typeof(int), typeof(string)];

    private readonly object[] _current = new object[ColumnNames.Length];

    public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
    {
        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) // producer 故障於此 rethrow
        {
            if (reader.TryRead(out var record))
            {
                _current[0] = batchId;
                _current[1] = record.RowNumber;
                _current[2] = sourceNo;
                _current[3] = record.SourceRowNumber;
                _current[4] = record.RowJson;
                return true;
            }
        }

        return false;
    }

    public override bool Read() =>
        throw new NotSupportedException("StagingBulkCopyDataReader 僅支援 async bulk copy(WriteToServerAsync → ReadAsync)。");

    // ---- SqlBulkCopy(EnableStreaming)實際會用到的成員(對齊 GlProjectionDataReader) ----

    public override int FieldCount => ColumnNames.Length;
    public override object GetValue(int ordinal) => _current[ordinal];
    public override bool IsDBNull(int ordinal) => _current[ordinal] is DBNull;
    public override string GetName(int ordinal) => ColumnNames[ordinal];
    public override Type GetFieldType(int ordinal) => ColumnTypes[ordinal];
    public override string GetDataTypeName(int ordinal) => ColumnTypes[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        var index = Array.IndexOf(ColumnNames, name);
        if (index < 0)
        {
            throw new IndexOutOfRangeException($"未知欄位 '{name}'。");
        }

        return index;
    }

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, _current.Length);
        Array.Copy(_current, values, count);
        return count;
    }

    // ---- 型別 getter:統一走 _current 轉型(SqlBulkCopy 主要用 GetValue) ----

    public override bool GetBoolean(int ordinal) => Convert.ToBoolean(_current[ordinal]);
    public override byte GetByte(int ordinal) => Convert.ToByte(_current[ordinal]);
    public override char GetChar(int ordinal) => Convert.ToChar(_current[ordinal]);
    public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(_current[ordinal]);
    public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(_current[ordinal]);
    public override double GetDouble(int ordinal) => Convert.ToDouble(_current[ordinal]);
    public override float GetFloat(int ordinal) => Convert.ToSingle(_current[ordinal]);
    public override Guid GetGuid(int ordinal) => (Guid)_current[ordinal];
    public override short GetInt16(int ordinal) => Convert.ToInt16(_current[ordinal]);
    public override int GetInt32(int ordinal) => Convert.ToInt32(_current[ordinal]);
    public override long GetInt64(int ordinal) => Convert.ToInt64(_current[ordinal]);
    public override string GetString(int ordinal) => (string)_current[ordinal];

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) =>
        throw new NotSupportedException();

    // ---- 其餘 DbDataReader 介面 ----

    public override object this[int ordinal] => _current[ordinal];
    public override object this[string name] => _current[GetOrdinal(name)];
    public override int Depth => 0;
    public override bool HasRows => true;
    public override bool IsClosed => false;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
