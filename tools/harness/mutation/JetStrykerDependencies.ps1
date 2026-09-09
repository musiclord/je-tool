Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Set-JetStrykerPackageReferences {
    param([Parameter(Mandatory)] [string] $SourceRoot)
    $changes = @(
        @{
            path = 'src/Directory.Packages.props'
            before = '    <PackageVersion Include="System.Net.Http.Json" Version="10.0.11" />'
            after = "    <PackageVersion Include=`"System.Net.Http.Json`" Version=`"10.0.11`" />`n    <PackageVersion Include=`"System.Security.Cryptography.Xml`" Version=`"9.0.18`" />`n    <PackageVersion Include=`"System.Security.Cryptography.Pkcs`" Version=`"9.0.18`" />"
        },
        @{
            path = 'src/Stryker.Abstractions/Stryker.Abstractions.csproj'
            before = '    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" />'
            after = "    <PackageReference Include=`"Microsoft.CodeAnalysis.CSharp`" />`n    <PackageReference Include=`"Microsoft.CodeAnalysis.VisualBasic`" />`n    <PackageReference Include=`"System.Security.Cryptography.Xml`" />`n    <PackageReference Include=`"System.Security.Cryptography.Pkcs`" />"
        }
    )
    foreach ($change in $changes) {
        $path = Join-Path $SourceRoot $change.path
        $beforeHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $source = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        if ([regex]::Matches($source, [regex]::Escape($change.before)).Count -ne 1) { throw "Pinned Stryker dependency anchor mismatch: $($change.path)." }
        [IO.File]::WriteAllText($path, $source.Replace($change.before, $change.after), [Text.UTF8Encoding]::new($false))
        [ordered]@{ path = $change.path; beforeSha256 = $beforeHash; afterSha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
}

function Set-JetStrykerDependencyLocks {
    param([Parameter(Mandatory)] [string] $SourceRoot, [Parameter(Mandatory)] [string] $ExpectedOverlaySha256)
    $overlayPath = Join-Path $PSScriptRoot 'stryker-dependency-overlay.json'
    if ((Get-Item -LiteralPath $overlayPath).Length -gt 1MB -or (Get-FileHash -LiteralPath $overlayPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedOverlaySha256) { throw 'Pinned Stryker dependency overlay hash mismatch.' }
    $overlay = Get-Content -LiteralPath $overlayPath -Raw | ConvertFrom-Json
    $expectedPaths = @(
        'src/Stryker.Abstractions/packages.lock.json', 'src/Stryker.Utilities/packages.lock.json',
        'src/Stryker.Configuration/packages.lock.json', 'src/Stryker.Solutions/packages.lock.json',
        'src/Stryker.TestRunner/packages.lock.json', 'src/Stryker.TestRunner.MicrosoftTestPlatform/packages.lock.json',
        'src/Stryker.TestRunner.VsTest/packages.lock.json', 'src/Stryker.Core/Stryker.Core/packages.lock.json',
        'src/Stryker.CLI/Stryker.CLI/packages.lock.json'
    )
    if ($overlay.schemaVersion -ne 1 -or $overlay.revision -cne '30005a2f52c53ac2b0de49a90145f326ea43d675' -or
        (($overlay.files.path | Sort-Object) -join ',') -cne (($expectedPaths | Sort-Object) -join ',')) { throw 'Pinned Stryker dependency overlay scope mismatch.' }
    foreach ($file in $overlay.files) {
        $path = Join-Path $SourceRoot $file.path
        if ((Get-Item -LiteralPath $path).Length -gt 1MB -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.beforeSha256) { throw 'Pinned Stryker original lock hash mismatch.' }
    }
    foreach ($file in $overlay.files) {
        $path = Join-Path $SourceRoot $file.path
        $source = [IO.File]::ReadAllText($path)
        foreach ($edit in @($file.edits | Sort-Object offset -Descending)) {
            $offset = [int]$edit.offset
            $before = [string]$edit.before
            if ($offset -lt 0 -or $offset + $before.Length -gt $source.Length -or $source.Substring($offset, $before.Length) -cne $before) { throw 'Pinned Stryker lock overlay anchor mismatch.' }
            $source = $source.Substring(0, $offset) + [string]$edit.after + $source.Substring($offset + $before.Length)
        }
        $resultHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source))).ToLowerInvariant()
        if ($resultHash -cne $file.afterSha256) { throw 'Pinned Stryker lock overlay result mismatch.' }
        [IO.File]::WriteAllText($path, $source, [Text.UTF8Encoding]::new($false))
        [ordered]@{ path = $file.path; beforeSha256 = $file.beforeSha256; afterSha256 = $resultHash }
    }
}
