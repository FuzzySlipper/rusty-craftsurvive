use std::collections::BTreeMap;

use rusty_engine::{
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
    svc_mesh::{MeshPayload, SurfaceMode},
};

use crate::{terrain_texture::terrain_material_ops, SurfaceSelection};

const TERRAIN_HANDLE: RenderHandle = RenderHandle::new(1);

pub fn initial_frame(mesh: &MeshPayload) -> Result<RenderFrameDiff, String> {
    let mut operations = terrain_material_ops()?;
    operations.extend([
        RenderDiff::Create {
            handle: TERRAIN_HANDLE,
            parent: None,
            node: RenderNode {
                geometry: Geometry::Cube,
                material: Material {
                    color: [1.0; 4],
                    wireframe: false,
                },
                transform: Transform::IDENTITY,
                visible: true,
                layer: RenderLayer::Scene,
                metadata: RenderMetadata {
                    source_entity: None,
                    source_scene_node: None,
                    tags: vec!["craftsurvive-terrain".to_owned()],
                    label: Some("Rust-authoritative island terrain".to_owned()),
                },
            },
        },
        RenderDiff::ReplaceMeshPayload {
            handle: TERRAIN_HANDLE,
            payload: mesh_descriptor(mesh)?,
        },
    ]);
    RenderFrameDiff::try_from_ops(operations)
        .map_err(|error| format!("build initial render frame: {error:?}"))
}

pub fn replacement_frame(mesh: &MeshPayload) -> Result<RenderFrameDiff, String> {
    RenderFrameDiff::try_from_ops(vec![RenderDiff::ReplaceMeshPayload {
        handle: TERRAIN_HANDLE,
        payload: mesh_descriptor(mesh)?,
    }])
    .map_err(|error| format!("build replacement render frame: {error:?}"))
}

