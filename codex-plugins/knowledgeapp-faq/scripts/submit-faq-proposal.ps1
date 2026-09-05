[CmdletBinding()]
param(
    [string]$TestDataRoot,
    [Parameter(ValueFromPipeline = $true)]
    [string]$ProposalJson
)

begin {
    $ErrorActionPreference = 'Stop'
}
process {
$json = if ($null -ne $ProposalJson) { $ProposalJson } else { [Console]::In.ReadToEnd() }
if ([string]::IsNullOrWhiteSpace($json)) {
    throw 'FAQ提案JSONを標準入力へ渡してください。'
}
if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 1MB) { throw 'FAQ提案は1MB以内にしてください。' }

try {
    $proposal = $json | ConvertFrom-Json
}
catch {
    throw 'FAQ提案JSONの形式が正しくありません。'
}

if ($proposal.formatVersion -ne 1 -and $proposal.formatVersion -ne 2) { throw 'formatVersionは1または2にしてください。' }
$requestId = [Guid]::Empty
if (-not [Guid]::TryParse([string]$proposal.requestId, [ref]$requestId)) { throw 'requestIdはUUIDにしてください。' }
$proposalKind = if ($proposal.formatVersion -eq 1) { 'create' } else { [string]$proposal.proposalKind }
if ($proposalKind -notin @('create', 'revise', 'merge')) { throw 'proposalKindはcreate、revise、mergeのいずれかにしてください。' }
if ($proposal.formatVersion -eq 2) {
    $seriesId = [Guid]::Empty
    if (-not [Guid]::TryParse([string]$proposal.seriesId, [ref]$seriesId)) { throw 'seriesIdはUUIDにしてください。' }
}
$sources = @($proposal.sourceArticles)
if (($proposalKind -eq 'create' -and $sources.Count -ne 0) -or
    ($proposalKind -eq 'revise' -and $sources.Count -ne 1) -or
    ($proposalKind -eq 'merge' -and ($sources.Count -lt 2 -or $sources.Count -gt 10))) {
    throw 'sourceArticlesの件数がproposalKindと一致しません。'
}
$createdAt = [DateTimeOffset]::MinValue
if (-not [DateTimeOffset]::TryParse([string]$proposal.createdAt, [ref]$createdAt)) { throw 'createdAtはRFC 3339形式にしてください。' }
if ($null -eq $proposal.faq) { throw 'faqがありません。' }
$title = [string]$proposal.faq.title
if ([string]::IsNullOrWhiteSpace($title) -or $title.Length -gt 200) { throw 'faq.titleは1～200文字にしてください。' }
if (([string]$proposal.faq.summary).Length -gt 500) { throw 'faq.summaryは500文字以内にしてください。' }
if ([int]$proposal.faq.importance -lt 1 -or [int]$proposal.faq.importance -gt 3) { throw 'faq.importanceは1～3にしてください。' }
if ($null -eq $proposal.faq.bodyDoc -or $proposal.faq.bodyDoc.type -ne 'doc') { throw 'faq.bodyDocはTiptapのdoc形式にしてください。' }

$candidates = @($proposal.existingCategoryCandidates)
if ($candidates.Count -gt 3) { throw 'existingCategoryCandidatesは3件以内にしてください。' }
if ($proposalKind -eq 'revise') {
    if ($candidates.Count -ne 0 -or $null -ne $proposal.newCategoryProposal) { throw 'reviseでは分類候補を指定しないでください。' }
}
elseif ($candidates.Count -eq 0 -and $null -eq $proposal.newCategoryProposal) { throw '既存分類候補または新規分類案が必要です。' }

$localDataFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localDataFolder)) { throw 'Windowsのローカル利用者データフォルダを取得できません。' }
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
$inbox = Join-Path $dataRoot 'codex-inbox'
if (-not (Test-Path -LiteralPath $inbox -PathType Container)) {
    throw 'KnowledgeApp C#版の提案箱がありません。C#版を一度起動してください。旧版の提案箱には送信しません。'
}

# Validation must not rewrite sourceUpdatedAt, IDs or body strings. In
# particular ConvertTo-Json would serialize PowerShell 7's DateTime coercion
# with a different timezone. Preserve unknown/duplicate properties as well,
# so KnowledgeApp's strict parser can reject them rather than normalizing away
# evidence. This also avoids a PowerShell-version-specific -DateKind option.
$target = Join-Path $inbox ($requestId.ToString() + '.knowledge-proposal.json')
if (Test-Path -LiteralPath $target) { throw '同じ受付番号の提案がすでにあります。新しいrequestIdで再作成してください。' }
$partial = $target + '.' + [Guid]::NewGuid().ToString() + '.partial'

try {
    [IO.File]::WriteAllText($partial, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $partial -Destination $target -ErrorAction Stop
}
finally {
    if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
}

Write-Output ('KnowledgeAppの確認待ち提案へ送信しました。受付番号: ' + $requestId.ToString())
}
