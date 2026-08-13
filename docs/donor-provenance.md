# Donor provenance

The bootstrap intentionally adapted bounded patterns rather than copying a donor repository or its
architecture wholesale.

| Donor | Adapted | Deliberately excluded |
|---|---|---|
| `rusty-engine-demo` | Native `winit` + Engine `RendererWebviewAdapter` lifecycle; polling raw renderer input into Rust semantic input; camera-relative FPS movement vocabulary; retained frame replacement after authoritative changes | Loading Bay schemas, scheduler, combat, browser/Tauri shell, Studio adapter, content pipeline, proof harness, updater/pinning history |
| `rusty-engine-ui` | The idea of a compact always-visible HUD/hotbar-style control summary, implemented initially with Engine telemetry and the native title | Angular/Nx workspace, fake transport, placeholder stores/actions, renderer facade, inventory/minimap/product shell |
| `rusty-procgen` | Deterministic seeded generation, bounded output, explicit material assignment, and compilation into ordinary Engine voxel facts | Piece graph/catalog architecture, cellular automata, trace viewers, publication integrations, broad generator framework |
| `rusty-engine-voxels` | Startup comparison posture for greedy cubes, marching cubes, and dual contouring over equivalent voxel cells | Voxelization pipelines, Studio playback, sprite/video inputs, kit baking, density calibration, asset-specific experiments |
| Rusty Engine task 6847 | Direct `CharacterControllerService`, durable character-motion facts, capsule collision, moving-platform carry, external impulses, and `FirstPersonLookService`; validated during task 6849 against Engine revision `9142e7bf77a909c6657bb1c69abda77a6a6b4434` | Universal scheduler, renderer-owned motion, device bindings, game-feel tuning, camera presentation, gameplay consequences |
| Rusty Engine task 6848 | Stateful `VoxelRenderProjector`, stable signed-world chunk handles, revisioned create/replace/destroy publication, and Engine-owned dirty/rebuilt/reused/removed chunk facts | Global terrain handles, downstream chunk registries, duplicate voxel authority, renderer-private mutation |
| Rusty Engine task 6851 | Guarded `VoxelChunkResidencyService` admission/eviction, complete dense payloads, retained mesh reuse, coherent collision/navigation revision publication, and explicit chunk leases | Engine-owned generation policy, ambient streaming scheduler, edit persistence policy, hidden pinning, infinite-world claims |

No asset files or source files were copied verbatim. The implementation uses current public Engine
facade APIs and owns only CraftSurvive-specific composition. The terrain source images are original
OpenAI image-generation outputs made for this repository; their prompts, mechanical atlas build,
layout, and hash are recorded under `content/textures/`.
