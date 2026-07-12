# Enables or disables compile-time AKDIAG (diagnostic [AKDiag] logging).
# Default builds have AKDIAG undefined — diagnostic log call sites are compiled out.
param(
	[Parameter(Mandatory = $true)]
	[bool]$Enabled
)

$ErrorActionPreference = 'Stop'

$csproj = Join-Path $PSScriptRoot '..\AdvancedKeybinds.csproj'
if (-not (Test-Path -LiteralPath $csproj)) {
	throw "csproj not found: $csproj"
}

$content = Get-Content -LiteralPath $csproj -Raw
$marker = '<!-- AKDIAG_DEFINE_MARKER -->'
$activeLine = '    <DefineConstants>$(DefineConstants);AKDIAG</DefineConstants>'
# Only match a real element line, never an XML comment.
$activePattern = '(?m)^\s*<DefineConstants>\s*\$\(DefineConstants\);\s*AKDIAG\s*</DefineConstants>\s*\r?$'

$hasActive = [regex]::IsMatch($content, $activePattern)

if ($Enabled) {
	if ($hasActive) {
		Write-Host "AKDIAG already enabled ($csproj)"
		exit 0
	}
	if ($content -notmatch [regex]::Escape($marker)) {
		throw "Could not find AKDIAG_DEFINE_MARKER in $csproj"
	}
	$updated = $content.Replace($marker, "$marker`r`n$activeLine")
	Set-Content -LiteralPath $csproj -Value $updated -NoNewline -Encoding utf8
	Write-Host "AKDIAG enabled ($csproj)"
}
else {
	if (-not $hasActive) {
		Write-Host "AKDIAG already disabled ($csproj)"
		exit 0
	}
	$updated = [regex]::Replace($content, $activePattern + '\r?\n?', '')
	if ($updated -eq $content) {
		throw "Failed to remove AKDIAG DefineConstants from $csproj"
	}
	Set-Content -LiteralPath $csproj -Value $updated -NoNewline -Encoding utf8
	Write-Host "AKDIAG disabled ($csproj)"
}
