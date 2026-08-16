[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $repositoryRoot 'src-tauri\Cargo.toml'

& cargo test `
    --release `
    --locked `
    --manifest-path $manifestPath `
    release_performance_fixture_meets_search_and_detail_targets `
    --lib `
    -- `
    --ignored `
    --nocapture

if ($LASTEXITCODE -ne 0) {
    throw 'リリース性能試験に失敗しました。PERF_RESULTと失敗した閾値を確認してください。'
}
