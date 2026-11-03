param(
    [string]$OutDir = "$env:USERPROFILE\.keyinsight-update-keys"
)

# 生成 RSA-3072 更新签名密钥对（路线图 PR-4 / §7.2）。
# 私钥文件必须妥善保管：加入 .gitignore，生产环境建议放入 GitHub Secrets 或受保护环境。

$ErrorActionPreference = 'Stop'

$code = @"
using System;
using System.Security.Cryptography;
using System.IO;
public class KeyGen {
    public static void Main(string[] args) {
        using (RSACryptoServiceProvider rsa = new RSACryptoServiceProvider(3072)) {
            rsa.PersistKeyInCsp = false;
            File.WriteAllText(args[0], rsa.ToXmlString(false));
            File.WriteAllText(args[1], rsa.ToXmlString(true));
        }
    }
}
"@

$tmpDir = Join-Path $env:TEMP ("KeyInsightKeyGen_" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmpDir | Out-Null
$csPath = Join-Path $tmpDir "keygen.cs"
$exePath = Join-Path $tmpDir "keygen.exe"
[System.IO.File]::WriteAllText($csPath, $code)

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
& $csc -nologo -out:$exePath $csPath
if ($LASTEXITCODE -ne 0) { throw "keygen compile failed" }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$publicPath = Join-Path $OutDir "update-public-key.xml"
$privatePath = Join-Path $OutDir "update-private-key.xml"

& $exePath $publicPath $privatePath
if ($LASTEXITCODE -ne 0) { throw "keygen failed" }

Write-Host "public key:  $publicPath"
Write-Host "private key: $privatePath"
Write-Host ""
Write-Host "NEXT STEPS:"
Write-Host "1. 将公钥 XML 内容更新到 Update/Manifest/ManifestVerifier.cs 的 DevPublicKeyXml 常量"
Write-Host "2. 私钥文件妥善保管（生产环境放入 GitHub Secrets: UPDATE_SIGNING_KEY_XML）"
Write-Host "3. 私钥路径加入 .gitignore，绝不提交"
