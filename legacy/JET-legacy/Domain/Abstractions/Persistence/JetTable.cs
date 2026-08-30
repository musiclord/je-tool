namespace JET.Domain.Abstractions.Persistence
{
    /// <summary>
    /// Logical table identities used across the codebase. Physical names live in
    /// provider-specific <see cref="ISchemaNames"/> implementations.
    /// </summary>
    public enum JetTable
    {
        ConfigProject,
        ConfigProjectState,
        ConfigImportBatch,
        ConfigImportColumn,
        StagingGlRawRow,
        StagingTbRawRow,
        StagingAccountMappingRawRow,
        StagingCalendarRawDay,
        TargetGlEntry,
        TargetTbBalance,
        ResultValidationV1,
        ResultValidationV2,
        ResultValidationV3,
        ResultValidationV4,
        ResultPrescreenR1,
        ResultPrescreenR2,
        ResultPrescreenR3,
        ResultPrescreenR4,
        ResultPrescreenR5,
        ResultPrescreenR6,
        ResultPrescreenR7,
        ResultPrescreenR8,
        ResultPrescreenA2,
        ResultPrescreenA3,
        ResultPrescreenA4,
        ResultFilterRun
    }
}
