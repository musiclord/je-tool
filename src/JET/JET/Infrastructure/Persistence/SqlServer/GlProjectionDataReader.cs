using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;
using Microsoft.Data.SqlClient;

namespace JET.Infrastructure;

/// <summary>
/// 串流投影 reader:包住 staging 的 <see cref="DbDataReader"/>,逐列 deserialize row_json →
/// <see cref="GlRowProjector.TryProject"/>。成功列以 target_gl_entry 的 19 欄(不含 IDENTITY 的
/// entry_id)曝給 <see cref="SqlBulkCopy"/>；失敗列完整計數並彙總，只保存有界值與列號樣本。
/// 語意對齊 LocalGlRepository：一旦出現錯誤即停止產出列，仍掃描其餘來源，最終整批 rollback。
/// </summary>
internal sealed class GlProjectionDataReader : DbDataReader
{
    private readonly ProjectionErrorCollector errorCollector = new();

    public static readonly string[] ColumnNames =
    [
        "batch_id", "source_row_number",
        "document_number", "line_item", "post_date", "approval_date", "voucher_date",
        "account_code", "account_name", "document_description",
        "source_module", "created_by", "approved_by", "is_manual",
        "amount_scaled", "debit_amount_scaled", "credit_amount_scaled", "dr_cr",
        "line_item_numeric_sort_key", "posting_status", "is_effective", "exclusion_reason"
    ];

