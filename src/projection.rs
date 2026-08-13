use std::collections::BTreeMap;

use rusty_engine::{
    engine_spatial::{VoxelCollisionScene, VoxelMeshChunk},
    render_model::{
        Geometry, Material, MeshAttribute, MeshAttributeKind, MeshAttributeName,
        MeshBoundsDescriptor, MeshBufferLayout, MeshGroupDescriptor, MeshIndexWidth,
        MeshPayloadDescriptor, MeshPayloadSource, MeshProvenance, RenderDiff, RenderFrameDiff,
        RenderHandle, RenderLayer, RenderMetadata, RenderNode, Transform,
    },
    render_presentation::{
        PresentationFrameDiff, PresentationOp, PresentationOpMeta, TelemetryOverlayCorner,
        TelemetryOverlayDescriptor, TelemetryOverlayHandle, TelemetryOverlayProjectionOp,
    },
    render_projection::{VoxelProjectionInstance, VoxelRenderProjector},
    svc_mesh::{MeshBounds, MeshGroup, MeshPayload, MeshStats, SurfaceMode},
};

use crate::{terrain_materials, terrain_texture_op, SurfaceSelection, PLATFORM_HALF_EXTENTS};

#[cfg(test)]
use crate::PLATFORM_INITIAL_CENTER;

const PLATFORM_HANDLE: RenderHandle = RenderHandle::new(2);
const TERRAIN_INSTANCE: &str = "craftsurvive-terrain";

#[derive(Debug)]
pub struct TerrainProjector {
    inner: VoxelRenderProjector,
    initialized: bool,
}

impl Default for TerrainProjector {
    fn default() -> Self {
        Self::new()
    }
}

impl TerrainProjector {
    pub fn new() -> Self {
        Self {
            inner: VoxelRenderProjector::with_publication_stream("craftsurvive:terrain:v1"),
            initialized: false,
        }
    }

    pub fn project(
        &mut self,
        scene: &VoxelCollisionScene,
        platform_position: [f64; 3],
        platform_changed: bool,
    ) -> Result<RenderFrameDiff, String> {
        let display_materials = terrain_materials()?;
        let mut projection_materials = display_materials.clone();
        for material in projection_materials.values_mut() {
            material.texture = None;
            material.voxel_surface = None;
        }
        let mut result = self
            .inner
            .project(
                &[VoxelProjectionInstance {
                    instance_id: TERRAIN_INSTANCE.to_owned(),
                    asset_id: "craftsurvive-terrain-v1".to_owned(),
                    transform: Transform::IDENTITY,
                    scene,
                }],
                &projection_materials,
            )
            .map_err(|error| format!("project retained terrain chunks: {error:?}"))?;

        for operation in &mut result.frame.ops {
            match operation {
                RenderDiff::DefineMaterial { material } => {
                    *material = display_materials
                        .values()
                        .find(|candidate| candidate.id == material.id)
                        .cloned()
                        .ok_or_else(|| {
                            format!("terrain projector emitted unknown material {}", material.id)
                        })?;
                }
                RenderDiff::ReplaceMeshPayload { handle, payload } => {
                    let chunk = scene
                        .mesh_chunks()
                        .iter()
                        .find(|chunk| {
                            self.inner.chunk_handle(TERRAIN_INSTANCE, chunk.chunk) == Some(*handle)
                        })
                        .ok_or_else(|| {
                            format!("terrain projector emitted unknown chunk handle {handle:?}")
                        })?;
                    *payload = mesh_descriptor(&chunk_mesh(chunk), chunk.translation)?;
                }
                _ => {}
            }
        }

        let mut extra = Vec::new();
        if !self.initialized {
            result.frame.ops.insert(0, terrain_texture_op()?);
            extra.push(RenderDiff::Create {
                handle: PLATFORM_HANDLE,
                parent: None,
                node: RenderNode {
                    geometry: Geometry::Cube,
                    material: Material {
                        color: [0.95, 0.65, 0.12, 1.0],
                        wireframe: false,
                    },
                    transform: platform_transform(platform_position),
                    visible: true,
                    layer: RenderLayer::Scene,
                    metadata: RenderMetadata {
                        source_entity: None,
                        source_scene_node: None,
                        tags: vec!["craftsurvive-moving-platform".to_owned()],
                        label: Some("Rust-authoritative moving platform".to_owned()),
                    },
                },
            });
        } else if platform_changed {
            extra.push(RenderDiff::Update {
                handle: PLATFORM_HANDLE,
                transform: Some(platform_transform(platform_position)),
                material: None,
                visible: None,
                metadata: None,
            });
        }
        append_published_ops(&mut result.frame, extra)?;
        self.initialized = true;
        Ok(result.frame)
    }
}

