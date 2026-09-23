#Requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $documents = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if ([string]::IsNullOrWhiteSpace($documents)) { throw 'The Documents directory is unavailable. Specify -OutputDirectory outside the repository.' }
    $version = (Get-CSharpTrialPackageMetadata).version
    $buildName = 'build-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $OutputDirectory = Join-Path $documents ("KnowledgeApp_Releases/$version/$buildName")
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
Assert-CSharpPackageNormalPath $outputRoot
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The standalone output must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new output directory; existing output is never overwritten.' }

# Validate the destination before running a build. Initial developer setup still
# uses npm ci; every release rebuilds the current UI and restores C# dependencies.
Push-Location -LiteralPath $repositoryRoot
try {
    & npm.cmd run build
    if ($LASTEXITCODE -ne 0) { throw 'The current React UI could not be built. Run npm ci during initial setup.' }
    $benchmarkProject = Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.StartupBenchmark/KnowledgeApp.StartupBenchmark.csproj'
    & dotnet build $benchmarkProject --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'C# dependency restore or isolated startup-check build failed.' }
    $benchmarkAssembly = Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.StartupBenchmark/bin/Release/net10.0-windows/KnowledgeApp.StartupBenchmark.dll'
    if (-not (Test-Path -LiteralPath $benchmarkAssembly -PathType Leaf)) { throw 'The isolated startup-check assembly was not produced.' }
    & (Join-Path $PSScriptRoot 'New-CSharpStandaloneExe.ps1') -OutputDirectory $outputRoot -StartupBenchmarkAssembly $benchmarkAssembly
} finally {
    Pop-Location
}
