# Merges or strips Config.KeyboardDiag localization for diagnostic vs public builds.
# Source of truth for KeyboardDiag text: AdvancedKeybinds.Dev/diag-localization/
param(
	[Parameter(Mandatory = $true)]
	[ValidateSet('Merge', 'Strip')]
	[string]$Mode,
	[string]$ModRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$locDir = Join-Path $ModRoot 'Localization'
$srcDir = Join-Path $ModRoot '..\AdvancedKeybinds.Dev\diag-localization'
if ($Mode -eq 'Merge') {
	$srcDir = (Resolve-Path $srcDir).Path
}

function Strip-KeyboardDiag([string]$text) {
	return [regex]::Replace($text, '(?ms)\r?\n\tKeyboardDiag:\s*\{.*?\r?\n\t\}\r?\n', "`n")
}

function Merge-KeyboardDiag([string]$text, [string]$block) {
	$clean = Strip-KeyboardDiag $text
	# Insert before ColorChannel (sibling under AdvancedKeybinds) or before Config close.
	$block = $block -replace '(?m)^//.*\r?\n', ''
	if ($clean -notmatch '(?m)^\tColorChannel:') {
		# Fallback: before final closing of AdvancedKeybinds after Config
		return $clean.TrimEnd() + "`n" + $block.TrimEnd() + "`n"
	}
	return [regex]::Replace($clean, '(?m)^(\tColorChannel:)', ($block.TrimEnd() + "`r`n`r`n`$1"))
}

Get-ChildItem $locDir -Filter '*_Mods.AdvancedKeybinds.hjson' | ForEach-Object {
	$text = [IO.File]::ReadAllText($_.FullName)
	if ($Mode -eq 'Strip') {
		$newText = Strip-KeyboardDiag $text
		if ($newText -ne $text) {
			[IO.File]::WriteAllText($_.FullName, $newText, [Text.UTF8Encoding]::new($false))
			Write-Host "Stripped KeyboardDiag from $($_.Name)"
		}
		return
	}

	$srcFile = Join-Path $srcDir $_.Name
	if (-not (Test-Path $srcFile)) {
		Write-Host "No diag loc for $($_.Name) — skip merge"
		return
	}
	$block = [IO.File]::ReadAllText($srcFile)
	$newText = Merge-KeyboardDiag $text $block
	if ($newText -ne $text) {
		[IO.File]::WriteAllText($_.FullName, $newText, [Text.UTF8Encoding]::new($false))
		Write-Host "Merged KeyboardDiag into $($_.Name)"
	}
}
