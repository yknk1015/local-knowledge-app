[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackageDirectory)

. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$source = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
Test-CSharpTrialPackage $source | Out-Null
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-package-check-' + [Guid]::NewGuid().ToString('D'))
Assert-CSharpPackageNormalPath $testRoot
if (Test-Path -LiteralPath $testRoot) { throw 'Test root already exists.' }
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
function Update-TestManifest([string]$Root, [scriptblock]$Change) {
    $path = Join-Path $Root 'PACKAGE-MANIFEST.json'
    $manifest = [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
    & $Change $manifest
    [IO.File]::WriteAllText($path, (($manifest | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
}
function Set-TestReadme([string]$Root, [string]$Text) {
    $readmePath = Join-Path $Root 'RELEASE-README.txt'
    [IO.File]::WriteAllText($readmePath, $Text, [Text.UTF8Encoding]::new($false))
    $readmeSize = (Get-Item -LiteralPath $readmePath).Length
    $readmeHash = Get-CSharpPackageSha256 $readmePath
    # Recompute the local consistency manifest so rejection must come from the
    # release description contract, not merely a stale file hash.
    Update-TestManifest $Root {
        param($manifest)
        $entry = $manifest.files | Where-Object { $_.path -ceq 'RELEASE-README.txt' }
        $entry.size = $readmeSize
        $entry.sha256 = $readmeHash
    }
}
$cases = @(
    @{name='rehearsal mistaken for production'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.dataRootIdentifier='jp.local.webknowledgesystem.csharp-rehearsal'}}},
    @{name='retired default launch contract'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.defaultLaunch='--production-candidate'}}},
    @{name='missing deferred-verification caveat with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Release authorization does not mean every final test, target-device check, installed-plugin interaction or code-signing review has passed.', 'Everything has passed.'))}},
    @{name='missing explicit migration contract with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Existing FAQs are migrated only by selecting a full backup and explicitly confirming restore in the C# application.', 'Legacy migration is automatic.'))}},
    @{name='injected retired trial README'; accept=$false; change={param($root) [IO.File]::WriteAllText((Join-Path $root 'TRIAL-README.txt'), 'PROTOTYPE ONLY - PRODUCTION CUTOVER IS PROHIBITED.', [Text.UTF8Encoding]::new($false))}},
    @{name='unapproved release claim'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.releaseAuthorized=$false}}},
    @{name='phase 16 metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.phase=16}}},
    @{name='missing production launch metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('defaultLaunch')}}},
    @{name='missing recovery metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('restoreRecovery')}}},
    @{name='missing preflight metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('requestPreflight')}}},
    @{name='missing default launch warning with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Normal launch without arguments opens the C# production data root; --rehearsal explicitly opens the separate test environment.', 'Default launch is unspecified.'))}},
    @{name='missing recovery warning with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Interrupted restore is rolled back from a verified safety backup before normal use; invalid recovery state stops startup.', 'Recovery is unspecified.'))}},
    @{name='injected restore recovery journal'; accept=$false; change={param($root) [IO.File]::WriteAllText((Join-Path $root 'restore-pending.json'), '{}', [Text.UTF8Encoding]::new($false))}},
    @{name='repository-independent package'; accept=$true; change={param($root)}},
    @{name='missing runtime DLL'; accept=$false; change={param($root) [IO.File]::Delete((Join-Path $root 'KnowledgeApp.Data.dll'))}},
    @{name='missing lazy UI chunk'; accept=$false; change={param($root) $file=Get-ChildItem -LiteralPath (Join-Path $root 'ui/assets') -Filter 'TagsPage-*.js' -File | Select-Object -First 1; [IO.File]::Delete($file.FullName)}},
    @{name='changed UI bytes'; accept=$false; change={param($root) [IO.File]::AppendAllText((Join-Path $root 'ui/index.html'), 'synthetic changed bytes', [Text.UTF8Encoding]::new($false))}},
    @{name='injected database name'; accept=$false; change={param($root) [IO.File]::WriteAllText((Join-Path $root 'synthetic.db'), 'synthetic non-database marker', [Text.UTF8Encoding]::new($false))}},
    @{name='unlisted script'; accept=$false; change={param($root) [IO.File]::WriteAllText((Join-Path $root 'ui/assets/Injected-abc.js'), 'synthetic marker', [Text.UTF8Encoding]::new($false))}},
    @{name='empty WebView profile'; accept=$false; change={param($root) [IO.Directory]::CreateDirectory((Join-Path $root 'KnowledgeApp.CSharp.exe.WebView2')) | Out-Null}},
    @{name='empty data folder'; accept=$false; change={param($root) [IO.Directory]::CreateDirectory((Join-Path $root 'data')) | Out-Null}},
    @{name='empty unknown folder'; accept=$false; change={param($root) [IO.Directory]::CreateDirectory((Join-Path $root 'unknown')) | Out-Null}},
    @{name='retired disposable mode'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.mode='isolated-synthetic-trial'}}},
    @{name='previous phase metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.phase=13}}},
    @{name='phase 14 metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.phase=14}}},
    @{name='stale phase 14 README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text + "`nKnowledgeApp C# migration rehearsal - phase 14`n")}},
    @{name='missing Codex depth metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('codexJsonDepth')}}},
    @{name='old Codex depth metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.codexJsonDepth=64}}},
    @{name='missing Codex depth README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Codex JSON supports at most 127 nested object/array containers, including the root; existing file size limits remain unchanged.', 'Codex JSON depth is unspecified.'))}},
    @{name='missing restore confirmation metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('restoreConfirmation')}}},
    @{name='wrong article compatibility metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.articleCompatibility='body-json-depth-32'}}},
    @{name='missing restore confirmation README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Restore requires a single-use confirmation bound to the login session, selected path and archive SHA-256; it expires after 10 minutes.', 'Restore confirmation is unspecified.'))}},
    @{name='missing article limit README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Article JSON supports depth 128; the C# request transport still has a 16 MiB limit.', 'Article limits are unspecified.'))}},
    @{name='missing legacy migration metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('legacyBackupRestore')}}},
    @{name='wrong legacy migration metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.legacyBackupRestore='current-schema-only'}}},
    @{name='missing legacy migration README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Legacy backups with database schema 1 through 8 are verified and migrated in staging before restore.', 'Legacy migration is unspecified.'))}},
    @{name='legacy shared data root metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.dataRootIdentifier='jp.local.webknowledgesystem'}}},
    @{name='disposable retention metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.dataRetention='deleted-on-exit'}}},
    @{name='seeded FAQ metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.initialFaqs='seeded'}}},
    @{name='production-open claim'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.productionDataOpened=$true}}},
    @{name='unapproved cutover metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.cutoverAllowed=$false}}},
    @{name='false all-test-pass claim'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.allFinalChecksPassed=$true}}},
    @{name='wrong release version'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.version='0.4.4'}}},
    @{name='wrong acceptance scope'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.acceptanceScope='all-final-tests-passed'}}},
    @{name='missing retention metadata'; accept=$false; change={param($root) Update-TestManifest $root {param($manifest) $manifest.PSObject.Properties.Remove('dataRetention')}}},
    @{name='stale shutdown README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text + "`nNormal shutdown deletes its synthetic data after safe cleanup; nothing is carried to the next run.`n")}},
    @{name='missing fixed root README with matching hash'; accept=$false; reason='Release description'; change={param($root) $text=[IO.File]::ReadAllText((Join-Path $root 'RELEASE-README.txt'), [Text.Encoding]::UTF8); Set-TestReadme $root ($text.Replace('Production data root: %LOCALAPPDATA%\jp.local.webknowledgesystem.csharp', 'Production data root: unspecified'))}}
)
$passed = 0
try {
    foreach ($case in $cases) {
        $destination = Join-Path $testRoot ('case-' + $passed)
        [IO.Directory]::CreateDirectory($destination) | Out-Null
        foreach ($file in Get-CSharpPackageFiles $source) {
            $relative = $file.FullName.Substring($source.Length + 1)
            $copy = Join-Path $destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
            [IO.File]::Copy($file.FullName, $copy, $false)
        }
        & $case.change $destination
        $accepted = $false
        $failureReason = ''
        try { Test-CSharpTrialPackage $destination | Out-Null; $accepted = $true } catch { $failureReason = $_.Exception.Message }
        if ($accepted -ne $case.accept) { throw ('FAIL: ' + $case.name) }
        if ($case.reason -and $failureReason -notmatch [regex]::Escape($case.reason)) { throw ('FAIL: wrong rejection reason for ' + $case.name) }
        $passed++
        Write-Output ('PASS: ' + $case.name)
    }
    Write-Output "PASS: $passed isolated package scenarios; no application or database opened."
} finally {
    $absolute = [IO.Path]::GetFullPath($testRoot).TrimEnd('\', '/')
    if ([IO.Path]::GetDirectoryName($absolute) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') -or
        [IO.Path]::GetFileName($absolute) -notmatch '^knowledgeapp-package-check-[0-9a-f-]{36}$') { throw 'Refusing unsafe cleanup target.' }
    # Check every entry again before deleting only the generated synthetic test tree.
    @(Get-CSharpPackageFiles $absolute) | Out-Null
    @(Get-CSharpPackageDirectories $absolute) | Out-Null
    [IO.Directory]::Delete($absolute, $true)
}
