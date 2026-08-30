using Dapper;
using Microsoft.Data.Sqlite;
using JET.Domain.Abstractions.Persistence;
using JET.Infrastructure.Configuration;
using JET.Infrastructure.Persistence.Schema;

namespace JET.Infrastructure.Persistence.Sqlite
{
    /// <summary>
    /// Creates the JET physical schema in a SQLite database. Idempotent. Designed to run
    /// once at app startup; safe to call multiple times.
    /// </summary>
    /// <remarks>
    /// Tables created (Phase 2 + Phase 3 scope per <c>plan.md</c>):
    /// <list type="bullet">
    /// <item><c>config_project</c>, <c>config_project_state</c></item>
    /// <item><c>config_import_batch</c>, <c>config_import_column</c></item>
    /// <item><c>staging_gl_raw_row</c>, <c>staging_tb_raw_row</c>,
    /// <c>staging_account_mapping_raw_row</c></item>
    /// <item><c>staging_calendar_raw_day</c></item>
    /// </list>
    /// <item><c>target_gl_entry</c>, <c>target_tb_balance</c></item>
    /// <c>result_*</c> tables are deferred to a later round.
    /// </remarks>
    public sealed class SqliteSchemaInitializer : ISchemaInitializer
    {
        private readonly string _connectionString;
        private readonly ISchemaNames _names;

        public SqliteSchemaInitializer(DatabaseOptions databaseOptions)
            : this(databaseOptions.SqliteConnectionString, new SqliteSchemaNames())
        {
        }

        public SqliteSchemaInitializer(string connectionString, ISchemaNames names)
        {
            _connectionString = connectionString;
            _names = names;
        }

        public async Task<SchemaInitializationResult> EnsureAsync(CancellationToken cancellationToken)
        {
            EnsureDataDirectoryExists();

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            foreach (var ddl in BuildDdlStatements())
            {
                await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken));
            }

