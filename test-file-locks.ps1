param(
    [string]$ConverterPath = (Join-Path $PSScriptRoot 'SilentReg.exe'),
    [string]$OutputDirectory = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$work = Join-Path $OutputDirectory ('test-output-locks-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$inputPath = Join-Path $work 'sample input.reg'
$outputPath = Join-Path $work 'sample output.exe'
$statusPath = Join-Path $work 'result.txt'
[IO.File]::WriteAllText($inputPath, "Windows Registry Editor Version 5.00`r`n`r`n[HKEY_CURRENT_USER\Software\SilentRegDiagnostic]`r`n`"Example`"=`"test`"`r`n", [Text.Encoding]::Unicode)
$originalHash = (Get-FileHash -LiteralPath $inputPath).Hash
$checks = 0

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    $script:checks++
    Write-Output ('PASS: ' + $message)
}
function Start-Conversion([switch]$Overwrite) {
    $arguments = '--status "' + $statusPath + '" --input "' + $inputPath + '" --output "' + $outputPath + '"'
    if ($Overwrite) { $arguments += ' --force' }
    Start-Process -FilePath $ConverterPath -ArgumentList $arguments -PassThru
}
function Open-TestLock {
    for ($attempt = 0; ; $attempt++) {
        try { return [IO.File]::Open($outputPath, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None) }
        catch [IO.IOException] {
            if ($attempt -ge 20) { throw }
            Start-Sleep -Milliseconds 250
        }
    }
}

$process = Start-Conversion
$process.WaitForExit()
Assert ($process.ExitCode -eq 0) ('New output saves successfully: ' + (Get-Content -LiteralPath $statusPath -Raw))
$process = Start-Conversion -Overwrite
$process.WaitForExit()
Assert ($process.ExitCode -eq 0) 'Existing output can be replaced in the same folder.'
$before = (Get-FileHash -LiteralPath $outputPath).Hash
$process = Start-Conversion
$process.WaitForExit()
Assert ($process.ExitCode -ne 0) 'Existing output remains protected without overwrite permission.'
Assert ((Get-FileHash -LiteralPath $outputPath).Hash -eq $before) 'Rejected overwrite preserves existing output bytes.'

$lock = Open-TestLock
try {
    $process = Start-Conversion -Overwrite
    Start-Sleep -Milliseconds 2000
    $process.Refresh()
    Assert (-not $process.HasExited) 'Converter waits while a transient destination lock is held.'
} finally { $lock.Dispose() }
$process.WaitForExit()
Assert ($process.ExitCode -eq 0) 'Conversion succeeds after the transient lock is released.'

$before = (Get-FileHash -LiteralPath $outputPath).Hash
$lock = Open-TestLock
try {
    $process = Start-Conversion -Overwrite
    if (-not $process.WaitForExit(15000)) { throw 'Permanent-lock test exceeded its bounded retry time.' }
    Assert ($process.ExitCode -ne 0) 'A persistent lock returns failure after bounded retries.'
} finally { $lock.Dispose() }
Assert ((Get-FileHash -LiteralPath $outputPath).Hash -eq $before) 'A persistent lock leaves existing output bytes intact.'
$errorText = Get-Content -LiteralPath $statusPath -Raw
Assert ($errorText.Contains($outputPath) -and $errorText.Contains('in use')) 'Lock error identifies the destination and explains how to recover.'
Assert ((Get-FileHash -LiteralPath $inputPath).Hash -eq $originalHash) 'Input registry file stays unchanged through all save tests.'
Assert (@(Get-ChildItem -LiteralPath $work -Filter '.SilentReg-*.exe' -Force).Count -eq 0) 'No staged executable is created in the destination folder.'
Write-Output ('Passed ' + $checks + ' file-save checks. Artifacts: ' + $work)
