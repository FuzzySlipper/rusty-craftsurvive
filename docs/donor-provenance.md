# Donor provenance

The bootstrap intentionally adapted bounded patterns rather than copying a donor repository or its
architecture wholesale.

| Donor | Adapted | Deliberately excluded |
|---|---|---|
| `rusty-engine-demo` | Native `winit` + Engine `RendererWebviewAdapter` lifecycle; polling raw renderer input into Rust semantic input; camera-relative FPS movement vocabulary; retained frame replacement after authoritative changes | Loading Bay schemas, scheduler, combat, browser/Tauri shell, Studio adapter, content pipeline, proof harness, updater/pinning history |
| `rusty-engine-ui` | The idea of a compact always-visible HUD/hotbar-style control summary, implemented initially with Engine telemetry and the native title | Angular/Nx workspace, fake transport, placeholder stores/actions, renderer facade, inventory/minimap/product shell |
| `rusty-procgen` | Deterministic seeded generation, bounded output, explicit material assignment, and compilation into ordinary Engine voxel facts | Piece graph/catalog architecture, cellular automata, trace viewers, publication integrations, broad generator framework |
| `rusty-engine-voxels` | Startup comparison posture for greedy cubes, marching cubes, and dual contouring over equivalent voxel cells | Voxelization pipelines, Studio playback, sprite/video inputs, kit baking, density calibration, asset-specific experiments |

No asset files or source files were copied verbatim. The implementation uses current public Engine
facade APIs and owns only CraftSurvive-specific composition.

