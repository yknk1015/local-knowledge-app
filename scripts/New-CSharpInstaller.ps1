[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PackageDirectory,
    [string]$SyntheticTestRoot,
    [switch]$MissingRuntimeTest
)

. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$verified = Test-CSharpTrialPackage $packageRoot
$template = Join-Path $PSScriptRoot 'installer/KnowledgeApp.CSharp.Trial.nsi'
Assert-CSharpPackageNormalPath $template
$makensis = $env:KNOWLEDGEAPP_NSIS_COMPILER
if (-not $makensis) {
    $command = Get-Command makensis.exe -CommandType Application -ErrorAction SilentlyContinue
    if ($command) { $makensis = $command.Source }
}
if (-not $makensis) { throw 'Install NSIS 3 and add makensis.exe to PATH, or explicitly set KNOWLEDGEAPP_NSIS_COMPILER to its absolute path. No compiler is downloaded automatically.' }
if (-not [IO.Path]::IsPathFullyQualified($makensis)) { throw 'The explicitly selected NSIS compiler path must be absolute.' }
Assert-CSharpPackageNormalPath $makensis
if (-not (Test-Path -LiteralPath $makensis -PathType Leaf)) { throw 'The explicitly selected NSIS compiler does not exist.' }
if ($MissingRuntimeTest -and -not $SyntheticTestRoot) { throw 'A missing-runtime fixture is restricted to an isolated synthetic installer root.' }

