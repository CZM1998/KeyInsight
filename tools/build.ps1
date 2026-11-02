param(
    [string]$Version = "2.0.0"
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if (-not (Test-Path (Join-Path $root 'KeyInsight.cs'))) {
    $root = $PSScriptRoot
}

$v = $Version.Trim()
if ($v.StartsWith('v') -or $v.StartsWith('V')) { $v = $v.Substring(1) }
$parts = $v.Split('.')
while ($parts.Count -lt 4) { $parts += '0' }
$ver4 = ($parts[0..3] -join '.')
$versionFile = Join-Path $root 'VersionInfo.cs'
$versionSource = "using System.Reflection;`r`n[assembly: AssemblyVersion(""$ver4"")]`r`n[assembly: AssemblyFileVersion(""$ver4"")]`r`n"
[System.IO.File]::WriteAllText($versionFile, $versionSource, (New-Object System.Text.UTF8Encoding($false)))

$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw 'csc.exe not found' }

$out = Join-Path $root 'KeyInsight.exe'
$ico = Join-Path $root 'Assets\app.ico'
$png = Join-Path $root 'Assets\app256.png'
$manifest = Join-Path $root 'app.manifest'

$files = Get-ChildItem -Path $root -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\Assets\\|\\Tests\\|\\artifacts\\' } |
    ForEach-Object { $_.FullName }

$args = @(
    '-nologo',
    '-target:winexe',
    '-optimize+',
    "-out:$out",
    "-win32icon:$ico",
    "-win32manifest:$manifest",
    "-resource:$ico,app.ico",
    "-resource:$png,app256.png",
    '-r:System.dll',
    '-r:System.Core.dll',
    '-r:System.Drawing.dll',
    '-r:System.Windows.Forms.dll',
    '-r:System.Web.Extensions.dll'
)
$args += $files

& $csc $args
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host ("built " + $out + " (" + (Get-Item $out).Length + " bytes)")
