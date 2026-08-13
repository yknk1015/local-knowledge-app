[CmdletBinding()]
param([string]$TestDataRoot)

$ErrorActionPreference = 'Stop'

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
$catalogPath = Join-Path $dataRoot 'codex-bridge\categories.json'
if (-not (Test-Path -LiteralPath $catalogPath -PathType Leaf)) {
    throw 'KnowledgeAppの分類カタログがありません。KnowledgeAppを一度起動し、「Codexからの提案」で提案を更新してください。'
}

$catalog = Get-Content -Raw -Encoding UTF8 -LiteralPath $catalogPath | ConvertFrom-Json
if ($catalog.formatVersion -ne 1 -or $null -eq $catalog.categories) {
    throw 'KnowledgeAppの分類カタログ形式が正しくありません。アプリを更新してから再試行してください。'
}

$catalog | ConvertTo-Json -Depth 20
