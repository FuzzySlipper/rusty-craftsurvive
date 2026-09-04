# Product provenance

CraftSurvive retains semantic product ownership while using the installed
Rusty.Engine SDK for reusable mechanisms. This document records sources for
current product meaning and content; it is not a second implementation route.

| Source | Current product use | Boundary retained |
| --- | --- | --- |
| Earlier CraftSurvive terrain/player work | Deterministic recipe vocabulary, bounded terrain fixtures, first-person controls, edit semantics, platform route, and global-position intent | C# owns policy and state; Engine owns collision, spatial services, presentation, persistence primitives, and lifecycle. |
| Rusty.Engine SDK character/look services | Character step, look integration, support/carry, crouch/step facts, external impulse, and controller continuation | Product owns cadence and tuning; it does not copy the solver or create a renderer. |
| Rusty.Engine SDK voxel services | Voxel sessions, chunk admission/leases, revisioned edits, retained scene projection, and material bindings | Product owns recipe and edit admission; Engine owns resident scene, mesh, collision, handles, and render resources. |
| Rusty.Engine SDK world-origin service | Exact global/local values, explicit player/platform roots, synchronized rebase, and continuation after origin changes | Product decides when to rebase and retains global meaning; Engine performs the atomic mechanism. |
| `content/textures/` | Canonical terrain atlas, sky panorama, source images, and checked metadata | The product selects the panorama through Engine Appearance and CameraView; terrain uses the admitted directional atlas mapping. |
| `content/animations/spatial-wizard.glb` | Accepted ghost-plate source selected by the C# presentation module | Product owns the source choice and tuning; Engine owns GLB admission, capture, directional selection, and retained realization. |
| Other `content/animations/` assets | User-owned assets retained for future product slices | They are not part of the current runtime. |
| `content/voxels/woodland-shrine-nano-model-solid64.vox` | Selected runtime source for C# microvoxel presentation | Product content is admitted through Engine `ProductContent`; CraftSurvive does not reimplement the VOX parser or conversion pipeline. |

The selected microvoxel runtime copy is
`content/voxels/woodland-shrine-nano-model-solid64.vox` (99,682 bytes, SHA-256
`a0a38ff44c6f753df55c772bbf957841fe82d074128dbd9b0e07deaebadd20c6`). It is
byte-identical to
`/home/dev/asset-pipeline/live-evidence/voxel-experiment-gallery/imports/shrine-nano-tripo-p2-vengi-solid64-20260822-001/members/model-solid64.vox`.
That member belongs to the asset-pipeline artifact set
`shrine-nano-tripo-p2-vengi-solid64-20260822-001`. Its recorded source and
conversion chain is Nano Banana 2 source image -> manually run Tripo Studio P2
mesh -> Vengi `voxconvert` 0.5.0.0 at revision
`4d5fbc9993c9bd877e0a4936cacdca41320439`, using longest-axis resolution 64,
enclosed-cavity fill, and palette creation -> the selected `.vox` output. The
artifact set and source comparison live under ignored asset-pipeline evidence;
the copied `.vox` is the current CraftSurvive runtime content.

The selected ghost source is
`content/animations/spatial-wizard.glb` (2,228,108 bytes, SHA-256
`b04f1dc32af87a6f5fb8d02cd1261a54a791ac93275fcc5a71e1f93cc23f9aca`).
It is byte-identical to the accepted pre-C# source preserved at CraftSurvive
revision `f2a0ca4854128335f29daf3f0c56ad7da1b39d91`. The default product preset
restores that experiment's named capture and shallow-relief values; the
`current` preset provides a same-source comparison with the former C# tuning.

Retired experiments are available only as semantic evidence when a future task
needs them. They are not current product architecture and no archive copy is
maintained in this checkout.
