#requires -Version 7.4
[CmdletBinding()]
param([string] $RepositoryRoot = ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))
$ErrorActionPreference = 'Stop'

# 前端行為測試的檔案自動收集：下面兩個資料夾第一層的 frontend-*.test.cjs 全部交給同一次 node --test。
#   src/JET/tests/frontend/  只讀 src/JET 裡檔案的前端測試，只有 src/JET 時也能執行，共用模組在它的 support/。
#   tools/tests/             要讀儲存庫其他檔案的前端測試，例如 Gui 驅動程式、需求文件與改壞工具。
# tools/tests/frontend-preview.test.cjs 另有 FrontendPreview 路線，不在這裡執行。
# 兩個資料夾底下還有其他 *.test.cjs、*.test.js 或 *.test.mjs 沒被收進來時直接失敗，
# 避免新增的測試檔從來沒有被任何路線執行。support/ 底下的共用夾具檔名不含 .test.，不會被收集。
$routedElsewhere = @('frontend-preview.test.cjs')

function Get-JetFrontendNodeTests {
    param([Parameter(Mandatory)] [string] $TestsRoot)
    $root = [IO.Path]::GetFullPath($TestsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $all = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.Name -match '\.test\.(?:cjs|js|mjs)$' })
    $topLevel = @($all | Where-Object { $_.DirectoryName -ieq $root })
    [string[]]$selected = @($topLevel | Where-Object { $_.Name -clike 'frontend-*.test.cjs' -and $routedElsewhere -cnotcontains $_.Name } |
        ForEach-Object { $_.FullName })
    [Array]::Sort($selected, [StringComparer]::Ordinal)
    $known = @($selected) + @($topLevel | Where-Object { $routedElsewhere -ccontains $_.Name } | ForEach-Object { $_.FullName })
    [string[]]$unrouted = @($all | Where-Object { $known -notcontains $_.FullName } |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName).Replace('\', '/') })
    [Array]::Sort($unrouted, [StringComparer]::Ordinal)
    [pscustomobject]@{ Selected = $selected; Unrouted = $unrouted }
}

# 每個前端測試檔都要載入記憶體與時間上限 support/resource-guard.cjs，直接載入或經由共用夾具都可以（2026-10-13）。
# tools/tests 的測試從 src/JET/tests/frontend/support/ 載入同一份。
# 沒有載入的測試程序失控時會占滿整台電腦的記憶體。回傳沒有載入的檔名。
function Get-JetUnguardedFrontendTests {
    param([Parameter(Mandatory)] [AllowEmptyCollection()] [string[]] $Files)
    $guardPattern = 'require\(\s*[''"](?:\./support/|(?:\.\./)+src/JET/tests/frontend/support/)(?:resource-guard|frontend-fixtures)\.cjs[''"]\s*\)'
    [string[]]@($Files | Where-Object { [IO.File]::ReadAllText($_) -notmatch $guardPattern } | ForEach-Object { [IO.Path]::GetFileName($_) })
}

