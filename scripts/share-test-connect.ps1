# Second half of share-test-setup.ps1, run WITHOUT admin (the tests run as
# you, not elevated): opens \\127.0.0.1 as the test user, with the password
# the setup kept encrypted for you. The password is never shown.
#
# Windows keeps one sign-in per server name and, with none open, connects to
# this PC as you (your account is allowed on the share, so a saved credential
# is never needed). So the connection is opened explicitly, and it must be the
# first one to 127.0.0.1 since Windows started or since the old one was closed
# (as administrator: Get-SmbSession | Where-Object ClientComputerName -match
# '^(127\.|\[?::1)' | Close-SmbSession -Force). Safe to run again.

param([string]$Server = '127.0.0.1')

$ErrorActionPreference = 'Stop'

$secret = Join-Path $env:LOCALAPPDATA 'OrdoSortShareTest\credential.dpapi'
if (-not (Test-Path $secret)) { throw "No $secret - run share-test-setup.ps1 as administrator first." }
$secure = Get-Content -Path $secret | ConvertTo-SecureString
$plain = [Runtime.InteropServices.Marshal]::PtrToStringUni(
    [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($secure))

$share = "\\$Server\OrdoSortTest`$"
$out = & net use $share /user:"$env:COMPUTERNAME\OrdoSortShareTest" $plain /persistent:no 2>&1
$code = $LASTEXITCODE
$plain = $null
if ($code -ne 0) {
    # 1219: already connected to this server as someone else (you).
    throw "Could not connect as the test user (net use exit $code): $($out -join ' ')"
}
Write-Host "Connected: $share as the test user."
