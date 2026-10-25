<#
.SYNOPSIS
  Prüft den Update-Ablauf der echten App: ältere Version installiert, neues Release, Aktualisierung über den Dialog.

.DESCRIPTION
  Alles läuft in einem eigenen Ordner unter %TEMP%; die installierte TerminalMgr und ihre Einstellungen bleiben unberührt
  (TERMINALMGR_INSTALL_DIR, TERMINALMGR_DATA_DIR und TERMINALMGR_UPDATE_URL leiten Ordner und Update-Adresse um).
  Gebaut wird zweimal aus dem aktuellen Quelltext: "alt" als 0.2.0 und "neu" als 0.2.50. Ein lokaler Webserver spielt GitHub.

  Szenarien:
    Success         Dialog -> "Jetzt aktualisieren" -> neue Version läuft, Sicherung weg, Einstellungen und angeheftete Einträge erhalten.
    BrokenPackage   Das Paket hat eine gültige Prüfsumme, aber eine EXE, die nicht startet -> alte Version wird wiederhergestellt und gestartet.
    BadChecksum     Falsche Prüfsumme -> Fehlermeldung im Dialog, Installation unverändert.
#>
[CmdletBinding()]
param([ValidateSet('All', 'Success', 'BrokenPackage', 'BadChecksum')] [string[]]$Scenario = @('All'))

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path ([IO.Path]::GetTempPath()) ('TerminalMgr-updatetest-' + [guid]::NewGuid().ToString('N'))
$port = Get-Random -Minimum 20000 -Maximum 40000
$failures = [System.Collections.Generic.List[string]]::new()
New-Item -ItemType Directory -Force $work | Out-Null
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

function Wants([string]$Name) { $Scenario -contains 'All' -or $Scenario -contains $Name }
function Say([string]$Text) { Write-Host $Text -ForegroundColor Cyan }
function Check([bool]$Condition, [string]$Message) {
    if ($Condition) { Write-Host "  PASS $Message" -ForegroundColor Green } else { Write-Host "  FAIL $Message" -ForegroundColor Red; $failures.Add($Message) }
}
function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$What) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) { $value = & $Condition; if ($value) { return $value }; Start-Sleep -Milliseconds 400 }
    Write-Host "  Zeitüberschreitung: $What" -ForegroundColor Yellow
    return $null
}
function Find-Window([string]$Name) {
    # A dialog owned by the main window is listed below that window, not at the top level; a message box of the updater is top level.
    $isWindow = New-Object System.Windows.Automation.AndCondition(
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)),
        (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Window)))
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $top = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $isWindow)
    if ($top) { return $top }
    $pids = @((Test-AppProcesses).Id)
    foreach ($window in $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($pids -contains $window.Current.ProcessId) {
            $owned = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $isWindow)
            if ($owned) { return $owned }
        }
    }
}
function Find-Child($Window, [string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $Window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}
function Click($Element) { $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Test-AppProcesses { Get-Process -Name 'TerminalMgr.App' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path.StartsWith($work, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } }
function Stop-TestProcesses {
    Test-AppProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
    Get-Process -Name 'TerminalMgr.Updater' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path.StartsWith($env:TEMP, [StringComparison]::OrdinalIgnoreCase) } catch { $false } } | Stop-Process -Force -ErrorAction SilentlyContinue
}

function New-Release([string]$Name, [string]$Version, [scriptblock]$Tamper) {
    $folder = Join-Path $work "release-$Name"
    $app = Join-Path $folder 'TerminalMgr-win-x64'
    robocopy (Join-Path $work 'new') $app /E /NFL /NDL /NJH /NJS /NP | Out-Null
    $global:LASTEXITCODE = 0
    if ($Tamper) { & $Tamper $app }
    $zip = Join-Path $folder "TerminalMgr-win-x64-$Version.zip"
    Compress-Archive -Path $app -DestinationPath $zip -CompressionLevel Fastest
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    return [pscustomobject]@{ Folder = $folder; Zip = $zip; Hash = $hash; Name = Split-Path $zip -Leaf }
}

