$ErrorActionPreference = 'Stop'

$projectDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$output = Join-Path $projectDir 'CodexQuotaToolbar.exe'
$source = Join-Path $projectDir 'CodexQuotaToolbar.cs'
$setupSource = Join-Path $projectDir 'CodexQuotaToolbarSetup.cs'
$distDir = Join-Path $projectDir 'dist'
$setupOutput = Join-Path $distDir 'Codex额度工具条-Setup.exe'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw "C# compiler not found: $compiler"
}

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /out:$output `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:System.Web.Extensions.dll `
    $source

if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $distDir -Force | Out-Null

$resourceArgument = "/resource:$output,CodexQuotaToolbar.Payload"

& $compiler /nologo /target:winexe /optimize+ /platform:anycpu /out:$setupOutput `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Windows.Forms.dll `
    /reference:Microsoft.CSharp.dll `
    $resourceArgument `
    $setupSource

if ($LASTEXITCODE -ne 0) {
    throw "Setup build failed with exit code $LASTEXITCODE"
}

Get-Item -LiteralPath $output, $setupOutput | Select-Object FullName, Length, LastWriteTime

