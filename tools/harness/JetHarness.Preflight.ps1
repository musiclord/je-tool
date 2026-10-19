using namespace System.IO

# 由 JetHarness.psm1 點號載入。執行前的檢查：SQL Server 連線設定與私人案件資料。

function Get-JetProviderConnectionPreflight {
    param([Parameter(Mandatory = $true)] $Registry)

    $variableName = [string]$Registry.testSettings.providerConnectionEnvironmentVariable
    $connectionString = [Environment]::GetEnvironmentVariable(
        $variableName,
        [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($connectionString)) {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'blocked' `
                -Reason 'sqlserver_connection_not_configured' `
                -Evidence ([ordered]@{ variable = $variableName; configured = $false })
            SensitiveValues = @()
        }
    }

    try {
        $builder = [Data.Common.DbConnectionStringBuilder]::new()
        $builder.ConnectionString = $connectionString
        if ($builder.Count -eq 0) {
            throw [FormatException]::new('Connection string has no entries.')
        }

        $redactions = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        [void]$redactions.Add($connectionString)
        if (-not [string]::IsNullOrWhiteSpace($builder.ConnectionString)) {
            [void]$redactions.Add($builder.ConnectionString)
        }
        foreach ($keyObject in $builder.Keys) {
            $key = [string]$keyObject
            $value = [string]$builder[$keyObject]
            if ([string]::IsNullOrWhiteSpace($value)) {
                continue
            }
            [void]$redactions.Add("$key=$value")
            [void]$redactions.Add("$key = $value")
            if ($value.Length -ge 4 -and $key -match '^(?i:data source|server|address|addr|network address|initial catalog|database|user id|uid|password|pwd|attachdbfilename|application name)$') {
                [void]$redactions.Add($value)
            }
        }

        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'passed' `
                -Reason $null `
                -Evidence ([ordered]@{ variable = $variableName; configured = $true; syntax = 'parsed' })
            SensitiveValues = @($redactions | Sort-Object)
        }
    }
    catch {
        return [pscustomobject]@{
            Step = New-JetSyntheticStep `
                -Name 'provider-connection-preflight' `
                -Status 'failed' `
                -Reason 'provider_connection_invalid' `
                -Evidence ([ordered]@{ variable = $variableName; configured = $true; syntax = 'invalid' })
            SensitiveValues = @($connectionString)
        }
    }
}

function Get-JetPrivateCasePreflight {
    param([Parameter(Mandatory = $true)] $Registry)

    $rootVariable = [string]$Registry.testSettings.privateCaseRootEnvironmentVariable
    $manifestVariable = [string]$Registry.testSettings.privateCaseManifestEnvironmentVariable
    $providerVariable = [string]$Registry.testSettings.privateCaseProviderEnvironmentVariable
    $connectionVariable = [string]$Registry.testSettings.providerConnectionEnvironmentVariable
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

    $provider = $providerValue.Trim().ToLowerInvariant()
    if ($provider -notin @('sqlite', 'duckdb', 'sqlserver')) {
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

    $connectionValue = $null
    if ($provider -ceq 'sqlserver') {
        $connectionPreflight = Get-JetProviderConnectionPreflight -Registry $Registry
        foreach ($value in @($connectionPreflight.SensitiveValues)) {
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                [void]$sensitiveValues.Add([string]$value)
            }
        }
        if ([string]$connectionPreflight.Step.status -cne 'passed') {
            return [pscustomobject]@{
                Step = New-JetSyntheticStep `
                    -Name 'private-case-preflight' `
                    -Status ([string]$connectionPreflight.Step.status) `
                    -Reason ([string]$connectionPreflight.Step.classification.reason) `
                    -Evidence ([ordered]@{
                        configured = $configured
                        provider = 'sqlserver'
                        connectionConfigured = $false
                        pathsRecorded = $false
                    })
                EnvironmentVariablesToSet = @{}
                SensitiveValues = @($sensitiveValues | Sort-Object)
                PathInspected = $false
                Provider = 'sqlserver'
            }
        }
        $connectionValue = [Environment]::GetEnvironmentVariable(
            $connectionVariable,
            [EnvironmentVariableTarget]::Process)
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
    if ($provider -ceq 'sqlserver') {
        $environmentVariables[$connectionVariable] = $connectionValue
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