pub fn platform_frame(position: [f64; 3]) -> Result<RenderFrameDiff, String> {
    RenderFrameDiff::try_from_ops(vec![RenderDiff::Update {
        handle: PLATFORM_HANDLE,
        transform: Some(platform_transform(position)),
        material: None,
        visible: None,
        metadata: None,
    }])
    .map_err(|error| format!("build moving platform frame: {error:?}"))
}

fn platform_transform(position: [f64; 3]) -> Transform {
    Transform {
        translation: position.map(|value| value as f32),
        rotation: [0.0, 0.0, 0.0, 1.0],
        scale: PLATFORM_HALF_EXTENTS.map(|value| value * 2.0),
    }
}

fn append_published_ops(
    frame: &mut RenderFrameDiff,
    operations: impl IntoIterator<Item = RenderDiff>,
) -> Result<(), String> {
    frame.ops.extend(operations);
    if let Some(publication) = &mut frame.publication {
        publication.operation_count = u32::try_from(frame.ops.len())
            .map_err(|_| "terrain frame operation count exceeds u32".to_owned())?;
    }
    frame
        .validate()
        .map_err(|error| format!("validate terrain publication: {error:?}"))
}

fn chunk_mesh(chunk: &VoxelMeshChunk) -> MeshPayload {
    MeshPayload {
        surface_mode: chunk.surface_mode,
        positions: chunk.positions.clone(),
        normals: chunk.normals.clone(),
        tile_coordinates: chunk.tile_coordinates.clone(),
        indices: chunk.indices.clone(),
        groups: chunk
            .groups
            .iter()
            .map(|group| MeshGroup {
                material_slot: group.material_slot,
                start: group.start,
                count: group.count,
            })
            .collect(),
        bounds: MeshBounds {
            min: chunk.bounds_min,
            max: chunk.bounds_max,
        },
        stats: MeshStats {
            surface_mode: chunk.surface_mode,
            vertices: chunk.vertices,
            indices: chunk.indices.len() as u32,
            triangles: chunk.indices.len() as u32 / 3,
            quads: chunk.quads,
            faces_emitted: chunk.quads,
            faces_culled: chunk.faces_culled,
            ..MeshStats::default()
        },
    }
}

pub fn telemetry_frame(surface: SurfaceSelection) -> Result<PresentationFrameDiff, String> {
    PresentationFrameDiff::try_from_ops(vec![PresentationOp::TelemetryOverlay {
        meta: PresentationOpMeta::new(0),
        op: TelemetryOverlayProjectionOp::Create {
            handle: TelemetryOverlayHandle::new(1),
            descriptor: TelemetryOverlayDescriptor {
                title: format!(
                    "CraftSurvive [{}] | WASD move | Shift sprint | Control crouch | Space jump | H impulse | arrows look | LMB/F break | RMB/G place",
                    surface.as_str()
                ),
                corner: TelemetryOverlayCorner::TopLeft,
                refresh_interval_ms: 250,
                max_frame_time_samples: 60,
                visible: true,
            },
        },
    }])
    .map_err(|error| format!("build telemetry presentation: {error:?}"))
}

