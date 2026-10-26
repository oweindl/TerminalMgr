<#
.SYNOPSIS
  Installiert TerminalMgr aus dem neuesten GitHub-Release: das passende Paket für den Prozessor, geprüft, mit Verknüpfungen.

.DESCRIPTION
  Für Einsteiger gedacht. Das Skript
    1. erkennt den Prozessor (x64, arm64 oder x86),
    2. lädt das passende Paket und seine Prüfsumme vom GitHub-Release,
    3. prüft die SHA-256-Prüfsumme (bei Abweichung wird nichts installiert),
    4. entpackt nach %LOCALAPPDATA%\Programs\TerminalMgr (dort kann sich die App später selbst aktualisieren),
    5. legt Verknüpfungen auf dem Desktop und im Startmenü an.
  Einstellungen, angeheftete Clipboard-Einträge und Sprachmodelle liegen woanders (%LOCALAPPDATA%\TerminalMgr) und bleiben erhalten.

.PARAMETER InstallDirectory
  Zielordner. Vorgabe: %LOCALAPPDATA%\Programs\TerminalMgr. Nur dort aktualisiert sich die App selbst.

.PARAMETER Version
  Eine bestimmte Version statt der neuesten, zum Beispiel 0.2.34.

.PARAMETER NoShortcuts
  Keine Verknüpfungen anlegen.

.PARAMETER Start
  TerminalMgr nach der Installation starten.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\Install-TerminalMgr.ps1 -Start
#>
[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:LOCALAPPDATA 'Programs\TerminalMgr'),
    [string]$Version,
    [switch]$NoShortcuts,
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest ist mit Fortschrittsanzeige sehr langsam
$repository = 'oweindl/TerminalMgr'

function Write-Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# 32-Bit-PowerShell auf 64-Bit-Windows meldet x86; PROCESSOR_ARCHITEW6432 verrät den echten Prozessor.
$cpu = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$arch = switch ($cpu) { 'AMD64' { 'x64' } 'ARM64' { 'arm64' } 'x86' { 'x86' } default { throw "Dieser Prozessor wird nicht unterstützt: $cpu" } }
Write-Step "Prozessor: $arch"

Write-Step 'Neuestes Release suchen'
$api = if ($Version) { "https://api.github.com/repos/$repository/releases/tags/v$Version" } else { "https://api.github.com/repos/$repository/releases/latest" }
try { $release = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'TerminalMgr-Installer' } }
catch { throw "Das Release konnte nicht abgefragt werden (Internetverbindung?): $($_.Exception.Message)" }
$tag = $release.tag_name
$number = $tag.TrimStart('v')
$zipName = "TerminalMgr-win-$arch-$number.zip"
$zipAsset = $release.assets | Where-Object { $_.name -eq $zipName }
$shaAsset = $release.assets | Where-Object { $_.name -eq "$zipName.sha256" }
if (-not $zipAsset -or -not $shaAsset) {
    $have = ($release.assets | Where-Object { $_.name -like '*.zip' } | ForEach-Object { $_.name }) -join ', '
    throw "Das Release $tag hat kein Paket für Ihren Prozessor ($arch). Vorhanden: $have"
}
# Nur GitHub selbst als Quelle.
foreach ($asset in @($zipAsset, $shaAsset)) {
    $uri = [Uri]$asset.browser_download_url
    if ($uri.Scheme -ne 'https' -or -not ($uri.Host -eq 'github.com' -or $uri.Host.EndsWith('.githubusercontent.com'))) { throw "Unerwartete Download-Adresse: $uri" }
}
Write-Host "Version $number, Paket $zipName ($([math]::Round($zipAsset.size / 1MB)) MB)"

# Eine laufende Installation darf nicht überschrieben werden.
$exe = Join-Path $InstallDirectory 'TerminalMgr.App.exe'
$running = Get-Process -Name 'TerminalMgr.App' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -like "$InstallDirectory*" }
if ($running) { throw 'TerminalMgr läuft noch. Bitte das Programm schließen und das Skript noch einmal starten.' }

