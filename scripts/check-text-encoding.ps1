[CmdletBinding()]
param([switch]$StagedOnly)

$ErrorActionPreference = 'Stop'
$repositoryRoot = if ($PSScriptRoot) {
    Split-Path -Parent $PSScriptRoot
}
elseif ($env:KNOWLEDGE_REPOSITORY_ROOT) {
    [IO.Path]::GetFullPath($env:KNOWLEDGE_REPOSITORY_ROOT)
}
else {
    throw 'リポジトリの場所を取得できませんでした。'
}
$strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
$gitOutputEncoding = [Text.UTF8Encoding]::new($false)
$textExtensions = @(
    '.c', '.cc', '.cpp', '.css', '.csv', '.h', '.html', '.ini', '.js', '.json',
    '.jsx', '.md', '.ps1', '.rs', '.sh', '.sql', '.svg', '.toml', '.ts', '.tsx',
    '.txt', '.xml', '.yaml', '.yml'
)
$textFileNames = @(
    '.editorconfig', '.gitattributes', '.gitignore', 'Cargo.lock', 'package-lock.json',
    'pre-commit'
)
$violations = [System.Collections.Generic.List[string]]::new()

function Test-TextFile {
    param([Parameter(Mandatory)][string]$RelativePath)

    $extension = [IO.Path]::GetExtension($RelativePath).ToLowerInvariant()
    $name = [IO.Path]::GetFileName($RelativePath)
    return $textExtensions -contains $extension -or $textFileNames -contains $name
}

function Test-Utf8File {
    param([Parameter(Mandatory)][string]$RelativePath)

    if (-not (Test-TextFile -RelativePath $RelativePath)) { return }
    $fullPath = Join-Path $repositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { return }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $violations.Add("[UTF-8 BOM] $RelativePath")
    }

    try {
        $text = $strictUtf8.GetString($bytes)
    }
    catch {
        $violations.Add("[UTF-8ではない] $RelativePath")
        return
    }

    if ($text.Contains([char]0xFFFD)) {
        $violations.Add("[置換文字U+FFFD] $RelativePath")
    }
    if ($text.Contains([char]0x0000)) {
        $violations.Add("[NUL文字] $RelativePath")
    }
    if ($text.Contains("`r")) {
        $violations.Add("[改行コードがLFではない] $RelativePath")
    }
}

Push-Location $repositoryRoot
$previousOutputEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = $gitOutputEncoding
    $paths = if ($StagedOnly) {
        @(git -c core.quotepath=false diff --cached --name-only --diff-filter=ACMR)
    }
    else {
        @(git -c core.quotepath=false ls-files)
    }
    if ($LASTEXITCODE -ne 0) { throw 'Gitの追跡ファイル一覧を取得できませんでした。' }

    foreach ($path in $paths) {
        if ($path) { Test-Utf8File -RelativePath $path }
    }
}
finally {
    [Console]::OutputEncoding = $previousOutputEncoding
    Pop-Location
}

if ($violations.Count -gt 0) {
    Write-Error ("文字コード検査に失敗しました。ソース・設定・文書はUTF-8（BOMなし）、改行はLFで保存してください。`n" + ($violations -join "`n"))
}

Write-Output 'OK: 対象テキストはUTF-8（BOMなし）・LFで統一されています。'