fn mesh_descriptor(
    mesh: &MeshPayload,
    world_translation: [f32; 3],
) -> Result<MeshPayloadDescriptor, String> {
    let streams = presentation_streams(mesh, world_translation)?;
    let vertex_count = u32::try_from(streams.positions.len() / 3)
        .map_err(|_| "terrain vertex count exceeds u32".to_owned())?;
    let index_count = u32::try_from(streams.indices.len())
        .map_err(|_| "terrain index count exceeds u32".to_owned())?;
    Ok(MeshPayloadDescriptor {
        layout: MeshBufferLayout {
            vertex_count,
            index_count,
            index_width: MeshIndexWidth::U32,
            attributes: vec![
                MeshAttribute {
                    name: MeshAttributeName::Position,
                    components: 3,
                    kind: MeshAttributeKind::F32,
                },
                MeshAttribute {
                    name: MeshAttributeName::Normal,
                    components: 3,
                    kind: MeshAttributeKind::F32,
                },
                MeshAttribute {
                    name: MeshAttributeName::Uv,
                    components: 2,
                    kind: MeshAttributeKind::F32,
                },
            ],
        },
        groups: streams.groups,
        bounds: MeshBoundsDescriptor {
            min: mesh.bounds.min,
            max: mesh.bounds.max,
        },
        source: MeshPayloadSource::Inline {
            positions: streams.positions,
            normals: streams.normals,
            uvs: Some(streams.uvs),
            indices: streams.indices,
        },
        provenance: MeshProvenance::VoxelChunk,
    })
}

struct PresentationStreams {
    positions: Vec<f32>,
    normals: Vec<f32>,
    uvs: Vec<f32>,
    indices: Vec<u32>,
    groups: Vec<MeshGroupDescriptor>,
}

fn presentation_streams(
    mesh: &MeshPayload,
    world_translation: [f32; 3],
) -> Result<PresentationStreams, String> {
    if mesh.surface_mode != SurfaceMode::GreedyCubes {
        return reconstructed_presentation_streams(mesh, world_translation);
    }

    let mut lanes = BTreeMap::<u16, Vec<u32>>::new();
    for group in &mesh.groups {
        let start = usize::try_from(group.start).map_err(|_| "mesh group start exceeds usize")?;
        let end = start
            .checked_add(
                usize::try_from(group.count).map_err(|_| "mesh group count exceeds usize")?,
            )
            .ok_or_else(|| "mesh group range overflowed".to_owned())?;
        let indices = mesh
            .indices
            .get(start..end)
            .ok_or_else(|| "mesh group exceeds the terrain index stream".to_owned())?;
        if indices.len() % 6 != 0 {
            return Err("greedy terrain group does not contain complete quads".to_owned());
        }
        for quad in indices.chunks_exact(6) {
            let slot = if group.material_slot == 1 {
                grass_material_slot(quad_normal_y(mesh, quad)?, 1.0)
            } else {
                group.material_slot
            };
            lanes.entry(slot).or_default().extend_from_slice(quad);
        }
    }

    let mut indices = Vec::with_capacity(mesh.indices.len());
    let mut groups = Vec::with_capacity(lanes.len());
    for (material_slot, lane) in lanes {
        let start = u32::try_from(indices.len()).map_err(|_| "terrain index start exceeds u32")?;
        let count = u32::try_from(lane.len()).map_err(|_| "terrain index lane exceeds u32")?;
        indices.extend(lane);
        groups.push(MeshGroupDescriptor {
            material_slot,
            start,
            count,
        });
    }
    Ok(PresentationStreams {
        positions: mesh.positions.clone(),
        normals: mesh.normals.clone(),
        uvs: world_projected_vertex_uvs(mesh, world_translation)?,
        indices,
        groups,
    })
}

fn world_projected_vertex_uvs(
    mesh: &MeshPayload,
    world_translation: [f32; 3],
) -> Result<Vec<f32>, String> {
    let vertex_count = mesh.positions.len() / 3;
    if !mesh.positions.len().is_multiple_of(3) || mesh.normals.len() != mesh.positions.len() {
        return Err("terrain position and normal streams are misaligned".to_owned());
    }
    let mut uvs = Vec::with_capacity(vertex_count * 2);
    for vertex in 0..vertex_count {
        let position = vertex3(&mesh.positions, vertex, "position")?;
        let normal = vertex3(&mesh.normals, vertex, "normal")?;
        uvs.extend_from_slice(&project_position(
            add3(position, world_translation),
            dominant_axis(normal),
        ));
    }
    Ok(uvs)
}

