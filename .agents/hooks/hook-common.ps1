#requires -Version 7.4

<#
.SYNOPSIS
    三個 JET hook 共用的輸入、根目錄與輸出處理。

.DESCRIPTION
    同一份 hook 腳本由 Claude Code（`.claude/settings.json`）與 Codex（`.codex/hooks.json`）各自註冊。
    兩個平台都用 stdin 傳入 JSON，並接受 `hookSpecificOutput.additionalContext` 注入脈絡；差別在找專案根目錄：
    Claude Code 提供 `CLAUDE_PROJECT_DIR`，Codex 只在輸入的 `cwd` 與行程工作目錄給出位置。

    這個檔案只由同目錄的 hook 以 dot-source 載入，不單獨執行。
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $false)
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8

function Read-JetHookPayload {
    try {
        if (-not [Console]::IsInputRedirected) { return $null }
        $raw = [Console]::In.ReadToEnd()
        if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
        return ($raw | ConvertFrom-Json)
    } catch {
        return $null
    }
}

function Get-JetPayloadValue {
    param($Payload, [string] $Name)

    if ($null -eq $Payload) { return $null }
    $property = $Payload.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Resolve-JetHookRoot {
    param($Payload)

    # 依序嘗試 Claude 的專案目錄、平台傳入的 cwd、行程工作目錄；只接受含 tools/verify.ps1 的 Git 根目錄。
    $candidates = @($env:CLAUDE_PROJECT_DIR, [string](Get-JetPayloadValue -Payload $Payload -Name 'cwd'), (Get-Location).Path)
    foreach ($candidate in $candidates) {
        if ([string]::IsNullOrWhiteSpace($candidate) -or -not (Test-Path -LiteralPath $candidate -PathType Container)) {
            continue
        }
        $top = (& git -C $candidate rev-parse --show-toplevel 2>$null)
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($top)) { continue }
        $top = [IO.Path]::GetFullPath([string]$top)
        if (Test-Path -LiteralPath (Join-Path $top 'tools/verify.ps1') -PathType Leaf) {
            return $top
        }
    }
    return $null
}

function Write-JetHookContext {
    param([string] $EventName, [string] $Context)

    $payload = [ordered]@{
        hookSpecificOutput = [ordered]@{
            hookEventName     = $EventName
            additionalContext = $Context
        }
    }
    [Console]::Out.WriteLine(($payload | ConvertTo-Json -Depth 4 -Compress))
}
