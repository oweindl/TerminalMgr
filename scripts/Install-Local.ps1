<#
.SYNOPSIS
  Baut TerminalMgr, testet es und installiert es lokal an einen festen Pfad.

.DESCRIPTION
  1. web: npm ci und npm run bundle
  2. alle Tests (Konsole, WPF, Browser); bei einem Fehler bricht das Skript ab
  3. dotnet publish (win-x64, eigenständig) in einen temporären Ordner
  4. eine laufende TerminalMgr.App.exe aus dem Installationsordner sauber beenden
     (vorher fragen, wenn SSH-Sitzungen offen sein können; -Force fragt nicht)
  5. Ergebnis nach %LOCALAPPDATA%\Programs\TerminalMgr kopieren (fester Pfad, alter Inhalt wird ersetzt)
  6. Verknüpfung "TerminalMgr.lnk" auf dem Desktop und im Startmenü anlegen oder aktualisieren
  7. Version und Installationspfad ausgeben

  Einstellungen, angeheftete Clipboard-Einträge und Whisper-Modelle liegen in %LOCALAPPDATA%\TerminalMgr
  und werden nicht angefasst. Die Verknüpfungen zeigen immer auf denselben Pfad und bleiben nach Updates gültig.

.PARAMETER Force
  Nicht nachfragen und eine nicht reagierende App nach dem Warten beenden.

.PARAMETER SkipTests
  Nur zum schnellen Neu-Installieren eines bereits getesteten Stands: Schritt 2 überspringen.

.PARAMETER Start
  Die App nach der Installation starten.
#>
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$SkipTests,
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repo = Split-Path -Parent $PSScriptRoot
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\TerminalMgr'
$exe = Join-Path $installDir 'TerminalMgr.App.exe'
$publishDir = Join-Path ([IO.Path]::GetTempPath()) ('TerminalMgr-publish-' + [guid]::NewGuid().ToString('N'))

function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

# Native Befehle melden Fehler über den Exit-Code, nicht über Ausnahmen.
function Invoke-Checked([string]$What, [scriptblock]$Command) {
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What ist fehlgeschlagen (Exit $LASTEXITCODE)." }
}

function New-AppShortcut([string]$Path) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($Path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $installDir
    $link.IconLocation = "$exe,0"
    $link.Description = 'TerminalMgr - SSH-Sitzungen mit tmux'
    $link.Save()
}

function Stop-InstalledApp {
    $running = @(Get-Process -Name 'TerminalMgr.App' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    $others = @(Get-Process -Name 'TerminalMgr.App' -ErrorAction SilentlyContinue).Count - $running.Count
    if ($others -gt 0) { Write-Host "Hinweis: $others weitere TerminalMgr-Instanz(en) aus anderen Ordnern laufen und bleiben unberührt." -ForegroundColor Yellow }
    if ($running.Count -eq 0) { return }

    $pids = $running.Id
    $ssh = @(Get-CimInstance Win32_Process -Filter "Name='ssh.exe'" | Where-Object { $pids -contains $_.ParentProcessId })
    if ($ssh.Count -gt 0 -and -not $Force) {
        $answer = Read-Host "TerminalMgr läuft mit $($ssh.Count) offenen SSH-Verbindung(en). Beenden und aktualisieren? (j/N)"
        if ($answer -notmatch '^(j|ja|y|yes)$') { throw 'Abgebrochen: TerminalMgr läuft noch.' }
    }
    Write-Host 'Beende die laufende TerminalMgr ...'
    foreach ($process in $running) { [void]$process.CloseMainWindow() }   # schließt die SSH-Sitzungen geordnet
    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $deadline -and ($running | Where-Object { -not $_.HasExited })) { Start-Sleep -Milliseconds 300 }
    $alive = @($running | Where-Object { -not $_.HasExited })
    if ($alive.Count -gt 0) {
        if (-not $Force) { throw 'TerminalMgr hat sich nicht beendet. Bitte schließen oder mit -Force erneut starten.' }
        $alive | Stop-Process -Force
    }
}

try {
    Write-Step 'Terminal-Assets bauen (npm ci, npm run bundle)'
    Push-Location (Join-Path $repo 'web')
    try {
        Invoke-Checked 'npm ci' { npm ci }
        Invoke-Checked 'npm run bundle' { npm run bundle }
    } finally { Pop-Location }

    if (-not $SkipTests) {
        Write-Step 'Bauen und alle Tests ausführen'
        Push-Location $repo
        try {
            Invoke-Checked 'dotnet build' { dotnet build TerminalMgr.sln -c Release }
            Invoke-Checked 'Konsolentests' { dotnet run --project tests/TerminalMgr.Tests -c Release --no-build }
            Invoke-Checked 'WPF-Tests' { dotnet run --project tests/TerminalMgr.UiTests -c Release --no-build }
            Push-Location (Join-Path $repo 'web')
            try { Invoke-Checked 'Browsertests' { npm test } } finally { Pop-Location }
        } finally { Pop-Location }
    } else {
        Write-Host 'Tests übersprungen (-SkipTests).' -ForegroundColor Yellow
    }

    Write-Step "Veröffentlichen nach $publishDir"
    Push-Location $repo
    try {
        Invoke-Checked 'dotnet publish' { dotnet publish src/TerminalMgr.App -c Release -r win-x64 --self-contained true -o $publishDir }
    } finally { Pop-Location }
    if (-not (Test-Path (Join-Path $publishDir 'TerminalMgr.App.exe'))) { throw 'Die Veröffentlichung enthält keine TerminalMgr.App.exe.' }

    Write-Step 'Laufende App beenden'
    Stop-InstalledApp

    Write-Step "Installieren nach $installDir"
    # Der Pfad ist fest; die Prüfung verhindert, dass ein Fehler in $env:LOCALAPPDATA etwas anderes spiegelt.
    if ($installDir -notmatch '\\Programs\\TerminalMgr$') { throw "Unerwarteter Installationspfad: $installDir" }
    New-Item -ItemType Directory -Force $installDir | Out-Null
    robocopy $publishDir $installDir /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Kopieren fehlgeschlagen (robocopy Exit $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0

    Write-Step 'Verknüpfungen anlegen'
    $desktop = [Environment]::GetFolderPath('Desktop')            # funktioniert auch mit OneDrive-Desktop
    $programs = [Environment]::GetFolderPath('Programs')
    New-AppShortcut (Join-Path $desktop 'TerminalMgr.lnk')
    New-AppShortcut (Join-Path $programs 'TerminalMgr.lnk')
    Write-Host "Desktop:   $(Join-Path $desktop 'TerminalMgr.lnk')"
    Write-Host "Startmenü: $(Join-Path $programs 'TerminalMgr.lnk')"

    $version = (Get-Item $exe).VersionInfo.ProductVersion
    Write-Host ''
    Write-Host "TerminalMgr $version installiert in $installDir" -ForegroundColor Green
    if ($Start) { Start-Process $exe -WorkingDirectory $installDir }
}
finally {
    # Nur der eigene temporäre Ordner dieses Laufs wird entfernt.
    if ((Test-Path $publishDir) -and $publishDir.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and ((Split-Path $publishDir -Leaf) -like 'TerminalMgr-publish-*')) {
        Remove-Item -LiteralPath $publishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