fn quad_normal_y(mesh: &MeshPayload, quad: &[u32]) -> Result<f32, String> {
    let vertex = usize::try_from(quad[0]).map_err(|_| "terrain vertex index exceeds usize")?;
    let normal_y = mesh
        .normals
        .get(vertex * 3 + 1)
        .ok_or_else(|| "terrain quad references a missing normal".to_owned())?;
    Ok(*normal_y)
}

fn reconstructed_presentation_streams(
    mesh: &MeshPayload,
    world_translation: [f32; 3],
) -> Result<PresentationStreams, String> {
    let mut positions = Vec::with_capacity(mesh.indices.len() * 3);
    let mut normals = Vec::with_capacity(mesh.indices.len() * 3);
    let mut uvs = Vec::with_capacity(mesh.indices.len() * 2);
    let mut lanes = BTreeMap::<u16, Vec<u32>>::new();

    for group in &mesh.groups {
        let source_start =
            usize::try_from(group.start).map_err(|_| "mesh group start exceeds usize")?;
        let source_end = source_start
            .checked_add(
                usize::try_from(group.count).map_err(|_| "mesh group count exceeds usize")?,
            )
            .ok_or_else(|| "mesh group range overflowed".to_owned())?;
        let source = mesh
            .indices
            .get(source_start..source_end)
            .ok_or_else(|| "mesh group exceeds the terrain index stream".to_owned())?;
        if source.len() % 3 != 0 {
            return Err(
                "reconstructed terrain group does not contain complete triangles".to_owned(),
            );
        }
        for triangle in source.chunks_exact(3) {
            let projection = triangle_projection(mesh, triangle)?;
            let material_slot = if group.material_slot == 1 {
                grass_material_slot(projection.normal_y, projection.normal_magnitude)
            } else {
                group.material_slot
            };
            let uv_axis = if material_slot == 4 {
                projection.side_axis
            } else {
                projection.axis
            };
            let mut target_triangle = [0_u32; 3];
            for (corner, source_index) in triangle.iter().enumerate() {
                let source_vertex = usize::try_from(*source_index)
                    .map_err(|_| "terrain vertex index exceeds usize")?;
                let position = vertex3(&mesh.positions, source_vertex, "position")?;
                let normal = vertex3(&mesh.normals, source_vertex, "normal")?;
                let target_index = u32::try_from(positions.len() / 3)
                    .map_err(|_| "expanded terrain vertex count exceeds u32")?;
                positions.extend_from_slice(&position);
                normals.extend_from_slice(&normal);
                uvs.extend_from_slice(&project_position(
                    add3(position, world_translation),
                    uv_axis,
                ));
                target_triangle[corner] = target_index;
            }
            lanes
                .entry(material_slot)
                .or_default()
                .extend_from_slice(&target_triangle);
        }
    }

    let mut indices = Vec::with_capacity(mesh.indices.len());
    let mut groups = Vec::with_capacity(lanes.len());
    for (material_slot, lane) in lanes {
        let start = u32::try_from(indices.len()).map_err(|_| "terrain index start exceeds u32")?;
        let count = u32::try_from(lane.len()).map_err(|_| "terrain index lane exceeds u32")?;
        indices.extend(lane);
        groups.push(MeshGroupDescriptor {
            material_slot,
            start,
            count,
        });
    }

    Ok(PresentationStreams {
        positions,
        normals,
        uvs,
        indices,
        groups,
    })
}

struct TriangleProjection {
    axis: usize,
    side_axis: usize,
    normal_y: f32,
    normal_magnitude: f32,
}

