[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src-csharp/KnowledgeApp.Shared'
$task = Join-Path $project ('bin/package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $task 'publish'
$package = Join-Path $task 'app'
Assert-CSharpPackageNormalPath $task
if (Test-Path -LiteralPath $task) { throw '既存の配布フォルダーへ上書きしません。' }
[IO.Directory]::CreateDirectory($package) | Out-Null
& dotnet publish (Join-Path $project 'KnowledgeApp.Shared.csproj') --configuration Release --no-restore --self-contained false -p:DebugType=None -p:DebugSymbols=false "-p:PublishDir=$publish/"
if ($LASTEXITCODE -ne 0) { throw '共有サーバーのビルドに失敗しました。' }
$files = @(
    'KnowledgeApp.Shared.exe', 'KnowledgeApp.Shared.dll', 'KnowledgeApp.Shared.deps.json', 'KnowledgeApp.Shared.runtimeconfig.json',
    'KnowledgeApp.PathProbe.exe', 'KnowledgeApp.PathProbe.dll', 'KnowledgeApp.PathProbe.deps.json', 'KnowledgeApp.PathProbe.runtimeconfig.json',
    'KnowledgeApp.Data.dll', 'Isopoh.Cryptography.Argon2.dll', 'Isopoh.Cryptography.Blake2b.dll', 'Isopoh.Cryptography.SecureArray.dll',
    'Microsoft.Data.Sqlite.dll', 'Microsoft.Extensions.Hosting.WindowsServices.dll', 'System.ServiceProcess.ServiceController.dll',
    'SQLitePCLRaw.batteries_v2.dll', 'SQLitePCLRaw.core.dll', 'SQLitePCLRaw.provider.e_sqlite3.dll',
    'runtimes/win/lib/net10.0/System.ServiceProcess.ServiceController.dll', 'runtimes/win-x64/native/e_sqlite3.dll'
)
foreach ($file in Get-ChildItem -LiteralPath $publish -File -Filter '*.dll') {
    if ($file.Name -cnotin $files) { throw ('未確認の依存DLLです: ' + $file.Name) }
}
foreach ($relative in $files) {
    $source = Join-Path $publish $relative
    Assert-CSharpPackageNormalPath $source
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw ('配布ファイルが不足しています: ' + $relative) }
    $destination = Join-Path $package $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [IO.File]::Copy($source, $destination, $false)
}
[IO.File]::Copy((Join-Path $PSScriptRoot 'Install-KnowledgeAppShared.ps1'), (Join-Path $package 'Install-KnowledgeAppShared.ps1'))
[IO.File]::Copy((Join-Path $repo '共有サーバー導入手順.md'), (Join-Path $package '共有サーバー導入手順.md'))
$entries = @(Get-ChildItem -LiteralPath $package -File -Recurse | ForEach-Object { [ordered]@{path=$_.FullName.Substring($package.Length+1).Replace('\','/');bytes=$_.Length;sha256=(Get-CSharpPackageSha256 $_.FullName)} })
$manifest = [ordered]@{version='0.7.1';schemaVersion=9;runtime='Windows 11 x64 / ASP.NET Core Runtime 10';productionDataOpened=$false;serviceInstalled=$false;files=$entries}
[IO.File]::WriteAllText((Join-Path $package 'PACKAGE-MANIFEST.json'), (($manifest | ConvertTo-Json -Depth 6).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
& (Join-Path $PSScriptRoot 'check-no-runtime-data.ps1') -ReleaseDirectory $package
if ($LASTEXITCODE -ne 0) { throw '実データ混入検査に失敗しました。' }
$archive = Join-Path $task 'KnowledgeApp-Shared-0.7.1-Windows-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $archive)
[PSCustomObject]@{packageDirectory=$package;archive=$archive;sha256=(Get-CSharpPackageSha256 $archive);serviceInstalled=$false}
