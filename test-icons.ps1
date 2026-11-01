$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ('SilentRegIconTest-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
Push-Location -LiteralPath $PSScriptRoot
try {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $compiler)) { $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
    $exe = Join-Path $work 'IconTests.exe'
    & $compiler /nologo /target:exe /main:IconTests "/out:$exe" /resource:Runner.cs,Runner.cs /resource:assets\SilentReg.ico,SilentReg.ico /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:Microsoft.CSharp.dll SilentReg.cs test-icons.cs
    if ($LASTEXITCODE -ne 0) { throw 'Icon test compilation failed.' }
    & $exe $work
    if ($LASTEXITCODE -ne 0) { throw 'Icon tests failed.' }
}
finally {
    Pop-Location
    $resolved = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolved.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -match '^SilentRegIconTest-[0-9a-f]{32}$') {
        [IO.Directory]::Delete($resolved, $true)
    }
}