fn triangle_projection(mesh: &MeshPayload, triangle: &[u32]) -> Result<TriangleProjection, String> {
    let mut positions = [[0.0_f32; 3]; 3];
    for (corner, index) in triangle.iter().enumerate() {
        let vertex = usize::try_from(*index).map_err(|_| "terrain vertex index exceeds usize")?;
        positions[corner] = vertex3(&mesh.positions, vertex, "position")?;
    }
    let edge_a = subtract3(positions[1], positions[0]);
    let edge_b = subtract3(positions[2], positions[0]);
    let mut normal = cross3(edge_a, edge_b);
    let mut magnitude = magnitude3(normal);
    let mut source_normal = [0.0_f32; 3];
    for index in triangle {
        let vertex = usize::try_from(*index).map_err(|_| "terrain vertex index exceeds usize")?;
        let source = vertex3(&mesh.normals, vertex, "normal")?;
        for axis in 0..3 {
            source_normal[axis] += source[axis];
        }
    }
    if !magnitude.is_finite() || magnitude <= f32::EPSILON {
        normal = source_normal;
        magnitude = magnitude3(normal);
    }
    if !magnitude.is_finite() || magnitude <= f32::EPSILON {
        normal = [0.0, 1.0, 0.0];
        magnitude = 1.0;
    }
    normal = align_normal(normal, source_normal);
    let axis = dominant_axis(normal);
    let side_axis = if normal[0].abs() >= normal[2].abs() {
        0
    } else {
        2
    };
    Ok(TriangleProjection {
        axis,
        side_axis,
        normal_y: normal[1],
        normal_magnitude: magnitude,
    })
}

fn grass_material_slot(normal_y: f32, magnitude: f32) -> u16 {
    if normal_y > magnitude * 0.5 {
        1
    } else if normal_y < -magnitude * 0.5 {
        2
    } else {
        4
    }
}

fn align_normal(normal: [f32; 3], source_normal: [f32; 3]) -> [f32; 3] {
    if dot3(normal, source_normal) < 0.0 {
        normal.map(|component| -component)
    } else {
        normal
    }
}

fn dominant_axis(value: [f32; 3]) -> usize {
    (0..3)
        .max_by(|left, right| value[*left].abs().total_cmp(&value[*right].abs()))
        .unwrap_or(1)
}

fn dot3(left: [f32; 3], right: [f32; 3]) -> f32 {
    left.iter()
        .zip(right)
        .map(|(left, right)| left * right)
        .sum()
}

fn magnitude3(value: [f32; 3]) -> f32 {
    value
        .iter()
        .map(|component| component * component)
        .sum::<f32>()
        .sqrt()
}

fn subtract3(left: [f32; 3], right: [f32; 3]) -> [f32; 3] {
    [left[0] - right[0], left[1] - right[1], left[2] - right[2]]
}

fn add3(left: [f32; 3], right: [f32; 3]) -> [f32; 3] {
    [left[0] + right[0], left[1] + right[1], left[2] + right[2]]
}

fn cross3(left: [f32; 3], right: [f32; 3]) -> [f32; 3] {
    [
        left[1] * right[2] - left[2] * right[1],
        left[2] * right[0] - left[0] * right[2],
        left[0] * right[1] - left[1] * right[0],
    ]
}

fn project_position(position: [f32; 3], normal_axis: usize) -> [f32; 2] {
    match normal_axis {
        0 => [position[2], -position[1]],
        1 => [position[0], position[2]],
        _ => [position[0], -position[1]],
    }
}

fn vertex3(stream: &[f32], vertex: usize, label: &str) -> Result<[f32; 3], String> {
    let start = vertex
        .checked_mul(3)
        .ok_or_else(|| format!("terrain {label} offset overflowed"))?;
    let values = stream
        .get(start..start + 3)
        .ok_or_else(|| format!("terrain triangle references a missing {label}"))?;
    Ok([values[0], values[1], values[2]])
}

#[cfg(test)]
mod tests {
    use std::collections::BTreeSet;

    use super::*;
    use crate::{EditKind, EditOutcome, GameWorld, PlayerController, TerrainConfig};

