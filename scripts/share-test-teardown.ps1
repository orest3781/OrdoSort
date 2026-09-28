# Undoes share-test-setup.ps1: removes the two test shares, the stored
# credential and the local test user. Leaves the folder's files for you to
# delete. Run it from an elevated PowerShell.

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator).'
}

foreach ($name in 'OrdoSortTest$', 'OrdoSortTestRO$') {
    if (Get-SmbShare -Name $name -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $name -Force }
}
# The saved sign-in lives in your normal (not elevated) session; run
# 'cmdkey /delete:127.0.0.1' there too if it is still listed.
cmdkey /delete:127.0.0.1 | Out-Null
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $env:LOCALAPPDATA 'OrdoSortShareTest\credential.dpapi')
if (Get-LocalUser -Name 'OrdoSortShareTest' -ErrorAction SilentlyContinue) {
    Remove-LocalUser -Name 'OrdoSortShareTest'
}
Write-Host "Removed. The folder $(Join-Path $env:LOCALAPPDATA 'OrdoSortShareTest') is left in place."
