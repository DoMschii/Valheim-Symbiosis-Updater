# Valheim Symbiosis Updater

Independent auto-updater for Valheim Symbiosis.

**Updater version:** 1.0.0

The updater has its own version and repository. It does **not** use its own plugin version to decide whether the gameplay mod is outdated. Instead it reads the loaded `Valheim_Workers_Mount` version and compares that suite version with the latest release tag in the configured Valheim Symbiosis repository.

## Runtime flow

1. Valheim starts and BepInEx loads the updater.
2. The updater reads `Valheim_Symbiosis_Update.json` beside its DLL.
3. It checks the latest GitHub Release in the gameplay repository.
4. If the release is newer than the installed Mount/suite version, it downloads the standalone installer ZIP plus SHA-256 file.
5. After Valheim closes, a helper verifies SHA-256 and replaces the packaged Symbiosis DLLs.

The updater itself changes rarely and can be bundled into the standalone installer without coupling its version to gameplay versions.

## Development install

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Setup.ps1
```
