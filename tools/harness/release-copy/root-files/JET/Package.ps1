#requires -Version 7.4

[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/packages')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stagingPath = Join-Path $temporaryRoot ('jet-package-' + [guid]::NewGuid().ToString('N'))
$publishPath = Join-Path $stagingPath 'publish'
$archiveName = 'JET-win-x64.zip'

try {
    [IO.Directory]::CreateDirectory($publishPath) | Out-Null
    & dotnet publish (Join-Path $PSScriptRoot 'JET/JET.csproj') --configuration Release `
        -p:PublishProfile=FolderProfile --output $publishPath
    if ($LASTEXITCODE -ne 0) {
        throw "JET publish failed with exit code $LASTEXITCODE."
    }

    $requiredPaths = @('JET.exe', 'wwwroot/index.html')
    $project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'JET/JET.csproj') -Raw)
    $templatePaths = @($project.SelectNodes('/Project/ItemGroup/Content') |
        ForEach-Object { $_.GetAttribute('Include') } |
        Where-Object { $_ -like 'Templates*' })
    $requiredPaths += $templatePaths -split ';'
    foreach ($relativePath in $requiredPaths) {
        if (!(Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
            throw "Published file is missing: $relativePath"
        }
    }

    $stagedArchive = Join-Path $stagingPath $archiveName
    [IO.Compression.ZipFile]::CreateFromDirectory($publishPath, $stagedArchive)
    [IO.Directory]::CreateDirectory($outputPath) | Out-Null
    $archivePath = Join-Path $outputPath $archiveName
    Move-Item -LiteralPath $stagedArchive -Destination $archivePath -Force
    Write-Output $archivePath
}
finally {
    $resolvedStagingPath = [IO.Path]::GetFullPath($stagingPath)
    if ([IO.Path]::GetDirectoryName($resolvedStagingPath).Equals(
            [IO.Path]::TrimEndingDirectorySeparator($temporaryRoot), [StringComparison]::OrdinalIgnoreCase) `
        -and [IO.Path]::GetFileName($resolvedStagingPath) -match '^jet-package-[0-9a-f]{32}$' `
        -and (Test-Path -LiteralPath $resolvedStagingPath)) {
        Remove-Item -LiteralPath $resolvedStagingPath -Recurse -Force
    }
}
