[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PackageDirectory, [switch]$KeepArtifacts)

. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$source = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$package = Test-CSharpTrialPackage $source
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-csharp-installer-check-' + $runId)
Assert-CSharpPackageNormalPath $testRoot
if (Test-Path -LiteralPath $testRoot) { throw 'The owned installer fixture must not preexist.' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $testRoot '.synthetic-installer-check'), "KnowledgeApp.InstallerCheck.$runId`n", $utf8)
$installRoot = Join-Path $testRoot 'installed'
$productSubkey = "Software\KnowledgeApp\InstallerCheck\$runId"
$uninstallSubkey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\KnowledgeApp-CSharp-InstallerCheck-$runId"
$checks = 0
$children = [Collections.Generic.List[Diagnostics.Process]]::new()

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Installer regression failed: $Message" }
    $script:checks++
}
function Write-Fixture([string]$Path, [string]$Text) {
    $absolute = [IO.Path]::GetFullPath($Path)
    if (-not $absolute.StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing a fixture outside the owned root.' }
    Assert-CSharpPackageNormalPath $absolute
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($absolute)) | Out-Null
    [IO.File]::WriteAllText($absolute, $Text, $utf8)
}
function Run-Setup([string]$Executable, [int]$ExpectedCode, [switch]$Uninstall) {
    Assert-CSharpPackageNormalPath $Executable
    if (-not [IO.Path]::GetFullPath($Executable).StartsWith($testRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Only an owned synthetic installer can execute.' }
    $arguments = if ($Uninstall) { "/S _?=$installRoot" } else { '/S' }
    Write-Output ("Running owned " + $(if ($Uninstall) { 'uninstaller' } else { 'installer' }) + "; expecting exit $ExpectedCode after $script:checks passed checks.")
    $child = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $children.Add($child)
    if (-not $child.WaitForExit(60000)) { throw 'An owned installer child exceeded 60 seconds.' }
    Assert-Check ($child.ExitCode -eq $ExpectedCode) "Expected exit $ExpectedCode; received $($child.ExitCode)."
}
function Run-Uninstall([int]$ExpectedCode) {
    $copy = Join-Path $testRoot ('uninstall-run-' + [Guid]::NewGuid().ToString('N') + '.exe')
    [IO.File]::Copy((Join-Path $installRoot 'Uninstall-KnowledgeApp-CSharp.exe'), $copy, $false)
    Run-Setup $copy $ExpectedCode -Uninstall
}
function Check-Payload {
    foreach ($file in Get-CSharpPackageFiles $source) {
        $relative = $file.FullName.Substring($source.Length + 1)
        $installed = Join-Path $installRoot $relative
        Assert-CSharpPackageNormalPath $installed
        if (-not [IO.File]::Exists($installed) -or (Get-CSharpPackageSha256 $installed) -cne (Get-CSharpPackageSha256 $file.FullName)) {
            throw 'The installed payload differs from the verified source package.'
        }
    }
    $script:checks++
}
function Read-TestRegistry([string]$Subkey, [string]$Name) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($Subkey, $false)
    try { if ($null -eq $key) { return $null }; return $key.GetValue($Name) }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

try {
    # Read only the validated program package. Never copy the real data root,
    # currently installed application or production/rehearsal databases.
    $payload = Join-Path $testRoot 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    foreach ($file in Get-CSharpPackageFiles $source) {
        $destination = Join-Path $payload $file.FullName.Substring($source.Length + 1)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($file.FullName, $destination, $false)
    }
    $sentinel = Join-Path $testRoot 'mock-user-data/data/knowledge.db'
    $legacy = Join-Path $testRoot 'legacy-install/legacy.exe'
    Write-Fixture $sentinel 'Synthetic data-preservation sentinel, not a SQLite database.'
    Write-Fixture $legacy 'Synthetic legacy-application sentinel, not an executable.'
    $sentinelHash = Get-CSharpPackageSha256 $sentinel
    $legacyHash = Get-CSharpPackageSha256 $legacy

    Write-Output 'Building the GUID-isolated NSIS lifecycle fixtures...'
    $normal = & (Join-Path $PSScriptRoot 'New-CSharpInstaller.ps1') -PackageDirectory $payload -SyntheticTestRoot $testRoot
    $missing = & (Join-Path $PSScriptRoot 'New-CSharpInstaller.ps1') -PackageDirectory $payload -SyntheticTestRoot $testRoot -MissingRuntimeTest
    Assert-Check ($normal.syntheticRunId -ceq $runId -and $normal.mode -ceq 'csharp-production') 'The installer must be bound to the owned test root.'
    Run-Setup $missing.installer 4
    Assert-Check (-not (Test-Path -LiteralPath $installRoot)) 'Missing runtime must not create an installation.'
    Assert-Check ($null -eq (Read-TestRegistry $productSubkey 'InstallLocation')) 'Missing runtime must not register a product.'

    Write-Fixture (Join-Path $installRoot 'unknown.txt') 'Unowned installation directory sentinel.'
    Run-Setup $normal.installer 3
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installRoot 'unknown.txt'), $utf8) -ceq 'Unowned installation directory sentinel.') 'Unowned files must survive a refused installation.'
    [IO.File]::Delete((Join-Path $installRoot 'unknown.txt'))
    [IO.Directory]::Delete($installRoot, $false)

    Run-Setup $normal.installer 0
    Check-Payload
    Assert-Check ((Read-TestRegistry $productSubkey 'InstallLocation') -ieq $installRoot) 'The isolated product registration is incorrect.'
    Assert-Check ((Read-TestRegistry $uninstallSubkey 'DisplayVersion') -ceq $package.version) 'The uninstall version is incorrect.'
    Assert-Check ((Read-TestRegistry $uninstallSubkey 'DisplayName') -ceq 'KnowledgeApp (C#)') 'The release display name is incorrect.'
    $shortcutPath = Join-Path $testRoot 'shortcut.lnk'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($shortcutPath)
        try {
            Assert-Check ($shortcut.TargetPath -ieq (Join-Path $installRoot 'KnowledgeApp.CSharp.exe')) 'The shortcut must target the installed executable.'
            Assert-Check ([string]::IsNullOrEmpty($shortcut.Arguments)) 'The default release shortcut must use the approved no-argument C# production launch.'
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }

    $ownerPath = Join-Path $installRoot '.knowledgeapp-installer-owner'
    $ownerText = [IO.File]::ReadAllText($ownerPath, $utf8)
    Write-Fixture $ownerPath 'Unowned installation marker.'
    Run-Setup $normal.installer 3
    Check-Payload
    Run-Uninstall 3
    Check-Payload
    Write-Fixture $ownerPath $ownerText

    $activity = Join-Path $installRoot '.knowledgeapp-installation-lock'
    Write-Fixture $activity 'not empty'
    Run-Setup $normal.installer 3
    Check-Payload
    Write-Fixture $activity ''
    $lease = [IO.FileStream]::new($activity, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Run-Setup $normal.installer 2
        Check-Payload
        Run-Uninstall 2
        Check-Payload
    }
    finally { $lease.Dispose() }
    Run-Setup $normal.installer 0
    Check-Payload

    $unownedAsset = Join-Path $installRoot 'ui/assets/Unowned_test.png'
    Write-Fixture $unownedAsset 'Unowned image sentinel.'
    Run-Setup $normal.installer 3
    Assert-Check ([IO.File]::ReadAllText($unownedAsset, $utf8) -ceq 'Unowned image sentinel.') 'Unknown asset content must not be deleted during update.'
    Check-Payload
    [IO.File]::Delete($unownedAsset)

    $junction = Join-Path $installRoot 'ui/assets/linked-test'
    $junctionTarget = Join-Path $testRoot 'mock-user-data'
    New-Item -ItemType Junction -Path $junction -Value $junctionTarget | Out-Null
    try {
        Run-Setup $normal.installer 3
        Assert-Check ((Get-CSharpPackageSha256 $sentinel) -ceq $sentinelHash) 'An update must reject a junction without changing its target.'
        Check-Payload
    }
    finally { [IO.Directory]::Delete($junction, $false) }

    # Emulate an old hashed UI asset that the previous installer explicitly
    # owned. The new payload removes only this indexed name, never other files.
    $obsolete = Join-Path $installRoot 'ui/assets/Obsolete_owned.js'
    Write-Fixture $obsolete '// synthetic previous-build asset'
    [IO.File]::AppendAllText((Join-Path $installRoot '.knowledgeapp-installed-assets'), "Obsolete_owned.js`n", $utf8)
    Run-Setup $normal.installer 0
    Assert-Check (-not [IO.File]::Exists($obsolete)) 'An explicitly owned obsolete UI asset must be removed during update.'
    Check-Payload

    $ownedDll = Join-Path $installRoot 'KnowledgeApp.Data.dll'
    $busy = [IO.FileStream]::new($ownedDll, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        Run-Uninstall 5
        Assert-Check ($null -ne (Read-TestRegistry $productSubkey 'InstallLocation')) 'A partial uninstall must retain registration for a retry.'
        Assert-Check ([IO.File]::Exists((Join-Path $installRoot '.knowledgeapp-installer-owner'))) 'A partial uninstall must retain ownership for a retry.'
    }
    finally { $busy.Dispose() }
    Run-Setup $normal.installer 0
    Check-Payload
    Write-Fixture (Join-Path $installRoot 'unknown.txt') 'Preserve unowned residual file.'
    Run-Uninstall 0
    Assert-Check (-not [IO.File]::Exists((Join-Path $installRoot 'KnowledgeApp.CSharp.exe'))) 'The managed application must be uninstalled.'
    Assert-Check ([IO.File]::ReadAllText((Join-Path $installRoot 'unknown.txt'), $utf8) -ceq 'Preserve unowned residual file.') 'Uninstall must retain unknown files.'
    Assert-Check (-not [IO.File]::Exists($shortcutPath)) 'The owned shortcut must be removed.'
    Assert-Check ($null -eq (Read-TestRegistry $productSubkey 'InstallLocation')) 'The isolated product registration must be removed.'
    Assert-Check ($null -eq (Read-TestRegistry $uninstallSubkey 'DisplayName')) 'The isolated uninstall registration must be removed.'
    [IO.File]::Delete((Join-Path $installRoot 'unknown.txt'))
    [IO.Directory]::Delete($installRoot, $false)
    Run-Setup $normal.installer 0
    Check-Payload
    Run-Uninstall 0
    Assert-Check (-not [IO.Directory]::Exists($installRoot)) 'A clean uninstall must leave no installer-owned directory.'
    Assert-Check ((Get-CSharpPackageSha256 $sentinel) -ceq $sentinelHash) 'The synthetic user-data sentinel must remain unchanged.'
    Assert-Check ((Get-CSharpPackageSha256 $legacy) -ceq $legacyHash) 'The synthetic legacy application must remain unchanged.'
    [PSCustomObject]@{ passed=$true; checks=$checks; phase=$package.phase; scope='GUID-isolated NSIS lifecycle'; realApplicationLaunched=$false; productionDataOpened=$false; registryScope='owned GUID-only HKCU product and uninstall keys'; releaseAuthorized=$package.cutoverAllowed; allFinalChecksPassed=$false }
}
finally {
    foreach ($child in $children) {
        if (-not $child.HasExited) { $child.Kill(); $child.WaitForExit(5000) | Out-Null }
        $child.Dispose()
    }
    # Only the two compile-time GUID test keys are eligible for cleanup. Never
    # remove the common product parent or a real uninstall registration.
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($productSubkey, $false)
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($uninstallSubkey, $false)
    $resolved = [IO.Path]::GetFullPath($testRoot).TrimEnd('\', '/')
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
    if ([IO.Path]::GetDirectoryName($resolved) -ine $tempParent -or
        [IO.Path]::GetFileName($resolved) -cne ('knowledgeapp-csharp-installer-check-' + $runId)) { throw 'Refusing cleanup outside the owned installer fixture.' }
    Assert-CSharpPackageNormalPath $resolved
    # The fixture never creates links; verify every descendant before deleting
    # this exact newly created GUID root with a single PowerShell operation.
    @(Get-CSharpPackageFiles $resolved) | Out-Null
    if ($KeepArtifacts) { Write-Output "Synthetic installer artifacts retained at: $resolved" }
    else { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
