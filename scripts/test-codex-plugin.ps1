[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginScripts = Join-Path $repositoryRoot 'codex-plugins\knowledgeapp-faq\scripts'
$skillPath = Join-Path $repositoryRoot 'codex-plugins\knowledgeapp-faq\skills\knowledgeapp-faq\SKILL.md'
$proposalFormatPath = Join-Path $repositoryRoot 'codex-plugins\knowledgeapp-faq\skills\knowledgeapp-faq\references\proposal-format.md'
$previousTestMode = $env:KNOWLEDGEAPP_PLUGIN_TEST_MODE
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-plugin-test-' + [Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-bridge') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-bridge\delegations') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-bridge\mail-delegations') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-inbox') -Force | Out-Null
$resolvedRoot = (Resolve-Path -LiteralPath $testRoot).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'テスト用フォルダがWindows一時フォルダ外にあります。'
}

try {
    $forbiddenDirectAccess = @(
        'knowledge\.db',
        '\.db-wal',
        '\.db-shm',
        '(?i)sqlite',
        '\.pst',
        '\.msg',
        '(?i)Get-ChildItem'
    )
    foreach ($scriptFile in Get-ChildItem -LiteralPath $pluginScripts -Filter '*.ps1' -File) {
        $scriptText = [IO.File]::ReadAllText($scriptFile.FullName)
        foreach ($pattern in $forbiddenDirectAccess) {
            if ($scriptText -match $pattern) {
                throw ('CodexプラグインにDB・履歴・未選択FAQの直接探索につながる処理があります: ' + $scriptFile.Name)
            }
        }
        if ($scriptFile.Name -eq 'ExchangeLocation.Common.ps1') { continue }
        if ($scriptText -notmatch "(?m)^\`$dataRoot = Join-Path \`$localDataFolder 'jp\.local\.webknowledgesystem\.csharp'\s*$" -or
            $scriptText -notmatch '\[Environment\]::GetFolderPath\(\[Environment\+SpecialFolder\]::LocalApplicationData\)' -or
            $scriptText -match '\$env:LOCALAPPDATA' -or
            $scriptText -match "'jp\.local\.webknowledgesystem(?:\.csharp-rehearsal)?'") {
            throw ('通常コマンドの保存先がC#専用のWindows既知フォルダーに固定されていません: ' + $scriptFile.Name)
        }
    }

    $faqRuleText = [IO.File]::ReadAllText($skillPath) + [IO.File]::ReadAllText($proposalFormatPath)
    foreach ($requiredText in @('画像・スクリーンショット', '著作権侵害', '挿入位置', '代替テキスト')) {
        if ($faqRuleText.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw ('FAQ画像利用ルールに必須文言がありません: ' + $requiredText)
        }
    }
    foreach ($requiredText in @('【トップ分類名】', '接頭辞を含めない', '動的に付ける')) {
        if ($faqRuleText.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw ('FAQタイトル表示ルールに必須文言がありません: ' + $requiredText)
        }
    }
    foreach ($requiredText in @('get-mail-delegation.ps1', 'メール委譲番号', '`.pst`', '`.msg`', '未選択メール')) {
        if ($faqRuleText.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw ('メール委譲ルールに必須文言がありません: ' + $requiredText)
        }
    }
    foreach ($requiredText in @('jp.local.webknowledgesystem.csharp', '再発行', '自動移行', 'TestDataRoot')) {
        if ($faqRuleText.IndexOf($requiredText, [StringComparison]::Ordinal) -lt 0) {
            throw ('C#専用保存先・旧委譲非移行の説明がありません: ' + $requiredText)
        }
    }

    $catalog = '{"formatVersion":1,"generatedAt":"2026-08-12T00:00:00Z","categories":[]}'
    [IO.File]::WriteAllText(
        (Join-Path $testRoot 'codex-bridge\categories.json'),
        $catalog,
        [Text.UTF8Encoding]::new($false)
    )
    $env:KNOWLEDGEAPP_PLUGIN_TEST_MODE = '1'
    $catalogOutput = & (Join-Path $pluginScripts 'get-category-catalog.ps1') -TestDataRoot $testRoot
    if (($catalogOutput | ConvertFrom-Json).formatVersion -ne 1) {
        throw '分類カタログ取得コマンドのテストに失敗しました。'
    }

    $delegationId = [Guid]::NewGuid().ToString()
    $sourceArticleId = [Guid]::NewGuid().ToString()
    $delegation = [ordered]@{
        formatVersion = 1
        delegationId = $delegationId
        createdAt = '2026-08-12T00:00:00Z'
        kind = 'revise'
        articles = @([ordered]@{
            articleId = $sourceArticleId
            sourceUpdatedAt = '2026-08-12T00:00:00Z'
            categoryId = [Guid]::NewGuid().ToString()
            categoryPath = '開発用'
            title = '委譲元FAQ'
            summary = '委譲取得テスト'
            bodyDoc = [ordered]@{ type = 'doc'; content = @() }
            status = 'draft'
            importance = 1
            attachments = @()
        })
    } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText(
        (Join-Path $testRoot ('codex-bridge\delegations\' + $delegationId + '.knowledge-delegation.json')),
        $delegation,
        [Text.UTF8Encoding]::new($false)
    )
    $delegationOutput = & (Join-Path $pluginScripts 'get-delegation.ps1') -DelegationId $delegationId -TestDataRoot $testRoot
    if (($delegationOutput | ConvertFrom-Json).delegationId -ne $delegationId) {
        throw '委譲情報取得コマンドのテストに失敗しました。'
    }

    $mailDelegationId = [Guid]::NewGuid().ToString()
    $mailDelegation = [ordered]@{
        formatVersion = 1
        delegationId = $mailDelegationId
        createdAt = '2026-08-29T00:00:00Z'
        kind = 'mail-create'
        reviewConfirmed = $true
        mails = @([ordered]@{
            mailId = [Guid]::NewGuid().ToString()
            subject = '合成メール件名'
            sender = '送信者（マスク済み）'
            recipients = '宛先（マスク済み）'
            sentAt = '2026-08-29T00:00:00Z'
            bodyText = '合成メール本文'
        })
    } | ConvertTo-Json -Depth 20
    [IO.File]::WriteAllText(
        (Join-Path $testRoot ('codex-bridge\mail-delegations\' + $mailDelegationId + '.knowledge-mail-delegation.json')),
        $mailDelegation,
        [Text.UTF8Encoding]::new($false)
    )
    $mailDelegationOutput = & (Join-Path $pluginScripts 'get-mail-delegation.ps1') -DelegationId $mailDelegationId -TestDataRoot $testRoot
    $mailDelegationResult = $mailDelegationOutput | ConvertFrom-Json
    if ($mailDelegationResult.delegationId -ne $mailDelegationId -or
        $mailDelegationResult.mails.Count -ne 1 -or
        $mailDelegationResult.mails[0].bodyText -ne '合成メール本文') {
        throw 'メール委譲情報取得コマンドのテストに失敗しました。'
    }
    $unsafeMailDelegationId = [Guid]::NewGuid().ToString()
    $unsafeMailDelegation = $mailDelegation | ConvertFrom-Json
    $unsafeMailDelegation.delegationId = $unsafeMailDelegationId
    $unsafeMailDelegation.mails[0] | Add-Member -NotePropertyName sourcePath -NotePropertyValue 'C:\secret\original'
    [IO.File]::WriteAllText(
        (Join-Path $testRoot ('codex-bridge\mail-delegations\' + $unsafeMailDelegationId + '.knowledge-mail-delegation.json')),
        ($unsafeMailDelegation | ConvertTo-Json -Depth 20),
        [Text.UTF8Encoding]::new($false)
    )
    $unsafeRejected = $false
    try {
        & (Join-Path $pluginScripts 'get-mail-delegation.ps1') -DelegationId $unsafeMailDelegationId -TestDataRoot $testRoot | Out-Null
    }
    catch {
        $unsafeRejected = $true
    }
    if (-not $unsafeRejected) {
        throw 'メール委譲取得コマンドが原本パス項目を拒否しませんでした。'
    }

    $requestId = [Guid]::NewGuid().ToString()
    $proposal = [ordered]@{
        formatVersion = 2
        requestId = $requestId
        seriesId = $requestId
        createdAt = '2026-08-12T00:00:00Z'
        proposalKind = 'create'
        sourceArticles = @()
        faq = [ordered]@{
            title = '開発用テストFAQ'
            summary = 'プラグインの自動テスト用データです。'
            bodyDoc = [ordered]@{
                type = 'doc'
                content = @([ordered]@{
                    type = 'paragraph'
                    content = @([ordered]@{ type = 'text'; text = 'テスト回答' })
                })
            }
            importance = 1
        }
        existingCategoryCandidates = @()
        newCategoryProposal = [ordered]@{
            parentCategoryId = $null
            parentCategoryPath = $null
            name = '開発用テスト分類'
            description = '自動テスト用'
            reason = '分類が空のテストケースのため'
        }
    } | ConvertTo-Json -Depth 20
    $proposal | & (Join-Path $pluginScripts 'submit-faq-proposal.ps1') -TestDataRoot $testRoot | Out-Null
    $target = Join-Path $testRoot ('codex-inbox\' + $requestId + '.knowledge-proposal.json')
    if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
        throw '提案送信コマンドのテストに失敗しました。'
    }
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $proposalBytes = [IO.File]::ReadAllBytes($target)
    $savedProposal = $strictUtf8.GetString($proposalBytes) | ConvertFrom-Json
    if ($savedProposal.faq.title -ne '開発用テストFAQ' -or
        $savedProposal.faq.bodyDoc.content[0].content[0].text -ne 'テスト回答') {
        throw 'Codex提案の日本語UTF-8往復テストに失敗しました。'
    }
    # A selected but absent destination must fail even when the adjacent owned
    # synthetic source has a matching catalog, delegation and proposal inbox.
    # Neither the real legacy root nor the real C# root is inspected here.
    $missingRoot = Join-Path $testRoot 'knowledgeapp-plugin-test-csharp-missing'
    $cases = @(
        @{ Name = 'get-category-catalog.ps1'; Arguments = @{} },
        @{ Name = 'get-delegation.ps1'; Arguments = @{ DelegationId = $delegationId } },
        @{ Name = 'get-mail-delegation.ps1'; Arguments = @{ DelegationId = $mailDelegationId } },
        @{ Name = 'submit-faq-proposal.ps1'; Arguments = @{ ProposalJson = $proposal } }
    )
    foreach ($case in $cases) {
        $arguments = $case.Arguments
        foreach ($badRoot in @($missingRoot, $repositoryRoot)) {
            $rejected = $false
            try { & (Join-Path $pluginScripts $case.Name) -TestDataRoot $badRoot @arguments | Out-Null }
            catch { $rejected = $true }
            if (-not $rejected) { throw ('保存先不在・任意パスの拒否に失敗しました: ' + $case.Name) }
        }
        $env:KNOWLEDGEAPP_PLUGIN_TEST_MODE = '0'
        $rejected = $false
        try { & (Join-Path $pluginScripts $case.Name) -TestDataRoot $testRoot @arguments | Out-Null }
        catch { $rejected = $true }
        finally { $env:KNOWLEDGEAPP_PLUGIN_TEST_MODE = '1' }
        if (-not $rejected) { throw ('通常モードでテスト保存先が使われました: ' + $case.Name) }
    }
    if (Test-Path -LiteralPath $missingRoot) { throw '存在しない保存先を自動作成しました。' }
    if (-not [Linq.Enumerable]::SequenceEqual([byte[]]$proposalBytes, [byte[]][IO.File]::ReadAllBytes($target))) {
        throw '拒否された操作が既存提案を書き換えました。'
    }
    # Configured exchange: only owned synthetic JSON, ACL and generation are used.
    $settingsPath = Join-Path $testRoot 'device-settings'
    $exchangePath = Join-Path $testRoot 'configured-exchange'
    [IO.Directory]::CreateDirectory($settingsPath) | Out-Null
    foreach ($relative in @('codex-inbox', 'codex-bridge')) { [IO.Directory]::CreateDirectory((Join-Path $exchangePath $relative)) | Out-Null }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    Set-Acl -LiteralPath $exchangePath -AclObject $acl
    $environmentId = [guid]::NewGuid().ToString()
    $descriptor = @{ version=1; environmentId=$environmentId; generation=2; root=$exchangePath } | ConvertTo-Json
    $descriptorPath = Join-Path $settingsPath 'codex-location.json'
    [IO.File]::WriteAllText($descriptorPath, $descriptor, $strictUtf8)
    [IO.File]::WriteAllText((Join-Path $exchangePath '.knowledgeapp-codex-owner.json'), $descriptor, $strictUtf8)
    [IO.File]::WriteAllBytes((Join-Path $settingsPath 'codex-location.lock'), [byte[]]@())
    [IO.File]::WriteAllText((Join-Path $exchangePath 'codex-bridge/categories.json'), $catalog, $strictUtf8)
    $configured = & (Join-Path $pluginScripts 'get-category-catalog.ps1') -TestDataRoot $testRoot
    if (($configured -join "`n") -notmatch $environmentId) { throw '連携環境番号が返されませんでした。' }
    $configuredProposal = $proposal | ConvertFrom-Json
    $configuredProposal.requestId = [guid]::NewGuid().ToString()
    $configuredProposal.seriesId = $configuredProposal.requestId
    $configuredProposal | Add-Member environmentId $environmentId
    $configuredProposal | Add-Member exchangeGeneration 1
    $staleRejected = $false
    try { & (Join-Path $pluginScripts 'submit-faq-proposal.ps1') -TestDataRoot $testRoot -ProposalJson ($configuredProposal | ConvertTo-Json -Depth 20) | Out-Null }
    catch { $staleRejected = $true }
    if (-not $staleRejected) { throw '旧世代の提案を拒否しませんでした。' }
    $configuredProposal.exchangeGeneration = 2
    & (Join-Path $pluginScripts 'submit-faq-proposal.ps1') -TestDataRoot $testRoot -ProposalJson ($configuredProposal | ConvertTo-Json -Depth 20) | Out-Null
    if (-not (Test-Path -LiteralPath (Join-Path $exchangePath ('codex-inbox/' + $configuredProposal.requestId + '.knowledge-proposal.json')))) { throw '設定先への提案送信に失敗しました。' }
    $held = [IO.FileStream]::new((Join-Path $settingsPath 'codex-location.lock'), 'Open', 'ReadWrite', 'None')
    try {
        $locked = $false
        try { & (Join-Path $pluginScripts 'get-category-catalog.ps1') -TestDataRoot $testRoot | Out-Null } catch { $locked = $true }
        if (-not $locked) { throw '切替中の連携先を使用しました。' }
    } finally { $held.Dispose() }
    [IO.Directory]::CreateDirectory((Join-Path $exchangePath '.git')) | Out-Null
    $gitRejected = $false
    try { & (Join-Path $pluginScripts 'get-category-catalog.ps1') -TestDataRoot $testRoot | Out-Null } catch { $gitRejected = $true }
    if (-not $gitRejected) { throw '設定後にGit化された連携先を拒否しませんでした。' }
    Write-Output 'OK: 設定された専用連携先・ACL・環境/世代・旧世代拒否・切替ロック・Git化後の拒否を確認しました。'
    Write-Output 'OK: C#専用固定保存先、旧版へのフォールバック禁止、任意保存先拒否、DB・原本・未選択FAQ直接探索禁止、画像ルール、分類・FAQ・メール委譲取得と提案送信を確認しました。'
}
finally {
    if ($null -eq $previousTestMode) { Remove-Item Env:\KNOWLEDGEAPP_PLUGIN_TEST_MODE -ErrorAction SilentlyContinue }
    else { $env:KNOWLEDGEAPP_PLUGIN_TEST_MODE = $previousTestMode }
    if ((Test-Path -LiteralPath $resolvedRoot) -and
        $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
