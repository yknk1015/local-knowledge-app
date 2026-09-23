[CmdletBinding()]
param([string]$TestDataRoot)

$ErrorActionPreference = 'Stop'

$localDataFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localDataFolder)) {
    throw 'Windowsのローカル利用者データフォルダを取得できません。'
}

# C# is the only production destination. Never probe or fall back to the old
# Tauri or rehearsal root, and do not trust an environment-variable override.
$dataRoot = Join-Path $localDataFolder 'jp.local.webknowledgesystem.csharp'
if ($TestDataRoot) {
    $resolvedTestRoot = [IO.Path]::GetFullPath($TestDataRoot)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($env:KNOWLEDGEAPP_PLUGIN_TEST_MODE -ne '1' -or
        -not [IO.Path]::IsPathRooted($TestDataRoot) -or
        -not $resolvedTestRoot.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
        -not [IO.Path]::GetFileName($resolvedTestRoot).StartsWith('knowledgeapp-plugin-test-', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'TestDataRootはテストモードの絶対パスだけを指定できます。'
    }
    $dataRoot = $resolvedTestRoot
}
. ([scriptblock]::Create([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ExchangeLocation.Common.ps1'), [Text.UTF8Encoding]::new($false, $true))))
$exchange = Open-KnowledgeExchange $dataRoot
$dataRoot = $exchange.Root
try {
if ($exchange.Generation -gt 0) {
    Write-Output ('連携環境 environmentId: ' + $exchange.EnvironmentId + ' / exchangeGeneration: ' + $exchange.Generation + '。提案JSONのルートへこの2項目を含めてください。')
}
$catalogPath = Join-Path $dataRoot 'codex-bridge\categories.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw 'KnowledgeApp C#版の分類カタログがありません。C#版を一度起動し、「Codexからの提案」で提案を更新してください。旧版の保存先は探索しません。'
}

$catalog = Get-Content -Raw -Encoding UTF8 -LiteralPath $catalogPath | ConvertFrom-Json
if ($catalog.formatVersion -ne 1 -or $null -eq $catalog.categories) {
    throw 'KnowledgeAppの分類カタログ形式が正しくありません。アプリを更新してから再試行してください。'
}

$catalog | ConvertTo-Json -Depth 20

} finally { if ($null -ne $exchange.Lease) { $exchange.Lease.Dispose() } }
