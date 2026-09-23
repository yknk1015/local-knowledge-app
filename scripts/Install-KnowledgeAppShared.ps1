[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InstallDirectory,
    [Parameter(Mandatory=$true)][string]$HostName,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory=$true)][PSCredential]$ServiceCredential,
    [ValidateRange(1024,65535)][int]$Port = 7443
)
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw '管理者としてPowerShellを開いてください。' }
if ([Uri]::CheckHostName($HostName) -eq [UriHostNameType]::Unknown) { throw 'DNS名または固定IPを指定してください。' }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
function Assert-LocalSafePath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\') -or $Path.Contains('"') -or $Path.Substring(2).Contains(':')) { throw 'ローカルの通常パスを指定してください。' }
    $ancestor = $Path
    while ($ancestor) {
        if (Test-Path -LiteralPath $ancestor) {
            if ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw '再解析ポイントは指定できません。' }
        }
        if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw 'Gitリポジトリ内へは設置できません。' }
        $ancestor = [IO.Path]::GetDirectoryName($ancestor)
    }
}
Assert-LocalSafePath $install
$root = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonApplicationData)) 'KnowledgeApp.Shared'
Assert-LocalSafePath $root
$exe = Join-Path $install 'KnowledgeApp.Shared.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw '共有サーバー配布物全体を展開したフォルダーを指定してください。' }
if ((Get-Service -Name 'KnowledgeApp.Shared' -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $root)) { throw 'サービスまたはデータフォルダーが既に存在します。再初期化せず、運用手順の更新・復旧手順を使用してください。' }
$certificate = Get-Item -LiteralPath ('Cert:\LocalMachine\My\' + $CertificateThumbprint)
if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) { throw '有効な秘密鍵付きサーバー証明書を用意してください。' }
$account = [Security.Principal.NTAccount]::new($ServiceCredential.UserName)
$serviceSid = $account.Translate([Security.Principal.SecurityIdentifier])
[IO.Directory]::CreateDirectory($root) | Out-Null
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @([Security.Principal.SecurityIdentifier]::new('S-1-5-18'), [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'), $serviceSid)) {
    $rule = [Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
    $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $root -AclObject $acl
& $exe --initialize
if ($LASTEXITCODE -ne 0) { throw '管理者初期設定に失敗しました。データは保持しています。運用手順に従って設定を再開してください。' }
$settings = Join-Path $root 'device-settings'
[IO.Directory]::CreateDirectory($settings) | Out-Null
$serverId = [guid]::NewGuid().ToString()
$config = [ordered]@{version=1;serverId=$serverId;hostName=$HostName;port=$Port;certificateThumbprint=$CertificateThumbprint.ToLowerInvariant()}
[IO.File]::WriteAllText((Join-Path $settings 'server.json'), (($config | ConvertTo-Json).Replace("`r`n", "`n") + "`n"), [Text.UTF8Encoding]::new($false))
New-Service -Name 'KnowledgeApp.Shared' -DisplayName 'KnowledgeApp 共有サービス' -BinaryPathName ('"' + $exe + '"') -Credential $ServiceCredential -StartupType Automatic | Out-Null
Write-Output ('サーバーID: ' + $serverId)
Write-Output ('接続URL: https://' + $HostName + ':' + $Port + '/')
Write-Output '登録しました。サービス用アカウントへの証明書秘密鍵の読取権限、サービスとしてログオン、実行フォルダーの読取・実行権限、必要なLAN範囲だけのファイアウォール規則を設定してから Start-Service KnowledgeApp.Shared を実行してください。'
Write-Output 'NAS認証情報はWindows側で管理し、server.json、ソース、Gitへ記載しないでください。'
