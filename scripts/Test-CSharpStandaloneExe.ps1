#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Executable,
    [string]$StartupBenchmarkAssembly,
    [switch]$KeepExtracted
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$source = [IO.Path]::GetFullPath($Executable)
Assert-CSharpPackageNormalPath $source
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Standalone executable was not found.' }
if ($StartupBenchmarkAssembly) {
    $StartupBenchmarkAssembly = (Resolve-Path -LiteralPath $StartupBenchmarkAssembly).Path
    Assert-CSharpPackageNormalPath $StartupBenchmarkAssembly
    if ([IO.Path]::GetFileName($StartupBenchmarkAssembly) -cne 'KnowledgeApp.StartupBenchmark.dll') { throw 'Only the isolated startup benchmark assembly is allowed.' }
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-data-check-' + [Guid]::NewGuid().ToString('D'))
Assert-CSharpPackageNormalPath $testRoot
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$ownedRoot = [IO.Path]::GetFullPath($testRoot)
try {
    $project = Join-Path (Split-Path -Parent $PSScriptRoot) 'src-csharp/KnowledgeApp.StandaloneCheck/KnowledgeApp.StandaloneCheck.csproj'
    $buildOutput = & dotnet build $project --configuration Release --artifacts-path (Join-Path $testRoot 'build') 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('Standalone test hook build failed: ' + ($buildOutput -join "`n")) }
    $hook = @(Get-ChildItem -LiteralPath (Join-Path $testRoot 'build/bin') -Filter 'KnowledgeApp.StandaloneCheck.dll' -Recurse -File)
    if ($hook.Count -ne 1) { throw 'Standalone test hook was not built unambiguously.' }
    $launchRoot = Join-Path $testRoot 'launch'
    [IO.Directory]::CreateDirectory($launchRoot) | Out-Null
    $copy = Join-Path $launchRoot 'KnowledgeApp.CSharp.exe'
    [IO.File]::Copy($source, $copy, $false)
    $timings = @()
    for ($run = 0; $run -lt 2; $run++) {
        $resultPath = Join-Path $testRoot 'result.json'
        if (Test-Path -LiteralPath $resultPath) { Remove-Item -LiteralPath $resultPath -Force }
        $info = [Diagnostics.ProcessStartInfo]::new($copy)
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.WorkingDirectory = $launchRoot
        $info.Environment['DOTNET_STARTUP_HOOKS'] = $hook[0].FullName
        $info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $testRoot 'extract'
        $info.Environment['KNOWLEDGEAPP_STANDALONE_TEST_ROOT'] = $testRoot
        if ($StartupBenchmarkAssembly) { $info.Environment['KNOWLEDGEAPP_STANDALONE_BENCHMARK'] = $StartupBenchmarkAssembly }
        $info.Environment['DOTNET_ROOT'] = Join-Path $testRoot 'no-installed-runtime'
        $info.Environment['DOTNET_ROOT_X64'] = Join-Path $testRoot 'no-installed-runtime'
        $info.Environment['DOTNET_MULTILEVEL_LOOKUP'] = '0'
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $info
        $timer = [Diagnostics.Stopwatch]::StartNew()
        try {
            if (-not $process.Start()) { throw 'Standalone child process did not start.' }
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(60000)) { $process.Kill($true); throw 'Standalone child process timed out.' }
            $timer.Stop()
            $timings += [math]::Round($timer.Elapsed.TotalMilliseconds)
            if ($process.ExitCode -ne 0) { throw ('Standalone check failed: ' + $stderr.GetAwaiter().GetResult() + $stdout.GetAwaiter().GetResult()) }
        } finally { $process.Dispose() }
        $result = [IO.File]::ReadAllText((Join-Path $testRoot 'result.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
        if ($result.passed -ne $true -or $result.userDatabaseOpened -ne $false) { throw 'Standalone verification result is invalid.' }
        if ($result.applicationDirectoriesProtected -ne $true -or $result.rememberedCodexLocationProtected -ne $true) { throw 'Executable directory boundaries were not verified.' }
        if ($StartupBenchmarkAssembly -and $result.packagedLoginVerified -ne $true) { throw 'Packaged login was not verified.' }
        if (@(Get-ChildItem -LiteralPath $launchRoot -Force).Count -ne 1) { throw 'Application state was written beside the executable.' }
    }
    & (Join-Path $PSScriptRoot 'check-no-runtime-data.ps1') -ReleaseDirectory $result.extractedDirectory | Out-Host
    if (-not $?) { throw 'Extracted standalone payload data-safety check failed.' }
    [PSCustomObject]@{passed=$true; packagedLoginVerified=$result.packagedLoginVerified; applicationDirectoriesProtected=$result.applicationDirectoriesProtected; rememberedCodexLocationProtected=$result.rememberedCodexLocationProtected; firstVerificationMilliseconds=$timings[0]; cachedVerificationMilliseconds=$timings[1]; userDatabaseOpened=$false; extractedDirectory=if ($KeepExtracted) { $result.extractedDirectory } else { $null }; testRoot=if ($KeepExtracted) { $testRoot } else { $null }}
} catch {
    throw ('Standalone verification in ' + $testRoot + ': ' + $_.Exception.Message)
} finally {
    if (-not $KeepExtracted) {
        # Delete only this test's new GUID directory; never a supplied path.
        $resolved = [IO.Path]::GetFullPath($testRoot)
        if ($resolved -cne $ownedRoot -or [IO.Path]::GetDirectoryName($resolved) -cne [IO.Path]::GetTempPath().TrimEnd('\', '/')) { throw 'Unexpected standalone cleanup path.' }
        Assert-CSharpPackageNormalPath $resolved
        if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
