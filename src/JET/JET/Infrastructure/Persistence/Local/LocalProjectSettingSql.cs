using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using JET.AuditCore;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// project_setting 這一列的讀寫語句。建案、改期末財報準備日與改每週休息日都在呼叫端自己的交易裡執行，
/// 所以這裡只收連線與交易，不自己開交易。建案後不能改的欄位由資料表的觸發器擋下修改，
/// 這裡的更新語句也只列出可以改的欄位：SQLite 的 UPDATE OF 觸發器只看 SET 列了哪些欄位，不看值有沒有變。
/// </summary>
internal static class LocalProjectSettingSql
{
    internal static async Task InsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        ProtectedProjectSettings settings,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO project_setting (
                singleton, period_start, period_end, money_scale, roc_date_enabled,
                sample_seed, sample_seed_version, operator_id, last_accounting_period_date, non_working_days)
            VALUES (1, @periodStart, @periodEnd, @moneyScale, @rocDateEnabled,
                @sampleSeed, @sampleSeedVersion, @operatorId, @lastAccountingPeriodDate, @nonWorkingDays);
            """;
        command.AddWithValue("@periodStart", settings.PeriodStart);
        command.AddWithValue("@periodEnd", settings.PeriodEnd);
        command.AddWithValue("@moneyScale", settings.MoneyScale);
        command.AddWithValue("@rocDateEnabled", settings.RocDateEnabled ? 1 : 0);
        command.AddWithValue("@sampleSeed", settings.SampleSeed);
        command.AddWithValue("@sampleSeedVersion", settings.SampleSeedVersion);
        command.AddWithValue("@operatorId", settings.OperatorId);
        command.AddWithValue("@lastAccountingPeriodDate", settings.LastAccountingPeriodDate);
        command.AddWithValue("@nonWorkingDays", SerializeDays(settings.NonWorkingDays));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static Task UpdatePreparationDateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string? preparationDate,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            connection,
            transaction,
            "UPDATE project_setting SET last_accounting_period_date = @value WHERE singleton = 1;",
            preparationDate,
            cancellationToken);

    internal static Task UpdateNonWorkingDaysAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<int> nonWorkingDays,
        CancellationToken cancellationToken) =>
        UpdateAsync(
            connection,
            transaction,
            "UPDATE project_setting SET non_working_days = @value WHERE singleton = 1;",
            SerializeDays(nonWorkingDays),
            cancellationToken);

    /// <summary>
    /// 讀這一列並檢查每個值都合法：查核期間是 yyyy-MM-dd 而且起日不晚於迄日、金額存放倍率是正數、
    /// 種子與演算法版本合法、準備日是 yyyy-MM-dd、休息日是 0 到 6 的整數。沒有這一列或值不合法時回資料庫損壞。
    /// </summary>
    internal static async Task<ProtectedProjectSettings> ReadAsync(
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT period_start, period_end, money_scale, roc_date_enabled, sample_seed, sample_seed_version,
                   operator_id, last_accounting_period_date, non_working_days
            FROM project_setting
            WHERE singleton = 1;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw LocalEngineErrors.DatabaseCorrupt();
        }

        var settings = LocalEngineErrors.ReadStoredValue(() => new ProtectedProjectSettings(
            reader.GetString(0),
            reader.GetString(1),
            LocalEngineErrors.ReadStoredInt32(reader, 2),
            LocalEngineErrors.ReadStoredInt32(reader, 3) switch
            {
                0 => false,
                1 => true,
                _ => throw new FormatException("Stored ROC date option is not 0 or 1.")
            },
            reader.GetFieldValue<long>(4),
            LocalEngineErrors.ReadStoredInt32(reader, 5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : ParseDays(reader.GetString(8))));
        if (!IsValid(settings))
        {
            throw LocalEngineErrors.DatabaseCorrupt();
        }

        return settings;
    }

    private static async Task UpdateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object? value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.AddWithValue("@value", value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            // 建好的案件資料庫一定有這一列；沒有時交易在釋放時回復。
            throw LocalEngineErrors.DatabaseCorrupt();
        }
    }

    private static string? SerializeDays(IReadOnlyList<int>? days) =>
        days is null ? null : JsonSerializer.Serialize(days);

    private static IReadOnlyList<int> ParseDays(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Stored non-working days are not an array.");
        }

        return document.RootElement.EnumerateArray().Select(day => day.GetInt32()).ToArray();
    }

    private static bool IsValid(ProtectedProjectSettings settings) =>
        TryParseDate(settings.PeriodStart, out var start)
        && TryParseDate(settings.PeriodEnd, out var end)
        && start <= end
        && settings.MoneyScale > 0
        && JetAuditProgram.ResolveInfSamplingSeed(settings.SampleSeed, settings.SampleSeedVersion).IsValid
        && (settings.LastAccountingPeriodDate is null || TryParseDate(settings.LastAccountingPeriodDate, out _))
        && (settings.NonWorkingDays is null
            || settings.NonWorkingDays.All(day => day is >= NonWorkingDays.MinDay and <= NonWorkingDays.MaxDay));

    private static bool TryParseDate(string value, out DateOnly date) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
}
