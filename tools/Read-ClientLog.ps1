# Reads tModLoader client.log for Advanced Keybinds diagnostics (agent/dev use).
param(
	[int]$TailLines = 400,
	[switch]$AkDiagOnly,
	[switch]$Json
)

$ErrorActionPreference = 'SilentlyContinue'

$candidates = @(
	'F:\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log',
	'C:\Users\runra\Documents\My Games\Terraria\tModLoader\client.log',
	(Join-Path $env:USERPROFILE 'Documents\My Games\Terraria\tModLoader\client.log'),
	(Join-Path $env:USERPROFILE 'Documents\My Games\Terraria\tModLoader\Logs\client.log'),
	(Join-Path $env:USERPROFILE 'Desktop\client.log')
)

$logPath = $null
foreach ($p in $candidates) {
	if (Test-Path -LiteralPath $p) {
		$logPath = $p
		break
	}
}

if (-not $logPath) {
	$result = [ordered]@{
		found = $false
		path = $null
		message = 'client.log not found. Launch tModLoader, Build + Reload, then reproduce.'
		lines = @()
	}
	if ($Json) { $result | ConvertTo-Json -Depth 4 } else { Write-Output $result.message }
	exit 1
}

$raw = Get-Content -LiteralPath $logPath -Tail $TailLines -Encoding UTF8
$patterns = @('[AKDiag]', 'Advanced Keybinds', 'AdvancedKeybinds')
$lines = if ($AkDiagOnly) {
	$raw | Where-Object { $_ -match '\[AKDiag\]' }
} else {
	$raw | Where-Object { $p = $_; $patterns | Where-Object { $p -like "*$_*" } }
}

$result = [ordered]@{
	found = $true
	path = $logPath
	lastWriteTime = (Get-Item -LiteralPath $logPath).LastWriteTime.ToString('o')
	lineCount = $lines.Count
	lines = @($lines)
}

if ($Json) {
	$result | ConvertTo-Json -Depth 4
}
else {
	Write-Output "path: $($result.path)"
	Write-Output "lastWriteTime: $($result.lastWriteTime)"
	Write-Output "matches: $($result.lineCount)"
	foreach ($line in $lines) { Write-Output $line }
}
