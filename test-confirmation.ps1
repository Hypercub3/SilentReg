$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
$work = Join-Path ([IO.Path]::GetTempPath()) ('SilentRegConfirmationTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Push-Location -LiteralPath $PSScriptRoot
try {
    $testExe = Join-Path $work 'ConfirmationTests.exe'
    & $compiler /nologo /target:exe /main:ConfirmationTests /platform:anycpu "/out:$testExe" /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll SilentReg.cs test-confirmation.cs
    if ($LASTEXITCODE -ne 0) { throw 'Confirmation tests did not compile.' }
    & $testExe $work
    if ($LASTEXITCODE -ne 0) { throw 'Confirmation tests failed.' }
}
finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolved) -match '^SilentRegConfirmationTest-[0-9a-f]{32}$') {
        [IO.Directory]::Delete($resolved, $true)
    }
}
