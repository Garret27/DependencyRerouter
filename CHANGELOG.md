# Changelog

## 1.0.1
- Renamed to **G2727 Dependency Rerouter** to match the other G2727 tools.
- Menu moved to **Tools → Garret2727 → G2727 Dependency Rerouter**.

## 1.0.0
- First release.
- Scan a prefab, scene avatar or any asset for its dependencies (optionally nested).
- Folder tree view with folder checkboxes (select/deselect everything inside, mixed state), plus a flat list view.
- Filter by type, search by path, sort by type/path/size/shared usage.
- Optional project-wide "shared usage" scan to spot files other assets also use.
- Move mode (keeps GUIDs, nothing breaks) and Copy mode (duplicates and rewires references between the copies).
- Mirrors the original folder structure under the target folder, with a configurable prefix to strip.
- Preview of every old → new path, conflict handling (skip or rename).