function Start-Scenario([string]$Name, $Release, [string]$Hash) {
    $serve = Join-Path $work "serve-$Name"
    New-Item -ItemType Directory -Force $serve | Out-Null
    Copy-Item $Release.Zip $serve
    "$Hash  $($Release.Name)" | Set-Content (Join-Path $serve "$($Release.Name).sha256") -Encoding ascii
    $base = "http://127.0.0.1:$port"
    $json = @{ tag_name = 'v0.2.50'; draft = $false; prerelease = $false; body = "- fix: Testrelease`n- feat: Update-Ablauf"
        assets = @(@{ name = $Release.Name; size = (Get-Item $Release.Zip).Length; browser_download_url = "$base/$($Release.Name)" },
                   @{ name = "$($Release.Name).sha256"; size = 97; browser_download_url = "$base/$($Release.Name).sha256" }) } | ConvertTo-Json -Depth 5
    Set-Content (Join-Path $serve 'latest.json') $json -Encoding utf8
    $script:server = Start-Process python -ArgumentList '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $serve -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 1

    $install = Join-Path $work "install-$Name"; $data = Join-Path $work "data-$Name"
    robocopy (Join-Path $work 'old') $install /E /NFL /NDL /NJH /NJS /NP | Out-Null
    $global:LASTEXITCODE = 0
    New-Item -ItemType Directory -Force $data | Out-Null
    # Existing settings with a profile (no auto-connect) and a pinned clipboard entry must survive an update.
    @{ Profiles = @(@{ Id = 'p1'; Name = 'beispiel'; Destination = 'devbox'; AutoConnect = $false; Mode = 0; TmuxSession = 'main'; Command = '' })
       PinnedClips = @(@{ Text = 'angeheftet-vor-dem-update'; Source = 'test'; Created = (Get-Date).ToString('o'); Pinned = $true }); FontSize = 15 } |
        ConvertTo-Json -Depth 6 | Set-Content (Join-Path $data 'settings.json') -Encoding utf8
    $env:TERMINALMGR_INSTALL_DIR = $install; $env:TERMINALMGR_DATA_DIR = $data; $env:TERMINALMGR_UPDATE_URL = "$base/latest.json"
    $script:installDir = $install; $script:dataDir = $data
    $script:oldExeHash = (Get-FileHash (Join-Path $install 'TerminalMgr.App.exe')).Hash
    $script:app = Start-Process (Join-Path $install 'TerminalMgr.App.exe') -WorkingDirectory $install -PassThru
}
function Stop-Scenario { Stop-TestProcesses; if ($script:server) { Stop-Process -Id $script:server.Id -Force -ErrorAction SilentlyContinue; $script:server = $null } }

function Title-Of([string]$ExpectedStart) {
    Test-AppProcesses | Where-Object { $_.MainWindowTitle -like "$ExpectedStart*" } | Select-Object -First 1
}

