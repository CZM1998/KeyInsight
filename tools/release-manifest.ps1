param(
    [Parameter(Mandatory=$true)][string]$Version,
    [string]$SetupPath = "",
    [string]$PortablePath = "",
    [string]$PrivateKeyPath = "",
    [string]$OutputDir = "update"
)

# PR-7：发布清单生成 + 签名（路线图 §8.2）。
# 生成 manifest.json（含 SHA-256/长度/多线路 URL），签名输出 manifest.sig。
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

$code = @"
using System;
using System.Security.Cryptography;
using System.IO;
using System.Text;
public static class Tools {
    public static string Sha256(string path) {
        using (SHA256 sha = SHA256.Create())
        using (FileStream fs = File.OpenRead(path)) {
            byte[] h = sha.ComputeHash(fs);
            StringBuilder sb = new StringBuilder(h.Length * 2);
            for (int i = 0; i < h.Length; i++) sb.Append(h[i].ToString("x2"));
            return sb.ToString();
        }
    }
    public static string Sign(string manifestPath, string keyPath) {
        byte[] data = File.ReadAllBytes(manifestPath);
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072)) {
            rsa.PersistKeyInCsp = false;
            rsa.FromXmlString(File.ReadAllText(keyPath));
            return Convert.ToBase64String(rsa.SignData(data, new SHA256CryptoServiceProvider()));
        }
    }
}
"@

$tmpDir = Join-Path $env:TEMP ("KISign_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$csPath = Join-Path $tmpDir "tools.cs"
$exePath = Join-Path $tmpDir "tools.exe"
[System.IO.File]::WriteAllText($csPath, $code)
& $csc 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe' -nologo -out:$exePath $csPath 2>$null
if ($LASTEXITCODE -ne 0) {
    $csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    & $csc -nologo -out:$exePath $csPath
    if ($LASTEXITCODE -ne 0) { throw "tools compile failed" }
}

$setupSha = & $exePath "sha" $SetupPath
$portSha = & $exePath "sha" $PortablePath
$setupSize = (Get-Item $SetupPath).Length
$portSize = (Get-Item $PortablePath).Length

$ghBase = "https://github.com/CZM1998/KeyInsight/releases/download/v$Version"

$manifest = @"
{
  "schema": 1,
  "product": "KeyInsight",
  "channel": "stable",
  "version": "$Version",
  "publishedAt": "$(DateTime.UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))",
  "rolloutPercent": 100,
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
$manifestPath = Join-Path $OutputDir "manifest.json"
[System.IO.File]::WriteAllText($manifestPath, $manifest, (New-Object System.Text.UTF8Encoding($false)))

$sig = & $exePath "sign" $manifestPath $PrivateKeyPath
$manifestSigPath = Join-Path $OutputDir "manifest.sig"
[System.IO.File]::WriteAllText($manifestSigPath, $sig + "`n")

Write-Host "manifest: $manifestPath"
Write-Host "signature: $manifestSigPath"
Write-Host "setup sha256: $setupSha"
Write-Host "portable sha256: $portSha"
Write-Host ""
Write-Host "NEXT: 上传 manifest.json + manifest.sig 到受信路线（如 GitHub raw / 自有域名），"
Write-Host "并同步 Setup/portable 到 OSS/COS 镜像（tools/sync-mirrors.ps1）"
