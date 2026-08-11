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
    svc_mesh::MeshPayload,
};

use crate::SurfaceSelection;

const TERRAIN_HANDLE: RenderHandle = RenderHandle::new(1);

pub fn initial_frame(mesh: &MeshPayload) -> Result<RenderFrameDiff, String> {
    RenderFrameDiff::try_from_ops(vec![
        RenderDiff::Create {
            handle: TERRAIN_HANDLE,
            parent: None,
            node: RenderNode {
                geometry: Geometry::Cube,
                material: Material {
                    color: [0.28, 0.68, 0.31, 1.0],
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
    ])
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
                    "CraftSurvive [{}] | WASD + Space/Shift | arrows look | LMB/F break | RMB/G place",
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
    let vertex_count = u32::try_from(mesh.positions.len() / 3)
        .map_err(|_| "terrain vertex count exceeds u32".to_owned())?;
    let index_count = u32::try_from(mesh.indices.len())
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
            ],
        },
        groups: mesh
            .groups
            .iter()
            .map(|group| MeshGroupDescriptor {
                material_slot: group.material_slot,
                start: group.start,
                count: group.count,
            })
            .collect(),
        bounds: MeshBoundsDescriptor {
            min: mesh.bounds.min,
            max: mesh.bounds.max,
        },
        source: MeshPayloadSource::Inline {
            positions: mesh.positions.clone(),
            normals: mesh.normals.clone(),
            uvs: None,
            indices: mesh.indices.clone(),
        },
        provenance: MeshProvenance::VoxelChunk,
    })
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
}
