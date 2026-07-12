param(
    [Parameter(Mandatory = $true)]
    [string]$ZipPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedSha256
)

$ErrorActionPreference = "Stop"
$actual = (Get-FileHash -Algorithm SHA256 -Path $ZipPath).Hash.ToLowerInvariant()
if ($actual -ne $ExpectedSha256.ToLowerInvariant()) {
    throw "SHA-256 mismatch. Expected $ExpectedSha256 but got $actual"
}

$destination = Join-Path $env:TEMP "ChatGPTConnector-preview"
if (Test-Path $destination) { Remove-Item -Recurse -Force $destination }
Expand-Archive -Path $ZipPath -DestinationPath $destination
$executable = Join-Path $destination "ChatGPTConnector.exe"
if (-not (Test-Path $executable)) { throw "ChatGPTConnector.exe is missing from the archive." }

Write-Host "Integrity check passed." -ForegroundColor Green
Write-Host "Preview executable: $executable"
Write-Host "This preview is unsigned. Do not bypass Windows security warnings on untrusted copies."
