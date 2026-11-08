using namespace System.IO

# 由 JetHarness.psm1 點號載入。執行前的檢查：私人案件資料。
# 原本也檢查 SQL Server 的連線設定，隨 DuckDB 與 SQL Server 一起移除（裁定 2026-11-06-08），還原點是提交 7e88b7b。

function Get-JetPrivateCasePreflight {
    param([Parameter(Mandatory = $true)] $Registry)

    $rootVariable = [string]$Registry.testSettings.privateCaseRootEnvironmentVariable
    $manifestVariable = [string]$Registry.testSettings.privateCaseManifestEnvironmentVariable
    $providerVariable = [string]$Registry.testSettings.privateCaseProviderEnvironmentVariable
    $rootValue = [Environment]::GetEnvironmentVariable(
        $rootVariable,
        [EnvironmentVariableTarget]::Process)
    $manifestValue = [Environment]::GetEnvironmentVariable(
        $manifestVariable,
        [EnvironmentVariableTarget]::Process)
    $providerValue = [Environment]::GetEnvironmentVariable(
        $providerVariable,
        [EnvironmentVariableTarget]::Process)

    $sensitiveValues = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($value in @($rootValue, $manifestValue)) {
        if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
            [void]$sensitiveValues.Add([string]$value)
        }
    }

    $configured = [ordered]@{
        root = -not [string]::IsNullOrWhiteSpace($rootValue)
        manifest = -not [string]::IsNullOrWhiteSpace($manifestValue)
        provider = -not [string]::IsNullOrWhiteSpace($providerValue)
    }
    if (-not $configured.root -or -not $configured.manifest -or -not $configured.provider) {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'blocked' `
                -Reason 'private_case_input_not_configured' `
                -Evidence ([ordered]@{ configured = $configured; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $null
        }
    }

    # 目前只有 SQLite 案件（裁定 2026-11-06-08）；原本也接受 duckdb 與 sqlserver。
    $provider = $providerValue.Trim().ToLowerInvariant()
    if ($provider -cne 'sqlite') {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_provider_invalid' `
                -Evidence ([ordered]@{ configured = $configured; providerAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $null
        }
    }

    try {
        if (-not [IO.Path]::IsPathFullyQualified($rootValue)) {
            throw [ArgumentException]::new('PrivateCase root must be fully qualified.')
        }
        $rootFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($rootValue))
        [void]$sensitiveValues.Add($rootFull)
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_root_unavailable' `
                -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $provider
        }
    }

    try {
        if ([IO.Path]::IsPathRooted($manifestValue) -or
            $manifestValue.Length -gt 1024 -or
            $manifestValue -match '[\x00-\x1F]') {
            throw [ArgumentException]::new('PrivateCase manifest path is invalid.')
        }
        $manifestFull = [IO.Path]::GetFullPath($manifestValue, $rootFull)
        if (-not (Test-JetDescendantPath -Root $rootFull -Candidate $manifestFull)) {
            throw [ArgumentException]::new('PrivateCase manifest path must stay below the root.')
        }
        [void]$sensitiveValues.Add($manifestFull)
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_manifest_invalid' `
                -Evidence ([ordered]@{ configured = $configured; manifestAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $false
            Provider = $provider
        }
    }

    $pathInspected = $true
    try {
        $rootItem = Get-Item -LiteralPath $rootFull -Force -ErrorAction Stop
        if (-not $rootItem.PSIsContainer) {
            throw [DirectoryNotFoundException]::new('PrivateCase root is not a directory.')
        }
        if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            return [pscustomobject]@{
                Step = New-JetSyntheticStep `
                    -Name 'private-case-preflight' `
                    -Status 'failed' `
                    -Reason 'private_case_root_reparse' `
                    -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
                EnvironmentVariablesToSet = @{}
                SensitiveValues = @($sensitiveValues | Sort-Object)
                PathInspected = $pathInspected
                Provider = $provider
            }
        }
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'private-case-preflight' `
                -Status 'failed' `
                -Reason 'private_case_root_unavailable' `
                -Evidence ([ordered]@{ configured = $configured; rootAccepted = $false; pathsRecorded = $false })
            EnvironmentVariablesToSet = @{}
            SensitiveValues = @($sensitiveValues | Sort-Object)
            PathInspected = $pathInspected
            Provider = $provider
        }
    }

    $environmentVariables = @{
        $rootVariable = $rootFull
        $manifestVariable = $manifestValue
        $providerVariable = $provider
    }

    return [pscustomobject]@{
        Step = New-JetSyntheticStep `
            -Name 'private-case-preflight' `
            -Status 'passed' `
            -Reason $null `
            -Evidence ([ordered]@{
                configured = $configured
                provider = $provider
                rootAccepted = $true
                manifestPath = 'relative'
                pathsRecorded = $false
            })
        EnvironmentVariablesToSet = $environmentVariables
        SensitiveValues = @($sensitiveValues | Sort-Object)
        PathInspected = $pathInspected
        Provider = $provider
    }
}
