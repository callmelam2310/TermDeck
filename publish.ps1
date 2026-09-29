# Builds the portable package: dist\TermDeck-portable\TermDeck.exe (self-contained .NET runtime) + portable.txt
# Usage: powershell -ExecutionPolicy Bypass -File publish.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "dist\TermDeck-portable"

# Replace the program files but keep the user's data\ (config, tools) and tools\ folders.
if (Test-Path $out) {
    Get-ChildItem $out -Force | Where-Object { $_.Name -notin @("data", "tools") } | Remove-Item -Recurse -Force
}
dotnet publish (Join-Path $root "src\TermDeck\TermDeck.csproj") -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
# The WebView2 package copies its XML API docs to the output; they are not needed at runtime.
Get-ChildItem $out -Filter *.xml | Remove-Item

Set-Content -Path (Join-Path $out "portable.txt") -Encoding ascii -Value @"
This file enables portable mode: settings and WebView2 data live in the data\ folder next to TermDeck.exe.
Delete this file (and the data\ folder) to make TermDeck use %APPDATA%\TermDeck instead.
"@
New-Item -ItemType Directory -Force (Join-Path $out "tools") | Out-Null

$exe = Get-Item (Join-Path $out "TermDeck.exe")
"Done: $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"
