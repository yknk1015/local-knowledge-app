[CmdletBinding()]
param(
    [string]$PackageDirectory = $PSScriptRoot
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$resolvedPackageDirectory = (Resolve-Path -LiteralPath $PackageDirectory -ErrorAction Stop).Path
$expectedInstallers = @(
    [pscustomobject]@{
        FileName = "KnowledgeApp_0.2.0_x64-setup.exe"
        Sha256 = "AB2451B297B79C89C12C3196CEBA0302AD925C02764D2B6A53FE66B3BE178455"
        Purpose = "上書き更新試験専用の旧版（commit 5f99114e）"
    },
    [pscustomobject]@{
        FileName = "KnowledgeApp_0.3.4_x64-setup.exe"
        Sha256 = "9194B31560CF076F9E244836816E12BB54EDF75A90ECA165F73837C0400CDD03"
        Purpose = "旧検索情報・関連FAQ・変更済みパスワードを作る中間更新版"
    },
    [pscustomobject]@{
        FileName = "KnowledgeApp_0.4.3_x64-setup.exe"
        Sha256 = "6E3AA7508D511C1A6477C421982185FF3D447E731278D67CA5D16FA880271ABD"
        Purpose = "画像保存の実機確認を反映した本格利用候補版"
    }
)

$failures = [System.Collections.Generic.List[string]]::new()

Write-Host "KnowledgeApp Surface検証パッケージを確認します。"
Write-Host "パッケージ: $resolvedPackageDirectory"
Write-Host ""

foreach ($expected in $expectedInstallers) {
    $installerPath = Join-Path $resolvedPackageDirectory $expected.FileName
    if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
        $failures.Add("不足: $($expected.FileName)")
        continue
    }

    $actualHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
    $signature = Get-AuthenticodeSignature -LiteralPath $installerPath
    $hashResult = if ($actualHash -eq $expected.Sha256) { "OK" } else { "不一致" }

    Write-Host "$($expected.FileName)"
    Write-Host "  用途: $($expected.Purpose)"
    Write-Host "  SHA-256: $actualHash [$hashResult]"
    Write-Host "  Authenticode: $($signature.Status)"

    if ($actualHash -ne $expected.Sha256) {
        $failures.Add("SHA-256不一致: $($expected.FileName)")
    }
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
        $failures.Add("記録と異なる署名状態: $($expected.FileName) = $($signature.Status)")
    }
}

$manifestPath = Join-Path $resolvedPackageDirectory "SHA256SUMS.txt"
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $failures.Add("不足: SHA256SUMS.txt")
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
        if ([IO.Path]::IsPathRooted($relativeName) -or $relativeName.Contains("..")) {
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
            $failures.Add("マニフェストSHA-256不一致: $relativeName")
        }
    }
    foreach ($packageFile in Get-ChildItem -LiteralPath $resolvedPackageDirectory -File) {
        if ($packageFile.Name -eq "SHA256SUMS.txt") { continue }
        if (-not $manifestFiles.Contains($packageFile.Name)) {
            $failures.Add("SHA256SUMS.txtに未記載のファイル: $($packageFile.Name)")
        }
    }
    $unexpectedDirectories = @(Get-ChildItem -LiteralPath $resolvedPackageDirectory -Directory)
    foreach ($directory in $unexpectedDirectories) {
        $failures.Add("想定外のサブフォルダ: $($directory.Name)")
    }
}

$windowsInfo = Get-ItemProperty -LiteralPath "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
$windowsBuild = [int]$windowsInfo.CurrentBuildNumber
$windowsName = if ($windowsBuild -ge 22000) { "Windows 11" } else { "$($windowsInfo.ProductName)" }
Write-Host ""
Write-Host "Surface環境"
Write-Host "  Windows: $windowsName $($windowsInfo.DisplayVersion) build $windowsBuild"
Write-Host "  64ビットOS: $([Environment]::Is64BitOperatingSystem)"

if (-not [Environment]::Is64BitOperatingSystem) {
    $failures.Add("64ビットWindowsではありません。")
}

$webView2Names = [System.Collections.Generic.List[string]]::new()
$edgeUpdateRoots = @(
    "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
    "HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients"
)
foreach ($root in $edgeUpdateRoots) {
    if (-not (Test-Path -LiteralPath $root)) { continue }
    foreach ($client in Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue) {
        $properties = Get-ItemProperty -LiteralPath $client.PSPath -ErrorAction SilentlyContinue
        if ($null -ne $properties -and "$($properties.name)" -match "WebView2") {
            $webView2Names.Add("$($properties.name) $($properties.pv)".Trim())
        }
    }
}
$webView2Result = if ($webView2Names.Count -gt 0) { $webView2Names -join ", " } else { "レジストリでは未検出（インストーラー起動時にも確認）" }
Write-Host "  WebView2: $webView2Result"

$excelPaths = @(
    "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe",
    "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\excel.exe",
    "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\excel.exe"
)
$excelFound = $false
foreach ($excelPath in $excelPaths) {
    if (Test-Path -LiteralPath $excelPath) { $excelFound = $true; break }
}
Write-Host "  Excel: $(if ($excelFound) { '検出' } else { '未検出（CSV試験にはExcelが必要）' })"

Write-Host ""
if ($failures.Count -gt 0) {
    Write-Host "検証失敗:" -ForegroundColor Red
    foreach ($failure in $failures) { Write-Host "  - $failure" -ForegroundColor Red }
    exit 1
}

Write-Host "PASS: パッケージのファイルと候補インストーラーのSHA-256が一致しました。" -ForegroundColor Green
Write-Warning "3件のインストーラーは未署名です。許可された試験用Surface以外では実行せず、セキュリティ警告を会社規定に反して回避しないでください。"
