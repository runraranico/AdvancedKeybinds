# Dev loop helper: build AdvancedKeybinds, optionally wait for tMod reload in client.log, tail diagnostics.
# Usage:
#   .\tools\Dev-Iterate.ps1                    # build + show recent AKDiag
#   .\tools\Dev-Iterate.ps1 -WaitReload        # build + wait up to 60s for Mod Load Completed
#   .\tools\Dev-Iterate.ps1 -Watch             # rebuild on .cs save (Ctrl+C to stop)
#   .\tools\Dev-Iterate.ps1 -VerifyStartMenu   # after manual Win/Ctrl+Esc test, check log patterns
param(
	[switch]$WaitReload,
	[switch]$Watch,
	[switch]$VerifyStartMenu,
	[int]$ReloadTimeoutSec = 90,
	[int]$LogTail = 80
)

$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$csproj = Join-Path $root 'AdvancedKeybinds.csproj'
$readLog = Join-Path $PSScriptRoot 'Read-ClientLog.ps1'

function Get-ClientLogPath {
	foreach ($p in @(
		'F:\Steam\steamapps\common\tModLoader\tModLoader-Logs\client.log',
		(Join-Path $env:USERPROFILE 'Documents\My Games\Terraria\tModLoader\client.log'),
		(Join-Path $env:USERPROFILE 'Documents\My Games\Terraria\tModLoader\Logs\client.log')
	)) {
		if (Test-Path -LiteralPath $p) { return $p }
	}
	return $null
}

function Invoke-ModBuild {
	param([string]$Reason = 'manual')

	Write-Host "=== Build ($Reason) ===" -ForegroundColor Cyan
	$logBefore = (Get-ClientLogPath)
	$stampBefore = if ($logBefore) { (Get-Item -LiteralPath $logBefore).LastWriteTimeUtc } else { [datetime]::MinValue }

	$buildOutput = dotnet build $csproj -c Debug --nologo 2>&1 | Out-String
	$exit = $LASTEXITCODE

	Write-Host $buildOutput

	$dllOk = $buildOutput -match 'AdvancedKeybinds\.dll'
	$tmodLocked = $buildOutput -match 'TML003|being used by another process'

	if ($exit -eq 0) {
		Write-Host 'Build: OK (dll + tmod)' -ForegroundColor Green
	}
	elseif ($dllOk -and $tmodLocked) {
		Write-Host 'Build: DLL compiled; .tmod packaging locked (game running). EAC reload may still apply — check client.log.' -ForegroundColor Yellow
	}
	else {
		Write-Host "Build: FAILED (exit $exit)" -ForegroundColor Red
		return $false
	}

	if ($WaitReload -and $logBefore) {
		Write-Host "Waiting up to ${ReloadTimeoutSec}s for Mod Load Completed in client.log..." -ForegroundColor Cyan
		$deadline = (Get-Date).AddSeconds($ReloadTimeoutSec)
		while ((Get-Date) -lt $deadline) {
			Start-Sleep -Seconds 2
			$stampNow = (Get-Item -LiteralPath $logBefore).LastWriteTimeUtc
			if ($stampNow -le $stampBefore) { continue }

			$tail = Get-Content -LiteralPath $logBefore -Tail 40 -Encoding UTF8
			if ($tail -match 'Mod Load Completed') {
				Write-Host 'Reload: detected Mod Load Completed' -ForegroundColor Green
				break
			}
		}
	}

	return $true
}

function Show-RecentDiag {
	Write-Host "`n=== Recent StartMenuDiag / AKDiag ===" -ForegroundColor Cyan
	if (Test-Path -LiteralPath $readLog) {
		& $readLog -AkDiagOnly -TailLines $LogTail
	}
	else {
		Write-Host 'Read-ClientLog.ps1 not found.'
	}
}

function Test-StartMenuPatterns {
	$logPath = Get-ClientLogPath
	if (-not $logPath) {
		Write-Host 'client.log not found.' -ForegroundColor Red
		return
	}

	Write-Host "`n=== Start-menu log check (last $LogTail lines) ===" -ForegroundColor Cyan
	$lines = Get-Content -LiteralPath $logPath -Tail $LogTail -Encoding UTF8 |
		Where-Object { $_ -match 'StartMenuDiag' }

	if (-not $lines) {
		Write-Host 'No StartMenuDiag lines — reproduce Win/Ctrl+Esc in-game first.' -ForegroundColor Yellow
		return
	}

	$last = $lines[-1]
	Write-Host "Latest: $last"

	$hasNohotkeys = $lines | Where-Object { $_ -match 'raw=.*NOHOTKEYS|rawMode=.*NOHOTKEYS' }
	$hasThread = $lines | Where-Object { $_ -match 'path=thread' }
	$hasLeakWin = $lines | Where-Object { $_ -match 'LEAK.*vk=0x5B' }
	$blockedFgCore = $lines | Where-Object { $_ -match 'blocked=True.*fgCore=True' }
	$passInactive = $lines | Where-Object { $_ -match 'blocked=False.*reason=not-foreground' }

	Write-Host ''
	Write-Host "INPUT+NOHOTKEYS seen: $(if ($hasNohotkeys) { 'yes' } else { 'NO' })"
	Write-Host "path=thread seen:     $(if ($hasThread) { 'yes' } else { 'NO' })"
	Write-Host "blocked@fgCore:       $($blockedFgCore.Count) lines"
	Write-Host "inactive pass-through: $($passInactive.Count) lines"
	Write-Host "LEAK Win (0x5B):      $(if ($hasLeakWin) { 'YES — investigate' } else { 'none' })"
}

if ($VerifyStartMenu) {
	Test-StartMenuPatterns
	Show-RecentDiag
	exit 0
}

if ($Watch) {
	Write-Host 'Watch mode: rebuild on .cs save (Ctrl+C to stop). tModLoader must be running for hot reload.' -ForegroundColor Cyan
	$lastWrite = @{}
	while ($true) {
		Get-ChildItem -Path $root -Filter '*.cs' -Recurse |
			Where-Object { $_.FullName -notmatch '\\obj\\|\\bin\\' } |
			ForEach-Object {
				$t = $_.LastWriteTimeUtc.Ticks
				$key = $_.FullName
				if ($lastWrite.ContainsKey($key) -and $lastWrite[$key] -ne $t) {
					$lastWrite[$key] = $t
					Invoke-ModBuild -Reason $_.Name
					Show-RecentDiag
				}
				elseif (-not $lastWrite.ContainsKey($key)) {
					$lastWrite[$key] = $t
				}
			}
		Start-Sleep -Seconds 1
	}
}

$ok = Invoke-ModBuild
Show-RecentDiag
if ($VerifyStartMenu -or $ok) {
	Test-StartMenuPatterns
}

if (-not $ok) { exit 1 }
