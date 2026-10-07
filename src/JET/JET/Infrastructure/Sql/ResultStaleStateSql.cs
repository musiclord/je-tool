using System.Data.Common;
using JET.Domain;

namespace JET.Infrastructure;

/// <summary>
/// 衍生結果與 schema v7 stale singleton 的同交易寫入點。呼叫端提供的 schema prefix
/// 只來自固定本地空字串或經驗證的 SQL Server project schema。
/// </summary>
internal static class ResultStaleStateSql
{
    internal static async Task MarkInvalidatedWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditDependencyImpact impact,
        CancellationToken cancellationToken,
        string schemaPrefix)
    {
        var statements = new List<string>(3);

        if (impact.InvalidateValidation)
        {
            statements.Add(
                $"""
                UPDATE {schemaPrefix}config_result_stale_state
                SET validation_stale = 1
                WHERE singleton = 1
                  AND (
                      EXISTS (
                          SELECT 1
                          FROM {schemaPrefix}result_rule_run
                      WHERE run_kind = 'validate'));
                """);
        }

        if (impact.InvalidatePrescreen)
        {
            statements.Add(
                $"""
                UPDATE {schemaPrefix}config_result_stale_state
                SET prescreen_stale = 1
                WHERE singleton = 1
                  AND EXISTS (
                      SELECT 1
                      FROM {schemaPrefix}result_rule_run
                      WHERE run_kind = 'prescreen');
                """);
        }

        if (impact.InvalidateFilterScenarioDefinitions)
        {
            // 同一交易會刪掉全部情境與命中，沒有可重跑的篩選；回到「從未執行」，與 filter.commit 空清單一致。
            statements.Add($"UPDATE {schemaPrefix}config_result_stale_state SET filter_stale = 0 WHERE singleton = 1;");
        }
        else if (impact.InvalidateFilterHits)
        {
            statements.Add(FilterInvalidationStatement(schemaPrefix));
        }

        await ExecuteWithinAsync(
            connection,
            transaction,
            statements,
            cancellationToken);
    }

    internal static Task ClearRuleRunWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        string runKind,
        CancellationToken cancellationToken,
        string schemaPrefix) =>
        runKind switch
        {
            RuleRunKinds.Validate => ClearWithinAsync(
                connection,
                transaction,
                "validation_stale",
                cancellationToken,
                schemaPrefix),
            RuleRunKinds.Prescreen => ClearWithinAsync(
                connection,
                transaction,
                "prescreen_stale",
                cancellationToken,
                schemaPrefix),
            _ => Task.CompletedTask
        };

    internal static Task ClearFilterWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken,
        string schemaPrefix) =>
        ClearWithinAsync(
            connection,
            transaction,
            "filter_stale",
            cancellationToken,
            schemaPrefix);

    internal static Task MarkFilterInvalidatedWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken,
        string schemaPrefix) =>
        ExecuteWithinAsync(
            connection,
            transaction,
            [FilterInvalidationStatement(schemaPrefix)],
            cancellationToken);

    private static string FilterInvalidationStatement(string schemaPrefix) =>
        $"""
        UPDATE {schemaPrefix}config_result_stale_state
        SET filter_stale = 1
        WHERE singleton = 1
          AND (
              EXISTS (
                  SELECT 1
                  FROM {schemaPrefix}result_filter_run)
              OR EXISTS (
                  SELECT 1
                  FROM {schemaPrefix}config_filter_scenario));
        """;

    private static Task ClearWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        string column,
        CancellationToken cancellationToken,
        string schemaPrefix) =>
        ExecuteWithinAsync(
            connection,
            transaction,
            [$"UPDATE {schemaPrefix}config_result_stale_state SET {column} = 0 WHERE singleton = 1;"],
            cancellationToken);

    private static async Task ExecuteWithinAsync(
        DbConnection connection,
        DbTransaction transaction,
        IReadOnlyList<string> statements,
        CancellationToken cancellationToken)
    {
        if (statements.Count == 0)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
