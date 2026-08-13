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

if (-not $env:LOCALAPPDATA) {
    throw 'Windowsのローカル利用者データフォルダを取得できません。'
}

$dataRoot = Join-Path $env:LOCALAPPDATA 'jp.local.webknowledgesystem'
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

$path = Join-Path $dataRoot ('codex-bridge\delegations\' + $parsedId.ToString() + '.knowledge-delegation.json')
if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
    throw 'KnowledgeAppの委譲情報が見つかりません。アプリから新しい委譲番号を作成してください。'
}
$item = Get-Item -LiteralPath $path -Force
if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -gt 5MB) {
    throw 'KnowledgeAppの委譲情報を安全に読み取れません。'
}
$delegation = Get-Content -Raw -Encoding UTF8 -LiteralPath $path | ConvertFrom-Json
if ($delegation.formatVersion -ne 1 -or [string]$delegation.delegationId -ne $parsedId.ToString() -or $null -eq $delegation.articles) {
    throw 'KnowledgeAppの委譲情報の形式が正しくありません。'
}
$delegation | ConvertTo-Json -Depth 100
