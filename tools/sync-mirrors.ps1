param(
    [Parameter(Mandatory=$true)][string]$ManifestPath,
    [string]$OssAccessKey = "",
    [string]$OssSecretKey = "",
    [string]$OssEndpoint = "",
    [string]$OssBucket = "",
    [string]$CosSecretId = "",
    [string]$CosSecretKey = "",
    [string]$CosRegion = "",
    [string]$CosBucket = ""
)

# PR-7：国内镜像同步脚本（路线图 §8.3）。
# 使用前需要：
#   1. 阿里云 OSS / 腾讯云 COS 账号与 Bucket
#   2. 对应 CLI 工具（ossutil / coscli）已安装并配置
# 本脚本是模板——实际 Bucket/域名/凭据由项目方配置，不入库。

$ErrorActionPreference = 'Stop'
$manifest = Get-Content $ManifestPath -Raw | ConvertFrom-Json
$version = $manifest.version

$setup = "dist\KeyInsight-Setup-$version.exe"
$portable = "dist\KeyInsight-$version-portable.zip"
$manifestJson = $ManifestPath
$manifestSig = $ManifestPath + ".sig"

$files = @(@{local=$setup; remote="KeyInsight/$version/KeyInsight-Setup-$version.exe"},
           @{local=$portable; remote="KeyInsight/$version/KeyInsight-$version-portable.zip"},
           @{local=$manifestJson; remote="KeyInsight/$version/manifest.json"},
           @{local=$manifestSig; remote="KeyInsight/$version/manifest.sig"})

if ($OssBucket -and $OssAccessKey) {
    Write-Host "=== Aliyun OSS sync ==="
    foreach ($f in $files) {
        & ossutil cp $f.local "oss://$OssBucket/$($f.remote)" -e $OssEndpoint -i $OssAccessKey -k $OssSecretKey -f
        if ($LASTEXITCODE -ne 0) { throw "OSS sync failed: $($f.local)" }
        Write-Host "  uploaded $($f.remote)"
    }
}

if ($CosBucket -and $CosSecretId) {
    Write-Host "=== Tencent COS sync ==="
    foreach ($f in $files) {
        & coscli cp $f.local "cos://$CosBucket/$($f.remote)" --region $CosRegion
        if ($LASTEXITCODE -ne 0) { throw "COS sync failed: $($f.local)" }
        Write-Host "  uploaded $($f.remote)"
    }
}

Write-Host ""
Write-Host "同步完成后运行 tools/verify-mirrors.ps1 回验每个 URL 的 SHA-256"
