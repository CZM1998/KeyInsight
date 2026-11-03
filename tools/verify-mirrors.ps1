param(
    [Parameter(Mandatory=$true)][string]$ManifestUrl
)

# PR-7：镜像回验（路线图 §8.2）：从 manifest 下载每个资产 URL，检查长度 + SHA-256。
# 全部通过后 latest manifest 才允许发布（发布门禁）。

$ErrorActionPreference = 'Stop'
$manifest = Invoke-RestMethod -Uri $ManifestUrl -UserAgent "KeyInsight-Updater"
$version = $manifest.version
$failures = 0

foreach ($asset in $manifest.assets) {
    foreach ($url in $asset.urls) {
        Write-Host "checking $url ..."
        try {
            $tmp = Join-Path $env:TEMP ("verify_" + [Guid]::NewGuid().ToString("N"))
            Invoke-WebRequest -Uri $url -OutFile $tmp -UserAgent "KeyInsight-Updater" -TimeoutSec 120
            $size = (Get-Item $tmp).Length
            $sha = (Get-FileHash $tmp -Algorithm SHA256).Hash.ToLower()
            Remove-Item $tmp -Force

            if ($asset.size -gt 0 -and $size -ne $asset.size) {
                Write-Host "  FAIL size: $size != $($asset.size)"
                $failures++
            }
            elseif ($sha -ne $asset.sha256) {
                Write-Host "  FAIL sha256: $sha != $($asset.sha256)"
                $failures++
            }
            else {
                Write-Host "  OK ($size bytes)"
            }
        }
        catch {
            Write-Host "  FAIL download: $($_.Exception.Message)"
            $failures++
        }
    }
}

if ($failures -gt 0) {
    Write-Host ""
    Write-Host "MIRROR VERIFY FAILED: $failures failures — latest manifest 不应发布"
    exit 1
}
Write-Host ""
Write-Host "ALL MIRRORS VERIFIED — 可以发布 latest manifest"