    #[test]
    fn frame_contract_accepts_every_surface_mode() {
        for surface in SurfaceSelection::ALL {
            let world = GameWorld::new(surface).unwrap();
            TerrainProjector::new()
                .project(world.scene(), PLATFORM_INITIAL_CENTER.map(f64::from), true)
                .unwrap()
                .validate()
                .unwrap();
            telemetry_frame(surface).unwrap().validate().unwrap();
        }
    }

    #[test]
    fn accepted_edits_replace_only_stable_dirty_chunk_handles_in_every_mode() {
        for surface in SurfaceSelection::ALL {
            let mut world =
                GameWorld::with_terrain(surface, TerrainConfig::new(0x5eed, 32).unwrap()).unwrap();
            let mut projector = TerrainProjector::new();
            projector
                .project(world.scene(), PLATFORM_INITIAL_CENTER.map(f64::from), true)
                .unwrap();
            let handles = world
                .scene()
                .mesh_chunks()
                .iter()
                .map(|chunk| {
                    (
                        chunk.chunk,
                        projector
                            .inner
                            .chunk_handle(TERRAIN_INSTANCE, chunk.chunk)
                            .unwrap(),
                    )
                })
                .collect::<BTreeMap<_, _>>();
            let EditOutcome::Applied(receipt) = world
                .edit_from_view(
                    [0.5, 10.5, 7.5],
                    [0.0, -1.0, 0.0],
                    EditKind::Destroy,
                    0,
                    &PlayerController::default(),
                )
                .unwrap()
            else {
                panic!("expected a visible single-voxel edit")
            };
            let frame = projector
                .project(world.scene(), PLATFORM_INITIAL_CENTER.map(f64::from), false)
                .unwrap();
            let changed = frame
                .ops
                .iter()
                .filter_map(|operation| match operation {
                    RenderDiff::ReplaceMeshPayload { handle, .. }
                    | RenderDiff::Destroy { handle } => Some(*handle),
                    _ => None,
                })
                .collect::<BTreeSet<_>>();
            assert_eq!(
                changed.len(),
                receipt.rebuilt_chunks + receipt.removed_chunks
            );
            assert!(changed.len() < handles.len());
            for chunk in world.scene().mesh_chunks() {
                let handle = projector
                    .inner
                    .chunk_handle(TERRAIN_INSTANCE, chunk.chunk)
                    .unwrap();
                if let Some(previous) = handles.get(&chunk.chunk) {
                    assert_eq!(&handle, previous);
                }
            }
            assert!(handles.values().any(|handle| !changed.contains(handle)));
        }
    }

    #[test]
    fn textured_box_frame_splits_grass_top_and_keeps_side_texture_upright() {
        let world = GameWorld::new(SurfaceSelection::Box).unwrap();
        let (chunk, mesh, streams) = world
            .scene()
            .mesh_chunks()
            .iter()
            .find_map(|chunk| {
                let mesh = chunk_mesh(chunk);
                let streams = presentation_streams(&mesh, chunk.translation).ok()?;
                streams
                    .groups
                    .iter()
                    .any(|group| group.material_slot == 4)
                    .then_some((chunk, mesh, streams))
            })
            .expect("terrain chunk with a grass-side lane");
        let frame = TerrainProjector::new()
            .project(world.scene(), PLATFORM_INITIAL_CENTER.map(f64::from), true)
            .unwrap();
        frame.validate().unwrap();
        let payloads = frame
            .ops
            .iter()
            .filter_map(|operation| match operation {
                RenderDiff::ReplaceMeshPayload { payload, .. } => Some(payload),
                _ => None,
            })
            .collect::<Vec<_>>();
        assert!(payloads
            .iter()
            .any(|payload| payload.groups.iter().any(|group| group.material_slot == 1)));
        assert!(payloads
            .iter()
            .any(|payload| payload.groups.iter().any(|group| group.material_slot == 4)));
        let payload = mesh_descriptor(&mesh, chunk.translation).unwrap();
        let MeshPayloadSource::Inline { uvs, .. } = &payload.source else {
            panic!("terrain projection must remain inline")
        };
        let uvs = uvs.as_ref().expect("voxel tile coordinates");
        assert_eq!(uvs, &streams.uvs);
        assert!(uvs.iter().any(|coordinate| coordinate.abs() > 1.0));
        let side_group = streams
            .groups
            .iter()
            .find(|group| group.material_slot == 4)
            .expect("grass-side lane");
        let start = side_group.start as usize;
        let end = start + side_group.count as usize;
        for index in &streams.indices[start..end] {
            let vertex = *index as usize;
            assert_eq!(
                streams.uvs[vertex * 2 + 1],
                -streams.positions[vertex * 3 + 1]
            );
        }
    }

