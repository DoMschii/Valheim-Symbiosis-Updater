# Valheim Symbiosis Preloader Updater

Version **2.0.2**

This replaces the old normal BepInEx updater plugin with a **BepInEx 5 preloader patcher**.

## Startup flow

1. Thunderstore / r2modman launches Valheim.
2. BepInEx Preloader discovers `BepInEx/patchers/Valheim_Symbiosis_Updater.dll`.
3. `Initialize()` checks the configured GitHub manifest synchronously.
4. If a newer suite is available, the ZIP is downloaded and SHA-256 verified.
5. `Payload/plugins/**.dll` is copied into the current profile **before the normal Chainloader loads plugins**.
6. Valheim then continues with the freshly updated DLLs in the same startup.

BepInEx 5.4.23.5 discovers preloader patchers through the legacy `TargetDLLs` / `Patch(AssemblyDefinition)` contract and calls `Initialize()` before patch execution. This updater deliberately returns an empty `TargetDLLs` list because it does not rewrite a game assembly; it only needs the early initializer.

## Fail-open behavior

Network errors, GitHub downtime, a bad manifest, timeout, bad ZIP or SHA mismatch **never intentionally block Valheim**. The updater logs the issue and the installed gameplay DLLs remain in use.

If applying an update fails after one or more gameplay DLLs were replaced, the updater attempts to roll those files back from `BepInEx/cache/Valheim_Symbiosis_Updater/backup_*`.

## Config

Installed beside the patcher:

`BepInEx/patchers/Valheim_Symbiosis_Updater.json`

Default:

```json
{
  "enabled": true,
  "channel": "stable",
  "manifest_url": "https://raw.githubusercontent.com/DoMschii/Valheim-Symbiosis/main/update/stable.json",
  "timeout_seconds": 4
}
```

## GitHub manifest

The gameplay repository will publish `update/stable.json`.

See `update-manifest.example.json`.

The update ZIP must contain:

```text
Payload/
└─ plugins/
   ├─ Valheim_Workers/
   │  └─ Valheim_Workers.dll
   └─ ... other Symbiosis gameplay DLLs ...
```

The updater itself is intentionally excluded from gameplay update packages and keeps its own version/repository.

## Install

Run `Setup.ps1` or `Setup.bat`.

The setup script installs the patcher into all detected Thunderstore/r2modman Valheim profiles and disables the old v1 normal-plugin updater DLL.


## 2.0.1 runtime hotfix

- Workers Core (`Valheim_Workers.dll`) is now the authoritative installed release version.
  Mount's independent 4.x version is no longer compared to the Workers 0.x release manifest.
- If the Mono/BepInEx preloader reports `TLS Support not available`, downloads fall back
  to Windows `curl.exe` while keeping the same timeout and fail-open behavior.


## 2.0.2 suite revision hotfix

The installed suite version is now `max(Workers Core version, Valheim_Symbiosis.version)`.
That lets an RC package use a four-part build revision such as `0.6.0.1` for
Mount/DrakeForm/Ego fixes without causing an update loop on the next launch.
