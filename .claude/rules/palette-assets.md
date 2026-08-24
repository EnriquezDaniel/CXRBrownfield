# Guarded palette / registry assets

The seven guarded assets, all in `unity/CXRBrownfield/Assets/Resources/`:
`PrefabRegistry` · `TerrainRegistry` · `TileShapePalette` · `MaterialPalette` ·
`PathMaterialPalette` · `FencePalette` · `DecorPalette`.

Entries have been silently lost from these before (confirmed: the `DecorPalette` `AC` entry was
overwritten **in place at slot [2]** instead of appended). Rules:

1. **Append-only — never rewrite the whole `entries` array.** With MCP `assets-modify` /
   `object-modify`, target a specific index (`entries[7]`) via `pathPatches` / `jsonPatch`. A
   whole-array value in `content` silently drops every entry not in the payload.
2. **Never `Write`/`Edit` these `.asset` files directly while Unity is open.** Unity holds them in
   memory; the next `AssetDatabase.Refresh` discards your write or Unity's save overwrites it. Go
   through MCP `assets-modify` (live AssetDatabase) or ask the user to edit in the Inspector.
3. Run **`Tools → CXR → Palettes → Snapshot Now`** before a palette change and
   **`Tools → CXR → Palettes → Validate`** after; paste the console output into your reply.
4. **Never remove or rename a field** on one of the seven ScriptableObject classes without an
   explicit request plus a snapshot — the next save drops that column for every entry. *Adding* a
   field is safe (serializes as its default).
5. **Never `AssetDatabase.FindAssets("t:MaterialPalette")`** — it also matches
   `Assets/Resources/Material Palette.asset` (note the space), ProBuilder's
   `UnityEditor.ProBuilder.MaterialPalette`. Resolve by path via `PaletteGuard.Paths`.
6. **Recovery**, in order: `Tools → CXR → Palettes → Restore from Snapshot…` (gitignored
   `unity/CXRBrownfield/PaletteSnapshots/`) → `git show <rev>:<path>` → for commits made while
   these were still in Git LFS, `git show <rev>:<path> | git lfs smudge`.

`Assets/Editor/PaletteGuard.cs` is the safety net — don't disable it: it flushes dirty palettes to
disk within ~1 s, snapshots the raw YAML on every write, and logs a loud error plus pins an
unprunable `RECOVER_*.yaml` whenever an entry count drops. `Assets/Editor/PaletteValidator.cs` holds
the menu commands and is read-only apart from the dialog-confirmed restore.

Only `Assets/Resources/*.asset` is exempt from Git LFS (repo-root `.gitattributes`, rationale in its
comment) so these stay diffable/mergeable. The large `.asset` files — terrains, lightmaps, font
atlases — stay in LFS and must not be moved out.
