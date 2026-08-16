[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$BaselinePackageZip,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$baselineZip = (Resolve-Path -LiteralPath $BaselinePackageZip -ErrorAction Stop).Path
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory -ErrorAction Stop).Path
$packageName = "KnowledgeApp_Surface_Rehearsal_0.4.3"
$zipPath = Join-Path $outputRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"

if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $zipHashPath)) {
    throw "出力先に同名のZIPまたはハッシュがあります。既存成果物を退避してから再実行してください: $zipPath"
}

$expectedSources = @(
    [pscustomobject]@{
        Path = Join-Path $repositoryRoot "src-tauri\target\release\bundle\nsis\KnowledgeApp_0.3.4_x64-setup.exe"
        Sha256 = "9194B31560CF076F9E244836816E12BB54EDF75A90ECA165F73837C0400CDD03"
    },
    [pscustomobject]@{
        Path = Join-Path $repositoryRoot "src-tauri\target\release\bundle\nsis\KnowledgeApp_0.4.3_x64-setup.exe"
        Sha256 = "6E3AA7508D511C1A6477C421982185FF3D447E731278D67CA5D16FA880271ABD"
    }
)

foreach ($source in $expectedSources) {
    if (-not (Test-Path -LiteralPath $source.Path -PathType Leaf)) {
        throw "インストーラーが見つかりません: $($source.Path)"
    }
    $actualHash = (Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash
    if ($actualHash -ne $source.Sha256) {
        throw "固定したインストーラーとSHA-256が一致しません: $($source.Path)"
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "KnowledgeApp-Surface-Package-$([Guid]::NewGuid().ToString('N'))"
$baselineExtract = Join-Path $temporaryRoot "baseline"
$packageParent = Join-Path $temporaryRoot "output"
$packageRoot = Join-Path $packageParent $packageName

try {
    New-Item -ItemType Directory -Path $baselineExtract, $packageRoot -Force | Out-Null
    Expand-Archive -LiteralPath $baselineZip -DestinationPath $baselineExtract

    $baselineInstaller = @(Get-ChildItem -LiteralPath $baselineExtract -Recurse -File -Filter "KnowledgeApp_0.2.0_x64-setup.exe")
    $testImage = @(Get-ChildItem -LiteralPath $baselineExtract -Recurse -File -Filter "knowledgeapp-test-image.png")
    if ($baselineInstaller.Count -ne 1 -or $testImage.Count -ne 1) {
        throw "旧版パッケージから0.2.0インストーラーまたは合成画像を一意に取得できません。"
    }
    $baselineHash = (Get-FileHash -LiteralPath $baselineInstaller[0].FullName -Algorithm SHA256).Hash
    if ($baselineHash -ne "AB2451B297B79C89C12C3196CEBA0302AD925C02764D2B6A53FE66B3BE178455") {
        throw "更新試験専用0.2.0のSHA-256が固定値と一致しません。"
    }

    $copySources = @(
        $baselineInstaller[0].FullName,
        $expectedSources[0].Path,
        $expectedSources[1].Path,
        $testImage[0].FullName,
        (Join-Path $repositoryRoot "tests\release-fixtures\external-reference-test.html"),
        (Join-Path $repositoryRoot "tests\release-fixtures\six-digit-management-id.fixture.json"),
        (Join-Path $repositoryRoot "scripts\New-KnowledgeAppLargeCsvFixture.ps1"),
        (Join-Path $repositoryRoot "scripts\verify-surface-release-package.ps1"),
        (Join-Path $repositoryRoot "Surface検証パッケージ_最初にお読みください_0.4.3.md"),
        (Join-Path $repositoryRoot "Surface実機テスト計画書_0.4.3.md"),
        (Join-Path $repositoryRoot "Surface実機リハーサル手順_0.4.3.md"),
        (Join-Path $repositoryRoot "Surface実機試験結果_0.4.3.md"),
        (Join-Path $repositoryRoot "リリースノート_0.4.3.md"),
        (Join-Path $repositoryRoot "KnowledgeApp運用手順.md")
    )

    foreach ($sourcePath in $copySources) {
        $resolvedSource = (Resolve-Path -LiteralPath $sourcePath -ErrorAction Stop).Path
        Copy-Item -LiteralPath $resolvedSource -Destination (Join-Path $packageRoot ([IO.Path]::GetFileName($resolvedSource)))
    }

    $manifestLines = Get-ChildItem -LiteralPath $packageRoot -File |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "$hash *$($_.Name)"
        }
    $manifestText = ($manifestLines -join "`n") + "`n"
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot "SHA256SUMS.txt"),
        $manifestText,
        [Text.UTF8Encoding]::new($false)
    )

    & pwsh -NoProfile -File (Join-Path $packageRoot "verify-surface-release-package.ps1") -PackageDirectory $packageRoot
    if ($LASTEXITCODE -ne 0) {
        throw "Surface検証パッケージの内容検査に失敗しました。"
    }

    Compress-Archive -LiteralPath $packageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText(
        $zipHashPath,
        "$zipHash *$([IO.Path]::GetFileName($zipPath))`n",
        [Text.UTF8Encoding]::new($false)
    )

    [pscustomobject]@{
        ZipPath = $zipPath
        ZipBytes = (Get-Item -LiteralPath $zipPath).Length
        ZipSha256 = $zipHash
        PackageFiles = (Get-ChildItem -LiteralPath $packageRoot -File).Count
    }
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $resolvedSystemTemp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemporaryRoot.StartsWith($resolvedSystemTemp, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
