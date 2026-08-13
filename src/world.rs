use std::{collections::BTreeMap, time::Instant};

use rusty_engine::{
    engine_spatial::{
        VoxelChunkIdentity, VoxelChunkLeaseId, VoxelChunkLeaseRegistry, VoxelChunkPayload,
        VoxelChunkResidencyOperation, VoxelChunkResidencyService, VoxelChunkResidencyTransaction,
        VoxelCollisionScene, VoxelEdit, VoxelEditService, VoxelEditTransaction, VoxelPickHint,
        VoxelPickService,
    },
    svc_mesh::SurfaceMeshOptions,
};

use crate::{
    generate_island, island::material_at, IslandConfig, PlayerController, SurfaceSelection,
    TerrainConfig,
};

pub const MAX_BRUSH_RADIUS: u8 = 2;
const CHUNK_SIZE: i64 = 16;
const REQUEST_RADIUS: i64 = 1;
const RETAIN_RADIUS: i64 = 2;
const MAX_RESIDENCY_OPERATIONS_PER_TICK: usize = 16;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EditKind {
    Destroy,
    Place { material_slot: u16 },
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EditReceipt {
    pub voxel: [i64; 3],
    pub affected_voxels: usize,
    pub revision: u64,
    pub authority_hash: u64,
    pub voxel_count: usize,
    pub mesh_build_ms: f64,
    pub edit_ms: f64,
    pub dirty_chunks: usize,
    pub rebuilt_chunks: usize,
    pub reused_chunks: usize,
    pub removed_chunks: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EditRejection {
    PlayerOverlap { voxel: [i64; 3] },
    WorldBounds { voxel: [i64; 3] },
}

impl EditRejection {
    pub const fn code(self) -> &'static str {
        match self {
            Self::PlayerOverlap { .. } => "playerOverlap",
            Self::WorldBounds { .. } => "worldBounds",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub enum EditOutcome {
    Miss,
    Rejected(EditRejection),
    Applied(EditReceipt),
}

pub struct GameWorld {
    scene: VoxelCollisionScene,
    surface: SurfaceSelection,
    terrain: TerrainConfig,
    bounds: WorldBounds,
    metrics: WorldMetrics,
    residency: TerrainResidency,
    edit_overlay: BTreeMap<[i64; 3], Option<u16>>,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct TerrainResidencyReadout {
    pub center: [i64; 2],
    pub requested: usize,
    pub preparing: usize,
    pub resident: usize,
    pub pinned: usize,
    pub evictable: usize,
    pub admitted_total: u64,
    pub evicted_total: u64,
    pub cache_hits: u64,
    pub missed_deadlines: u64,
    pub resident_bytes: usize,
    pub generation_ms: f64,
    pub admission_ms: f64,
}

struct TerrainResidency {
    island: IslandConfig,
    leases: VoxelChunkLeaseRegistry,
    player_leases: BTreeMap<VoxelChunkIdentity, VoxelChunkLeaseId>,
    center: [i64; 2],
    requested: usize,
    preparing: usize,
    admitted_total: u64,
    evicted_total: u64,
    cache_hits: u64,
    missed_deadlines: u64,
    payload_cache: BTreeMap<VoxelChunkIdentity, Option<VoxelChunkPayload>>,
    generation_ms: f64,
    admission_ms: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct WorldMetrics {
    pub generation_ms: f64,
    pub authority_build_ms: f64,
    pub mesh_build_ms: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WorldMeshStats {
    pub chunks: usize,
    pub vertices: u64,
    pub triangles: u64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
struct WorldBounds {
    min: [i64; 3],
    max: [i64; 3],
}

impl WorldBounds {
    fn from_island(config: IslandConfig) -> Self {
        Self {
            min: [-config.radius, -config.depth, -config.radius],
            max: [config.radius, config.summit_height + 16, config.radius],
        }
    }

    fn contains(self, address: [i64; 3]) -> bool {
        (0..3).all(|axis| (self.min[axis]..=self.max[axis]).contains(&address[axis]))
    }
}

impl GameWorld {
    pub fn new(surface: SurfaceSelection) -> Result<Self, String> {
        Self::with_terrain(surface, TerrainConfig::default())
    }

    pub fn with_terrain(surface: SurfaceSelection, terrain: TerrainConfig) -> Result<Self, String> {
        let island = IslandConfig::from(terrain);
        let generation_started = Instant::now();
        let voxels = generate_island(island).into_iter().filter(|voxel| {
            voxel.address[0].div_euclid(CHUNK_SIZE).abs() <= RETAIN_RADIUS
                && voxel.address[2].div_euclid(CHUNK_SIZE).abs() <= RETAIN_RADIUS
        });
        let generation_ms = generation_started.elapsed().as_secs_f64() * 1_000.0;
        let authority_started = Instant::now();
        let scene = VoxelCollisionScene::from_material_voxels_with_mesh_options(
            1.0,
            16,
            voxels,
            SurfaceMeshOptions {
                mode: surface.engine_mode(),
                ..SurfaceMeshOptions::default()
            },
        )
        .map_err(|error| format!("build island authority: {error}"))?;
        let authority_build_ms = authority_started.elapsed().as_secs_f64() * 1_000.0;
        Ok(Self {
            scene,
            surface,
            terrain,
            bounds: WorldBounds::from_island(island),
            metrics: WorldMetrics {
                generation_ms,
                authority_build_ms,
                mesh_build_ms: authority_build_ms,
            },
            residency: TerrainResidency {
                island,
                leases: VoxelChunkLeaseRegistry::default(),
                player_leases: BTreeMap::new(),
                center: [0, 0],
                requested: 0,
                preparing: 0,
                admitted_total: 0,
                evicted_total: 0,
                cache_hits: 0,
                missed_deadlines: 0,
                payload_cache: BTreeMap::new(),
                generation_ms: 0.0,
                admission_ms: 0.0,
            },
            edit_overlay: BTreeMap::new(),
        })
    }

    pub const fn scene(&self) -> &VoxelCollisionScene {
        &self.scene
    }

    pub const fn surface(&self) -> SurfaceSelection {
        self.surface
    }

    pub const fn terrain(&self) -> TerrainConfig {
        self.terrain
    }

    pub const fn metrics(&self) -> WorldMetrics {
        self.metrics
    }

    pub fn mesh_stats(&self) -> WorldMeshStats {
        self.scene.mesh_chunks().iter().fold(
            WorldMeshStats {
                chunks: 0,
                vertices: 0,
                triangles: 0,
            },
            |mut stats, chunk| {
                stats.chunks += 1;
                stats.vertices += u64::from(chunk.vertices);
                stats.triangles += chunk.indices.len() as u64 / 3;
                stats
            },
        )
    }

    pub fn residency_readout(&self) -> TerrainResidencyReadout {
        let resident = VoxelChunkResidencyService::resident_chunks(&self.scene);
        let pinned = resident
            .iter()
            .filter(|chunk| self.residency.leases.is_pinned(chunk.chunk))
            .count();
        TerrainResidencyReadout {
            center: self.residency.center,
            requested: self.residency.requested,
            preparing: self.residency.preparing,
            resident: resident.len(),
            pinned,
            evictable: resident.len().saturating_sub(pinned),
            admitted_total: self.residency.admitted_total,
            evicted_total: self.residency.evicted_total,
            cache_hits: self.residency.cache_hits,
            missed_deadlines: self.residency.missed_deadlines,
            resident_bytes: resident.len() * CHUNK_SIZE.pow(3) as usize * size_of::<u16>(),
            generation_ms: self.residency.generation_ms,
            admission_ms: self.residency.admission_ms,
        }
    }

    pub fn sync_residency(&mut self, position: [f64; 3]) -> Result<bool, String> {
        let center = [
            (position[0].floor() as i64).div_euclid(CHUNK_SIZE),
            (position[2].floor() as i64).div_euclid(CHUNK_SIZE),
        ];
        self.residency.center = center;
        let current = VoxelChunkResidencyService::resident_chunks(&self.scene)
            .into_iter()
            .map(|chunk| (chunk.chunk, chunk))
            .collect::<BTreeMap<_, _>>();
        let generation_started = Instant::now();
        let requested = desired_chunks(
            self.residency.island,
            center,
            REQUEST_RADIUS,
            &self.edit_overlay,
            &mut self.residency.payload_cache,
        );
        let retained = desired_chunks(
            self.residency.island,
            center,
            RETAIN_RADIUS,
            &self.edit_overlay,
            &mut self.residency.payload_cache,
        );
        self.residency.generation_ms = generation_started.elapsed().as_secs_f64() * 1_000.0;
        self.residency.requested = requested.len();

        for identity in self
            .residency
            .player_leases
            .keys()
            .copied()
            .collect::<Vec<_>>()
        {
            if !requested.contains_key(&identity) {
                if let Some(lease) = self.residency.player_leases.remove(&identity) {
                    self.residency
                        .leases
                        .release(lease)
                        .map_err(|error| error.to_string())?;
                }
            }
        }

        let mut operations = Vec::new();
        for (chunk, payload) in &requested {
            if current.contains_key(chunk) {
                self.residency.cache_hits = self.residency.cache_hits.saturating_add(1);
            } else if operations.len() < MAX_RESIDENCY_OPERATIONS_PER_TICK {
                operations.push(VoxelChunkResidencyOperation::Admit {
                    chunk: *chunk,
                    payload: payload.clone(),
                });
            }
        }
        let missing_count = requested
            .keys()
            .filter(|chunk| !current.contains_key(chunk))
            .count();
        if missing_count > MAX_RESIDENCY_OPERATIONS_PER_TICK {
            self.residency.missed_deadlines = self.residency.missed_deadlines.saturating_add(1);
        }
        if operations.is_empty() {
            for (chunk, resident) in &current {
                if !retained.contains_key(chunk)
                    && !self.residency.leases.is_pinned(*chunk)
                    && operations.len() < MAX_RESIDENCY_OPERATIONS_PER_TICK
                {
                    operations.push(VoxelChunkResidencyOperation::Evict {
                        chunk: *chunk,
                        expected_content_hash: resident.content_hash,
                    });
                }
            }
        }
        self.residency.preparing = operations.len();
        let changed = !operations.is_empty();
        if changed {
            let admission_started = Instant::now();
            let expected_scene_source_revision = self.scene.source_revision();
            let receipt = VoxelChunkResidencyService::apply(
                &mut self.scene,
                &self.residency.leases,
                VoxelChunkResidencyTransaction {
                    expected_scene_source_revision,
                    operations: &operations,
                },
            )
            .map_err(|error| format!("publish terrain residency: {error}"))?;
            self.residency.admitted_total = self
                .residency
                .admitted_total
                .saturating_add(receipt.admitted.len() as u64);
            self.residency.evicted_total = self
                .residency
                .evicted_total
                .saturating_add(receipt.evicted.len() as u64);
            self.residency.admission_ms = admission_started.elapsed().as_secs_f64() * 1_000.0;
        }
        self.residency.preparing = 0;
        for chunk in requested.keys() {
            if !self.residency.player_leases.contains_key(chunk)
                && VoxelChunkResidencyService::resident_chunk(&self.scene, *chunk).is_some()
            {
                let evidence = self
                    .residency
                    .leases
                    .acquire(&self.scene, *chunk)
                    .map_err(|error| error.to_string())?;
                self.residency
                    .player_leases
                    .insert(*chunk, evidence.lease_id);
            }
        }
        Ok(changed)
    }

    pub fn target_from_view(&self, origin: [f64; 3], direction: [f64; 3]) -> Option<[i64; 3]> {
        self.scene
            .raycast(origin, direction, 8.0)
            .map(|hit| hit.voxel)
    }

    pub fn edit_from_view(
        &mut self,
        origin: [f64; 3],
        direction: [f64; 3],
        kind: EditKind,
        brush_radius: u8,
        player: &PlayerController,
    ) -> Result<EditOutcome, String> {
        let Some(hit) = self.scene.raycast(origin, direction, 8.0) else {
            return Ok(EditOutcome::Miss);
        };
        let anchor = VoxelPickService::validate(
            &self.scene,
            VoxelPickHint {
                origin,
                direction,
                max_distance: 8.0,
                claimed_voxel: hit.voxel,
                claimed_face: hit.face,
            },
        )
        .map_err(|error| format!("validate voxel edit ray: {error}"))?;
        let edit = match kind {
            EditKind::Destroy => anchor.remove_edit(),
            EditKind::Place { material_slot } => anchor.place_edit(material_slot),
        };
        let edited_voxel = edit.address();
        let edit_started = Instant::now();
        let edits = brush_edits(edited_voxel, brush_radius, kind)?;
        if let Some(edit) = edits
            .iter()
            .find(|edit| !self.bounds.contains(edit.address()))
        {
            return Ok(EditOutcome::Rejected(EditRejection::WorldBounds {
                voxel: edit.address(),
            }));
        }
        if matches!(kind, EditKind::Place { .. }) {
            if let Some(edit) = edits
                .iter()
                .find(|edit| player.overlaps_voxel(edit.address(), self.scene.voxel_size()))
            {
                return Ok(EditOutcome::Rejected(EditRejection::PlayerOverlap {
                    voxel: edit.address(),
                }));
            }
        }
        let expected_revision = self.scene.source_revision();
        let mesh_started = Instant::now();
        let prepared = VoxelEditService::preview(
            &self.scene,
            VoxelEditTransaction {
                expected_revision,
                edits: &edits,
            },
        )
        .map_err(|error| format!("apply coherent voxel edit: {error}"))?;
        let affected_voxels = prepared.deltas().len();
        let overlay = prepared.deltas().to_vec();
        let mesh_build_ms = mesh_started.elapsed().as_secs_f64() * 1_000.0;
        let receipt = VoxelEditService::commit(&mut self.scene, prepared)
            .map_err(|error| format!("commit coherent voxel edit: {error}"))?;
        if !receipt
            .projections
            .is_coherent_with(receipt.accepted_revision)
        {
            return Err("Engine accepted an incoherent voxel projection revision".to_owned());
        }
        for delta in overlay {
            self.edit_overlay
                .insert(delta.address, delta.after_material);
            self.residency
                .payload_cache
                .remove(&VoxelChunkIdentity::new(
                    delta.address[0].div_euclid(CHUNK_SIZE),
                    delta.address[1].div_euclid(CHUNK_SIZE),
                    delta.address[2].div_euclid(CHUNK_SIZE),
                ));
        }
        Ok(EditOutcome::Applied(EditReceipt {
            voxel: edited_voxel,
            affected_voxels,
            revision: receipt.accepted_revision.raw(),
            authority_hash: receipt.authority_hash,
            voxel_count: receipt.solid_voxel_count,
            mesh_build_ms,
            edit_ms: edit_started.elapsed().as_secs_f64() * 1_000.0,
            dirty_chunks: receipt.dirty_mesh_chunks.len(),
            rebuilt_chunks: receipt.rebuilt_mesh_chunks,
            reused_chunks: receipt.reused_mesh_chunks,
            removed_chunks: receipt.removed_mesh_chunks,
        }))
    }
}

pub fn brush_addresses(center: [i64; 3], radius: u8) -> Result<Vec<[i64; 3]>, String> {
    if radius > MAX_BRUSH_RADIUS {
        return Err(format!(
            "brush radius {radius} exceeds maximum {MAX_BRUSH_RADIUS}"
        ));
    }
    let radius = i64::from(radius);
    let mut addresses = Vec::new();
    for x in -radius..=radius {
        for y in -radius..=radius {
            for z in -radius..=radius {
                if x * x + y * y + z * z <= radius * radius {
                    addresses.push([center[0] + x, center[1] + y, center[2] + z]);
                }
            }
        }
    }
    Ok(addresses)
}

fn brush_edits(center: [i64; 3], radius: u8, kind: EditKind) -> Result<Vec<VoxelEdit>, String> {
    if matches!(kind, EditKind::Place { material_slot: 0 }) {
        return Err("material slot zero is reserved for empty voxels".to_owned());
    }
    brush_addresses(center, radius).map(|addresses| {
        addresses
            .into_iter()
            .map(|address| match kind {
                EditKind::Destroy => VoxelEdit::Clear { address },
                EditKind::Place { material_slot } => VoxelEdit::Set {
                    address,
                    material_slot,
                },
            })
            .collect()
    })
}

fn desired_chunks(
    island: IslandConfig,
    center: [i64; 2],
    radius: i64,
    overlay: &BTreeMap<[i64; 3], Option<u16>>,
    cache: &mut BTreeMap<VoxelChunkIdentity, Option<VoxelChunkPayload>>,
) -> BTreeMap<VoxelChunkIdentity, VoxelChunkPayload> {
    let min_chunk = (-island.radius).div_euclid(CHUNK_SIZE);
    let max_chunk = island.radius.div_euclid(CHUNK_SIZE);
    let min_y = (-island.depth).div_euclid(CHUNK_SIZE);
    let max_y = (island.summit_height + 16).div_euclid(CHUNK_SIZE);
    let mut chunks = BTreeMap::new();
    for chunk_x in center[0] - radius..=center[0] + radius {
        if !(min_chunk..=max_chunk).contains(&chunk_x) {
            continue;
        }
        for chunk_z in center[1] - radius..=center[1] + radius {
            if !(min_chunk..=max_chunk).contains(&chunk_z) {
                continue;
            }
            for chunk_y in min_y..=max_y {
                let identity = VoxelChunkIdentity::new(chunk_x, chunk_y, chunk_z);
                let payload = cache
                    .entry(identity)
                    .or_insert_with(|| {
                        let payload = chunk_payload(island, identity, overlay);
                        (payload.solid_voxel_count() > 0).then_some(payload)
                    })
                    .clone();
                if let Some(payload) = payload {
                    chunks.insert(identity, payload);
                }
            }
        }
    }
    chunks
}

fn chunk_payload(
    island: IslandConfig,
    chunk: VoxelChunkIdentity,
    overlay: &BTreeMap<[i64; 3], Option<u16>>,
) -> VoxelChunkPayload {
    let mut material_slots = Vec::with_capacity(CHUNK_SIZE.pow(3) as usize);
    for z in 0..CHUNK_SIZE {
        for y in 0..CHUNK_SIZE {
            for x in 0..CHUNK_SIZE {
                let address = [
                    chunk.x * CHUNK_SIZE + x,
                    chunk.y * CHUNK_SIZE + y,
                    chunk.z * CHUNK_SIZE + z,
                ];
                material_slots.push(
                    overlay
                        .get(&address)
                        .copied()
                        .unwrap_or_else(|| material_at(island, address))
                        .unwrap_or(0),
                );
            }
        }
    }
    VoxelChunkPayload::new([CHUNK_SIZE as u32; 3], material_slots)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{PlayerInput, PlayerPose};

    const STEP_SECONDS: f64 = 1.0 / 120.0;

    fn test_world(
        surface: SurfaceSelection,
        voxels: impl IntoIterator<Item = [i64; 3]>,
    ) -> GameWorld {
        let scene = VoxelCollisionScene::from_material_voxels_with_mesh_options(
            1.0,
            16,
            voxels
                .into_iter()
                .map(|address| rusty_engine::engine_spatial::MaterialVoxel {
                    address,
                    material_slot: 1,
                }),
            SurfaceMeshOptions {
                mode: surface.engine_mode(),
                ..SurfaceMeshOptions::default()
            },
        )
        .unwrap();
        GameWorld {
            scene,
            surface,
            terrain: TerrainConfig::new(0, 32).unwrap(),
            bounds: WorldBounds {
                min: [-16, -16, -16],
                max: [16, 32, 16],
            },
            metrics: WorldMetrics {
                generation_ms: 0.0,
                authority_build_ms: 0.0,
                mesh_build_ms: 0.0,
            },
            residency: TerrainResidency {
                island: IslandConfig::from(TerrainConfig::new(0, 32).unwrap()),
                leases: VoxelChunkLeaseRegistry::default(),
                player_leases: BTreeMap::new(),
                center: [0, 0],
                requested: 0,
                preparing: 0,
                admitted_total: 0,
                evicted_total: 0,
                cache_hits: 0,
                missed_deadlines: 0,
                payload_cache: BTreeMap::new(),
                generation_ms: 0.0,
                admission_ms: 0.0,
            },
            edit_overlay: BTreeMap::new(),
        }
    }

    fn player(position: [f64; 3], yaw_degrees: f64) -> PlayerController {
        let mut player = PlayerController::new(PlayerPose {
            position,
            yaw_degrees,
            pitch_degrees: 0.0,
        })
        .unwrap();
        player
            .step(
                &VoxelCollisionScene::from_solid_voxels(1.0, 16, [[0, 0, 0]]).unwrap(),
                PlayerInput::default(),
                STEP_SECONDS,
            )
            .unwrap();
        player
    }

    fn applied(outcome: EditOutcome) -> EditReceipt {
        match outcome {
            EditOutcome::Applied(receipt) => receipt,
            other => panic!("expected applied edit, got {other:?}"),
        }
    }

    fn floor_with(extra: impl IntoIterator<Item = [i64; 3]>) -> Vec<[i64; 3]> {
        let mut voxels = Vec::new();
        for x in -4..=4 {
            for z in -2..=2 {
                voxels.push([x, 0, z]);
            }
        }
        voxels.extend(extra);
        voxels
    }

    #[test]
    fn all_surface_modes_are_distinct_derived_paths_over_one_authority_shape() {
        let meshes = SurfaceSelection::ALL.map(|surface| GameWorld::new(surface).unwrap());
        let hashes = meshes
            .each_ref()
            .map(|world| world.scene().authority_hash());
        assert_eq!(hashes[0], hashes[1]);
        assert_eq!(hashes[1], hashes[2]);
        for (index, world) in meshes.iter().enumerate() {
            assert!(world
                .scene()
                .mesh_chunks()
                .iter()
                .all(|chunk| chunk.surface_mode == surface_mode(index)));
        }
        assert_ne!(
            meshes[0].mesh_stats().triangles,
            meshes[1].mesh_stats().triangles
        );
    }

    fn surface_mode(index: usize) -> rusty_engine::svc_mesh::SurfaceMode {
        SurfaceSelection::ALL[index].engine_mode()
    }

    #[test]
    fn destroy_and_place_advance_one_coherent_authority() {
        let mut world = GameWorld::new(SurfaceSelection::Box).unwrap();
        let before = world.scene().source_revision();
        let origin = [0.5, 10.5, 7.5];
        let direction = [0.0, -1.0, 0.0];
        let player = PlayerController::default();
        let removed = applied(
            world
                .edit_from_view(origin, direction, EditKind::Destroy, 0, &player)
                .unwrap(),
        );
        assert!(removed.revision > before.raw());
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(world.scene().source_revision()));
        let placed = applied(
            world
                .edit_from_view(
                    origin,
                    direction,
                    EditKind::Place { material_slot: 1 },
                    0,
                    &player,
                )
                .unwrap(),
        );
        assert!(placed.revision > removed.revision);
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(world.scene().source_revision()));
    }

    #[test]
    fn removing_support_causes_fall_to_the_next_lower_voxel() {
        let mut world = test_world(SurfaceSelection::Box, [[0, 0, 0], [0, -2, 0]]);
        let mut player = player([0.5, 2.55, 0.5], 0.0);
        assert!(player.motion().grounded);

        applied(
            world
                .edit_from_view(
                    player.pose().position,
                    [0.0, -1.0, 0.0],
                    EditKind::Destroy,
                    0,
                    &player,
                )
                .unwrap(),
        );
        player
            .step(world.scene(), PlayerInput::default(), STEP_SECONDS)
            .unwrap();
        assert!(!player.motion().grounded);
        for _ in 0..240 {
            player
                .step(world.scene(), PlayerInput::default(), STEP_SECONDS)
                .unwrap();
        }
        assert!(player.motion().grounded);
        assert!(player.pose().position[1] < 0.65);
        assert!(!player.overlaps_scene(world.scene()));
    }

    #[test]
    fn placement_overlapping_player_is_typed_rejection_without_mutation() {
        for surface in SurfaceSelection::ALL {
            let mut world = test_world(surface, [[0, 0, 0]]);
            let player = player([0.5, 2.55, 0.5], 0.0);
            let revision = world.scene().source_revision();
            let hash = world.scene().authority_hash();
            let chunks = chunk_hashes(&world);

            let outcome = world
                .edit_from_view(
                    player.pose().position,
                    [0.0, -1.0, 0.0],
                    EditKind::Place { material_slot: 1 },
                    0,
                    &player,
                )
                .unwrap();
            assert_eq!(
                outcome,
                EditOutcome::Rejected(EditRejection::PlayerOverlap { voxel: [0, 1, 0] })
            );
            assert_eq!(world.scene().source_revision(), revision);
            assert_eq!(world.scene().authority_hash(), hash);
            assert_eq!(chunk_hashes(&world), chunks);
        }
    }

    #[test]
    fn wall_removal_opens_and_replacement_blocks_every_surface_mode() {
        let mut observations = Vec::new();
        for surface in SurfaceSelection::ALL {
            let mut world = test_world(surface, floor_with([[1, 2, 0], [2, 2, 0]]));
            let mut walker = player([0.0, 2.55, 0.5], 90.0);
            applied(
                world
                    .edit_from_view(
                        walker.pose().position,
                        [1.0, 0.0, 0.0],
                        EditKind::Destroy,
                        0,
                        &walker,
                    )
                    .unwrap(),
            );
            for _ in 0..15 {
                walker
                    .step(
                        world.scene(),
                        PlayerInput {
                            forward: 1.0,
                            ..PlayerInput::default()
                        },
                        STEP_SECONDS,
                    )
                    .unwrap();
            }
            let opened_x = walker.pose().position[0];
            assert!(opened_x > 0.8);

            let mut blocked = player([0.0, 2.55, 0.5], 90.0);
            let placed = applied(
                world
                    .edit_from_view(
                        blocked.pose().position,
                        [1.0, 0.0, 0.0],
                        EditKind::Place { material_slot: 1 },
                        0,
                        &blocked,
                    )
                    .unwrap(),
            );
            assert!(world
                .scene()
                .projection_revisions()
                .is_coherent_with(world.scene().source_revision()));
            for _ in 0..15 {
                blocked
                    .step(
                        world.scene(),
                        PlayerInput {
                            forward: 1.0,
                            ..PlayerInput::default()
                        },
                        STEP_SECONDS,
                    )
                    .unwrap();
            }
            assert!(blocked.pose().position[0] < 0.71);
            observations.push((opened_x, blocked.pose().position[0], placed.revision));
        }
        assert_eq!(observations[0], observations[1]);
        assert_eq!(observations[1], observations[2]);
    }

    #[test]
    fn invalid_edit_preserves_prior_coherent_state() {
        let mut world = test_world(SurfaceSelection::Box, floor_with([[1, 2, 0]]));
        let mut player = player([0.0, 2.55, 0.5], 90.0);
        let revision = world.scene().source_revision();
        let hash = world.scene().authority_hash();
        let chunks = chunk_hashes(&world);

        assert!(world
            .edit_from_view(
                player.pose().position,
                [1.0, 0.0, 0.0],
                EditKind::Place { material_slot: 0 },
                0,
                &player,
            )
            .is_err());
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);

        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);
        assert_eq!(chunk_hashes(&world), chunks);
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(revision));
        for _ in 0..15 {
            player
                .step(
                    world.scene(),
                    PlayerInput {
                        forward: 1.0,
                        ..PlayerInput::default()
                    },
                    STEP_SECONDS,
                )
                .unwrap();
        }
        assert!(player.pose().position[0] < 0.71);
    }

    fn chunk_hashes(world: &GameWorld) -> Vec<([i64; 3], u64)> {
        world
            .scene()
            .mesh_chunks()
            .iter()
            .map(|chunk| (chunk.chunk, chunk.content_hash))
            .collect()
    }

    #[test]
    fn spherical_brushes_are_bounded_sorted_and_have_expected_volume() {
        assert_eq!(brush_addresses([0, 0, 0], 0).unwrap(), [[0, 0, 0]]);
        let medium = brush_addresses([10, 20, 30], 1).unwrap();
        let large = brush_addresses([10, 20, 30], 2).unwrap();
        assert_eq!(medium.len(), 7);
        assert_eq!(large.len(), 33);
        assert!(medium.windows(2).all(|pair| pair[0] < pair[1]));
        assert!(brush_addresses([0, 0, 0], MAX_BRUSH_RADIUS + 1).is_err());
    }

    #[test]
    fn volume_placement_overlap_rejects_every_cell_atomically() {
        let mut world = test_world(SurfaceSelection::Box, [[0, 0, 0]]);
        let player = player([0.5, 2.55, 0.5], 0.0);
        let revision = world.scene().source_revision();
        let hash = world.scene().authority_hash();
        let outcome = world
            .edit_from_view(
                player.pose().position,
                [0.0, -1.0, 0.0],
                EditKind::Place { material_slot: 1 },
                2,
                &player,
            )
            .unwrap();
        assert!(matches!(
            outcome,
            EditOutcome::Rejected(EditRejection::PlayerOverlap { .. })
        ));
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);
    }

    #[test]
    fn volume_outside_world_bounds_rejects_every_cell_atomically() {
        let mut world = test_world(SurfaceSelection::Box, [[16, 2, 0]]);
        let player = player([10.5, 2.55, 0.5], 90.0);
        let revision = world.scene().source_revision();
        let hash = world.scene().authority_hash();
        let outcome = world
            .edit_from_view(
                player.pose().position,
                [1.0, 0.0, 0.0],
                EditKind::Place { material_slot: 1 },
                2,
                &player,
            )
            .unwrap();
        assert!(matches!(
            outcome,
            EditOutcome::Rejected(EditRejection::WorldBounds { .. })
        ));
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);
    }

    #[test]
    fn volume_destroy_commits_multiple_cells_as_one_revision() {
        let center = [2, 2, 0];
        let solids = brush_addresses(center, 1)
            .unwrap()
            .into_iter()
            .filter(|address| address[0] >= center[0]);
        let mut world = test_world(SurfaceSelection::Box, solids);
        let player = player([-2.0, 2.55, 0.5], 90.0);
        let revision = world.scene().source_revision();
        let receipt = applied(
            world
                .edit_from_view(
                    player.pose().position,
                    [1.0, 0.0, 0.0],
                    EditKind::Destroy,
                    1,
                    &player,
                )
                .unwrap(),
        );
        assert!(receipt.affected_voxels > 1);
        assert_eq!(receipt.revision, revision.raw() + 1);
        assert!(receipt.edit_ms.is_finite());
        assert!(receipt.mesh_build_ms.is_finite());
    }

    #[test]
    fn bounded_residency_crosses_negative_chunks_and_retains_edits() {
        let mut world = GameWorld::new(SurfaceSelection::Box).unwrap();
        world.sync_residency([0.5, 6.0, 7.5]).unwrap();
        let edited = [0, 3, 7];
        world.edit_overlay.insert(edited, None);
        world
            .residency
            .payload_cache
            .remove(&VoxelChunkIdentity::new(0, 0, 0));

        for _ in 0..8 {
            world.sync_residency([-47.0, 6.0, 0.0]).unwrap();
        }
        let negative = world.residency_readout();
        assert!(negative.center[0] < 0);
        assert!(negative.evicted_total > 0);
        assert!(negative.resident <= 80);
        assert_eq!(negative.preparing, 0);

        for _ in 0..8 {
            world.sync_residency([0.5, 6.0, 7.5]).unwrap();
        }
        assert!(!world
            .scene()
            .material_voxels()
            .iter()
            .any(|voxel| voxel.address == edited));
        let returned = world.residency_readout();
        assert!(returned.admitted_total > 0);
        assert!(returned.pinned > 0);
        assert!(returned.resident <= 80);
    }

    #[test]
    fn residency_work_is_bounded_during_far_retargeting() {
        let mut world = GameWorld::new(SurfaceSelection::DualContouring).unwrap();
        world.sync_residency([0.5, 6.0, 7.5]).unwrap();
        let before = world.scene().source_revision().raw();
        world.sync_residency([47.0, 6.0, 0.0]).unwrap();
        let after = world.scene().source_revision().raw();
        assert!(after <= before + 1);
        assert_eq!(world.residency_readout().preparing, 0);
        assert!(world.residency_readout().missed_deadlines <= 1);
    }

    #[test]
    fn failed_chunk_admission_preserves_the_published_scene() {
        let mut world = GameWorld::new(SurfaceSelection::Box).unwrap();
        let revision = world.scene().source_revision();
        let hash = world.scene().authority_hash();
        let operations = [VoxelChunkResidencyOperation::Admit {
            chunk: VoxelChunkIdentity::new(3, 0, 3),
            payload: VoxelChunkPayload::new([1, 1, 1], vec![1]),
        }];
        assert!(VoxelChunkResidencyService::apply(
            &mut world.scene,
            &world.residency.leases,
            VoxelChunkResidencyTransaction {
                expected_scene_source_revision: revision,
                operations: &operations,
            },
        )
        .is_err());
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(revision));
    }
}
