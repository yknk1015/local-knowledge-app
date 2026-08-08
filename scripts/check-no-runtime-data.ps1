[CmdletBinding()]
param(
    [switch]$StagedOnly,
    [string]$ReleaseDirectory
)

$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$blockedRootNames = @(
    'data',
    'attachments',
    'manuals',
    'backup',
    'backups',
    'exports',
    'logs',
    'restore-staging',
    'safety-backups',
    'tmp'
)
$blockedRootPattern = '^(data|attachments|manuals|backup|backups|exports|logs|restore-staging|safety-backups|tmp)/'
$blockedFilePattern = '(?i)(^|/)(knowledge\.db(?:-(?:wal|shm|journal))?|[^/]+\.(?:db|sqlite|sqlite3)(?:-(?:wal|shm|journal))?|[^/]+\.faqbackup(?:\.[^/]*)?|[^/]+\.knowledge-export\.json|[^/]+\.partial)$'
$violations = [System.Collections.Generic.List[string]]::new()

function Convert-ToRepositoryPath {
    param([Parameter(Mandatory)][string]$Path)
    return $Path.Replace('\', '/').TrimStart('./')
}

function Test-BlockedPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Source
    )
    $normalized = Convert-ToRepositoryPath -Path $Path
    if ($normalized -match $blockedRootPattern -or $normalized -match $blockedFilePattern) {
        $violations.Add("[$Source] $normalized")
    }
}

foreach ($rootName in $blockedRootNames) {
    $candidate = Join-Path $repositoryRoot $rootName
    if (Test-Path -LiteralPath $candidate) {
        $violations.Add("[作業フォルダ] $rootName/ が存在します。利用者データはリポジトリ外へ保存してください。")
    }
}

$gitDirectory = Join-Path $repositoryRoot '.git'
if (Test-Path -LiteralPath $gitDirectory) {
    Push-Location $repositoryRoot
    try {
        if (-not $StagedOnly) {
            foreach ($path in @(git ls-files)) {
                if ($LASTEXITCODE -ne 0) { throw 'git ls-files に失敗しました。' }
                if ($path) { Test-BlockedPath -Path $path -Source '追跡中' }
            }
        }
        foreach ($path in @(git diff --cached --name-only --diff-filter=ACMR)) {
            if ($LASTEXITCODE -ne 0) { throw 'ステージ済みファイルの確認に失敗しました。' }
            if ($path) { Test-BlockedPath -Path $path -Source 'ステージ済み' }
        }
    }
    finally {
        Pop-Location
    }
}

if ($ReleaseDirectory) {
    $resolvedRelease = Resolve-Path -LiteralPath $ReleaseDirectory -ErrorAction SilentlyContinue
    if ($resolvedRelease) {
        foreach ($file in Get-ChildItem -LiteralPath $resolvedRelease -Recurse -File) {
            $relative = [System.IO.Path]::GetRelativePath($resolvedRelease.Path, $file.FullName)
            Test-BlockedPath -Path $relative -Source '配布物'
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Error ("利用者データまたは配布禁止ファイルを検出しました。`n" + ($violations -join "`n"))
    exit 1
}

Write-Output 'OK: 利用者データや配布禁止ファイルは検出されませんでした。'
