param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1, 20)][int]$Runs = 5
)
$ErrorActionPreference = 'Stop'
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
if ([IO.Path]::GetFileName($executablePath) -ne 'KnowledgeApp.StartupBenchmark.exe') { throw 'Only the isolated benchmark executable is allowed.' }
$outputFile = [IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [IO.Path]::GetDirectoryName($outputFile)
for ($ancestor = $outputDirectory; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
    if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw 'Measurement results must stay outside Git repositories.' }
}
[void][IO.Directory]::CreateDirectory($outputDirectory)
$root = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-data-check-' + [guid]::NewGuid().ToString('D'))
$results = [Collections.Generic.List[object]]::new()
$process = $null

function Save-Measurements {
    $temporary = $outputFile + '.' + [guid]::NewGuid().ToString('N') + '.partial'
    try {
        $json = ConvertTo-Json -InputObject @($results.ToArray()) -Depth 8
        [IO.File]::WriteAllText($temporary, ($json -replace "`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $outputFile -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

try {
    # Separate first initialization, one warm-up restart, and measured restarts.
    for ($run = 0; $run -lt ($Runs + 2); $run++) {
        $sample = if ($run -eq 0) { 'initial' } elseif ($run -eq 1) { 'warmup' } else { 'warm-' + ($run - 1) }
        $row = [ordered]@{
            run = $run; sample = $sample; initial = ($run -eq 0); measured = ($run -ge 2)
            startupStatus = 'starting'; processToLoginMs = $null; stdoutObservedMs = $null
            stages = $null; exitCode = $null; shutdownStatus = 'pending'; shutdownWarnings = @(); stderr = ''
        }
        $results.Add($row)
        Save-Measurements
        $start = [Diagnostics.ProcessStartInfo]::new($executablePath)
        $start.UseShellExecute = $false
        $start.CreateNoWindow = $true
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $start.ArgumentList.Add($root)
        $timer = [Diagnostics.Stopwatch]::StartNew()
        $startUnixMicroseconds = [long](([DateTime]::UtcNow.Ticks - [long]621355968000000000) / 10)
        $process = [Diagnostics.Process]::Start($start)
        $errors = $process.StandardError.ReadToEndAsync()
        $line = $process.StandardOutput.ReadLineAsync()
        try {
            if (-not $line.Wait(65000)) { throw 'Startup measurement timed out.' }
            $row.stdoutObservedMs = [Math]::Round($timer.Elapsed.TotalMilliseconds, 3)
            if (-not $line.Result -or -not $line.Result.StartsWith('READY ')) { throw 'Login was not confirmed.' }
            $ready = $line.Result.Substring(6) | ConvertFrom-Json
            $elapsed = ([long]$ready.readyUnixMicroseconds - $startUnixMicroseconds) / 1000.0
            if (-not $ready.readyUnixMicroseconds -or $elapsed -lt 0 -or $elapsed -gt 65000) { throw 'Invalid startup timestamp.' }
            $row.processToLoginMs = [Math]::Round($elapsed, 3)
            $row.stages = $ready.stages
            $row.startupStatus = 'ready'
            # Preserve the observed startup before waiting for any shutdown/cleanup.
            Save-Measurements
            Write-Host "$sample ready after $($row.processToLoginMs) ms."
            $remainingOutput = $process.StandardOutput.ReadToEndAsync()
            if (-not $process.WaitForExit(15000)) { throw 'Owned benchmark process did not close.' }
            $row.exitCode = $process.ExitCode
            if (-not $errors.Wait(5000) -or -not $remainingOutput.Wait(5000)) { throw 'Benchmark diagnostic streams did not close.' }
            $row.stderr = $errors.Result.Trim()
            $closedLine = @($remainingOutput.Result -split '\r?\n' | Where-Object { $_.StartsWith('CLOSED ') })
            if ($closedLine.Count -ne 1) { throw 'Benchmark shutdown was not reported.' }
            $closed = $closedLine[0].Substring(7) | ConvertFrom-Json
            $row.shutdownWarnings = @($closed.shutdownWarnings)
            $row.shutdownStatus = if ($process.ExitCode -ne 0 -or $closed.failed) { 'failed' } elseif ($row.shutdownWarnings.Count -gt 0) { 'warning' } else { 'clean' }
            Save-Measurements
            if ($row.shutdownStatus -eq 'failed') { throw "Benchmark failed: $($row.stderr)" }
            if ($row.shutdownStatus -eq 'warning') { Write-Warning "$sample startup completed; $($row.shutdownWarnings -join ' ')" }
        }
        catch {
            if ($row.startupStatus -ne 'ready') { $row.startupStatus = 'failed' }
            if ($row.shutdownStatus -eq 'pending') { $row.shutdownStatus = 'incomplete' }
            if ($errors.IsCompletedSuccessfully) { $row.stderr = $errors.Result.Trim() }
            $row['failure'] = $_.Exception.Message
            Save-Measurements
            throw
        }
        finally {
            if ($process -and $process.HasExited) { $process.Dispose(); $process = $null }
        }
        Start-Sleep -Milliseconds 750
    }
    $results | ForEach-Object { [pscustomobject]@{ sample = $_.sample; measured = $_.measured; milliseconds = $_.processToLoginMs; shutdown = $_.shutdownStatus } } | Format-Table
}
finally {
    # Only our exact, newly generated TEMP/GUID root; never follow a guessed path.
    if (([IO.Path]::GetDirectoryName($root) -ne [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath())) -or
        ([IO.Path]::GetFileName($root) -notmatch '^knowledgeapp-data-check-[0-9a-f-]{36}$')) { throw 'Unsafe cleanup root.' }
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $root) {
        try { Remove-Item -LiteralPath $root -Recurse -Force }
        catch { Write-Warning "Owned synthetic data was retained at $root. Measurement results are already saved." }
    }
}
