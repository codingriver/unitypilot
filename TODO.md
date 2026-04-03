# UnityPilot TODO

## Completed

### Session: 2026-04-03

- [x] **P0a: GameObject Duplicate** — `unity_gameobject_duplicate(instanceId)`
- [x] **P0b: Scene Unload** — `unity_scene_unload(scenePath, removeScene)`
- [x] **P1a: Assets Find Built-In** — `unity_asset_find_built_in(query, assetType)`
- [x] **P1b: SerializedObject Get/Modify** — `unity_asset_get_data(...)`, `unity_asset_modify_data(...)`
- [x] **SceneView Navigation** — `unity_sceneview_navigate(lookAtInstanceId, pivot, rotation, size, orthographic, in2DMode)`
- [x] **Undo/Redo** — `unity_editor_undo(steps)`, `unity_editor_redo(steps)`
- [x] **ExecuteCommand** — `unity_editor_execute_command(commandName)`
- [x] **Cross-project migration assessment** — Existing 67+ tools sufficient; GUID-based dependency analysis deferred

## Pending / Future Work

### Context Menu Support
- [ ] **Context Menu integration** — Expose right-click context menus for Hierarchy, Project, and Inspector windows
  - Requires `EditorUtility.DisplayPopupMenu` or custom `GenericMenu` bridging
  - Consider: enumerate available context menu items per selection context
  - Consider: programmatic invocation of context menu actions by path (similar to `ExecuteMenuItem`)
  - Complexity: Medium-High (context menus are not easily introspectable in Unity)

### Deferred Items
- [ ] **GUID-based dependency analysis** — Trace asset dependencies across projects via `.meta` GUID references
  - Useful for cross-project migration workflows
  - High complexity; requires parsing `.meta` files and `fileIDToRecycleName` mappings
- [ ] **Batch asset import/export** — Bulk operations for cross-project asset transfer
- [ ] **Editor Window management** — Open/close/focus specific editor windows programmatically
