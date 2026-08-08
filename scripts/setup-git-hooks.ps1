[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

Push-Location $repositoryRoot
try {
    git rev-parse --is-inside-work-tree *> $null
    if ($LASTEXITCODE -ne 0) {
        throw 'Gitリポジトリが初期化されていません。先に git init を実行してください。'
    }
    git config core.hooksPath .githooks
    if ($LASTEXITCODE -ne 0) {
        throw 'Git hookの設定に失敗しました。'
    }
    Write-Output 'Gitのコミット前検査を有効にしました。'
}
finally {
    Pop-Location
}
