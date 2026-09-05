[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('get-category-catalog.ps1', 'get-delegation.ps1', 'get-mail-delegation.ps1', 'submit-faq-proposal.ps1')]
    [string]$CommandName,
    [Parameter(Mandatory = $true)]
    [string]$TestDataRoot,
    [string]$DelegationId
)

$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false, $true)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
$taskRoot = [IO.Path]::GetFullPath($TestDataRoot)
$taskOwner = [IO.Path]::GetDirectoryName($taskRoot)
$temporaryRoot = [IO.Path]::GetTempPath().TrimEnd([char[]]'\/')
if ($env:KNOWLEDGEAPP_PLUGIN_TEST_MODE -ne '1' -or
    [IO.Path]::GetDirectoryName($taskOwner) -ne $temporaryRoot -or
    [IO.Path]::GetFileName($taskOwner) -notmatch '^knowledgeapp-data-check-[0-9a-f-]{36}$' -or
    [IO.Path]::GetFileName($taskRoot) -ne 'knowledgeapp-plugin-test-commands') {
    throw 'Only an owned synthetic plugin test root is allowed.'
}
$sourceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$command = Join-Path $sourceRoot ('codex-plugins\knowledgeapp-faq\scripts\' + $CommandName)
# Windows PowerShell 5.1 otherwise reads BOM-less source in the ANSI code page.
# Execute the actual fixed source with an explicit UTF-8 decode, not a copy or
# a rewritten implementation. Proposal content is passed only as a parameter.
$commandBlock = [scriptblock]::Create([IO.File]::ReadAllText($command, $utf8))
if ($CommandName -in @('get-delegation.ps1', 'get-mail-delegation.ps1')) {
    & $commandBlock -TestDataRoot $taskRoot -DelegationId $DelegationId
}
elseif ($CommandName -eq 'submit-faq-proposal.ps1') {
    & $commandBlock -TestDataRoot $taskRoot -ProposalJson ([Console]::In.ReadToEnd())
}
else {
    & $commandBlock -TestDataRoot $taskRoot
}
