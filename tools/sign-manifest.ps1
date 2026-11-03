param(
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
    [string]$OutputPath = ""
)

# 对 manifest.json 原始字节进行 RSA-3072 + SHA-256 PKCS#1 v1.5 签名（路线图 PR-4）。
# 输出 manifest.sig（Base64 文本）。签名私钥不入库。

$ErrorActionPreference = 'Stop'

$code = @"
using System;
using System.Security.Cryptography;
using System.IO;
public static class ManifestSigner {
    public static string Sign(string manifestPath, string keyPath) {
        byte[] data = File.ReadAllBytes(manifestPath);
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072)) {
            rsa.PersistKeyInCsp = false;
            rsa.FromXmlString(File.ReadAllText(keyPath));
            byte[] sig = rsa.SignData(data, new SHA256CryptoServiceProvider());
            return Convert.ToBase64String(sig);
        }
    }
}
"@

$tmpDir = Join-Path $env:TEMP ("KeyInsightSign_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$csPath = Join-Path $tmpDir "signer.cs"
$exePath = Join-Path $tmpDir "signer.exe"
[System.IO.File]::WriteAllText($csPath, $code)

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc -nologo -out:$exePath $csPath
if ($LASTEXITCODE -ne 0) { throw "signer compile failed" }

$sig = & $exePath $ManifestPath $PrivateKeyPath
if ($LASTEXITCODE -ne 0) { throw "signing failed" }

if ($OutputPath -eq "") { $OutputPath = $ManifestPath + ".sig" }
[System.IO.File]::WriteAllText($OutputPath, $sig + "`n")
Write-Host "manifest signature written to $OutputPath"
