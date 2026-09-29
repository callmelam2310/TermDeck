# Builds the GitHub release assets into dist\release\:
#   TermDeck-Setup-<version>.exe              (installer, needs Inno Setup 6)
#   TermDeck-<version>-portable-win-x64.zip   (portable, no user data)
# Usage: powershell -ExecutionPolicy Bypass -File build-release.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\TermDeck\TermDeck.csproj"
$dist = Join-Path $root "dist"
$release = Join-Path $dist "release"
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force $release | Out-Null

# Installer
& (Join-Path $root "build-installer.ps1")
Move-Item (Join-Path $dist "TermDeck-Setup-$version.exe") $release

# Portable zip from a clean folder (never from dist\TermDeck-portable, which may hold your own data\)
$portable = Join-Path $dist "portable-staging\TermDeck"
if (Test-Path (Split-Path $portable)) { Remove-Item (Split-Path $portable) -Recurse -Force }
Copy-Item (Join-Path $dist "installer-staging") $portable -Recurse
Set-Content -Path (Join-Path $portable "portable.txt") -Encoding ascii -Value @"
This file enables portable mode: settings and WebView2 data live in the data\ folder next to TermDeck.exe.
Delete this file (and the data\ folder) to make TermDeck use %APPDATA%\TermDeck instead.
"@
New-Item -ItemType Directory -Force (Join-Path $portable "tools") | Out-Null
Compress-Archive -Path $portable -DestinationPath (Join-Path $release "TermDeck-$version-portable-win-x64.zip")
Remove-Item (Split-Path $portable) -Recurse -Force

# Checksums for the release notes
Get-ChildItem $release -File | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower(), $_.Name
} | Set-Content (Join-Path $release "SHA256SUMS.txt") -Encoding ascii

Get-ChildItem $release | ForEach-Object { "{0,-45} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB) }
