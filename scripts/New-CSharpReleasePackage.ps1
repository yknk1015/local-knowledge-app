[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# Keep the reviewed allowlist/manifest implementation in one place. The legacy
# script name remains an internal compatibility entry point, not a trial mode.
& (Join-Path $PSScriptRoot 'New-CSharpTrialPackage.ps1')