    #[test]
    fn reconstructed_modes_receive_complete_finite_world_space_uvs() {
        for surface in [
            SurfaceSelection::MarchingCubes,
            SurfaceSelection::DualContouring,
        ] {
            let world = GameWorld::new(surface).unwrap();
            let payloads = world
                .scene()
                .mesh_chunks()
                .iter()
                .map(|chunk| mesh_descriptor(&chunk_mesh(chunk), chunk.translation).unwrap())
                .collect::<Vec<_>>();
            for payload in &payloads {
                let MeshPayloadSource::Inline {
                    positions,
                    uvs,
                    indices,
                    ..
                } = &payload.source
                else {
                    panic!("terrain projection must remain inline")
                };
                let uvs = uvs.as_ref().expect("reconstructed presentation UVs");
                assert_eq!(uvs.len(), positions.len() / 3 * 2);
                assert_eq!(indices.len(), positions.len() / 3);
                assert!(uvs.iter().all(|value| value.is_finite()));
            }
            assert!(payloads
                .iter()
                .any(|payload| payload.groups.iter().any(|group| group.material_slot == 1)));
            assert!(payloads
                .iter()
                .any(|payload| payload.groups.iter().any(|group| group.material_slot == 4)));
        }
    }

    #[test]
    fn geometric_triangle_projection_does_not_follow_smoothed_vertex_normals() {
        let normal = cross3(
            subtract3([1.0, 2.0, 0.0], [0.0, 0.0, 0.0]),
            subtract3([0.0, 3.0, 0.0], [0.0, 0.0, 0.0]),
        );
        let axis = (0..3)
            .max_by(|left, right| normal[*left].abs().total_cmp(&normal[*right].abs()))
            .unwrap();
        assert_eq!(axis, 2);
        assert_eq!(project_position([1.0, 2.0, 0.0], axis), [1.0, -2.0]);
    }

    #[test]
    fn vertical_face_uvs_keep_world_y_as_the_texture_vertical_axis() {
        let position = [3.0, 7.0, 11.0];
        for normal in [[1.0, 0.0, 0.0], [-1.0, 0.0, 0.0]] {
            let uv = project_position(position, dominant_axis(normal));
            assert_eq!(uv[1], -position[1]);
        }
        for normal in [[0.0, 0.0, 1.0], [0.0, 0.0, -1.0]] {
            let uv = project_position(position, dominant_axis(normal));
            assert_eq!(uv[1], -position[1]);
        }
    }

    #[test]
    fn grass_side_projection_never_uses_the_horizontal_plane() {
        for normal in [
            [0.8_f32, 0.6, 0.1],
            [-0.8, 0.6, 0.1],
            [0.1, 0.6, 0.8],
            [0.1, 0.6, -0.8],
        ] {
            let side_axis = if normal[0].abs() >= normal[2].abs() {
                0
            } else {
                2
            };
            let uv = project_position([3.0, 7.0, 11.0], side_axis);
            assert_eq!(uv[1], -7.0);
        }
    }

    #[test]
    fn grass_surface_assignment_keeps_dirt_below_and_is_winding_invariant() {
        assert_eq!(grass_material_slot(1.0, 1.0), 1);
        assert_eq!(grass_material_slot(0.0, 1.0), 4);
        assert_eq!(grass_material_slot(-1.0, 1.0), 2);
        let source = [0.0, 1.0, 0.0];
        assert_eq!(align_normal([0.0, 1.0, 0.0], source), source);
        assert_eq!(align_normal([0.0, -1.0, 0.0], source), source);
    }
}