pub fn telemetry_frame(surface: SurfaceSelection) -> Result<PresentationFrameDiff, String> {
    PresentationFrameDiff::try_from_ops(vec![PresentationOp::TelemetryOverlay {
        meta: PresentationOpMeta::new(0),
        op: TelemetryOverlayProjectionOp::Create {
            handle: TelemetryOverlayHandle::new(1),
            descriptor: TelemetryOverlayDescriptor {
                title: format!(
                    "CraftSurvive [{}] | WASD move | Space jump | arrows look | LMB/F break | RMB/G place",
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

fn mesh_descriptor(mesh: &MeshPayload) -> Result<MeshPayloadDescriptor, String> {
    let streams = presentation_streams(mesh)?;
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

fn presentation_streams(mesh: &MeshPayload) -> Result<PresentationStreams, String> {
    if mesh.surface_mode != SurfaceMode::GreedyCubes {
        return reconstructed_presentation_streams(mesh);
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
            let slot = if group.material_slot == 1 && !quad_faces_up(mesh, quad)? {
                4
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
        uvs: mesh.tile_coordinates.clone(),
        indices,
        groups,
    })
}

fn quad_faces_up(mesh: &MeshPayload, quad: &[u32]) -> Result<bool, String> {
    let vertex = usize::try_from(quad[0]).map_err(|_| "terrain vertex index exceeds usize")?;
    let normal_y = mesh
        .normals
        .get(vertex * 3 + 1)
        .ok_or_else(|| "terrain quad references a missing normal".to_owned())?;
    Ok(*normal_y > 0.9)
}

fn reconstructed_presentation_streams(mesh: &MeshPayload) -> Result<PresentationStreams, String> {
    let mut positions = Vec::with_capacity(mesh.indices.len() * 3);
    let mut normals = Vec::with_capacity(mesh.indices.len() * 3);
    let mut uvs = Vec::with_capacity(mesh.indices.len() * 2);
    let mut indices = Vec::with_capacity(mesh.indices.len());
    let mut groups = Vec::with_capacity(mesh.groups.len());

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
        let start = u32::try_from(indices.len()).map_err(|_| "terrain index start exceeds u32")?;
        for triangle in source.chunks_exact(3) {
            let projection_axis = triangle_projection_axis(mesh, triangle)?;
            for source_index in triangle {
                let source_vertex = usize::try_from(*source_index)
                    .map_err(|_| "terrain vertex index exceeds usize")?;
                let position = vertex3(&mesh.positions, source_vertex, "position")?;
                let normal = vertex3(&mesh.normals, source_vertex, "normal")?;
                let target_index = u32::try_from(positions.len() / 3)
                    .map_err(|_| "expanded terrain vertex count exceeds u32")?;
                positions.extend_from_slice(&position);
                normals.extend_from_slice(&normal);
                uvs.extend_from_slice(&project_position(position, projection_axis));
                indices.push(target_index);
            }
        }
        groups.push(MeshGroupDescriptor {
            material_slot: group.material_slot,
            start,
            count: u32::try_from(source.len()).map_err(|_| "terrain group count exceeds u32")?,
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

fn triangle_projection_axis(mesh: &MeshPayload, triangle: &[u32]) -> Result<usize, String> {
    let mut normal = [0.0_f32; 3];
    for index in triangle {
        let vertex = usize::try_from(*index).map_err(|_| "terrain vertex index exceeds usize")?;
        let source = vertex3(&mesh.normals, vertex, "normal")?;
        for axis in 0..3 {
            normal[axis] += source[axis];
        }
    }
    Ok((0..3)
        .max_by(|left, right| normal[*left].abs().total_cmp(&normal[*right].abs()))
        .unwrap_or(1))
}

fn project_position(position: [f32; 3], normal_axis: usize) -> [f32; 2] {
    match normal_axis {
        0 => [position[2], position[1]],
        1 => [position[0], position[2]],
        _ => [position[0], position[1]],
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
    use super::*;
    use crate::GameWorld;

    #[test]
    fn frame_contract_accepts_every_surface_mode() {
        for surface in SurfaceSelection::ALL {
            let world = GameWorld::new(surface).unwrap();
            initial_frame(world.presentation_mesh())
                .unwrap()
                .validate()
                .unwrap();
            telemetry_frame(surface).unwrap().validate().unwrap();
        }
    }

    #[test]
    fn textured_box_frame_splits_grass_top_and_side_and_retains_tile_coordinates() {
        let world = GameWorld::new(SurfaceSelection::Box).unwrap();
        let frame = initial_frame(world.presentation_mesh()).unwrap();
        frame.validate().unwrap();
        let payload = frame
            .ops
            .iter()
            .find_map(|operation| match operation {
                RenderDiff::ReplaceMeshPayload { payload, .. } => Some(payload),
                _ => None,
            })
            .unwrap();
        assert!(payload.groups.iter().any(|group| group.material_slot == 1));
        assert!(payload.groups.iter().any(|group| group.material_slot == 4));
        let MeshPayloadSource::Inline { uvs, .. } = &payload.source else {
            panic!("terrain projection must remain inline")
        };
        let uvs = uvs.as_ref().expect("voxel tile coordinates");
        assert_eq!(uvs, &world.presentation_mesh().tile_coordinates);
        assert!(uvs.iter().any(|coordinate| coordinate.abs() > 1.0));
    }

    #[test]
    fn reconstructed_modes_receive_complete_finite_world_space_uvs() {
        for surface in [
            SurfaceSelection::MarchingCubes,
            SurfaceSelection::DualContouring,
        ] {
            let world = GameWorld::new(surface).unwrap();
            let frame = initial_frame(world.presentation_mesh()).unwrap();
            let payload = frame
                .ops
                .iter()
                .find_map(|operation| match operation {
                    RenderDiff::ReplaceMeshPayload { payload, .. } => Some(payload),
                    _ => None,
                })
                .unwrap();
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
    }
}
