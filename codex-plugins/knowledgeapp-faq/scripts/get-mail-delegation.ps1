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
$path = Join-Path $dataRoot ('codex-bridge\mail-delegations\' + $parsedId.ToString() + '.knowledge-mail-delegation.json')
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw 'KnowledgeApp C#版のメール委譲情報が見つかりません。C#版から新しいメール委譲番号を作成してください。旧版で発行した委譲は自動移行・探索しません。'
}
$item = Get-Item -LiteralPath $path -Force
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -gt 1MB) {
    throw 'KnowledgeAppのメール委譲情報を安全に読み取れません。'
}

$delegation = Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
$topLevelAllowed = @('formatVersion', 'delegationId', 'createdAt', 'kind', 'reviewConfirmed', 'mails')
$unexpectedTopLevel = @($delegation.PSObject.Properties.Name | Where-Object { $_ -notin $topLevelAllowed })
if ($unexpectedTopLevel.Count -gt 0) {
    throw 'KnowledgeAppのメール委譲情報に許可されていない項目があります。'
}
if ($delegation.formatVersion -ne 1 -or
    [string]$delegation.delegationId -ne $parsedId.ToString() -or
    [string]$delegation.kind -ne 'mail-create' -or
    $delegation.reviewConfirmed -ne $true -or
    $null -eq $delegation.mails) {
    throw 'KnowledgeAppのメール委譲情報の形式が正しくありません。'
}
$createdAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$delegation.createdAt, [ref]$createdAt)) {
    throw 'KnowledgeAppのメール委譲情報の日時が正しくありません。'
}

$mails = @($delegation.mails)
if ($mails.Count -lt 1 -or $mails.Count -gt 20) {
    throw 'KnowledgeAppのメール委譲は1～20件にしてください。'
}
$mailAllowed = @('mailId', 'subject', 'sender', 'recipients', 'sentAt', 'bodyText')
foreach ($mail in $mails) {
    $unexpectedMail = @($mail.PSObject.Properties.Name | Where-Object { $_ -notin $mailAllowed })
    if ($unexpectedMail.Count -gt 0) {
        throw 'KnowledgeAppのメール委譲に原本情報または許可されていない項目があります。'
    }
    $mailId = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$mail.mailId, [ref]$mailId) -or
        [string]::IsNullOrWhiteSpace([string]$mail.bodyText) -or
        ([string]$mail.bodyText).Length -gt 200000 -or
        ([string]$mail.subject).Length -gt 500 -or
        ([string]$mail.sender).Length -gt 500 -or
        ([string]$mail.recipients).Length -gt 2000) {
        throw 'KnowledgeAppのメール委譲に不正なIDまたは長さの項目があります。'
    }
}

$delegation | ConvertTo-Json -Depth 20

} finally { if ($null -ne $exchange.Lease) { $exchange.Lease.Dispose() } }
