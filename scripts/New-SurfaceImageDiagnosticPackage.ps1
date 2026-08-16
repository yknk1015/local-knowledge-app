[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = (Resolve-Path -LiteralPath $OutputDirectory -ErrorAction Stop).Path
$packageName = 'KnowledgeApp_Surface_Image_Diagnostic_0.4.2'
$zipPath = Join-Path $outputRoot "$packageName.zip"
$zipHashPath = "$zipPath.sha256"
$installerPath = Join-Path $repositoryRoot 'src-tauri\target\release\bundle\nsis\KnowledgeApp_0.4.2_x64-setup.exe'
$expectedInstallerHash = '3E70C67DCC171F9B21EE3DD6A58ACA8C061879218D130BC0A67D568FCD18DD0E'

if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $zipHashPath)) {
    throw "出力先に同名の診断ZIPまたはハッシュがあります: $zipPath"
}
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "診断版インストーラーが見つかりません: $installerPath"
}
if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ne $expectedInstallerHash) {
    throw '診断版インストーラーの固定SHA-256と一致しません。'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "KnowledgeApp-Image-Diagnostic-$([Guid]::NewGuid().ToString('N'))"
$packageParent = Join-Path $temporaryRoot 'output'
$packageRoot = Join-Path $packageParent $packageName

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $copySources = @(
        $installerPath,
        (Join-Path $repositoryRoot 'Surface画像保存診断手順_0.4.2.md'),
        (Join-Path $repositoryRoot 'scripts\verify-surface-image-diagnostic.ps1')
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
    [IO.File]::WriteAllText(
        (Join-Path $packageRoot 'SHA256SUMS.txt'),
        (($manifestLines -join "`n") + "`n"),
        [Text.UTF8Encoding]::new($false)
    )

    & pwsh -NoProfile -File (Join-Path $packageRoot 'verify-surface-image-diagnostic.ps1') -PackageDirectory $packageRoot
    if ($LASTEXITCODE -ne 0) {
        throw '診断パッケージの内容検査に失敗しました。'
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
