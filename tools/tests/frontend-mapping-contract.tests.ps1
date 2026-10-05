#requires -Version 7.4
[CmdletBinding()]
param([string] $RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))
$ErrorActionPreference = 'Stop'
$node = Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
& $node --test (Join-Path $RepositoryRoot 'tools/tests/frontend-mapping.test.cjs') (Join-Path $RepositoryRoot 'tools/tests/frontend-workflow.test.cjs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'public-frontend-contract.tests.ps1') -RepositoryRoot $RepositoryRoot
exit 0
