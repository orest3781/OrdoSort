# One-time setup for the network-share tests (Integration only). Run it from
# an elevated PowerShell:  powershell -ExecutionPolicy Bypass -File scripts\share-test-setup.ps1
#
# It makes this PC act as a file server for two "stations":
#   \\localhost\OrdoSortTest$   - you, as usual
#   \\127.0.0.1\OrdoSortTest$   - a local test user, OrdoSortShareTest, who can
#                                 edit files but not change their permissions or
#                                 owner (a plain "Modify" user on an office share)
#   \\127.0.0.1\OrdoSortTestRO$ - the same folder, read-only for the test user
# Windows keeps one set of credentials per server name, so the two names give
# two users. The test user's password is random, never shown, and kept only in
# your Windows Credential Manager (for 127.0.0.1), put there by
# share-test-connect.ps1. Undo with share-test-teardown.ps1.

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this from an elevated PowerShell (Run as administrator).'
}

$userName = 'OrdoSortShareTest'
$owner = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$root = Join-Path $env:LOCALAPPDATA 'OrdoSortShareTest\share'

# 24 random characters from a set every password policy accepts.
$chars = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!#%+'
$bytes = New-Object byte[] 24
[Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$plain = -join ($bytes | ForEach-Object { $chars[$_ % $chars.Length] }) + 'a1!'
$secure = ConvertTo-SecureString $plain -AsPlainText -Force

if (Get-LocalUser -Name $userName -ErrorAction SilentlyContinue) {
    Set-LocalUser -Name $userName -Password $secure
} else {
    New-LocalUser -Name $userName -Password $secure -PasswordNeverExpires `
        -UserMayNotChangePassword -Description 'OrdoSort network-share tests' | Out-Null
}

New-Item -ItemType Directory -Force -Path $root | Out-Null
# The test user may create, change and delete files here (Modify), but not
# change permissions or take ownership: exactly what PR #6's bug needs.
icacls $root /grant "${userName}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed ($LASTEXITCODE)" }

foreach ($name in 'OrdoSortTest$', 'OrdoSortTestRO$') {
    if (Get-SmbShare -Name $name -ErrorAction SilentlyContinue) { Remove-SmbShare -Name $name -Force }
}
New-SmbShare -Name 'OrdoSortTest$' -Path $root -FullAccess $owner -ChangeAccess $userName `
    -Description 'OrdoSort network-share tests' | Out-Null
New-SmbShare -Name 'OrdoSortTestRO$' -Path $root -FullAccess $owner -ReadAccess $userName `
    -Description 'OrdoSort network-share tests (read-only)' | Out-Null

# The password is kept encrypted for this Windows user only (DPAPI). A
# credential saved from here would land in the elevated session's store, which
# ordinary programs don't see, so share-test-connect.ps1 (run without admin)
# turns this into the saved sign-in for 127.0.0.1.
$secret = Join-Path (Split-Path $root) 'credential.dpapi'
$secure | ConvertFrom-SecureString | Set-Content -Path $secret -Encoding ascii
$plain = $null

Write-Host "Ready. Shares: \\localhost\OrdoSortTest$ (you), \\127.0.0.1\OrdoSortTest$ (test user), \\127.0.0.1\OrdoSortTestRO$ (read-only)."
Write-Host "Folder: $root"
Write-Host "Next, WITHOUT admin: powershell -ExecutionPolicy Bypass -File scripts\share-test-connect.ps1"
