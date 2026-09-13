# ProductContent bundles (#8253)

CraftSurvive declares `RustyEngineContentBundle Include="procgen"`. The SDK
inventories its 24 files (53,586 bytes). Those files no longer enter the eager
legacy ProductContent snapshot. The workbench opens the bundle when reading
the bank or loading an artifact, reads bundle-relative names, and disposes the
collection after the read. The authored `_index.json` retains curation; the
Engine inventory supplies discovery and file identities.

Exact installed SDK/runtime pair: `0.1.0-dev.baf031173e19`, Engine source
`baf031173e19a647d34527accd2d63a0ca116873`. Normal launch and SDK pins advance
together. Historical evidence under product-content-8186 remains unchanged.

Verification:
- Release ordinary-package build and CoreCLR staging: zero warnings/errors.
- A separately staged Product was copied to `Relocated`; all 92 file hashes
  matched. The packaged host launched it from `/tmp`, without source lookup.
- `craft.procgen.bank` retained the exact prior SHA256
  `850e5ae85554775e92bd8e3ce931d2ec4ea0dd2ba9950784c44e5a10205e7b21` and 15 entries.
- Two `craft.procgen.load procgen/workbench-11.json` operations applied at
  revisions 1 and 2, exercising bundle open/read/release/reopen and real Engine
  geometry/collision use. Both passed 38/38 scoped body checks, with no failures
  or unavailable probes. See loaded.txt and reloaded.txt.
- Engine packaged-consumer tests separately exercised both CoreCLR and
  NativeAOT: selective legacy snapshot, metadata discovery, ordered directory
  reads, a >1 MiB file, missing names, retained native references and managed
  bytes after bundle disposal, repeated disposal and reopen. Rust tests proved
  unopened payloads are not read and final reference disposal releases source
  ownership.

This was a native host/content exercise; no browser or GPU rendering acceptance
is claimed. No local headless browser or external playtest session was started.
The owned host on port 37310 was stopped, and listener absence was verified.
