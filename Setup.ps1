$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Version = '2.0.0'
$Root = $PSScriptRoot

$ProfileRoots = @(
    (Join-Path $env:APPDATA 'Thunderstore Mod Manager\DataFolder\Valheim\profiles'),
    (Join-Path $env:APPDATA 'r2modmanPlus-local\Valheim\profiles')
) | Select-Object -Unique

function Write-Step([string]$Text) {
    Write-Host "`n==> $Text" -ForegroundColor Cyan
}

function Get-BepTargets {
    $list = @()
    foreach ($profileRoot in $ProfileRoots) {
        if (-not (Test-Path $profileRoot)) { continue }

        $list += @(
            Get-ChildItem $profileRoot -Directory -ErrorAction SilentlyContinue |
                ForEach-Object { Join-Path $_.FullName 'BepInEx' } |
                Where-Object {
                    (Test-Path (Join-Path $_ 'core\BepInEx.dll')) -and
                    (Test-Path (Join-Path $_ 'core\Mono.Cecil.dll'))
                }
        )
    }

    return @($list | Select-Object -Unique)
}

while (Get-Process -Name 'valheim' -ErrorAction SilentlyContinue) {
    Write-Host 'Valheim laeuft noch. Bitte schliessen; Updater-Installation wartet...' -ForegroundColor Yellow
    Start-Sleep -Seconds 2
}

$targets = @(Get-BepTargets)
if ($targets.Count -eq 0) {
    throw 'Kein Thunderstore/r2modman Valheim-Profil mit BepInEx 5 gefunden.'
}

$buildTarget = $targets[0]
$stage = Join-Path $env:TEMP 'Valheim_Symbiosis_PreloaderUpdater_Build'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Copy-Item (Join-Path $Root 'Patcher.cs') $stage -Force
Copy-Item (Join-Path $Root 'Valheim_Symbiosis_Updater.csproj') $stage -Force

Write-Step "Baue Valheim Symbiosis Preloader Updater v$Version"
& dotnet build (Join-Path $stage 'Valheim_Symbiosis_Updater.csproj') -c Release --nologo /p:BepInExPath="$buildTarget"
if ($LASTEXITCODE -ne 0) {
    throw "Updater-Build fehlgeschlagen (ExitCode $LASTEXITCODE). Bestehende Installation bleibt unveraendert."
}

$dll = Join-Path $stage 'bin\Release\netstandard2.1\Valheim_Symbiosis_Updater.dll'
if (-not (Test-Path $dll)) {
    throw "Build meldete Erfolg, DLL fehlt aber: $dll"
}

$configSource = Join-Path $Root 'Valheim_Symbiosis_Updater.json'
if (-not (Test-Path $configSource)) {
    throw "Updater-Konfiguration fehlt: $configSource"
}

Write-Step "Installiere Patcher in $($targets.Count) Valheim-Profil(e)"

foreach ($bep in $targets) {
    $patcherDir = Join-Path $bep 'patchers'
    New-Item -ItemType Directory -Path $patcherDir -Force | Out-Null

    $destDll = Join-Path $patcherDir 'Valheim_Symbiosis_Updater.dll'
    $destCfg = Join-Path $patcherDir 'Valheim_Symbiosis_Updater.json'

    if (Test-Path $destDll) {
        Copy-Item $destDll ($destDll + '.pre-v2.0.0.bak') -Force
    }

    Copy-Item $dll $destDll -Force

    # Existing user config wins. Only seed the default config on first install.
    if (-not (Test-Path $destCfg)) {
        Copy-Item $configSource $destCfg -Force
    }

    # Remove/disable the old post-start updater plugin. The v2 preloader replaces it.
    $legacyCandidates = @(
        (Join-Path $bep 'plugins\Valheim_Symbiosis\Valheim_Symbiosis_Updater.dll'),
        (Join-Path $bep 'plugins\Valheim_Symbiosis_Updater.dll')
    )

    foreach ($legacy in $legacyCandidates) {
        if (Test-Path $legacy) {
            $backup = $legacy + '.legacy-plugin-v1.bak'
            Copy-Item $legacy $backup -Force
            Remove-Item $legacy -Force
            Write-Host "Legacy-Updater deaktiviert: $legacy" -ForegroundColor DarkGray
        }
    }

    Write-Host "Patcher: $destDll" -ForegroundColor Green
    Write-Host "Config:  $destCfg" -ForegroundColor DarkGray
}

Write-Host "`nFERTIG: Preloader Updater v$Version installiert." -ForegroundColor Green
Write-Host 'Ablauf ab jetzt: Thunderstore Play -> BepInEx Preloader -> Updatecheck -> ggf. Download/Install -> Chainloader -> Valheim.' -ForegroundColor Cyan
Write-Host 'Bis update/stable.json im Gameplay-GitHub liegt, meldet der Patcher nur einen fehlgeschlagenen Check und startet Valheim normal weiter.' -ForegroundColor Yellow
