$ErrorActionPreference = 'Stop'

# Historical Trial function names are kept for build-script compatibility.
# The single accepted payload is now the phase 17 C# 0.5.0 production release.
function Get-CSharpTrialPackageMetadata {
    [ordered]@{
        formatVersion=1
        phase=17
        version='0.5.0'
        mode='csharp-production'
        runtime='win-x64-framework-dependent'
        dataRootIdentifier='jp.local.webknowledgesystem.csharp'
        dataRetention='retained-on-exit'
        initialFaqs='empty'
        legacyBackupRestore='schema-1-through-7-staged'
        restoreConfirmation='session-path-sha256-single-use'
        articleCompatibility='body-json-depth-128'
        codexJsonDepth=127
        defaultLaunch='no-arguments-production'
        rehearsalLaunch='--rehearsal'
        legacyDataMigration='explicit-selected-full-backup-restore-only'
        releaseAuthorized=$true
        acceptanceScope='user-approved-small-scale-cutover'
        allFinalChecksPassed=$false
        restoreRecovery='durable-intent-verified-safety-backup'
        requestPreflight='utf8-16mib-depth-144-input-retained'
        productionDataOpened=$false
        cutoverAllowed=$true
    }
}

function Assert-CSharpTrialReadme([string]$Text) {
    if ([string]::IsNullOrWhiteSpace($Text) -or $Text.Length -gt 65536) { throw 'Release description is missing or too large.' }
    $required = @(
        '(?m)^KnowledgeApp C# 0\.5\.0 - production release - phase 17$',
        '(?m)^Production data root: %LOCALAPPDATA%\\jp\.local\.webknowledgesystem\.csharp$',
        '(?m)^First launch starts with no FAQ or proposal seeds\. Initial login: 0000 with a blank password\.$',
        '(?m)^Normal shutdown retains the database, images, settings, history, import/export state and Codex state\.$',
        '(?m)^Every launch requires login; login sessions and open FAQ tabs are not restored\.$',
        '(?m)^Legacy backups with database schema 1 through 7 are verified and migrated in staging before restore\.$',
        '(?m)^Restore requires a single-use confirmation bound to the login session, selected path and archive SHA-256; it expires after 10 minutes\.$',
        '(?m)^Article JSON supports depth 128; the C# request transport still has a 16 MiB limit\.$',
        '(?m)^Codex JSON supports at most 127 nested object/array containers, including the root; existing file size limits remain unchanged\.$',
        '(?m)^Normal launch without arguments opens the C# production data root; --rehearsal explicitly opens the separate test environment\.$',
        '(?m)^The retired --production-candidate argument and all arbitrary data paths are refused\.$',
        '(?m)^The existing Tauri data root, installation and registration are not opened, modified or removed automatically\.$',
        '(?m)^Existing FAQs are migrated only by selecting a full backup and explicitly confirming restore in the C# application\.$',
        '(?m)^Production startup holds an exclusive SQLite lease and creates a verified full safety backup\.$',
        '(?m)^Interrupted restore is rolled back from a verified safety backup before normal use; invalid recovery state stops startup\.$',
        '(?m)^Requests exceeding 16 MiB UTF-8 or depth 144 are rejected before sending; unsaved input is retained\.$',
        '(?m)^C# is the development and operation primary for this user-approved small-scale cutover\.$',
        '(?m)^Release authorization does not mean every final test, company-device check, installed-plugin interaction or code-signing review has passed\.$'
    )
    $normalized = $Text.Replace("`r`n", "`n")
    foreach ($pattern in $required) {
        if ($normalized -cnotmatch $pattern) { throw 'Release description does not match the production release contract.' }
    }
    if ($normalized -imatch 'PROTOTYPE ONLY|PRODUCTION CUTOVER IS PROHIBITED|isolated-synthetic-trial|phase\s+(11|12|13|14|15|16)\b|Every run creates an isolated synthetic database|shutdown deletes|nothing is carried to the next run|fixed Tauri data root') {
        throw 'Release description still describes a retired phase or disposable trial mode.'
    }
}

function Get-CSharpPackageSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}

