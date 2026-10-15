# Changelog

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
