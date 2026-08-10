use rusty_engine::{
    engine_spatial::{
        VoxelCollisionScene, VoxelEditService, VoxelEditTransaction, VoxelPickHint,
        VoxelPickService,
    },
    svc_mesh::{
        mesh_cells_standalone_with_options, MeshPayload, MeshVoxelCell, SurfaceMeshOptions,
    },
};

use crate::{generate_island, IslandConfig, SurfaceSelection};

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EditKind {
    Destroy,
    Place { material_slot: u16 },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EditReceipt {
    pub voxel: [i64; 3],
    pub revision: u64,
    pub authority_hash: u64,
    pub voxel_count: usize,
}

pub struct GameWorld {
    scene: VoxelCollisionScene,
    surface: SurfaceSelection,
    presentation_mesh: MeshPayload,
}

impl GameWorld {
    pub fn new(surface: SurfaceSelection) -> Result<Self, String> {
        let scene = VoxelCollisionScene::from_material_voxels(
            1.0,
            16,
            generate_island(IslandConfig::default()),
        )
        .map_err(|error| format!("build island authority: {error}"))?;
        let presentation_mesh = build_presentation_mesh(&scene, surface)?;
        Ok(Self {
            scene,
            surface,
            presentation_mesh,
        })
    }

    pub const fn scene(&self) -> &VoxelCollisionScene {
        &self.scene
    }

    pub const fn surface(&self) -> SurfaceSelection {
        self.surface
    }

    pub const fn presentation_mesh(&self) -> &MeshPayload {
        &self.presentation_mesh
    }

    pub fn edit_from_view(
        &mut self,
        origin: [f64; 3],
        direction: [f64; 3],
        kind: EditKind,
    ) -> Result<Option<EditReceipt>, String> {
        let Some(hit) = self.scene.raycast(origin, direction, 8.0) else {
            return Ok(None);
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
        let expected_revision = self.scene.source_revision();
        let receipt = VoxelEditService::apply(
            &mut self.scene,
            VoxelEditTransaction {
                expected_revision,
                edits: &[edit],
            },
        )
        .map_err(|error| format!("apply coherent voxel edit: {error}"))?;
        if !receipt
            .projections
            .is_coherent_with(receipt.accepted_revision)
        {
            return Err("Engine accepted an incoherent voxel projection revision".to_owned());
        }
        self.presentation_mesh = build_presentation_mesh(&self.scene, self.surface)?;
        Ok(Some(EditReceipt {
            voxel: edited_voxel,
            revision: receipt.accepted_revision.raw(),
            authority_hash: receipt.authority_hash,
            voxel_count: receipt.solid_voxel_count,
        }))
    }
}

fn build_presentation_mesh(
    scene: &VoxelCollisionScene,
    surface: SurfaceSelection,
) -> Result<MeshPayload, String> {
    let cells = scene
        .material_voxels()
        .iter()
        .map(|voxel| MeshVoxelCell {
            coordinate: voxel.address,
            material_slot: voxel.material_slot,
        })
        .collect::<Vec<_>>();
    mesh_cells_standalone_with_options(
        scene.voxel_size(),
        [0.0; 3],
        &cells,
        SurfaceMeshOptions {
            mode: surface.engine_mode(),
            ..SurfaceMeshOptions::default()
        },
    )
    .map_err(|error| format!("build {} presentation mesh: {error}", surface.as_str()))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_surface_modes_are_distinct_derived_paths_over_one_authority_shape() {
        let meshes = SurfaceSelection::ALL.map(|surface| GameWorld::new(surface).unwrap());
        let hashes = meshes
            .each_ref()
            .map(|world| world.scene().authority_hash());
        assert_eq!(hashes[0], hashes[1]);
        assert_eq!(hashes[1], hashes[2]);
        assert_eq!(meshes[0].presentation_mesh().surface_mode, surface_mode(0));
        assert_eq!(meshes[1].presentation_mesh().surface_mode, surface_mode(1));
        assert_eq!(meshes[2].presentation_mesh().surface_mode, surface_mode(2));
        assert_ne!(
            meshes[0].presentation_mesh().stats.triangles,
            meshes[1].presentation_mesh().stats.triangles
        );
    }

    fn surface_mode(index: usize) -> rusty_engine::svc_mesh::SurfaceMode {
        SurfaceSelection::ALL[index].engine_mode()
    }

    #[test]
    fn destroy_and_place_advance_one_coherent_authority() {
        let mut world = GameWorld::new(SurfaceSelection::Box).unwrap();
        let before = world.scene().source_revision();
        let origin = [0.5, 12.0, 0.5];
        let direction = [0.0, -1.0, 0.0];
        let removed = world
            .edit_from_view(origin, direction, EditKind::Destroy)
            .unwrap()
            .unwrap();
        assert!(removed.revision > before.raw());
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(world.scene().source_revision()));
        let placed = world
            .edit_from_view(origin, direction, EditKind::Place { material_slot: 1 })
            .unwrap()
            .unwrap();
        assert!(placed.revision > removed.revision);
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(world.scene().source_revision()));
    }
}
