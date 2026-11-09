# Dependency Rerouter

A Unity editor tool for VRChat creators and anyone else. Pick a prefab, an avatar in the scene, or any asset. It lists every file that asset depends on as a folder tree, lets you choose what you want, and moves or copies those files into a folder of your choice.

## Install

### VRChat Creator Companion (VCC / ALCOM)
1. Open **https://garret27.github.io/g2727-vpm/** and click **Add to VCC**, or in VCC go to **Settings → Packages → Add Repository** and paste:
   ```
   https://garret27.github.io/g2727-vpm/index.json
   ```
2. In your project, open **Manage Project** and add **Dependency Rerouter**.

### Manual
Download the `.zip` from [Releases](https://github.com/Garret27/DependencyRerouter/releases) and extract it into your project's `Packages/` folder.

## Usage
Open the tool from **Tools → Dependency Rerouter**. You can also right-click an asset or a Hierarchy object and choose **Reroute Dependencies…**.

1. **Source.** Drop in a prefab, scene object or asset and press **Scan**.
2. **Pick files.**
   - The folder tree shows everything the source uses. Folder checkboxes select or deselect everything inside them.
   - The type buttons filter the list, and the search box filters by path.
   - **Scan shared usage** highlights files that other assets also use.
   - Anything in `Packages/` is locked.
3. **Destination.** Choose a target folder. The original folder structure is recreated under it. **Strip prefix** controls how much of the original path is kept.
4. **Mode.**
   - **Move** relocates the originals. GUIDs are kept, so every reference keeps working.
   - **Copy** duplicates the selected files and rewires the copies to point at each other, leaving the originals untouched. Unselected files stay referenced from their original location. Copy needs Asset Serialization set to Force Text, which is the default.
5. Check the **Preview**, then press the button to run. A log is written to the Console.

> Moving can't be undone with Ctrl+Z, so back up your project before big moves.

## License
MIT
