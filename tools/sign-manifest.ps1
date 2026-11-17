param(
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [Parameter(Mandatory=$true)][string]$PrivateKeyPath,
    [string]$OutputPath = ""
)

# 对 manifest.json 原始字节进行 RSA-3072 + SHA-256 PKCS#1 v1.5 签名（路线图 PR-4）。
# 输出 manifest.sig（Base64 文本）。签名私钥不入库。

$ErrorActionPreference = 'Stop'

$manifest = (Resolve-Path -LiteralPath $ManifestPath).ProviderPath
$key = (Resolve-Path -LiteralPath $PrivateKeyPath).ProviderPath
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString([IO.File]::ReadAllText($key))
    $signature = $rsa.SignData([IO.File]::ReadAllBytes($manifest), $sha256)
    if ([string]::IsNullOrEmpty($OutputPath)) { $OutputPath = $ManifestPath + ".sig" }
    $output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
    [IO.File]::WriteAllText($output, [Convert]::ToBase64String($signature) + [Environment]::NewLine)
    Write-Host "manifest signature written to $OutputPath"
}
finally {
    $sha256.Dispose()
    $rsa.Dispose()
}