            var dataSource = new SqliteConnectionStringBuilder(_connectionString).DataSource;
            return new SchemaInitializationResult(true, $"SQLite schema ready at {dataSource}.");
        }

        private void EnsureDataDirectoryExists()
        {
            var builder = new SqliteConnectionStringBuilder(_connectionString);
            var dataSource = builder.DataSource;
            if (string.IsNullOrWhiteSpace(dataSource) || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var directory = Path.GetDirectoryName(dataSource);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
        }

        private IEnumerable<string> BuildDdlStatements()
        {
            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.ConfigProject)}
                (
                    project_id        TEXT NOT NULL PRIMARY KEY,
                    project_code      TEXT NOT NULL,
                    entity_name       TEXT NOT NULL,
                    operator_id       TEXT NOT NULL,
                    industry          TEXT NOT NULL,
                    period_start      TEXT NOT NULL,
                    period_end        TEXT NOT NULL,
                    last_period_start TEXT NOT NULL,
                    created_utc       TEXT NOT NULL
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.ConfigProjectState)}
                (
                    project_id   TEXT NOT NULL PRIMARY KEY,
                    current_step INTEGER NOT NULL,
                    updated_utc  TEXT NOT NULL
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.ConfigImportBatch)}
                (
                    batch_id     TEXT NOT NULL PRIMARY KEY,
                    project_id   TEXT NOT NULL,
                    dataset_kind TEXT NOT NULL,
                    file_name    TEXT NOT NULL,
                    row_count    INTEGER NOT NULL,
                    imported_utc TEXT NOT NULL
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.ConfigImportColumn)}
                (
                    batch_id     TEXT NOT NULL,
                    column_index INTEGER NOT NULL,
                    column_name  TEXT NOT NULL,
                    PRIMARY KEY (batch_id, column_index)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.StagingGlRawRow)}
                (
                    batch_id  TEXT NOT NULL,
                    row_index INTEGER NOT NULL,
                    payload   TEXT NOT NULL,
                    PRIMARY KEY (batch_id, row_index)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.StagingTbRawRow)}
                (
                    batch_id  TEXT NOT NULL,
                    row_index INTEGER NOT NULL,
                    payload   TEXT NOT NULL,
                    PRIMARY KEY (batch_id, row_index)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.StagingAccountMappingRawRow)}
                (
                    batch_id  TEXT NOT NULL,
                    row_index INTEGER NOT NULL,
                    payload   TEXT NOT NULL,
                    PRIMARY KEY (batch_id, row_index)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.StagingCalendarRawDay)}
                (
                    batch_id TEXT NOT NULL,
                    date_iso TEXT NOT NULL,
                    kind     TEXT NOT NULL,
                    PRIMARY KEY (batch_id, date_iso, kind)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.TargetGlEntry)}
                (
                    project_id  TEXT NOT NULL,
                    batch_id    TEXT NOT NULL,
                    doc_num     TEXT NOT NULL,
                    line_id     TEXT NOT NULL,
                    post_date   TEXT NULL,
                    doc_date    TEXT NULL,
                    acc_num     TEXT NULL,
                    acc_name    TEXT NULL,
                    description TEXT NULL,
                    je_source   TEXT NULL,
                    create_by   TEXT NULL,
                    approve_by  TEXT NULL,
                    manual      INTEGER NULL,
                    dr_amount   REAL NOT NULL DEFAULT 0,
                    cr_amount   REAL NOT NULL DEFAULT 0,
                    amount      REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (project_id, batch_id, doc_num, line_id)
                );
                """;

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.TargetTbBalance)}
                (
                    project_id       TEXT NOT NULL,
                    batch_id         TEXT NOT NULL,
                    acc_num          TEXT NOT NULL,
                    acc_name         TEXT NULL,
                    change_amount    REAL NOT NULL DEFAULT 0,
                    opening_balance  REAL NULL,
                    closing_balance  REAL NULL,
                    PRIMARY KEY (project_id, batch_id, acc_num)
                );
                """;

            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_project_doc_amount ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, doc_num, amount);";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_project_acc_num ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, acc_num);";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_project_doc_date ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, doc_date, post_date);";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_project_post_date ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, post_date);";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_missing_acc_num ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, batch_id, doc_num, line_id, acc_num) WHERE acc_num IS NULL OR TRIM(acc_num) = '';";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_missing_doc_num ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, batch_id, doc_num, line_id, acc_num) WHERE doc_num IS NULL OR TRIM(doc_num) = '';";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_gl_entry_missing_description ON {_names.Resolve(JetTable.TargetGlEntry)} (project_id, batch_id, doc_num, line_id, acc_num) WHERE description IS NULL OR TRIM(description) = '';";
            yield return $"CREATE INDEX IF NOT EXISTS ix_target_tb_balance_project_acc_num ON {_names.Resolve(JetTable.TargetTbBalance)} (project_id, acc_num);";

            foreach (var table in new[]
            {
                JetTable.ResultValidationV1,
                JetTable.ResultValidationV2,
                JetTable.ResultValidationV3,
                JetTable.ResultValidationV4,
            })
            {
                yield return $"""
                    CREATE TABLE IF NOT EXISTS {_names.Resolve(table)}
                    (
                        project_id TEXT NOT NULL,
                        run_id     TEXT NOT NULL,
                        row_no     INTEGER NOT NULL,
                        batch_id   TEXT NOT NULL,
                        doc_num    TEXT NULL,
                        line_id    TEXT NULL,
                        acc_num    TEXT NULL,
                        reason     TEXT NOT NULL,
                        PRIMARY KEY (project_id, run_id, row_no)
                    );
                    """;
            }

            foreach (var table in new[]
            {
                JetTable.ResultPrescreenR1,
                JetTable.ResultPrescreenR2,
                JetTable.ResultPrescreenR3,
                JetTable.ResultPrescreenR4,
                JetTable.ResultPrescreenR5,
                JetTable.ResultPrescreenR6,
                JetTable.ResultPrescreenR7,
                JetTable.ResultPrescreenR8,
                JetTable.ResultPrescreenA2,
                JetTable.ResultPrescreenA3,
                JetTable.ResultPrescreenA4,
            })
            {
                yield return $"""
                    CREATE TABLE IF NOT EXISTS {_names.Resolve(table)}
                    (
                        project_id TEXT NOT NULL,
                        run_id     TEXT NOT NULL,
                        row_no     INTEGER NOT NULL,
                        batch_id   TEXT NOT NULL,
                        doc_num    TEXT NULL,
                        line_id    TEXT NULL,
                        acc_num    TEXT NULL,
                        reason     TEXT NOT NULL,
                        PRIMARY KEY (project_id, run_id, row_no)
                    );
                    """;
            }

            yield return $"""
                CREATE TABLE IF NOT EXISTS {_names.Resolve(JetTable.ResultFilterRun)}
                (
                    project_id      TEXT NOT NULL,
                    run_id          TEXT NOT NULL,
                    row_no          INTEGER NOT NULL,
                    scenario_label  TEXT NOT NULL,
                    batch_id        TEXT NOT NULL,
                    doc_num         TEXT NULL,
                    line_id         TEXT NULL,
                    post_date       TEXT NULL,
                    doc_date        TEXT NULL,
                    acc_num         TEXT NULL,
                    acc_name        TEXT NULL,
                    description     TEXT NULL,
                    amount          REAL NOT NULL DEFAULT 0,
                    PRIMARY KEY (project_id, run_id, row_no)
                );
                """;
        }
    }
}
