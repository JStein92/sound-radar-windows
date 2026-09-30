# Builds SoundRadar.exe (Release) and the MSI installer, then copies both to .\artifacts.
#   .\build.ps1                  (version from SoundRadar.csproj)
#   .\build.ps1 -Version 1.0.1   (override, e.g. for a release)
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$versionArg = if ($Version) { "-p:Version=$Version" } else { $null }

dotnet build src\SoundRadar\SoundRadar.csproj -c Release -nologo -v q $versionArg
if ($LASTEXITCODE) { throw "App build failed" }

# The MSI takes its version from SoundRadar.exe, so it always matches the app.
dotnet build installer\SoundRadar.Installer.wixproj -c Release -nologo -v q
if ($LASTEXITCODE) { throw "Installer build failed" }

$built = (Get-Item src\SoundRadar\bin\Release\net48\SoundRadar.exe).VersionInfo.ProductVersion
New-Item -ItemType Directory -Force artifacts | Out-Null
Copy-Item installer\bin\Release\SoundRadar-Setup.msi "artifacts\SoundRadar-Setup-$built.msi" -Force
try {
    Copy-Item src\SoundRadar\bin\Release\net48\SoundRadar.exe "artifacts\SoundRadar.exe" -Force
}
catch [System.IO.IOException] {
    Write-Warning "artifacts\SoundRadar.exe is running, so it wasn't refreshed. Quit SoundRadar and rebuild to update it (the MSI is up to date)."
}
Get-ChildItem artifacts | Select-Object Name, @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB) } }
