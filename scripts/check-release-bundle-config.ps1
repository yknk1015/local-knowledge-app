[CmdletBinding()]
param([string]$InstallerDirectory)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$configPath = Join-Path $repositoryRoot 'src-tauri\tauri.conf.json'
$config = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json
$violations = [System.Collections.Generic.List[string]]::new()

if ('nsis' -notin @($config.bundle.targets)) {
    $violations.Add('bundle.targetsにNSISが指定されていません。')
}
if ([string]$config.bundle.windows.nsis.installMode -ne 'currentUser') {
    $violations.Add('NSISが利用者単位インストールではありません。')
}
if (@($config.bundle.resources).Count -ne 0) {
    $violations.Add('bundle.resourcesは空である必要があります。利用者データや外部資料を同梱しないでください。')
}
if ($null -ne $config.bundle.externalBin -and @($config.bundle.externalBin).Count -ne 0) {
    $violations.Add('未検査の外部バイナリが配布物へ追加されています。')
}

$serializedConfig = $config | ConvertTo-Json -Depth 100
$blockedPattern = '(?i)(knowledge\.db|\.db-(wal|shm|journal)|\.faqbackup|\.knowledge-(export\.json|faq\.csv|proposal\.json|delegation\.json)|codex-inbox|codex-bridge|manuals[\\/])'
if ($serializedConfig -match $blockedPattern) {
    $violations.Add('Tauri配布設定に利用者データまたは外部資料の同梱指定があります。')
}

if ($InstallerDirectory) {
    $resolvedInstallerDirectory = Resolve-Path -LiteralPath $InstallerDirectory -ErrorAction SilentlyContinue
    if (-not $resolvedInstallerDirectory) {
        $violations.Add('NSIS出力フォルダがありません。')
    }
    else {
        $expectedName = "$($config.productName)_$($config.version)_*-setup.exe"
        $installers = @(Get-ChildItem -LiteralPath $resolvedInstallerDirectory.Path -Filter $expectedName -File)
        if ($installers.Count -ne 1) {
            $violations.Add("現行版NSISインストーラーは1件である必要があります。対象: $expectedName、検出件数: $($installers.Count)")
        }
        elseif ($installers[0].Length -le 0) {
            $violations.Add('NSISインストーラーが空です。')
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Error ($violations -join "`n")
    exit 1
}

Write-Output 'OK: NSIS利用者単位インストールと、利用者データ・外部資料を同梱しない配布設定を確認しました。'
