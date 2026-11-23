# Copyright 2026 Shazron Abdullah and Bunyi contributors
#
# Licensed under the Apache License, Version 2.0 (the "License");
# you may not use this file except in compliance with the License.
# You may obtain a copy of the License at
#
#     http://www.apache.org/licenses/LICENSE-2.0
#
# Unless required by applicable law or agreed to in writing, software
# distributed under the License is distributed on an "AS IS" BASIS,
# WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
# See the License for the specific language governing permissions and
# limitations under the License.
#
# Adapted for DevelApp software-field-guide-toolkit from shaztechio/bunyi-app
#
# Same adaptation lineage as DevelApp-Repo-LocalTaskhandler and
# Robot-MCP-Service scripts/sign-files.ps1 (proven green 2026-11-11,
# run 43089090840): env var names aligned with the DevelApp org
# secrets; identical sign+verify contract (org decision
# DevelApp-ai/DevelApp-Repo-Global#38).

#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Path,
    [string]$Thumbprint = $env:CERTUM_KEY_ID,
    [string]$SignToolPath = $env:DEVELAPP_SIGNTOOL,
    [switch]$VerifyOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsWindows) { throw 'Authenticode signing requires Windows.' }

$Thumbprint = ($Thumbprint -replace '\s', '').ToUpperInvariant()
if ($Thumbprint -notmatch '^[0-9A-F]{40}$') {
    throw 'Thumbprint must be a SHA-1 certificate thumbprint (40 hex characters).'
}

if (-not $SignToolPath) {
    # Discover the newest x64 SignTool from the installed Windows SDK.
    $sdk = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $candidates = @(Get-ChildItem -Path "$sdk/*/x64/signtool.exe" -File |
        Sort-Object { [version]$_.Directory.Parent.Name } -Descending)
    if (-not $candidates.Count) {
        throw 'Install the Windows SDK x64 SignTool, or set DEVELAPP_SIGNTOOL.'
    }
    $SignToolPath = $candidates[0].FullName
}
$SignToolPath = (Resolve-Path -LiteralPath $SignToolPath).Path

$files = @($Path | ForEach-Object { (Resolve-Path -LiteralPath $_).Path })
if (-not $files.Count) { throw 'No files supplied for signing.' }

if (-not $VerifyOnly) {
    $certificate = Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)) {
        throw 'The signing certificate must be valid and connected to its private key (SimplySign must be connected).'
    }

    # RFC 3161 timestamp against the Certum TSA so signatures outlive the
    # cloud certificate validity window.
    & $SignToolPath sign /sha1 $Thumbprint /fd SHA256 /tr http://time.certum.pl /td SHA256 @files
    if ($LASTEXITCODE -ne 0) { throw "SignTool signing failed (exit $LASTEXITCODE)." }
}

foreach ($file in $files) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $Thumbprint) {
        throw "Invalid signature or unexpected publisher certificate: $file"
    }
    if (-not $signature.TimeStamperCertificate) { throw "Missing trusted timestamp: $file" }
    & $SignToolPath verify /pa /all /tw $file
    if ($LASTEXITCODE -ne 0) { throw "SignTool verification failed (exit $LASTEXITCODE): $file" }
}
