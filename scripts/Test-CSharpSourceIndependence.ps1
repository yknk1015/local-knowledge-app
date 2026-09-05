[CmdletBinding()]
param([string]$DataAssembly)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $repositoryRoot 'src-tauri')) {
    throw 'Run the primary C# checks without src-tauri in this checkout.'
}
# Immutable schema 1-7 bytes from legacy baseline 277f38c. New migrations must
# be new numbered files; never edit an already deployed historical migration.
$expected = @{
    '0001_initial.sql' = '7d51d12c8f85d90dd6c4eacecefc636c30df27b747135a19b8d1a85df30ad9fd'
    '0002_article_display_flags.sql' = 'f5f21a7054bbfd0e9f76f16b41faffb9b6dd5fddac796f15877c6aa9a41f2ef6'
    '0003_codex_proposals.sql' = 'b9a46d1476248630c4d9f0dce0dbab92d1b033da62be9c46e3068398b30f3d5d'
    '0004_codex_delegation_history.sql' = '367a1e75258462d9a7e7a933e0b1b15bce6ff006bb0edd254ae96a8aab7dd9ce'
    '0005_article_merge_relations.sql' = '0e9cbe57c9a156954407a0a14e82740e056ac9b3eceac47f2006be37e173b779'
    '0006_users_and_article_audit.sql' = '3668efb48db5d4effdebc8a73e8a08232d0d543673bb232bfb4e9d196039cbb7'
    '0007_management_codes.sql' = 'd07e07b07161dfd14400e861f139ad05e519be9cbd9a493285ec63e433b33e29'
}
foreach ($entry in $expected.GetEnumerator()) {
    $path = Join-Path $repositoryRoot ('src-csharp/KnowledgeApp.Data/Migrations/' + $entry.Key)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.Value) {
        throw ('Historical migration changed: ' + $entry.Key)
    }
}
if (-not $DataAssembly) { $DataAssembly = Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.Data/bin/Release/net10.0/KnowledgeApp.Data.dll' }
$assembly = [Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $DataAssembly).Path)
foreach ($entry in $expected.GetEnumerator()) {
    $name = 'KnowledgeApp.Data.Migrations.' + $entry.Key
    $stream = $assembly.GetManifestResourceStream($name)
    if (-not $stream) { throw ('Missing embedded migration: ' + $name) }
    try {
        $hash = [Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $hash.Dispose() }
        if ($actual -cne $entry.Value) { throw ('Embedded migration changed: ' + $name) }
    }
    finally { $stream.Dispose() }
}
$project = [IO.File]::ReadAllText((Join-Path $repositoryRoot 'src-csharp/KnowledgeApp.Data/KnowledgeApp.Data.csproj'), [Text.Encoding]::UTF8)
if ($project.Contains('src-tauri') -or -not $project.Contains('<EmbeddedResource Include="Migrations\*.sql" />')) {
    throw 'The data project must embed C#-owned migrations.'
}
$workflow = [IO.File]::ReadAllText((Join-Path $repositoryRoot '.github/workflows/quality.yml'), [Text.Encoding]::UTF8)
if ($workflow -match 'src-tauri|cargo |npm run tauri|--with-legacy') {
    throw 'The primary C# workflow must not require legacy source or tools.'
}
Write-Output 'OK: C# source independence and 7 byte-identical source/embedded historical migrations verified.'
