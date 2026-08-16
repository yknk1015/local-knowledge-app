[CmdletBinding()]
param(
    [string]$PackageDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$installerName = 'KnowledgeApp_0.4.2_x64-setup.exe'
$expectedInstallerHash = '3E70C67DCC171F9B21EE3DD6A58ACA8C061879218D130BC0A67D568FCD18DD0E'
$expectedFiles = @(
    $installerName,
    'Surface画像保存診断手順_0.4.2.md',
    'verify-surface-image-diagnostic.ps1'
)
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($fileName in $expectedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $resolvedPackageDirectory $fileName) -PathType Leaf)) {
        $failures.Add("不足: $fileName")
    }
}

$manifestPath = Join-Path $resolvedPackageDirectory 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $failures.Add('不足: SHA256SUMS.txt')
}
else {
    $manifestFiles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $manifestPath -Encoding UTF8) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -notmatch '^([A-Fa-f0-9]{64}) \*(.+)$') {
            $failures.Add("SHA256SUMS.txtの形式不正: $line")
            continue
        }
        $expectedHash = $Matches[1].ToUpperInvariant()
        $relativeName = $Matches[2]
        if ([IO.Path]::GetFileName($relativeName) -ne $relativeName) {
            $failures.Add("SHA256SUMS.txtの不正な相対パス: $relativeName")
            continue
        }
        if (-not $manifestFiles.Add($relativeName)) {
            $failures.Add("SHA256SUMS.txtの重複記載: $relativeName")
            continue
        }
        $targetPath = Join-Path $resolvedPackageDirectory $relativeName
        if (-not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            $failures.Add("マニフェスト記載ファイルがありません: $relativeName")
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
        if ($actualHash -ne $expectedHash) {
            $failures.Add("SHA256不一致: $relativeName")
        }
    }
    foreach ($fileName in $expectedFiles) {
        if (-not $manifestFiles.Contains($fileName)) {
            $failures.Add("SHA256SUMS.txtに未記載: $fileName")
        }
    }
}

$installerPath = Join-Path $resolvedPackageDirectory $installerName
if (Test-Path -LiteralPath $installerPath -PathType Leaf) {
    $installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    if ($installerHash -ne $expectedInstallerHash) {
        $failures.Add('診断版インストーラーの固定SHA-256と一致しません。')
    }
    $signatureStatus = (Get-AuthenticodeSignature -LiteralPath $installerPath).Status.ToString()
    if ($signatureStatus -ne 'NotSigned') {
        $failures.Add("想定外の署名状態: $signatureStatus")
    }
}

$windowsInfo = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$windowsName = if ([int]$windowsInfo.CurrentBuildNumber -ge 22000) {
    "Windows 11 $($windowsInfo.EditionID)"
}
else {
    $windowsInfo.ProductName
}
$webView = Get-ItemProperty -Path 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\*' -ErrorAction SilentlyContinue |
    Where-Object { $_.name -like '*WebView2*' } |
    Select-Object -First 1

Write-Host 'Surface画像保存診断パッケージを確認しました。'
Write-Host "  Windows: $windowsName $($windowsInfo.DisplayVersion) build $($windowsInfo.CurrentBuildNumber)"
Write-Host "  WebView2: $(if ($webView) { $webView.pv } else { '未検出' })"
Write-Host "  Installer SHA-256: $expectedInstallerHash"
Write-Host '  Authenticode: NotSigned'

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Error $failure }
    exit 1
}

Write-Host 'PASS: 診断パッケージの全ファイルと固定ハッシュが一致しました。' -ForegroundColor Green
Write-Warning '未署名の試験専用診断版です。許可されたSurface以外では実行しないでください。'
