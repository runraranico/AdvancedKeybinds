# Builds Release for distribution. Diagnostic [AKDiag] logging is off by default
# (AKDIAG compile symbol is not defined). Do not enable AKDIAG for workshop builds.
param(
	[switch]$SkipDotnetBuild,
	[switch]$WaitForTModBuild
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj = Join-Path $root 'AdvancedKeybinds.csproj'
$toggle = Join-Path $PSScriptRoot 'Set-AkDiagEnabled.ps1'

# Ensure AKDIAG is not in the project before packaging.
& $toggle -Enabled $false

if (-not $SkipDotnetBuild) {
	Write-Host 'Building Release (AkDiag / AKDIAG off)...'
	dotnet build $csproj -c Release --nologo
}

if ($WaitForTModBuild) {
	Write-Host ''
	Write-Host 'In tModLoader: Build + Reload (or publish .tmod), then press Enter here.'
	Read-Host 'Press Enter when the distribution build is finished'
}
else {
	Write-Host ''
	Write-Host 'Run tModLoader Build + Reload if you are packaging a .tmod.'
	Write-Host 'AKDIAG remains off (default). Use Set-AkDiagEnabled.ps1 -Enabled $true only for local debugging.'
}
