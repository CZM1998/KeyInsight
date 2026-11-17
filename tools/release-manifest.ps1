param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$SetupPath = "",
    [string]$PortablePath = "",
    [string]$PrivateKeyPath = "",
    [string]$OutputDir = "update",
    [string]$OutputName = "KeyInsight-update.json"
)

# PR-7 + 复审方案 §3.3：发布清单生成 + 签名。
# 生成固定名 KeyInsight-update.json（含 SHA-256/长度/官方 URL），签名输出 .sig，
# 两者作为 Release 资产上传（releases/latest/download/ 固定名发现，不依赖 api.github.com）。
# 签名私钥不入库；生产环境通过 GitHub Secret UPDATE_SIGNING_KEY_XML 传入。

$ErrorActionPreference = 'Stop'

if (-not $SetupPath) { $SetupPath = "dist\KeyInsight-Setup-$Version.exe" }
if (-not $PortablePath) { $PortablePath = "dist\KeyInsight-$Version-portable.zip" }
if (-not (Test-Path $SetupPath)) { throw "setup not found: $SetupPath" }
if (-not (Test-Path $PortablePath)) { throw "portable not found: $PortablePath" }
if (-not $PrivateKeyPath) {
    $PrivateKeyPath = Join-Path $env:USERPROFILE ".keyinsight-update-keys\update-private-key.xml"
}
if (-not (Test-Path $PrivateKeyPath)) { throw "private key not found: $PrivateKeyPath" }

$setupSha = (Get-FileHash -LiteralPath $SetupPath -Algorithm SHA256).Hash.ToLowerInvariant()
$portSha = (Get-FileHash -LiteralPath $PortablePath -Algorithm SHA256).Hash.ToLowerInvariant()
$setupSize = (Get-Item $SetupPath).Length
$portSize = (Get-Item $PortablePath).Length

$ghBase = "https://github.com/CZM1998/KeyInsight/releases/download/v$Version"
$publishedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')

$manifest = @"
{
  "schema": 1,
  "product": "KeyInsight",
  "channel": "stable",
  "version": "$Version",
  "publishedAt": "$publishedAt",
  "rolloutPercent": 100,
  "minUpgradeFrom": null,
  "revoked": [],
  "assets": [
    {
      "id": "windows-x64-setup",
      "kind": "setup",
      "fileName": "KeyInsight-Setup-$Version.exe",
      "size": $setupSize,
      "sha256": "$setupSha",
      "urls": [
        "$ghBase/KeyInsight-Setup-$Version.exe"
      ]
    },
    {
      "id": "windows-x64-portable",
      "kind": "portable",
      "fileName": "KeyInsight-$Version-portable.zip",
      "size": $portSize,
      "sha256": "$portSha",
      "urls": [
        "$ghBase/KeyInsight-$Version-portable.zip"
      ]
    }
  ],
  "keyId": "release-key-2026"
}
"@

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }
$manifestPath = Join-Path (Resolve-Path -LiteralPath $OutputDir).ProviderPath $OutputName
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

$manifestSigPath = $manifestPath + ".sig"
& (Join-Path $PSScriptRoot "sign-manifest.ps1") -ManifestPath $manifestPath -PrivateKeyPath $PrivateKeyPath -OutputPath $manifestSigPath
Write-Host "manifest: $manifestPath"
Write-Host "signature: $manifestSigPath"
Write-Host "setup sha256: $setupSha"
Write-Host "portable sha256: $portSha"
Write-Host ""
Write-Host "NEXT: 将 $OutputName 与 .sig 作为 Release 资产上传（固定名，releases/latest/download 发现）；"
Write-Host "Setup/portable 可同步到 OSS/COS 镜像（tools/sync-mirrors.ps1），客户端按前缀自动展开候选线路"
