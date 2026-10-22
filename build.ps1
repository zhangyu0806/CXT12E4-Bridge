param(
    [switch]$Windowed
)

$ErrorActionPreference = 'Stop'

$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$sourceRoot = Join-Path $projectRoot 'src\Cxt1024Bridge'
$outputRoot = Join-Path $projectRoot 'outputs'
$outputPath = Join-Path $outputRoot 'Cxt12E4Bridge.exe'
$compatibilityOutputPath = Join-Path $outputRoot 'Cxt1024Bridge.exe'
$wpfRoot = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\WPF'
$target = if ($Windowed) { 'winexe' } else { 'exe' }

New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

$sources = Get-ChildItem -LiteralPath $sourceRoot -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:$target /platform:anycpu /optimize+ /warn:4 /out:$outputPath /reference:System.dll /reference:System.Core.dll /reference:System.Web.Extensions.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:"$wpfRoot\UIAutomationClient.dll" /reference:"$wpfRoot\UIAutomationTypes.dll" /reference:"$wpfRoot\WindowsBase.dll" $sources
if ($LASTEXITCODE -ne 0) {
    throw "C# compiler exited with code $LASTEXITCODE"
}

# Keep the previous filename as a byte-identical compatibility copy until the
# existing Startup shortcut is migrated.
Copy-Item -Force -LiteralPath $outputPath -Destination $compatibilityOutputPath

Get-FileHash -Algorithm SHA256 -LiteralPath $outputPath | Format-List
