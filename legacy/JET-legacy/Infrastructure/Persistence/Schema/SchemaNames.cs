using JET.Domain.Abstractions.Persistence;

namespace JET.Infrastructure.Persistence.Schema
{
    /// <summary>
    /// SQLite uses underscore-joined names within a single schema/file (no schema prefix).
    /// </summary>
    public sealed class SqliteSchemaNames : ISchemaNames
    {
        private static readonly IReadOnlyDictionary<JetTable, string> Map = new Dictionary<JetTable, string>
        {
            [JetTable.ConfigProject] = "config_project",
            [JetTable.ConfigProjectState] = "config_project_state",
            [JetTable.ConfigImportBatch] = "config_import_batch",
            [JetTable.ConfigImportColumn] = "config_import_column",
            [JetTable.StagingGlRawRow] = "staging_gl_raw_row",
            [JetTable.StagingTbRawRow] = "staging_tb_raw_row",
            [JetTable.StagingAccountMappingRawRow] = "staging_account_mapping_raw_row",
            [JetTable.StagingCalendarRawDay] = "staging_calendar_raw_day",
            [JetTable.TargetGlEntry] = "target_gl_entry",
            [JetTable.TargetTbBalance] = "target_tb_balance",
            [JetTable.ResultValidationV1] = "result_validation_v1",
            [JetTable.ResultValidationV2] = "result_validation_v2",
            [JetTable.ResultValidationV3] = "result_validation_v3",
            [JetTable.ResultValidationV4] = "result_validation_v4",
            [JetTable.ResultPrescreenR1] = "result_prescreen_r1",
            [JetTable.ResultPrescreenR2] = "result_prescreen_r2",
            [JetTable.ResultPrescreenR3] = "result_prescreen_r3",
            [JetTable.ResultPrescreenR4] = "result_prescreen_r4",
            [JetTable.ResultPrescreenR5] = "result_prescreen_r5",
            [JetTable.ResultPrescreenR6] = "result_prescreen_r6",
            [JetTable.ResultPrescreenR7] = "result_prescreen_r7",
            [JetTable.ResultPrescreenR8] = "result_prescreen_r8",
            [JetTable.ResultPrescreenA2] = "result_prescreen_a2",
            [JetTable.ResultPrescreenA3] = "result_prescreen_a3",
            [JetTable.ResultPrescreenA4] = "result_prescreen_a4",
            [JetTable.ResultFilterRun] = "result_filter_run",
        };

        public string Resolve(JetTable table) => Map[table];
    }

    /// <summary>
    /// SQL Server uses dotted schema-qualified names (<c>config.project</c>,
    /// <c>staging.gl_raw_row</c>). Concrete DDL is reserved for the SQL Server provider.
    /// </summary>
    public sealed class SqlServerSchemaNames : ISchemaNames
    {
        private static readonly IReadOnlyDictionary<JetTable, string> Map = new Dictionary<JetTable, string>
        {
            [JetTable.ConfigProject] = "config.project",
            [JetTable.ConfigProjectState] = "config.project_state",
            [JetTable.ConfigImportBatch] = "config.import_batch",
            [JetTable.ConfigImportColumn] = "config.import_column",
            [JetTable.StagingGlRawRow] = "staging.gl_raw_row",
            [JetTable.StagingTbRawRow] = "staging.tb_raw_row",
            [JetTable.StagingAccountMappingRawRow] = "staging.account_mapping_raw_row",
            [JetTable.StagingCalendarRawDay] = "staging.calendar_raw_day",
            [JetTable.TargetGlEntry] = "target.gl_entry",
            [JetTable.TargetTbBalance] = "target.tb_balance",
            [JetTable.ResultValidationV1] = "result.validation_v1",
            [JetTable.ResultValidationV2] = "result.validation_v2",
            [JetTable.ResultValidationV3] = "result.validation_v3",
            [JetTable.ResultValidationV4] = "result.validation_v4",
            [JetTable.ResultPrescreenR1] = "result.prescreen_r1",
            [JetTable.ResultPrescreenR2] = "result.prescreen_r2",
            [JetTable.ResultPrescreenR3] = "result.prescreen_r3",
            [JetTable.ResultPrescreenR4] = "result.prescreen_r4",
            [JetTable.ResultPrescreenR5] = "result.prescreen_r5",
            [JetTable.ResultPrescreenR6] = "result.prescreen_r6",
            [JetTable.ResultPrescreenR7] = "result.prescreen_r7",
            [JetTable.ResultPrescreenR8] = "result.prescreen_r8",
            [JetTable.ResultPrescreenA2] = "result.prescreen_a2",
            [JetTable.ResultPrescreenA3] = "result.prescreen_a3",
            [JetTable.ResultPrescreenA4] = "result.prescreen_a4",
            [JetTable.ResultFilterRun] = "result.filter_run",
        };

        public string Resolve(JetTable table) => Map[table];
    }
}
