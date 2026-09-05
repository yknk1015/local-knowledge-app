[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [string]$SourceUiDirectory
)
. (Join-Path $PSScriptRoot 'CSharpTrialPackage.Common.ps1')
Test-CSharpTrialPackage $PackageDirectory $SourceUiDirectory