# 先用合成目錄確認收集規則本身：收進第一層的前端測試，排除另有路線的預覽測試，擋下其他測試檔。
$base = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/harness/frontend-mapping-contract'))
$owned = Join-Path $base ([Guid]::NewGuid().ToString('N'))
try {
    foreach ($directory in @($owned, "$owned/support", "$owned/nested", "$owned/fixtures")) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    foreach ($file in @('frontend-b.test.cjs', 'frontend-a.test.cjs', 'frontend-preview.test.cjs', 'orphan.test.cjs', 'legacy.test.js',
            'support/frontend-fixtures.cjs', 'support/frontend-x.test.cjs', 'nested/frontend-c.test.cjs', 'fixtures/data.json', 'helper.cjs')) {
        [IO.File]::WriteAllText((Join-Path $owned $file), '// synthetic')
    }
    $synthetic = Get-JetFrontendNodeTests -TestsRoot $owned
    $selectedNames = @($synthetic.Selected | ForEach-Object { [IO.Path]::GetFileName($_) })
    if (($selectedNames -join ',') -cne 'frontend-a.test.cjs,frontend-b.test.cjs') {
        throw "Synthetic collection selected the wrong files: $($selectedNames -join ', ')"
    }
    if (($synthetic.Unrouted -join ',') -cne 'legacy.test.js,nested/frontend-c.test.cjs,orphan.test.cjs,support/frontend-x.test.cjs') {
        throw "Synthetic collection did not reject every unrouted test file: $($synthetic.Unrouted -join ', ')"
    }
    # 資源上限的檢查：直接載入或經由共用夾具載入都算，兩者都沒有的檔案要被找出來。
    [IO.File]::WriteAllText((Join-Path $owned 'frontend-guarded.test.cjs'), "'use strict';`nrequire('./support/resource-guard.cjs');`n")
    [IO.File]::WriteAllText((Join-Path $owned 'frontend-fixtures-user.test.cjs'), "const support = require(`"./support/frontend-fixtures.cjs`");`n")
    [IO.File]::WriteAllText((Join-Path $owned 'frontend-cross-tree.test.cjs'), "require('../../src/JET/tests/frontend/support/resource-guard.cjs');`n")
    $unguardedNames = Get-JetUnguardedFrontendTests -Files @(
        (Join-Path $owned 'frontend-guarded.test.cjs'), (Join-Path $owned 'frontend-fixtures-user.test.cjs'),
        (Join-Path $owned 'frontend-cross-tree.test.cjs'), (Join-Path $owned 'frontend-a.test.cjs'))
    if (($unguardedNames -join ',') -cne 'frontend-a.test.cjs') {
        throw "Synthetic resource-guard check flagged the wrong files: $($unguardedNames -join ', ')"
    }
} finally {
    if (Test-Path -LiteralPath $owned) {
        $resolved = (Resolve-Path -LiteralPath $owned).Path
        if (-not $resolved.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Refusing cleanup outside the owned frontend mapping contract directory.'
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

# 改壞檢查用的副本只給 tools/tests/support/mutate-frontend.cjs 使用；正式路線一律讀工作樹的前端。
[Environment]::SetEnvironmentVariable('JET_FRONTEND_TEST_WWWROOT', $null)

$productTestsRoot = Join-Path $RepositoryRoot 'src/JET/tests/frontend'
[string[]]$selected = @()
foreach ($relative in @('src/JET/tests/frontend', 'tools/tests')) {
    $collection = Get-JetFrontendNodeTests -TestsRoot (Join-Path $RepositoryRoot $relative)
    if ($collection.Unrouted.Count -ne 0) {
        throw "Test files under $relative are not run by any route: $($collection.Unrouted -join ', '). Name frontend behaviour tests $relative/frontend-*.test.cjs, or route them explicitly."
    }
    if ($relative -ceq 'src/JET/tests/frontend' -and $collection.Selected.Count -eq 0) { throw 'No frontend behaviour tests were collected from src/JET/tests/frontend.' }
    $selected += @($collection.Selected)
}
Write-Output ('Frontend behaviour test files: ' + (($selected | ForEach-Object { [IO.Path]::GetRelativePath($RepositoryRoot, $_).Replace('\', '/') }) -join ', '))

[string[]]$unguarded = Get-JetUnguardedFrontendTests -Files ($selected + @(Join-Path $RepositoryRoot 'tools/tests/frontend-preview.test.cjs'))
if ($unguarded.Count -ne 0) {
    throw "Frontend test files must load src/JET/tests/frontend/support/resource-guard.cjs, directly or through support/frontend-fixtures.cjs: $($unguarded -join ', ')"
}

$node = Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1 -ExpandProperty Source
# 同時執行的測試檔數照 support/resource-guard-marker.cjs 的上限（2026-10-14）。node --test 預設依 CPU 核心數同時開程序，
# 每個程序各有 2048 MB 上限，合計卻會隨核心數放大；一次 4 個時合計不超過驗證步驟的 8192 MB。
$markerPath = Join-Path $productTestsRoot 'support/resource-guard-marker.cjs'
$parallelText = & $node -p 'String(require(process.argv[1]).MAX_PARALLEL_TEST_FILES)' $markerPath
$parallel = 0
if ($LASTEXITCODE -ne 0 -or -not [int]::TryParse("$parallelText".Trim(), [ref]$parallel) -or $parallel -lt 1) {
    throw "Could not read MAX_PARALLEL_TEST_FILES from src/JET/tests/frontend/support/resource-guard-marker.cjs: $parallelText"
}
& $node --test "--test-concurrency=$parallel" @selected
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'public-frontend-contract.tests.ps1') -RepositoryRoot $RepositoryRoot
exit 0
