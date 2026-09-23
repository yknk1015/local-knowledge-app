# The descriptor contains only exchange location and generation, never database/authentication data.
function Assert-KnowledgeExchangePath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]' -or $Path -match '^\\\\' -or $Path -match '[<>"|?*]' -or $Path.Substring(2).Contains(':')) { throw '通常のローカル絶対パスだけ使用できます。' }
    foreach ($part in $Path.Substring(3).Split([char[]]'\/')) {
        if ($part -in @('.', '..') -or $part.EndsWith('.') -or $part.EndsWith(' ')) { throw 'パスの省略や末尾の空白は使用できません。' }
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([char[]]'\/')
    $drive = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full))
    if ($drive.DriveType -ne [IO.DriveType]::Fixed) { throw 'Codex連携先はローカルドライブにしてください。' }
    $current = $full
    while ($current) {
        try {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Device)) -ne 0) { throw 'リンクや特殊なパスは使用できません。' }
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        try {
            [void][IO.File]::GetAttributes([IO.Path]::Combine($current, '.git'))
            throw 'Gitリポジトリ内の連携先は使用できません。'
        } catch [IO.FileNotFoundException] { } catch [IO.DirectoryNotFoundException] { }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}
function Read-KnowledgeExchangeJson([string]$Path) {
    [void](Assert-KnowledgeExchangePath $Path)
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -gt 65536) { throw '連携先の設定サイズが不正です。' }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true))
        try { return ($reader.ReadToEnd() | ConvertFrom-Json) } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
function Open-KnowledgeExchange([string]$DataRoot) {
    [void](Assert-KnowledgeExchangePath $DataRoot)
    $descriptor = Join-Path $DataRoot 'device-settings\codex-location.json'
    if (-not [IO.File]::Exists($descriptor)) { return @{ Root=$DataRoot; Lease=$null; Generation=0; EnvironmentId='' } }
    $lockPath = Join-Path $DataRoot 'device-settings\codex-location.lock'
    [void](Assert-KnowledgeExchangePath $lockPath)
    $lease = [IO.FileStream]::new($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $config = Read-KnowledgeExchangeJson $descriptor
        if ($config.version -ne 1 -or $config.generation -lt 1 -or
            ([string]$config.environmentId -notmatch '^[0-9a-fA-F-]{36}$') -or
            @($config.PSObject.Properties.Name | Where-Object { $_ -notin @('version','environmentId','generation','root') }).Count -ne 0) { throw '連携先の設定形式が正しくありません。' }
        $root = Assert-KnowledgeExchangePath ([string]$config.root)
        $allowedSids = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')
        foreach ($rule in (Get-Acl -LiteralPath $root).GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
            if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin $allowedSids -and
                ($rule.FileSystemRights -band ([Security.AccessControl.FileSystemRights]::Write -bor [Security.AccessControl.FileSystemRights]::Delete -bor [Security.AccessControl.FileSystemRights]::DeleteSubdirectoriesAndFiles -bor [Security.AccessControl.FileSystemRights]::ChangePermissions -bor [Security.AccessControl.FileSystemRights]::TakeOwnership))) { throw '連携先を他の利用者が変更できます。権限を確認してください。' }
        }
        $owner = Read-KnowledgeExchangeJson (Join-Path $root '.knowledgeapp-codex-owner.json')
        if ($owner.version -ne 1 -or $owner.environmentId -cne $config.environmentId -or
            $owner.generation -ne $config.generation -or $owner.root -cne $config.root) { throw '連携先の環境・世代が一致しません。' }
        return @{ Root=$root; Lease=$lease; Generation=$config.generation; EnvironmentId=$config.environmentId }
    } catch { $lease.Dispose(); throw }
}