$work = Join-Path ([IO.Path]::GetTempPath()) ('TerminalMgr-install-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    Write-Step 'Herunterladen'
    $zip = Join-Path $work $zipName
    Invoke-WebRequest -Uri $zipAsset.browser_download_url -OutFile $zip -Headers @{ 'User-Agent' = 'TerminalMgr-Installer' }
    $shaText = (Invoke-WebRequest -Uri $shaAsset.browser_download_url -Headers @{ 'User-Agent' = 'TerminalMgr-Installer' }).Content
    if ($shaText -is [byte[]]) { $shaText = [Text.Encoding]::ASCII.GetString($shaText) }

    Write-Step 'Prüfsumme prüfen'
    $expected = $null
    foreach ($line in ($shaText -split "`n")) {
        $t = $line.Trim()
        if ($t.Length -ge 64 -and $t.Substring(0, 64) -match '^[0-9a-fA-F]{64}$') { $expected = $t.Substring(0, 64).ToLowerInvariant(); break }
    }
    if (-not $expected) { throw 'Die Prüfsummen-Datei ist unlesbar. Es wurde nichts installiert.' }
    $actual = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'Die Prüfsumme stimmt nicht. Die Datei ist beschädigt oder verändert; es wurde nichts installiert.' }
    Write-Host 'Prüfsumme stimmt.'

    Write-Step 'Entpacken'
    $extract = Join-Path $work 'extract'
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    $source = if (Test-Path (Join-Path $extract 'TerminalMgr.App.exe')) { $extract } else { (Get-ChildItem $extract -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'TerminalMgr.App.exe') } | Select-Object -First 1).FullName }
    if (-not $source) { throw 'Das Paket enthält keine TerminalMgr.App.exe.' }

    Write-Step "Installieren nach $InstallDirectory"
    New-Item -ItemType Directory -Force $InstallDirectory | Out-Null
    robocopy $source $InstallDirectory /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Kopieren fehlgeschlagen (robocopy Exit $LASTEXITCODE)." }
    $global:LASTEXITCODE = 0

    if (-not $NoShortcuts) {
        Write-Step 'Verknüpfungen anlegen'
        $shell = New-Object -ComObject WScript.Shell
        foreach ($folder in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
            $link = $shell.CreateShortcut((Join-Path $folder 'TerminalMgr.lnk'))
            $link.TargetPath = $exe; $link.WorkingDirectory = $InstallDirectory; $link.Description = 'TerminalMgr'; $link.IconLocation = "$exe,0"
            $link.Save()
            Write-Host (Join-Path $folder 'TerminalMgr.lnk')
        }
    }
}
finally {
    if ((Test-Path $work) -and (Split-Path $work -Leaf) -like 'TerminalMgr-install-*') { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}

# Voraussetzungen, die das Skript nicht selbst einrichtet: nur ein Hinweis, falls sie fehlen.
$ssh = Join-Path $env:WINDIR 'System32\OpenSSH\ssh.exe'
if (-not (Test-Path $ssh)) {
    Write-Host ''
    Write-Host 'Hinweis: Der Windows-OpenSSH-Client fehlt. Einstellungen > System > Optionale Features > "Funktion hinzufügen" > "OpenSSH-Client".' -ForegroundColor Yellow
}
$webView = @('HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}') | Where-Object { Test-Path $_ }
if (-not $webView) {
    Write-Host ''
    Write-Host 'Hinweis: Die Microsoft Edge WebView2 Runtime scheint zu fehlen (Windows 11 hat sie). Download: https://go.microsoft.com/fwlink/p/?LinkId=2124703' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "TerminalMgr $number ($arch) ist installiert in $InstallDirectory" -ForegroundColor Green
if ($Start) { Start-Process $exe -WorkingDirectory $InstallDirectory }