function Assert-CSharpPackageNormalPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Package paths must not traverse reparse points.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-CSharpTrialBinaryPaths {
    @(
        'KnowledgeApp.CSharp.exe', 'KnowledgeApp.CSharp.dll', 'KnowledgeApp.CSharp.deps.json', 'KnowledgeApp.CSharp.runtimeconfig.json',
        'KnowledgeApp.Data.dll', 'KnowledgeApp.Mail.dll', 'KnowledgeApp.Migration.dll',
        'Isopoh.Cryptography.Argon2.dll', 'Isopoh.Cryptography.Blake2b.dll', 'Isopoh.Cryptography.SecureArray.dll',
        'Microsoft.Data.Sqlite.dll', 'Microsoft.IO.RecyclableMemoryStream.dll', 'Microsoft.Maui.Graphics.dll',
        'Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.WinForms.dll', 'Microsoft.Web.WebView2.Wpf.dll',
        'MsgReader.dll', 'OpenMcdf.dll', 'RtfPipe.dll', 'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll',
        'SQLitePCLRaw.provider.e_sqlite3.dll', 'UtfUnknown.dll',
        'runtimes/win-x64/native/e_sqlite3.dll', 'runtimes/win-x64/native/WebView2Loader.dll',
        'da/MsgReader.resources.dll', 'de/MsgReader.resources.dll', 'es/MsgReader.resources.dll',
        'fr/MsgReader.resources.dll', 'nl/MsgReader.resources.dll', 'pt/MsgReader.resources.dll',
        'pt-BR/MsgReader.resources.dll', 'zh-CN/MsgReader.resources.dll', 'zh-TW/MsgReader.resources.dll'
    )
}

function Get-CSharpPackageFiles([string]$Directory) {
    Assert-CSharpPackageNormalPath $Directory
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        Assert-CSharpPackageNormalPath $item.FullName
        if ($item.PSIsContainer) { Get-CSharpPackageFiles $item.FullName } else { $item }
    }
}

function Get-CSharpPackageDirectories([string]$Directory) {
    Assert-CSharpPackageNormalPath $Directory
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Directory -Force) {
        Assert-CSharpPackageNormalPath $item.FullName
        $item
        Get-CSharpPackageDirectories $item.FullName
    }
}

function Test-CSharpPackageRelativePath([string]$Path) {
    if ($Path -cin (Get-CSharpTrialBinaryPaths)) { return $true }
    if ($Path -cin @('ui-manifest.json', 'PACKAGE-MANIFEST.json', 'RELEASE-README.txt', 'notices/dependency-inventory.json')) { return $true }
    if ($Path -ceq 'ui/index.html' -or $Path -cmatch '^ui/assets/[A-Za-z0-9_-]+\.(js|css|woff2?|png|jpe?g|webp|gif|ico)$') { return $true }
    return $Path -cmatch '^notices/(nuget|npm)/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.(txt|nuspec)$'
}

function Get-CSharpPackageEntries([string]$Directory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    foreach ($file in Get-CSharpPackageFiles $root) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not (Test-CSharpPackageRelativePath $relative)) { throw "Unapproved release package entry: $relative" }
        if ($relative -ceq 'PACKAGE-MANIFEST.json') { continue }
        if ($file.Length -le 0 -or $file.Length -gt 128MB) { throw 'Invalid package file size.' }
        [PSCustomObject][ordered]@{path=$relative; size=$file.Length; sha256=(Get-CSharpPackageSha256 $file.FullName)}
    }
}

