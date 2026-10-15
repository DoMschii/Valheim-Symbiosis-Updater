# Changelog

## 2.0.2
- Added suite build-revision support through the updater marker.
- Installed release version is now max(Workers Core, suite marker).
- Prevents repeated downloads after component-only RC refreshes.
- Retains the Windows curl.exe TLS fallback introduced in 2.0.1.


## 2.0.1
- Fixed release-version detection: Workers Core is authoritative, not Mount.
- Added Windows curl.exe TLS fallback for Mono/BepInEx preloader environments.
- Preserved SHA256, rollback, timeout and fail-open behavior.


## 2.0.0
- Rebuilt updater as a BepInEx 5 preloader patcher.
- Update check now happens before normal plugins are loaded.
- New gameplay DLLs can be applied in the same startup; no second Valheim launch is required.
- Added SHA-256 validation.
- Added transactional per-DLL backup/rollback.
- Added ZIP path-traversal protection.
- Added configurable startup timeout.
- Added stable-channel manifest configuration.
- Setup migrates away from the old post-start updater plugin.
