[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DelegationId,
    [string]$TestDataRoot
)

$ErrorActionPreference = 'Stop'
$parsedId = [Guid]::Empty
if (-not [Guid]::TryParse($DelegationId, [ref]$parsedId)) {
    throw 'DelegationIdはKnowledgeAppに表示されたUUIDにしてください。'
}

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
$path = Join-Path $dataRoot ('codex-bridge\delegations\' + $parsedId.ToString() + '.knowledge-delegation.json')
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw 'KnowledgeApp C#版の委譲情報が見つかりません。C#版から新しい委譲番号を作成してください。旧版で発行した委譲は自動移行・探索しません。'
}
$item = Get-Item -LiteralPath $path -Force
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -gt 5MB) {
    throw 'KnowledgeAppの委譲情報を安全に読み取れません。'
}
$delegationJson = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false, $true))
$delegation = $delegationJson | ConvertFrom-Json
if ($delegation.formatVersion -ne 1 -or [string]$delegation.delegationId -ne $parsedId.ToString() -or $null -eq $delegation.articles) {
    throw 'KnowledgeAppの委譲情報の形式が正しくありません。'
}
# ConvertFrom-Json can coerce ISO timestamps to DateTime on PowerShell 7.
# Never serialize that inspection object: exact source-version strings and
# all body strings must remain unchanged on both Windows PowerShell 5.1 and 7.
Write-Output $delegationJson

} finally { if ($null -ne $exchange.Lease) { $exchange.Lease.Dispose() } }
