#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Outcome,

    [string] $Seconds = '1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$parsedSeconds = 0
if (-not [int]::TryParse($Seconds, [ref] $parsedSeconds) -or $parsedSeconds -lt 1 -or $parsedSeconds -gt 30) {
    [Console]::Error.WriteLine('Seconds must be between 1 and 30.')
    exit 64
}

switch -CaseSensitive ($Outcome) {
    'Success' {
        [Console]::Out.WriteLine('foundation-probe:stdout')
        [Console]::Out.WriteLine("foundation-probe:repository-path:$((Get-Location).Path)")
        [Console]::Error.WriteLine('foundation-probe:stderr')
        exit 0
    }
    'Failure' {
        [Console]::Out.WriteLine('foundation-probe:before-failure')
        [Console]::Error.WriteLine('foundation-probe:intentional-failure')
        exit 23
    }
    'Sleep' {
        [Console]::Out.WriteLine('foundation-probe:sleeping')
        Start-Sleep -Seconds $parsedSeconds
        [Console]::Out.WriteLine('foundation-probe:awake')
        exit 0
    }
    'OutputLimit' {
        $chunk = 'x' * 4096
        for ($index = 0; $index -lt 100; $index++) {
            [Console]::Out.WriteLine($chunk)
            [Console]::Error.WriteLine($chunk)
        }
        exit 0
    }
    'NuGetUnavailable' {
        [Console]::Out.WriteLine('error NU1301: synthetic service index is unavailable')
        exit 1
    }
    'SensitiveOutput' {
        [Console]::Out.WriteLine('foundation-probe:JET-HARNESS-CONTRACT-SECRET-20260828')
        [Console]::Error.WriteLine('foundation-probe:JET-HARNESS-CONTRACT-SECRET-20260828')
        exit 0
    }
    default {
        [Console]::Error.WriteLine("Unknown outcome: $Outcome")
        exit 64
    }
}
