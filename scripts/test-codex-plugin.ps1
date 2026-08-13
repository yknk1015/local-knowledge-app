[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginScripts = Join-Path $repositoryRoot 'codex-plugins\knowledgeapp-faq\scripts'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('knowledgeapp-plugin-test-' + [Guid]::NewGuid().ToString())
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-bridge') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-bridge\delegations') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $testRoot 'codex-inbox') -Force | Out-Null
$resolvedRoot = (Resolve-Path -LiteralPath $testRoot).Path
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
if (-not $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'テスト用フォルダがWindows一時フォルダ外にあります。'
}

try {
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
    Write-Output 'OK: Codexプラグインの分類取得、委譲取得、提案送信を確認しました。'
}
finally {
    Remove-Item Env:\KNOWLEDGEAPP_PLUGIN_TEST_MODE -ErrorAction SilentlyContinue
    if ((Test-Path -LiteralPath $resolvedRoot) -and
        $resolvedRoot.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolvedRoot -Recurse -Force
    }
}
