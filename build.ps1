$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
Push-Location -LiteralPath $PSScriptRoot
$buildDirectory = Join-Path ([IO.Path]::GetTempPath()) ('SilentRegCompiler-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $buildDirectory | Out-Null
try {
    $staged = Join-Path $buildDirectory 'SilentReg.exe'
    & $compiler /nologo /target:winexe /platform:anycpu /optimize+ "/out:$staged" /win32manifest:app.manifest /win32icon:assets\SilentReg.ico /resource:Runner.cs,Runner.cs /resource:assets\SilentReg.ico,SilentReg.ico /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll SilentReg.cs
    if ($LASTEXITCODE -ne 0) { throw "Compiler exited with code $LASTEXITCODE" }
    # Copy the complete local build; synced drives may reject file renames.
    [IO.File]::Copy($staged, (Join-Path $PSScriptRoot 'SilentReg.exe'), $true)
    Write-Output (Join-Path $PSScriptRoot 'SilentReg.exe')
}
finally {
    Pop-Location
    $resolvedBuild = [IO.Path]::GetFullPath($buildDirectory)
    $resolvedTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedBuild.StartsWith($resolvedTemp, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedBuild) -match '^SilentRegCompiler-[0-9a-f]{32}$') {
        try { [IO.Directory]::Delete($resolvedBuild, $true) } catch { Write-Warning $_.Exception.Message }
    }
}
