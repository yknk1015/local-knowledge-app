#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$StartupBenchmarkAssembly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
Assert-CSharpPackageNormalPath $outputRoot
if ($outputRoot.Equals($repositoryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($repositoryRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The standalone output must be outside the repository.'
}
if (Test-Path -LiteralPath $outputRoot) { throw 'Choose a new output directory; existing output is never overwritten.' }
if ($StartupBenchmarkAssembly) {
    $StartupBenchmarkAssembly = (Resolve-Path -LiteralPath $StartupBenchmarkAssembly).Path
    Assert-CSharpPackageNormalPath $StartupBenchmarkAssembly
    if ([IO.Path]::GetFileName($StartupBenchmarkAssembly) -cne 'KnowledgeApp.StartupBenchmark.dll' -or
        -not (Test-Path -LiteralPath $StartupBenchmarkAssembly -PathType Leaf)) { throw 'Only the isolated startup benchmark assembly is allowed.' }
}
& (Join-Path $PSScriptRoot 'check-no-runtime-data.ps1')
if (-not $?) { throw 'Source data-safety check failed.' }
[IO.Directory]::CreateDirectory($outputRoot) | Out-Null
$workRoot = Join-Path $outputRoot 'build'

# Reuse the reviewed dependency allowlist, exact UI hashes and license inventory.
$package = & (Join-Path $PSScriptRoot 'New-CSharpTrialPackage.ps1') -OutputDirectory (Join-Path $workRoot 'verified-package') | Select-Object -Last 1
if (-not $package.packageDirectory) { throw 'Verified source package was not created.' }
$sourcePackage = $package.packageDirectory
$assetsRoot = Join-Path $workRoot 'assets'
[IO.Directory]::CreateDirectory($assetsRoot) | Out-Null
Copy-Item -LiteralPath (Join-Path $sourcePackage 'notices') -Destination (Join-Path $assetsRoot 'notices') -Recurse
$readme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer/StandaloneReadme.ja.txt'), [Text.Encoding]::UTF8)
[IO.File]::WriteAllText((Join-Path $assetsRoot 'STANDALONE-README.txt'), $readme, [Text.UTF8Encoding]::new($false))

# A separate helper preserves the network timeout boundary. Give it its own
# self-contained host; .NET deliberately does not extract hostfxr/hostpolicy DLLs
# from the main bundle, so a plain helper would depend on installed .NET.
$probeRoot = Join-Path $workRoot 'probe'
& dotnet publish (Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.PathProbe/KnowledgeApp.PathProbe.csproj') --configuration Release --runtime win-x64 --self-contained true --artifacts-path (Join-Path $workRoot 'probe-artifacts') --output $probeRoot -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Standalone path helper publish failed.' }
$probeFiles = @(Get-ChildItem -LiteralPath $probeRoot -Force)
if ($probeFiles.Count -ne 1 -or $probeFiles[0].Name -cne 'KnowledgeApp.PathProbe.exe') { throw 'Path helper must be one self-contained executable.' }
$hostProject = Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.CSharp/KnowledgeApp.CSharp.csproj'
$hostArtifacts = Join-Path $workRoot 'host-artifacts'
& dotnet restore $hostProject --runtime win-x64 --artifacts-path $hostArtifacts -p:StandaloneExe=true
if ($LASTEXITCODE -ne 0) { throw 'Standalone runtime restore failed.' }
$hostAssets = [IO.File]::ReadAllText((Join-Path $hostArtifacts 'obj/KnowledgeApp.CSharp/project.assets.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$runtimeNotices = @()
foreach ($dependency in $hostAssets.project.frameworks.PSObject.Properties.Value.downloadDependencies) {
    if ($dependency.name -notin @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) { continue }
    if ($dependency.version -cnotmatch '^\[([0-9.]+), \1\]$') { throw 'Runtime package version is not pinned.' }
    $runtimeVersion = $Matches[1]
    $runtimeFolder = $null
    foreach ($cache in $hostAssets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $cache ($dependency.name.ToLowerInvariant() + '/' + $runtimeVersion)
        Assert-CSharpPackageNormalPath $candidate
        if (Test-Path -LiteralPath $candidate -PathType Container) { $runtimeFolder = $candidate; break }
    }
    if (-not $runtimeFolder) { throw 'Bundled runtime license source was not found.' }
    $noticeDirectory = Join-Path $assetsRoot ('notices/nuget/' + $dependency.name + '.' + $runtimeVersion)
    [IO.Directory]::CreateDirectory($noticeDirectory) | Out-Null
    $notices = @(Get-ChildItem -LiteralPath $runtimeFolder -File | Where-Object { $_.Name -imatch '^(LICENSE|THIRD-PARTY-NOTICES)(\.TXT)?$|\.nuspec$' })
    if (-not @($notices | Where-Object { $_.Name -imatch '^LICENSE' }).Count) { throw 'Bundled runtime license is missing.' }
    foreach ($notice in $notices) {
        Assert-CSharpPackageNormalPath $notice.FullName
        [IO.File]::Copy($notice.FullName, (Join-Path $noticeDirectory ($notice.Name + '.txt')), $false)
    }
    $runtimeNotices += [ordered]@{package=$dependency.name; version=$runtimeVersion; noticeFiles=@($notices.Name)}
}
if ($runtimeNotices.Count -ne 2) { throw 'Both bundled .NET runtime license sets are required.' }
$inventoryPath = Join-Path $assetsRoot 'notices/dependency-inventory.json'
$inventory = [IO.File]::ReadAllText($inventoryPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
$inventory | Add-Member -NotePropertyName bundledRuntimes -NotePropertyValue $runtimeNotices
[IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
$publishRoot = Join-Path $workRoot 'publish'
& dotnet publish $hostProject --configuration Release --runtime win-x64 --self-contained true --no-restore --artifacts-path $hostArtifacts --output $publishRoot -p:StandaloneExe=true -p:DebugType=None -p:DebugSymbols=false "-p:StandaloneProbeDirectory=$probeRoot" "-p:StandaloneAssetsDirectory=$assetsRoot"
if ($LASTEXITCODE -ne 0) { throw 'Standalone application publish failed.' }
$files = @(Get-ChildItem -LiteralPath $publishRoot -Force)
if ($files.Count -ne 1 -or $files[0].Name -cne 'KnowledgeApp.CSharp.exe' -or $files[0].PSIsContainer) {
    throw 'The published output is not one executable; no standalone release was approved.'
}
$distribution = Join-Path $outputRoot 'standalone'
[IO.Directory]::CreateDirectory($distribution) | Out-Null
$executable = Join-Path $distribution 'KnowledgeApp.CSharp.exe'
[IO.File]::Copy($files[0].FullName, $executable, $false)
& (Join-Path $PSScriptRoot 'check-no-runtime-data.ps1') -ReleaseDirectory $distribution
if (-not $?) { throw 'Standalone output data-safety check failed.' }
$verification = & (Join-Path $PSScriptRoot 'Test-CSharpStandaloneExe.ps1') -Executable $executable -StartupBenchmarkAssembly $StartupBenchmarkAssembly
$result = [ordered]@{
    executable=$executable; version=(Get-CSharpTrialPackageMetadata).version
    bytes=(Get-Item -LiteralPath $executable).Length; sha256=(Get-CSharpPackageSha256 $executable)
    runtime='win-x64-self-contained'; webView2Runtime='required-on-target'; applicationInstallationRequired=$false
    dataRootIdentifier='jp.local.webknowledgesystem.csharp'; firstVerificationMilliseconds=$verification.firstVerificationMilliseconds
    cachedVerificationMilliseconds=$verification.cachedVerificationMilliseconds; verificationPassed=$verification.passed
    packagedLoginVerified=$verification.packagedLoginVerified
    applicationDirectoriesProtected=$verification.applicationDirectoriesProtected
    rememberedCodexLocationProtected=$verification.rememberedCodexLocationProtected
    allTargetDeviceChecksPassed=$false
}
[IO.File]::WriteAllText((Join-Path $outputRoot 'STANDALONE-MANIFEST.json'), ($result | ConvertTo-Json -Depth 4).Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $outputRoot 'STANDALONE-README.txt'), $readme, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $outputRoot 'SHA256SUMS.txt'), ($result.sha256 + "  standalone/KnowledgeApp.CSharp.exe`n"), [Text.UTF8Encoding]::new($false))
[PSCustomObject]$result
