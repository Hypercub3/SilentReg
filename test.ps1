param([switch]$ApplyRegistry)
$ErrorActionPreference = 'Stop'
$work = Join-Path $PSScriptRoot ('test-output-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$exe = Join-Path $PSScriptRoot 'SilentReg.exe'
$testId = [guid]::NewGuid().ToString('N')
$key = 'Software\SilentRegTest-' + $testId
$checks = [System.Collections.Generic.List[string]]::new()

function Assert($condition, $message) {
    if (-not $condition) { throw $message }
    $checks.Add($message)
    Write-Output ('PASS: ' + $message) | Out-Host
}

function Convert-Reg($file, $output, $extra = '') {
    $statusFile = Join-Path $work 'build-status.txt'
    if (Test-Path -LiteralPath $statusFile) { Remove-Item -LiteralPath $statusFile }
    $arguments = '--status "' + $statusFile + '" --input "' + $file + '" --output "' + $output + '" ' + $extra
    $process = Start-Process -FilePath $exe -ArgumentList $arguments -Wait -PassThru
    return $process.ExitCode
}

function Get-FallbackLogState($output) {
    $log = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('SilentReg\Logs\' + [IO.Path]::GetFileName($output) + '.log')
    if (Test-Path -LiteralPath $log) { return (Get-FileHash -LiteralPath $log).Hash + ':' + (Get-Item -LiteralPath $log).LastWriteTimeUtc.Ticks }
    return 'absent'
}

try {
    $file = Join-Path $work "settings & 100% O'Brien.reg"
    $output = Join-Path $work "settings & 100% O'Brien.silent.exe"
    $content = "Windows Registry Editor Version 5.00`r`n`r`n[HKEY_CURRENT_USER\$key]`r`n" +
        '"Text"="Unicode: caf' + [char]0xE9 + '"' + "`r`n" +
        '"Number"=dword:0000002a' + "`r`n" +
        '"Blob"=hex:00,01,fe,ff' + "`r`n" +
        '"RemoveMe"=-' + "`r`n`r`n" + "[-HKEY_CURRENT_USER\$key\RemovedChild]`r`n"
    [IO.File]::WriteAllText($file, $content, [Text.Encoding]::Unicode)
    $before = (Get-FileHash -LiteralPath $file).Hash
    Assert ((Convert-Reg $file $output) -eq 0) 'Converter builds an importer from a UTF-16 registry file.'
    Assert (((Get-FileHash -LiteralPath $file).Hash) -eq $before) 'Conversion leaves the input bytes unchanged.'
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($output))
    $stream = $assembly.GetManifestResourceStream('payload.reg')
    $memory = [IO.MemoryStream]::new()
    try { $stream.CopyTo($memory) } finally { $stream.Dispose() }
    $embedded = $memory.ToArray()
    Assert ([Convert]::ToBase64String($embedded) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($file))) 'Embedded registry payload matches the original byte for byte.'
    $memory.Dispose()
    $binary = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($output))
    Assert ($binary.Contains('level="asInvoker"')) 'Default importer has an explicit non-elevating manifest.'
    Assert ((Convert-Reg $file $output) -ne 0) 'Existing output is protected without --force.'
    Assert ((Convert-Reg $file $output '--force --view 32') -eq 0) 'Explicit overwrite and 32-bit selection compile.'

    $adminOutput = Join-Path $work 'admin.exe'
    Assert ((Convert-Reg $file $adminOutput '--admin --view 64') -eq 0) 'Administrator and 64-bit options compile without importing.'
    $adminBinary = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($adminOutput))
    Assert ($adminBinary.Contains('level="requireAdministrator"')) 'Administrator importer requests elevation in its manifest.'
    Assert (-not $adminBinary.Contains('level="asInvoker"')) 'Administrator importer does not retain an asInvoker manifest.'
    Assert ((Convert-Reg $file (Join-Path $work 'bad-view.exe') '--view invalid') -ne 0) 'Invalid registry view is rejected.'
    $badFile = Join-Path $work 'not-registry.reg'
    [IO.File]::WriteAllText($badFile, 'not a registry file')
    Assert ((Convert-Reg $badFile (Join-Path $work 'bad.exe')) -ne 0) 'Invalid registry-file header is rejected.'
    $ansi = Join-Path $work 'legacy.reg'
    [IO.File]::WriteAllText($ansi, "REGEDIT4`r`n`r`n[HKEY_CURRENT_USER\$key]`r`n`"Legacy`"=`"ok`"`r`n", [Text.Encoding]::ASCII)
    $legacyOutput = Join-Path $work 'legacy.exe'
    Assert ((Convert-Reg $ansi $legacyOutput) -eq 0) 'Legacy REGEDIT4 format compiles.'

    if ($ApplyRegistry) {
        $testKey = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($key)
        $testKey.SetValue('RemoveMe', 'old')
        $child = $testKey.CreateSubKey('RemovedChild')
        $child.Dispose()
        $testKey.Dispose()
        # Input removal verifies that the EXE is self contained.
        Remove-Item -LiteralPath $file
        $fallbackBefore = Get-FallbackLogState $output
        $process = Start-Process -FilePath $output -Wait -PassThru
        Assert ($process.ExitCode -eq 0) 'Generated importer succeeds with no external registry file.'
        $testKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        try {
            Assert ($testKey.GetValue('Text') -ceq ('Unicode: caf' + [char]0xE9)) 'UTF-16 string imports correctly.'
            Assert ($testKey.GetValue('Number') -eq 42) 'DWORD imports correctly.'
            Assert ([Convert]::ToBase64String($testKey.GetValue('Blob')) -ceq 'AAH+/w==') 'Binary data imports correctly.'
            Assert ($null -eq $testKey.GetValue('RemoveMe')) 'Value deletion imports correctly.'
            $removed = $testKey.OpenSubKey('RemovedChild')
            Assert ($null -eq $removed) 'Subkey deletion imports correctly.'
        } finally { $testKey.Dispose() }
        Assert (-not (Test-Path -LiteralPath ($output + '.log'))) 'Successful import creates no adjacent log.'
        Assert ((Get-FallbackLogState $output) -ceq $fallbackBefore) 'Successful import creates or updates no fallback log.'

        $process = Start-Process -FilePath $legacyOutput -Wait -PassThru
        Assert ($process.ExitCode -eq 0) 'Legacy-format importer executes successfully.'
        $testKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($key)
        try { Assert ($testKey.GetValue('Legacy') -ceq 'ok') 'Legacy-format value imports correctly.' }
        finally { $testKey.Dispose() }

        # Simulate a damaged embedded file to exercise reg.exe's actual failure path.
        [IO.File]::WriteAllText($badFile, "Windows Registry Editor Version 5.00`r`n`r`n[HKEY_CURRENT_USER\$key]`r`n`"x`"=`"y`"`r`n", [Text.Encoding]::Unicode)
        $failedOutput = Join-Path $work 'fails.exe'
        Assert ((Convert-Reg $badFile $failedOutput) -eq 0) 'Failure-test importer compiles.'
        $damaged = [IO.File]::ReadAllBytes($failedOutput)
        $headerHex = [BitConverter]::ToString([Text.Encoding]::Unicode.GetBytes('Windows Registry Editor Version 5.00')).Replace('-', '')
        $binaryHex = [BitConverter]::ToString($damaged).Replace('-', '')
        $headerOffset = $binaryHex.IndexOf($headerHex, [StringComparison]::Ordinal)
        Assert ($headerOffset -ge 0) 'Embedded header is located for the damaged-payload test.'
        $damaged[[int]($headerOffset / 2)] = [byte][char]'X'
        [IO.File]::WriteAllBytes($failedOutput, $damaged)
        $fallbackBefore = Get-FallbackLogState $failedOutput
        $process = Start-Process -FilePath $failedOutput -Wait -PassThru
        Assert ($process.ExitCode -ne 0) 'Importer failure returns a nonzero exit code.'
        Assert (-not (Test-Path -LiteralPath ($failedOutput + '.log'))) 'Failed import creates no adjacent log.'
        Assert ((Get-FallbackLogState $failedOutput) -ceq $fallbackBefore) 'Failed import creates or updates no fallback log.'
    }
    Write-Output ('Passed ' + $checks.Count + ' checks. Artifacts: ' + $work)
}
finally {
    if ($ApplyRegistry) {
        # Delete only the GUID-named test key created above.
        [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($key, $false)
        # Clean the empty parent used by an earlier version of this test, if present.
        $oldParent = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\SilentRegTests')
        if ($null -ne $oldParent) {
            $empty = $oldParent.SubKeyCount -eq 0 -and $oldParent.ValueCount -eq 0
            $oldParent.Dispose()
            if ($empty) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKey('Software\SilentRegTests', $false) }
        }
    }
}
