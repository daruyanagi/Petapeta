<#
.SYNOPSIS
    Petapeta のリリース用にバージョンを更新する(+ 任意でローカル検証用 ZIP を生成)。

.DESCRIPTION
    配布は GitHub リリース(ZIP)と winget のみで、どちらも CI
    (.github/workflows/release.yml)が v* タグの push をトリガーに自動で行う。
    本スクリプトは CI を起動する前のバージョン更新
    (csproj / Package.appxmanifest)を担当する。
    MSIX / Microsoft Store 配布は行わない。

.PARAMETER Version
    新しいバージョン(例 1.0.1)。指定すると csproj と appxmanifest を更新する。
    省略時は csproj の現在値を使う(ファイルは変更しない)。

.PARAMETER WithZip
    ローカル検証用に GitHub 配布相当の ZIP(自己完結・アンパッケージド)を生成する。
    本番の GitHub リリース ZIP は CI が作るため通常は不要。

.EXAMPLE
    .\Release.ps1 -Version 1.0.1           # バージョン更新のみ
    .\Release.ps1 -Version 1.0.1 -WithZip  # 更新 + ローカル検証用 ZIP も生成
#>
[CmdletBinding()]
param(
    [string] $Version,
    [switch] $WithZip
)

$ErrorActionPreference = 'Stop'
$root     = $PSScriptRoot
$proj     = Join-Path $root 'Petapeta.csproj'
$manifest = Join-Path $root 'Package.appxmanifest'
$outDir   = Join-Path $root 'publish\release'

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

# ── バージョン解決(必要なら更新) ──────────────────────────────────────────────
Step 'バージョン'
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version は x.y.z 形式で指定してください: $Version" }
    (Get-Content $proj -Raw) -replace '<Version>[\d.]+</Version>', "<Version>$Version</Version>" |
        Set-Content $proj -Encoding utf8 -NoNewline
    # Identity の Version のみを置換する(XML 宣言の version="1.0" や MinVersion を巻き込まない)
    (Get-Content $manifest -Raw) -replace '(<Identity[^>]*?Version=")[\d.]+(")', "`${1}$Version.0`${2}" |
        Set-Content $manifest -Encoding utf8 -NoNewline
    Write-Host "csproj / appxmanifest を $Version に更新しました"
} else {
    if ((Get-Content $proj -Raw) -match '<Version>([\d.]+)</Version>') { $Version = $Matches[1] }
    else { throw 'csproj から Version を読み取れませんでした' }
}
Write-Host "対象バージョン: $Version"

# ── ローカル検証用 ZIP(自己完結・アンパッケージド。本番は CI が生成) ─────────────
if ($WithZip) {
    Remove-Item $outDir -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $outDir | Out-Null
    try { Stop-Process -Name Petapeta -Force -ErrorAction Stop } catch {}

    foreach ($rid in 'win-x64', 'win-arm64') {
        $plat = if ($rid -eq 'win-x64') { 'x64' } else { 'arm64' }
        Step "ZIP 発行: $rid"
        $pubDir = Join-Path $root "publish\$rid"
        dotnet publish $proj -c Release -r $rid -p:Platform=$plat -p:PlatformTarget=$plat -p:EffectivePlatform=$plat -p:WindowsPackageType=None -p:SelfContained=true -o $pubDir
        if ($LASTEXITCODE -ne 0) { throw "publish 失敗: $rid" }
        $zip = Join-Path $outDir "Petapeta-$Version-$rid.zip"
        Compress-Archive -Path "$pubDir\*" -DestinationPath $zip -Force
        Write-Host "→ $zip"
    }

    Step '完了:成果物'
    Get-ChildItem $outDir | Select-Object Name, @{N='MB';E={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
}

Write-Host @"
次の手順(手動):
  1. バージョン更新分をコミットして main へ push
  2. v$Version タグを push(または gh release create v$Version)
     → CI が GitHub リリースの ZIP と winget 公開を自動で行う
"@ -ForegroundColor Yellow