$installRoot = '$LOCALAPPDATA\Programs\KnowledgeApp-CSharp'
$productKey = 'Software\KnowledgeApp\CSharp'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\KnowledgeApp-CSharp'
$shortcut = '$SMPROGRAMS\KnowledgeApp (C#).lnk'
$owner = 'KnowledgeApp.CSharp.Installer.Release.v1'
$testRunId = $null
if ($SyntheticTestRoot) {
    $testRoot = [IO.Path]::GetFullPath($SyntheticTestRoot).TrimEnd('\', '/')
    Assert-CSharpPackageNormalPath $testRoot
    $expectedParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    $testLeaf = [IO.Path]::GetFileName($testRoot)
    if ([IO.Path]::GetDirectoryName($testRoot) -ine $expectedParent -or
        $testLeaf -cnotmatch '^knowledgeapp-csharp-installer-check-([0-9a-f]{32})$') {
        throw 'Installer regression may target only a GUID-named direct child of the OS temp directory.'
    }
    $testRunId = $Matches[1]
    $marker = Join-Path $testRoot '.synthetic-installer-check'
    Assert-CSharpPackageNormalPath $marker
    if (-not [IO.File]::Exists($marker) -or
        [IO.File]::ReadAllText($marker, [Text.Encoding]::UTF8) -cne "KnowledgeApp.InstallerCheck.$testRunId`n") {
        throw 'The synthetic installer root ownership marker is absent or invalid.'
    }
    $installRoot = Join-Path $testRoot 'installed'
    $productKey = "Software\KnowledgeApp\InstallerCheck\$testRunId"
    $uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\KnowledgeApp-CSharp-InstallerCheck-$testRunId"
    $shortcut = Join-Path $testRoot 'shortcut.lnk'
    $owner = "KnowledgeApp.CSharp.InstallerCheck.$testRunId"
}

$work = Join-Path ([IO.Path]::GetDirectoryName($packageRoot)) ('installer-' + [Guid]::NewGuid().ToString('N'))
Assert-CSharpPackageNormalPath $work
if (Test-Path -LiteralPath $work) { throw 'An existing installer build directory will not be overwritten.' }
[IO.Directory]::CreateDirectory($work) | Out-Null

function Quote-Nsis([string]$Text, [switch]$RuntimeVariables) {
    if ($Text -match '[\r\n\x00]') { throw 'Unexpected NSIS control character.' }
    $safe = $Text.Replace('$', '$$').Replace('"', '$\"')
    if ($RuntimeVariables) {
        foreach ($name in @('LOCALAPPDATA', 'SMPROGRAMS')) { $safe = $safe.Replace('$$' + $name, '$' + $name) }
    }
    return '"' + $safe + '"'
}
function Write-Generated([string]$Name, [string[]]$Lines) {
    $path = Join-Path $work $Name
    [IO.File]::WriteAllText($path, ($Lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    return $path
}

$files = @(Get-CSharpPackageFiles $packageRoot | Sort-Object FullName)
$inputHashes = @($files | ForEach-Object { (Get-CSharpPackageSha256 $_.FullName) + ' ' + $_.FullName })
$assetNames = @($files | Where-Object { $_.DirectoryName -ieq (Join-Path $packageRoot 'ui/assets') } | ForEach-Object { $_.Name })
$assetIndex = Write-Generated 'installed-assets.txt' $assetNames
$copy = [Collections.Generic.List[string]]::new()
$validate = [Collections.Generic.List[string]]::new()
$uninstallValidate = [Collections.Generic.List[string]]::new()
$delete = [Collections.Generic.List[string]]::new()
$removeDirectories = [Collections.Generic.List[string]]::new()
$directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $files) {
    $relative = $file.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
    if (-not (Test-CSharpPackageRelativePath $relative)) { throw 'An unapproved file cannot be placed into the installer.' }
    $windowsRelative = $relative.Replace('/', '\')
    $parent = [IO.Path]::GetDirectoryName($windowsRelative)
    $out = if ($parent) { '$INSTDIR\' + $parent } else { '$INSTDIR' }
    # All runtime paths are produced from the validated package allowlist. Source
    # files are embedded individually; File /r and arbitrary resources are absent.
    $copy.Add('SetOutPath "' + $out + '"')
    $copy.Add('File ' + (Quote-Nsis ('/oname=' + [IO.Path]::GetFileName($windowsRelative))) + ' ' + (Quote-Nsis $file.FullName))
    $validate.Add('Push "$INSTDIR\' + $windowsRelative + '"')
    $validate.Add('Call RequireNormalPath')
    $uninstallValidate.Add('Push "$INSTDIR\' + $windowsRelative + '"')
    $uninstallValidate.Add('Call un.RequireNormalPath')
    $delete.Add('Delete "$INSTDIR\' + $windowsRelative + '"')
    while ($parent) { [void]$directories.Add($parent); $parent = [IO.Path]::GetDirectoryName($parent) }
}
foreach ($directory in @($directories | Sort-Object Length -Descending)) { $removeDirectories.Add('RMDir "$INSTDIR\' + $directory + '"') }
$copyInclude = Write-Generated 'payload.nsh' $copy.ToArray()
$validateInclude = Write-Generated 'validate.nsh' $validate.ToArray()
$uninstallValidateInclude = Write-Generated 'uninstall-validate.nsh' $uninstallValidate.ToArray()
$deleteInclude = Write-Generated 'delete.nsh' $delete.ToArray()
$removeDirectoriesInclude = Write-Generated 'remove-directories.nsh' $removeDirectories.ToArray()
$output = Join-Path $work ('KnowledgeApp-CSharp-' + $verified.version + '-setup.exe')
$configuration = [Collections.Generic.List[string]]::new()
foreach ($entry in @{
    OUTPUT_FILE=$output; INSTALL_ROOT=$installRoot; PRODUCT_KEY=$productKey; UNINSTALL_KEY=$uninstallKey;
    SHORTCUT_PATH=$shortcut; OWNER_FILE='.knowledgeapp-installer-owner'; OWNER_VALUE=$owner;
    ACTIVITY_FILE='.knowledgeapp-installation-lock'; ASSET_INDEX_FILE='.knowledgeapp-installed-assets'; ASSET_INDEX_SOURCE=$assetIndex; PAYLOAD_INCLUDE=$copyInclude;
    VALIDATE_INCLUDE=$validateInclude; UNINSTALL_VALIDATE_INCLUDE=$uninstallValidateInclude; DELETE_INCLUDE=$deleteInclude;
    REMOVE_DIRECTORIES_INCLUDE=$removeDirectoriesInclude
}.GetEnumerator()) {
    $configuration.Add('!define ' + $entry.Key + ' ' + (Quote-Nsis $entry.Value -RuntimeVariables:($entry.Key -in @('INSTALL_ROOT','SHORTCUT_PATH') -and -not $testRunId)))
}
$configuration.Add('!define PHASE ' + $verified.phase)
$configuration.Add('!define PRODUCT_VERSION ' + (Quote-Nsis $verified.version))
if ($MissingRuntimeTest) { $configuration.Add('!define SYNTHETIC_MISSING_RUNTIME') }
$configInclude = Write-Generated 'config.nsh' $configuration.ToArray()
& $makensis '/V2' '/INPUTCHARSET' 'UTF8' ('/DCONFIG_INCLUDE=' + $configInclude) $template
if ($LASTEXITCODE -ne 0 -or -not [IO.File]::Exists($output)) { throw 'The C# release NSIS compiler failed.' }
$after = Test-CSharpTrialPackage $packageRoot
if ($after.files -ne $verified.files) { throw 'The package changed during installer generation.' }
$afterHashes = @(Get-CSharpPackageFiles $packageRoot | Sort-Object FullName | ForEach-Object { (Get-CSharpPackageSha256 $_.FullName) + ' ' + $_.FullName })
if ([string]::Join("`n", $inputHashes) -cne [string]::Join("`n", $afterHashes)) { throw 'The package content changed during installer generation.' }
[PSCustomObject]@{
    installer=$output; sha256=(Get-CSharpPackageSha256 $output); phase=$verified.phase;
    mode='csharp-production'; version=$verified.version; installRoot=$installRoot; productRegistryKey=$productKey;
    uninstallRegistryKey=$uninstallKey; shortcut=$shortcut; syntheticRunId=$testRunId;
    inputPackage=$packageRoot; files=$files.Count; productionDataOpened=$false; cutoverAllowed=$verified.cutoverAllowed; allFinalChecksPassed=$verified.allFinalChecksPassed
}
