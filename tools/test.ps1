param(
    [string]$Suite = "All"
)

# 独立测试构建与执行（P0-01）：Core/Stats/Hook + Tests 编译为单独的测试 exe，
# 不参与主程序 build.ps1 的源文件 glob。无 NuGet 依赖，框架自带 csc.exe。
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'KeyInsight.cs'))) {
    $root = $PSScriptRoot
}

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'csc.exe not found' }

$outDir = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$out = Join-Path $outDir 'KeyInsight.Tests.exe'

$files = @()
$files += Get-ChildItem -Path (Join-Path $root 'Core') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
$files += Get-ChildItem -Path (Join-Path $root 'Stats') -Filter *.cs | ForEach-Object { $_.FullName }
$files += Get-ChildItem -Path (Join-Path $root 'Hook') -Filter *.cs | ForEach-Object { $_.FullName }
$files += Join-Path $root 'Config.cs'
$files += Join-Path $root 'ConfigV2.cs'
# Update pipeline (route catalog / manifest / downloader) is logic-testable without UI
$files += Get-ChildItem -Path (Join-Path $root 'Update') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }
# KeyLayout is a pure mapping table (no WinForms); stats projections use it for labels
$files += Join-Path $root 'UI\KeyLayout.cs'
$files += Get-ChildItem -Path (Join-Path $root 'Tests') -Recurse -Filter *.cs | ForEach-Object { $_.FullName }

if ($files.Count -eq 0) { throw 'no test sources found' }

$cscArgs = @(
    '-nologo',
    '-target:exe',
    '-optimize+',
    "-out:$out",
    '-r:System.dll',
    '-r:System.Core.dll',
    '-r:System.Web.Extensions.dll'
)
$cscArgs += $files

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $out $Suite
exit $LASTEXITCODE
