#requires -Version 7.4
[CmdletBinding()]
param([string] $RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))
$ErrorActionPreference = 'Stop'
$node = Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
& $node --test (Join-Path $RepositoryRoot 'tools/tests/frontend-mapping.test.cjs')
exit $LASTEXITCODE
