# Builds the Windows installer: dist\TermDeck-Setup-<version>.exe (Inno Setup 6 required).
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$project = Join-Path $root "src\TermDeck\TermDeck.csproj"
$staging = Join-Path $root "dist\installer-staging"
$dist = Join-Path $root "dist"

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup" }

$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

# Plain publish (no portable.txt / data\): the installed app stores settings in %APPDATA%\TermDeck.
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
dotnet publish $project -c Release -o $staging
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Get-ChildItem $staging -Filter *.xml | Remove-Item

& $iscc "/DAppVersion=$version" "/DSourceDir=$staging" "/DOutputDir=$dist" (Join-Path $root "installer\TermDeck.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

$setup = Get-Item (Join-Path $dist "TermDeck-Setup-$version.exe")
"Done: $($setup.FullName) ($([math]::Round($setup.Length / 1MB, 1)) MB)"