try {
    Say "Arbeitsordner: $work"
    Say 'Baue die alte Version 0.2.0 ...'
    dotnet publish (Join-Path $repo 'src/TerminalMgr.App') -c Release -r win-x64 --self-contained true "-p:Version=0.2.0" -o (Join-Path $work 'old') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Veröffentlichung der alten Version fehlgeschlagen.' }
    Say 'Baue die neue Version 0.2.50 ...'
    dotnet publish (Join-Path $repo 'src/TerminalMgr.App') -c Release -r win-x64 --self-contained true "-p:Version=0.2.50" -o (Join-Path $work 'new') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Veröffentlichung der neuen Version fehlgeschlagen.' }
    Check (Test-Path (Join-Path $work 'old/TerminalMgr.Updater.exe')) 'Der Updater liegt im Programmordner'

    if (Wants 'Success') {
        Say "`n== Szenario Success"
        $release = New-Release 'ok' '0.2.50' $null
        Start-Scenario 'ok' $release $release.Hash
        $dialog = Wait-Until { Find-Window 'Update verfügbar' } 60 'Update-Dialog'
        Check ($null -ne $dialog) 'Beim Start erscheint der Update-Dialog'
        if ($dialog) {
            Check ($null -ne (Find-Child $dialog 'TerminalMgr 0.2.50 ist verfügbar')) 'Der Dialog nennt Version 0.2.50'
            Check ($null -ne (Find-Child $dialog 'Diese Version überspringen')) 'Die drei Auswahlmöglichkeiten sind da (überspringen)'
            Click (Find-Child $dialog 'Jetzt aktualisieren')
            $new = Wait-Until { Title-Of 'TerminalMgr 0.2.50' } 150 'neue Version startet'
            Check ($null -ne $new) 'Nach der Aktualisierung läuft TerminalMgr 0.2.50'
            Start-Sleep -Seconds 6
            Check (-not (Test-Path "$($script:installDir).old")) 'Die Sicherung wurde nach dem erfolgreichen Start gelöscht'
            Check ((Get-Item (Join-Path $script:installDir 'TerminalMgr.App.exe')).VersionInfo.FileVersion -eq '0.2.50.0') 'Im Installationsordner liegt die neue EXE'
            Check ((Get-Content (Join-Path $script:dataDir 'settings.json') -Raw) -match 'angeheftet-vor-dem-update') 'Der angeheftete Clipboard-Eintrag ist erhalten'
            Check ((Get-Content (Join-Path $script:dataDir 'settings.json') -Raw) -match 'beispiel') 'Das Profil ist erhalten'
            Check (-not (Test-Path (Join-Path $script:dataDir 'update/0.2.50'))) 'Der vorbereitete Ordner wurde aufgeräumt'
            $log = Join-Path $script:dataDir 'update/updater.log'
            Check ((Test-Path $log) -and ((Get-Content $log -Raw) -match 'Die neue Version läuft')) 'Das Updater-Protokoll meldet den erfolgreichen Start'
            Check ($null -eq (Find-Window 'Update verfügbar')) 'Die neue Version bietet kein weiteres Update an'
        }
        Stop-Scenario
    }

    if (Wants 'BrokenPackage') {
        Say "`n== Szenario BrokenPackage (gültige Prüfsumme, EXE startet nicht)"
        $release = New-Release 'broken' '0.2.50' { param($app) 'das ist keine ausführbare Datei' | Set-Content (Join-Path $app 'TerminalMgr.App.exe') }
        Start-Scenario 'broken' $release $release.Hash
        $dialog = Wait-Until { Find-Window 'Update verfügbar' } 60 'Update-Dialog'
        Check ($null -ne $dialog) 'Der Update-Dialog erscheint'
        if ($dialog) {
            Click (Find-Child $dialog 'Jetzt aktualisieren')
            $message = Wait-Until { Find-Window 'TerminalMgr' } 120 'Fehlermeldung des Updaters'
            Check ($null -ne $message) 'Der Updater meldet den Fehlschlag'
            $restored = Wait-Until { Title-Of 'TerminalMgr 0.2.0' } 60 'alte Version wieder gestartet'
            Check ($null -ne $restored) 'Die alte Version 0.2.0 läuft wieder'
            if ($message) { $ok = Find-Child $message 'OK'; if ($ok) { Click $ok } }
            Check ((Get-FileHash (Join-Path $script:installDir 'TerminalMgr.App.exe')).Hash -eq $script:oldExeHash) 'Im Installationsordner liegt wieder die alte EXE'
            Check (-not (Test-Path "$($script:installDir).old")) 'Keine Sicherung bleibt zurück'
            $log = Join-Path $script:dataDir 'update/updater.log'
            Check ((Test-Path $log) -and ((Get-Content $log -Raw) -match 'wiederhergestellt')) 'Das Protokoll hält die Wiederherstellung fest'
            Check ((Get-Content (Join-Path $script:dataDir 'settings.json') -Raw) -match 'angeheftet-vor-dem-update') 'Die Einstellungen sind unverändert'
        }
        Stop-Scenario
    }

    if (Wants 'BadChecksum') {
        Say "`n== Szenario BadChecksum"
        $release = New-Release 'badsum' '0.2.50' $null
        Start-Scenario 'badsum' $release ('0' * 64)
        $dialog = Wait-Until { Find-Window 'Update verfügbar' } 60 'Update-Dialog'
        Check ($null -ne $dialog) 'Der Update-Dialog erscheint'
        if ($dialog) {
            Click (Find-Child $dialog 'Jetzt aktualisieren')
            $text = Wait-Until { $w = Find-Window 'Update verfügbar'; if ($w) { $e = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like '*Prüfsumme*' } | Select-Object -First 1; if ($e) { $e.Current.Name } } } 90 'Fehlermeldung im Dialog'
            Check ($null -ne $text -and $text -match 'unverändert') 'Der Dialog meldet die falsche Prüfsumme und dass nichts verändert wurde'
            $w = Find-Window 'Update verfügbar'
            if ($w) { Click (Find-Child $w 'Später') }
            Start-Sleep -Seconds 2
            Check ($null -ne (Title-Of 'TerminalMgr 0.2.0')) 'Die alte Version läuft weiter'
            Check ((Get-FileHash (Join-Path $script:installDir 'TerminalMgr.App.exe')).Hash -eq $script:oldExeHash) 'Die Installation ist unverändert'
            Check (-not (Test-Path (Join-Path $script:dataDir 'update/0.2.50'))) 'Es bleibt kein halber Download zurück'
        }
        Stop-Scenario
    }
}
finally {
    Stop-Scenario
    Remove-Item Env:TERMINALMGR_INSTALL_DIR, Env:TERMINALMGR_DATA_DIR, Env:TERMINALMGR_UPDATE_URL -ErrorAction SilentlyContinue
    # Only the own working folder of this run is removed.
    if ((Test-Path $work) -and $work.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase) -and (Split-Path $work -Leaf) -like 'TerminalMgr-updatetest-*') {
        Start-Sleep -Seconds 1
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($failures.Count -gt 0) { Write-Host "`n$($failures.Count) Prüfung(en) fehlgeschlagen." -ForegroundColor Red; exit 1 }
Write-Host "`nAlle Prüfungen des Update-Ablaufs bestanden." -ForegroundColor Green
