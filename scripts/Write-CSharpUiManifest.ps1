[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$UiDirectory,
    [Parameter(Mandatory = $true)][string]$ManifestPath
)

$ErrorActionPreference = 'Stop'
function Get-UiSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}
$uiRoot = [IO.Path]::GetFullPath($UiDirectory).TrimEnd('\', '/')
$manifest = [IO.Path]::GetFullPath($ManifestPath)
if ([IO.Path]::GetDirectoryName($uiRoot) -ne [IO.Path]::GetDirectoryName($manifest) -or
    [IO.Path]::GetFileName($manifest) -ne 'ui-manifest.json') { throw 'Manifest must be adjacent to the fixed ui directory.' }
function Assert-NormalPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            if (((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse paths are not permitted.' }
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}
Assert-NormalPath $uiRoot
Assert-NormalPath $manifest
$files = [Collections.Generic.List[object]]::new()
foreach ($entry in Get-ChildItem -LiteralPath $uiRoot -Force) {
    Assert-NormalPath $entry.FullName
    if ($entry.PSIsContainer) {
        if ($entry.Name -cne 'assets') { throw 'Only the assets directory is allowed in ui.' }
        $entries = @(Get-ChildItem -LiteralPath $entry.FullName -Force)
    } else { $entries = @($entry) }
    foreach ($file in $entries) {
        Assert-NormalPath $file.FullName
        $relative = $file.FullName.Substring($uiRoot.Length + 1).Replace('\', '/')
        if ($file.PSIsContainer -or ($relative -cne 'index.html' -and $relative -cnotmatch '^assets/[A-Za-z0-9_-]+\.(js|css|woff2?|png|jpe?g|webp|gif|ico)$')) { throw 'Unexpected UI file.' }
        $maximum = if ($relative -ceq 'index.html') { 1MB } else { 16MB }
        if ($file.Length -le 0 -or $file.Length -gt $maximum) { throw 'UI file size is not permitted.' }
        $files.Add([PSCustomObject][ordered]@{path=$relative; size=$file.Length; sha256=(Get-UiSha256 $file.FullName)})
    }
}
if ($files.Count -lt 3 -or $files.Count -gt 1024 -or 'index.html' -cnotin @($files.path) -or
    @($files.path | Where-Object { $_ -cmatch '\.js$' }).Count -eq 0 -or
    @($files.path | Where-Object { $_ -cmatch '\.css$' }).Count -eq 0 -or
    ($files | Measure-Object -Property size -Sum).Sum -gt 128MB) { throw 'UI bundle is incomplete or too large.' }
$json = [ordered]@{formatVersion=1; files=@($files | Sort-Object path)} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText($manifest, $json.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Output "UI manifest: $($files.Count) files."
