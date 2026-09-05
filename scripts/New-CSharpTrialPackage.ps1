[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectRoot = Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.CSharp'
$sourceUi = Join-Path $repositoryRoot 'dist'
Assert-CSharpPackageNormalPath $sourceUi
if (-not (Test-Path -LiteralPath (Join-Path $sourceUi 'index.html') -PathType Leaf)) { throw 'Run npm run build first.' }
$taskRoot = Join-Path $projectRoot ('bin/pkg-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
Assert-CSharpPackageNormalPath $taskRoot
if (Test-Path -LiteralPath $taskRoot) { throw 'Refusing to overwrite an existing package directory.' }
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
$publishRoot = Join-Path $taskRoot 'publish'
$packageRoot = Join-Path $taskRoot 'app'
[IO.Directory]::CreateDirectory($packageRoot) | Out-Null
& dotnet publish (Join-Path $projectRoot 'KnowledgeApp.CSharp.csproj') --configuration Release --no-restore --self-contained false -p:DebugType=None -p:DebugSymbols=false "-p:OutputPath=$taskRoot/build/" "-p:PublishDir=$publishRoot/"
if ($LASTEXITCODE -ne 0) { throw 'C# release publish failed. No package was approved.' }
foreach ($published in Get-CSharpPackageFiles $publishRoot) {
    $relative = $published.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
    if ($published.Extension -ieq '.dll' -and
        -not $relative.StartsWith('runtimes/', [StringComparison]::Ordinal) -and
        $relative -cnotin (Get-CSharpTrialBinaryPaths)) { throw 'A new managed dependency needs an explicit package allowlist review.' }
    if ($relative.StartsWith('runtimes/win-x64/', [StringComparison]::Ordinal) -and
        $relative -cnotin (Get-CSharpTrialBinaryPaths)) { throw 'A new Windows x64 native dependency needs an explicit package allowlist review.' }
}

function Copy-PackageFile([string]$Source, [string]$Relative) {
    if (-not (Test-CSharpPackageRelativePath $Relative)) { throw 'Unapproved copy destination.' }
    Assert-CSharpPackageNormalPath $Source
    $destination = Join-Path $packageRoot $Relative
    Assert-CSharpPackageNormalPath $destination
    if (Test-Path -LiteralPath $destination) { throw 'Refusing to overwrite package contents.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($Source, $destination, $false)
}
function Write-PackageText([string]$Relative, [string]$Text) {
    if (-not (Test-CSharpPackageRelativePath $Relative)) { throw 'Unapproved text destination.' }
    $destination = Join-Path $packageRoot $Relative
    Assert-CSharpPackageNormalPath $destination
    if (Test-Path -LiteralPath $destination) { throw 'Refusing to overwrite package contents.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::WriteAllText($destination, $Text.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
}
foreach ($relative in Get-CSharpTrialBinaryPaths) { Copy-PackageFile (Join-Path $publishRoot $relative) $relative }
Copy-PackageFile (Join-Path $publishRoot 'ui-manifest.json') 'ui-manifest.json'
$publishUi = Join-Path $publishRoot 'ui'
foreach ($file in Get-CSharpPackageFiles $publishUi) {
    Copy-PackageFile $file.FullName ('ui/' + $file.FullName.Substring($publishUi.Length + 1).Replace('\', '/'))
}

# Retain dependency declarations and locally available license/notice texts.
# This inventory is not a legal approval or a claim that release review is complete.
$assets = [IO.File]::ReadAllText((Join-Path $projectRoot 'obj/project.assets.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json
$nuget = [Collections.Generic.List[object]]::new()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $safeName = $library.Name.Replace('/', '.')
    if ($safeName -cnotmatch '^[A-Za-z0-9_.-]+$') { throw 'Unexpected dependency name.' }
    $packageFolder = $null
    foreach ($cache in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $cache $library.Value.path
        Assert-CSharpPackageNormalPath $candidate
        if (Test-Path -LiteralPath $candidate -PathType Container) { $packageFolder = $candidate; break }
    }
    if (-not $packageFolder) { throw 'A restored dependency cannot be found locally.' }
    $copied = [Collections.Generic.List[string]]::new()
    foreach ($relative in $library.Value.files) {
        $leaf = [IO.Path]::GetFileName($relative)
        if ($leaf -notmatch '(?i)(\.nuspec$|^(license|notice|third-party-notices)(\.txt)?$)') { continue }
        $source = [IO.Path]::GetFullPath((Join-Path $packageFolder $relative))
        if (-not $source.StartsWith([IO.Path]::GetFullPath($packageFolder).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Dependency notice escaped its package.' }
        $targetLeaf = if ($leaf -match '\.nuspec$') { $leaf } else { $leaf + '.txt' }
        $targetLeaf = $targetLeaf -replace '[^A-Za-z0-9_.-]', '_'
        $target = "notices/nuget/$safeName/$targetLeaf"
        Copy-PackageFile $source $target
        $copied.Add($target)
    }
    $nuget.Add([ordered]@{package=$library.Name; localNoticeFiles=@($copied)})
}
$lockPath = Join-Path $repositoryRoot 'package-lock.json'
$lockText = [IO.File]::ReadAllText($lockPath, [Text.Encoding]::UTF8)
# Windows PowerShell 5.1 cannot represent package-lock's empty root property.
$lock = $lockText.Replace('"": {', '"__root__": {') | ConvertFrom-Json
$npm = [Collections.Generic.List[object]]::new()
foreach ($entry in $lock.packages.PSObject.Properties) {
    if ($entry.Name -ceq '__root__' -or $entry.Value.dev -eq $true) { continue }
    if ($entry.Name -cnotmatch '^node_modules/(?:[A-Za-z0-9_.@-]+/)*[A-Za-z0-9_.-]+$') { throw 'Unexpected npm dependency path.' }
    $safeName = ($entry.Name + '.' + $entry.Value.version) -replace '[^A-Za-z0-9_.-]', '_'
    $directory = Join-Path $repositoryRoot $entry.Name
    Assert-CSharpPackageNormalPath $directory
    $copied = [Collections.Generic.List[string]]::new()
    if (Test-Path -LiteralPath $directory -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $directory -File -Force) {
            if ($file.Name -notmatch '(?i)^(license|licence|notice)([._-].*)?$') { continue }
            $leaf = ($file.Name -replace '[^A-Za-z0-9_.-]', '_') + '.txt'
            $target = "notices/npm/$safeName/$leaf"
            Copy-PackageFile $file.FullName $target
            $copied.Add($target)
        }
    }
    $npm.Add([ordered]@{package=$entry.Name; version=$entry.Value.version; declaredLicense=$entry.Value.license; localNoticeFiles=@($copied)})
}
Write-PackageText 'notices/dependency-inventory.json' ([ordered]@{scope='Local restored NuGet and non-development npm lockfile dependencies; incomplete license text coverage requires release review.'; nuget=@($nuget); npm=@($npm)} | ConvertTo-Json -Depth 8)
$trialReadme = @'
KnowledgeApp C# 0.5.0 - production release - phase 17

Windows 11 x64. This is a framework-dependent portable package, not an installer.
Prerequisites: Microsoft .NET 10 Desktop Runtime (x64) and Microsoft Edge WebView2 Runtime.
No runtime is downloaded or installed automatically. Follow your company policy.
Extract the complete package to a local folder and run KnowledgeApp.CSharp.exe.
The adjacent ui folder and ui-manifest.json are mandatory; moving only the exe is unsupported.
The source repository, Node.js and npm are not required to run this package.

C# is the development and operation primary for this user-approved small-scale cutover.
Production data root: %LOCALAPPDATA%\jp.local.webknowledgesystem.csharp
First launch starts with no FAQ or proposal seeds. Initial login: 0000 with a blank password.
Normal shutdown retains the database, images, settings, history, import/export state and Codex state.
Every launch requires login; login sessions and open FAQ tabs are not restored.
Legacy backups with database schema 1 through 7 are verified and migrated in staging before restore.
Restore requires a single-use confirmation bound to the login session, selected path and archive SHA-256; it expires after 10 minutes.
Article JSON supports depth 128; the C# request transport still has a 16 MiB limit.
Codex JSON supports at most 127 nested object/array containers, including the root; existing file size limits remain unchanged.
Normal launch without arguments opens the C# production data root; --rehearsal explicitly opens the separate test environment.
The retired --production-candidate argument and all arbitrary data paths are refused.
The existing Tauri data root, installation and registration are not opened, modified or removed automatically.
Existing FAQs are migrated only by selecting a full backup and explicitly confirming restore in the C# application.
Production startup holds an exclusive SQLite lease and creates a verified full safety backup.
Interrupted restore is rolled back from a verified safety backup before normal use; invalid recovery state stops startup.
Requests exceeding 16 MiB UTF-8 or depth 144 are rejected before sending; unsaved input is retained.
The original backup is not modified. A full safety backup precedes replacement of current data.
Existing legacy users and passwords are preserved; valid pre-authentication legacy data receives initial admin 0000.
Direct startup of an older-schema database remains blocked; use explicit verified backup restore.
Moving or replacing this package does not move or remove the separate application data.
The package contains no database, FAQ, mail, browser profile or persisted state.
No legacy or rehearsal data is copied or imported automatically.
Use only synthetic content with --rehearsal. Its fixed root is %LOCALAPPDATA%\jp.local.webknowledgesystem.csharp-rehearsal.
The old Tauri application keeps separate data. Changes are not synchronized between the old and C# applications.
After selecting the C# application as primary, do not edit the old copy as if it were synchronized.
No process is killed and no lock is forcibly removed. Activation from a different session is refused.

PACKAGE-MANIFEST.json records file SHA-256 hashes for consistency checks, not authenticity or signing.
Dependency metadata and locally available notices are under notices. This is not legal approval.
Release authorization does not mean every final test, company-device check, installed-plugin interaction or code-signing review has passed.
Review the final migration record for remaining verification. Company policy still governs installation and data sharing.
'@
# Windows PowerShell 5.1 parses BOM-less scripts using the system code page.
# Read the Japanese documentation as explicit UTF-8 data, not source tokens.
$trialReadme += "`n`n" + [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer/ReleaseReadme.ja.txt'), [Text.Encoding]::UTF8)
Assert-CSharpTrialReadme $trialReadme
Write-PackageText 'RELEASE-README.txt' $trialReadme
$entries = @(Get-CSharpPackageEntries $packageRoot | Sort-Object path)
$packageManifest = Get-CSharpTrialPackageMetadata
$packageManifest.files = $entries
Write-PackageText 'PACKAGE-MANIFEST.json' ($packageManifest | ConvertTo-Json -Depth 6)
$verified = Test-CSharpTrialPackage $packageRoot $sourceUi
$zip = Join-Path $taskRoot 'KnowledgeApp-CSharp-0.5.0-win-x64.zip'
if (Test-Path -LiteralPath $zip) { throw 'Refusing to overwrite an archive.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageRoot, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
[PSCustomObject]@{packageDirectory=$packageRoot; archive=$zip; sha256=(Get-CSharpPackageSha256 $zip); version=$verified.version; phase=$verified.phase; files=$verified.files; uiFiles=$verified.uiFiles; cutoverAllowed=$verified.cutoverAllowed; allFinalChecksPassed=$verified.allFinalChecksPassed}
