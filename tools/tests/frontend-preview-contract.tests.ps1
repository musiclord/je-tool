#requires -Version 7.4
[CmdletBinding()]
param([string] $RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))
$ErrorActionPreference = 'Stop'
$node = Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
& $node --test (Join-Path $PSScriptRoot 'frontend-preview.test.cjs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$base = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/harness/frontend-preview-contract'))
$owned = Join-Path $base ([Guid]::NewGuid().ToString('N'))
$source = Join-Path $owned 'source'
$candidate = Join-Path $owned 'candidate'
try {
    foreach ($directory in @("$source/wwwroot", "$source/Templates", "$candidate/wwwroot", "$candidate/frontend-preview")) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    [IO.File]::WriteAllText((Join-Path $source 'wwwroot/index.html'), '<html>synthetic</html>')
    [IO.File]::WriteAllText((Join-Path $source 'Templates/example.xlsx'), 'synthetic package boundary only')
    [IO.File]::WriteAllText((Join-Path $candidate 'wwwroot/fixtures.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $candidate 'frontend-preview/server.cjs'), '// synthetic')
    $registry = Join-Path $owned 'registry.json'
    [IO.File]::WriteAllText($registry, '{"packageSettings":{"templateRelativePaths":["Templates/example.xlsx"]}}')
    $manifest = Join-Path $owned 'manifest.json'
    & pwsh -NoProfile -File (Join-Path $RepositoryRoot 'tools/harness/JetPackageVerifier.ps1') `
        -Mode Verify -SourceRoot $source -CandidatePath $candidate -OwnedRoot $owned -RegistryPath $registry -ManifestPath $manifest
    if ($LASTEXITCODE -ne 1) { throw 'Contaminated preview package must be rejected.' }
    $result = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    $rejected = @($result.errors | Where-Object code -eq 'frontend_preview_forbidden' | Select-Object -ExpandProperty path)
    if ($rejected -notcontains 'wwwroot/fixtures.json' -or $rejected -notcontains 'frontend-preview/server.cjs') {
        throw 'Package verifier did not identify both preview contamination paths.'
    }
    Write-Output 'Preview package contamination: both synthetic paths rejected.'
} finally {
    if (Test-Path -LiteralPath $owned) {
        $resolved = (Resolve-Path -LiteralPath $owned).Path
        if (-not $resolved.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside the owned preview contract directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
exit 0