    private static readonly Type[] ColumnTypes =
    [
        typeof(string), typeof(long),
        typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string),
        typeof(string), typeof(string), typeof(string), typeof(int),
        typeof(long), typeof(long), typeof(long), typeof(string), typeof(string),
        typeof(string), typeof(bool), typeof(string)
    ];

    private readonly DbDataReader _staging;
    private readonly string _batchId;
    private readonly GlMappingSpec _spec;
    private readonly int _moneyScale;
    private readonly DateParseOptions _dateOptions;
    private readonly DateOnly _periodStart;
    private readonly DateOnly _periodEnd;
    private readonly bool _postingStatusMapped;
    private readonly GlEffectivePopulationMatcher? _postingStatusMatcher;
    private readonly IReadOnlyDictionary<int, string>? _sourceLabels;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationToken _cancellationToken;
    private readonly Action<ProjectionProgress>? _progress;
    private readonly int _progressRowInterval;
    private readonly object[] _current = new object[ColumnNames.Length];

    public GlProjectionDataReader(
        DbDataReader staging,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        IReadOnlyDictionary<int, string>? sourceLabels,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null,
        int progressRowInterval = 20_000)
        : this(
            staging,
            batchId,
            spec,
            moneyScale,
            dateOptions,
            DateOnly.MinValue,
            DateOnly.MaxValue,
            postingStatusMapped: false,
            postingStatusPolicy: null,
            sourceLabels,
            jsonOptions,
            cancellationToken,
            progress,
            progressRowInterval)
    {
    }

    public GlProjectionDataReader(
        DbDataReader staging,
        string batchId,
        GlMappingSpec spec,
        int moneyScale,
        DateParseOptions dateOptions,
        DateOnly periodStart,
        DateOnly periodEnd,
        bool postingStatusMapped,
        GlPostingStatusPolicy? postingStatusPolicy,
        IReadOnlyDictionary<int, string>? sourceLabels,
        JsonSerializerOptions jsonOptions,
        CancellationToken cancellationToken,
        Action<ProjectionProgress>? progress = null,
        int progressRowInterval = 20_000)
    {
        _staging = staging;
        _batchId = batchId;
        _spec = spec;
        _moneyScale = moneyScale;
        _dateOptions = dateOptions;
        _periodStart = periodStart;
        _periodEnd = periodEnd;
        _postingStatusMapped = postingStatusMapped;
        _postingStatusMatcher = postingStatusPolicy is null
            ? null
            : new GlEffectivePopulationMatcher(postingStatusPolicy);
        _sourceLabels = sourceLabels;
        _jsonOptions = jsonOptions;
        _cancellationToken = cancellationToken;
        _progress = progress;
        _progressRowInterval = progressRowInterval;
        ManualAutoCodes = new ManualAutoListedCodeAudit(spec);
    }

    public IReadOnlyList<RowProjectionError> Errors => errorCollector.Samples;

    public int TotalErrorCount => errorCollector.TotalErrorCount;

    internal ProjectionResult FailedResult() => errorCollector.FailedResult();

    public int ValidRowCount { get; private set; }

    // V3：只列一側的人工/自動清單代碼有沒有出現在來源裡（與 SQLite、DuckDB 同一份判斷）。
    public ManualAutoListedCodeAudit ManualAutoCodes { get; }

    // 完整性測試的匯入控制總數累計（與 SQLite 逐列累計等價;SqlBulkCopy 串流時於 Read 內累加）。
    public long SourceRowCount { get; private set; }

    public long TotalDebitScaled { get; private set; }

    public long TotalCreditScaled { get; private set; }

    public long EffectiveRowCount { get; private set; }

    public long ExcludedByPeriodCount { get; private set; }

    public long ExcludedByPostingStatusCount { get; private set; }

    public long EffectiveDebitScaled { get; private set; }

    public long EffectiveCreditScaled { get; private set; }

    public override bool Read()
    {
        while (_staging.Read())
        {
            _cancellationToken.ThrowIfCancellationRequested();
            SourceRowCount++;
            if (SourceRowCount % _progressRowInterval == 0)
            {
                _progress?.Invoke(new ProjectionProgress(SourceRowCount));
            }

            var rowNumber = _staging.GetInt64(0);
            var sourceNo = _staging.GetInt32(1);
            var sourceRowNumber = _staging.GetInt32(2);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(_staging.GetString(3), _jsonOptions)
                ?? [];

            var stagingRow = new StagingRow(sourceRowNumber, values);

            if (!GlRowProjector.TryProject(
                    stagingRow,
                    _spec,
                    _moneyScale,
                    _dateOptions,
                    out var projected,
                    out var error,
                    _cancellationToken,
                    collectRdeValues: false))
            {
                errorCollector.Observe(error! with { SourceLabel = _sourceLabels?.GetValueOrDefault(sourceNo) });

                continue; // 續掃以蒐集多筆錯誤,最終整批 rollback
            }

            if (TotalErrorCount > 0)
            {
                continue; // 已確定失敗,不再產出列(與 SQLite 一致)
            }

            ManualAutoCodes.Observe(stagingRow);

            var postDate = projected!.PostDate is null
                ? (DateOnly?)null
                : DateOnly.ParseExact(
                    projected.PostDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
            var classification = GlEffectivePopulation.ClassifyWithMatcher(
                postDate,
                projected.PostingStatus,
                _periodStart,
                _periodEnd,
                _postingStatusMapped,
                _postingStatusMatcher);

            long nextTotalDebit;
            long nextTotalCredit;
            var nextEffectiveDebit = EffectiveDebitScaled;
            var nextEffectiveCredit = EffectiveCreditScaled;
            try
            {
                nextTotalDebit = checked(TotalDebitScaled + projected.DebitAmountScaled);
                nextTotalCredit = checked(TotalCreditScaled + projected.CreditAmountScaled);
                if (classification.IsEffective)
                {
                    nextEffectiveDebit = checked(EffectiveDebitScaled + projected.DebitAmountScaled);
                    nextEffectiveCredit = checked(EffectiveCreditScaled + projected.CreditAmountScaled);
                }
            }
            catch (OverflowException)
            {
                errorCollector.Observe(GlRowProjector.CreateControlTotalOverflowError(
                    stagingRow,
                    _spec,
                    projected.AmountScaled) with
                {
                    SourceLabel = _sourceLabels?.GetValueOrDefault(sourceNo)
                });

                continue;
            }

            FillCurrent(rowNumber, projected, classification);
            ValidRowCount++;
            TotalDebitScaled = nextTotalDebit;
            TotalCreditScaled = nextTotalCredit;
            switch (classification.Disposition)
            {
                case GlEffectivePopulationDisposition.Effective:
                    EffectiveRowCount++;
                    EffectiveDebitScaled = nextEffectiveDebit;
                    EffectiveCreditScaled = nextEffectiveCredit;
                    break;
                case GlEffectivePopulationDisposition.ExcludedByPeriod:
                    ExcludedByPeriodCount++;
                    break;
                case GlEffectivePopulationDisposition.ExcludedByPostingStatus:
                    ExcludedByPostingStatusCount++;
                    break;
                default:
                    throw new InvalidOperationException("未知的 GL 有效母體分類。");
            }
            return true;
        }

        return false;
    }

    public void ReportFinalProgress()
    {
        if (SourceRowCount > 0 && SourceRowCount % _progressRowInterval != 0)
        {
            _progress?.Invoke(new ProjectionProgress(SourceRowCount));
        }
    }

    private void FillCurrent(
        long rowNumber,
        GlProjectedRow p,
        GlEffectivePopulationClassification classification)
    {
        // target 的 source_row_number 存批次排序鍵(== staging row_number;INF 抽樣基礎)。
        _current[0] = _batchId;
        _current[1] = rowNumber;
        _current[2] = (object?)p.DocumentNumber ?? DBNull.Value;
        _current[3] = (object?)p.LineItem ?? DBNull.Value;
        _current[4] = (object?)p.PostDate ?? DBNull.Value;
        _current[5] = (object?)p.ApprovalDate ?? DBNull.Value;
        _current[6] = (object?)p.VoucherDate ?? DBNull.Value;
        _current[7] = (object?)p.AccountCode ?? DBNull.Value;
        _current[8] = (object?)p.AccountName ?? DBNull.Value;
        _current[9] = (object?)p.DocumentDescription ?? DBNull.Value;
        _current[10] = (object?)p.SourceModule ?? DBNull.Value;
        _current[11] = (object?)p.CreatedBy ?? DBNull.Value;
        _current[12] = (object?)p.ApprovedBy ?? DBNull.Value;
        _current[13] = p.IsManual is null ? DBNull.Value : p.IsManual.Value ? 1 : 0;
        _current[14] = p.AmountScaled;
        _current[15] = p.DebitAmountScaled;
        _current[16] = p.CreditAmountScaled;
        _current[17] = p.DrCr;
        _current[18] = (object?)LineItemNumericSortKey.CreateOrNull(p.LineItem) ?? DBNull.Value;
        _current[19] = (object?)p.PostingStatus ?? DBNull.Value;
        _current[20] = classification.IsEffective;
        _current[21] = (object?)classification.StorageReason ?? DBNull.Value;
    }

    // ---- SqlBulkCopy(EnableStreaming)實際會用到的成員 ----

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
    public override bool IsClosed => _staging.IsClosed;
    public override int RecordsAffected => -1;
    public override bool NextResult() => false;
    public override IEnumerator GetEnumerator() => throw new NotSupportedException();
}