function Test-CSharpTrialPackage([string]$Directory, [string]$SourceUiDirectory) {
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    Assert-CSharpPackageNormalPath $root
    $manifestPath = Join-Path $root 'PACKAGE-MANIFEST.json'
    Assert-CSharpPackageNormalPath $manifestPath
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 1MB) { throw 'Package manifest exceeds limit.' }
    $manifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $metadata = Get-CSharpTrialPackageMetadata
    foreach ($key in $metadata.Keys) {
        $property = $manifest.PSObject.Properties[$key]
        $value = if ($null -ne $property) { $property.Value } else { $null }
        $validType = if ($metadata[$key] -is [bool]) { $value -is [bool] }
            elseif ($metadata[$key] -is [string]) { $value -is [string] }
            else { $value -is [int] -or $value -is [long] }
        if (-not $validType -or $value -cne $metadata[$key]) {
            throw 'Unsupported package manifest or production data contract.'
        }
    }
    $actual = @(Get-CSharpPackageEntries $root | Sort-Object path)
    $expected = @($manifest.files | Sort-Object path)
    $allowedDirectories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $actual) {
        $relative = $file.path
        while ($relative.Contains('/')) {
            $relative = $relative.Substring(0, $relative.LastIndexOf('/'))
            [void]$allowedDirectories.Add($relative)
        }
    }
    foreach ($subdirectory in Get-CSharpPackageDirectories $root) {
        $relative = $subdirectory.FullName.Substring($root.Length + 1).Replace('\', '/')
        if (-not $allowedDirectories.Contains($relative)) { throw "Unexpected or empty package directory: $relative" }
    }
    if ($expected.Count -ne $actual.Count -or $expected.Count -gt 4096) { throw 'Package file set changed.' }
    for ($i = 0; $i -lt $actual.Count; $i++) {
        if ($actual[$i].path -cne $expected[$i].path -or $actual[$i].size -ne $expected[$i].size -or $actual[$i].sha256 -cne $expected[$i].sha256) { throw 'Package hash or file set mismatch.' }
    }
    foreach ($required in @((Get-CSharpTrialBinaryPaths)) + @('ui-manifest.json', 'RELEASE-README.txt', 'notices/dependency-inventory.json', 'ui/index.html')) {
        if ($required -cnotin @($actual.path)) { throw "Missing required package entry: $required" }
    }
    # A manifest version alone must not relabel a retired executable as release.
    # Read PE version resources without loading or launching the application.
    foreach ($assembly in @('KnowledgeApp.CSharp.exe', 'KnowledgeApp.CSharp.dll')) {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root $assembly))
        if ($version.FileVersion -cne ($metadata.version + '.0')) { throw 'The C# executable version does not match the release metadata.' }
    }
    $readmePath = Join-Path $root 'RELEASE-README.txt'
    if ((Get-Item -LiteralPath $readmePath).Length -gt 65536) { throw 'Release description is too large.' }
    Assert-CSharpTrialReadme ([IO.File]::ReadAllText($readmePath, [Text.Encoding]::UTF8))
    $uiManifest = [IO.File]::ReadAllText((Join-Path $root 'ui-manifest.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
    $uiFiles = @($actual | Where-Object { $_.path.StartsWith('ui/', [StringComparison]::Ordinal) })
    if ($uiManifest.formatVersion -ne 1 -or $uiManifest.files.Count -ne $uiFiles.Count) { throw 'UI manifest file set mismatch.' }
    foreach ($file in $uiManifest.files) {
        $actualFile = @($uiFiles | Where-Object { $_.path -ceq ('ui/' + $file.path) })
        if ($actualFile.Count -ne 1 -or $actualFile[0].size -ne $file.size -or $actualFile[0].sha256 -cne $file.sha256) { throw 'UI hash mismatch.' }
    }
    if ($SourceUiDirectory) {
        $source = [IO.Path]::GetFullPath($SourceUiDirectory).TrimEnd('\', '/')
        $sourceFiles = @(Get-CSharpPackageFiles $source)
        if ($sourceFiles.Count -ne $uiFiles.Count) { throw 'UI differs from the current React build.' }
        foreach ($file in $sourceFiles) {
            $relative = 'ui/' + $file.FullName.Substring($source.Length + 1).Replace('\', '/')
            $copy = @($uiFiles | Where-Object { $_.path -ceq $relative })
            if ($copy.Count -ne 1 -or $copy[0].sha256 -cne (Get-CSharpPackageSha256 $file.FullName)) { throw 'Current React UI hash mismatch.' }
        }
    }
    [PSCustomObject]@{passed=$true; phase=$metadata.phase; version=$metadata.version; mode=$metadata.mode; files=$actual.Count; uiFiles=$uiFiles.Count; productionDataOpened=$false; cutoverAllowed=$metadata.cutoverAllowed; allFinalChecksPassed=$metadata.allFinalChecksPassed}
}
