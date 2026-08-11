use rusty_engine::{
    engine_spatial::{
        VoxelCollisionScene, VoxelEditDelta, VoxelEditService, VoxelEditTransaction, VoxelPickHint,
        VoxelPickService,
    },
    svc_mesh::{
        mesh_cells_standalone_with_options, MeshPayload, MeshVoxelCell, SurfaceMeshOptions,
    },
};

use crate::{generate_island, IslandConfig, PlayerController, SurfaceSelection};

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

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EditRejection {
    PlayerOverlap { voxel: [i64; 3] },
}

impl EditRejection {
    pub const fn code(self) -> &'static str {
        match self {
            Self::PlayerOverlap { .. } => "playerOverlap",
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum EditOutcome {
    Miss,
    Rejected(EditRejection),
    Applied(EditReceipt),
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
        player: &PlayerController,
    ) -> Result<EditOutcome, String> {
        self.edit_from_view_with_mesh_builder(
            origin,
            direction,
            kind,
            player,
            build_presentation_mesh_from_cells,
        )
    }

    fn edit_from_view_with_mesh_builder(
        &mut self,
        origin: [f64; 3],
        direction: [f64; 3],
        kind: EditKind,
        player: &PlayerController,
        mesh_builder: impl FnOnce(
            &[MeshVoxelCell],
            f64,
            SurfaceSelection,
        ) -> Result<MeshPayload, String>,
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
        let expected_revision = self.scene.source_revision();
        let prepared = VoxelEditService::preview(
            &self.scene,
            VoxelEditTransaction {
                expected_revision,
                edits: &[edit],
            },
        )
        .map_err(|error| format!("apply coherent voxel edit: {error}"))?;
        if matches!(kind, EditKind::Place { .. })
            && player.overlaps_voxel(edited_voxel, self.scene.voxel_size())
        {
            return Ok(EditOutcome::Rejected(EditRejection::PlayerOverlap {
                voxel: edited_voxel,
            }));
        }
        let candidate_cells = candidate_mesh_cells(&self.scene, prepared.deltas());
        let presentation_mesh =
            mesh_builder(&candidate_cells, self.scene.voxel_size(), self.surface)?;
        let receipt = VoxelEditService::commit(&mut self.scene, prepared)
            .map_err(|error| format!("commit coherent voxel edit: {error}"))?;
        if !receipt
            .projections
            .is_coherent_with(receipt.accepted_revision)
        {
            return Err("Engine accepted an incoherent voxel projection revision".to_owned());
        }
        self.presentation_mesh = presentation_mesh;
        Ok(EditOutcome::Applied(EditReceipt {
            voxel: edited_voxel,
            revision: receipt.accepted_revision.raw(),
            authority_hash: receipt.authority_hash,
            voxel_count: receipt.solid_voxel_count,
        }))
    }
}

fn candidate_mesh_cells(
    scene: &VoxelCollisionScene,
    deltas: &[VoxelEditDelta],
) -> Vec<MeshVoxelCell> {
    let mut materials = scene
        .material_voxels()
        .iter()
        .map(|voxel| (voxel.address, voxel.material_slot))
        .collect::<std::collections::BTreeMap<_, _>>();
    for delta in deltas {
        match delta.after_material {
            Some(material_slot) => {
                materials.insert(delta.address, material_slot);
            }
            None => {
                materials.remove(&delta.address);
            }
        }
    }
    materials
        .into_iter()
        .map(|(coordinate, material_slot)| MeshVoxelCell {
            coordinate,
            material_slot,
        })
        .collect()
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
    build_presentation_mesh_from_cells(&cells, scene.voxel_size(), surface)
}

fn build_presentation_mesh_from_cells(
    cells: &[MeshVoxelCell],
    voxel_size: f64,
    surface: SurfaceSelection,
) -> Result<MeshPayload, String> {
    mesh_cells_standalone_with_options(
        voxel_size,
        [0.0; 3],
        cells,
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
    use crate::{PlayerInput, PlayerPose};

    const STEP_SECONDS: f64 = 1.0 / 120.0;

    fn test_world(
        surface: SurfaceSelection,
        voxels: impl IntoIterator<Item = [i64; 3]>,
    ) -> GameWorld {
        let scene = VoxelCollisionScene::from_material_voxels(
            1.0,
            16,
            voxels
                .into_iter()
                .map(|address| rusty_engine::engine_spatial::MaterialVoxel {
                    address,
                    material_slot: 1,
                }),
        )
        .unwrap();
        let presentation_mesh = build_presentation_mesh(&scene, surface).unwrap();
        GameWorld {
            scene,
            surface,
            presentation_mesh,
        }
    }

    fn player(position: [f64; 3], yaw_degrees: f64) -> PlayerController {
        let mut player = PlayerController::new(PlayerPose {
            position,
            yaw_degrees,
            pitch_degrees: 0.0,
        });
        player.step(
            &VoxelCollisionScene::from_solid_voxels(1.0, 16, [[0, 0, 0]]).unwrap(),
            PlayerInput::default(),
            STEP_SECONDS,
        );
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
        let player = PlayerController::default();
        let removed = applied(
            world
                .edit_from_view(origin, direction, EditKind::Destroy, &player)
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
                    &player,
                )
                .unwrap(),
        );
        player.step(world.scene(), PlayerInput::default(), STEP_SECONDS);
        assert!(!player.motion().grounded);
        for _ in 0..240 {
            player.step(world.scene(), PlayerInput::default(), STEP_SECONDS);
        }
        assert!(player.motion().grounded);
        assert!(player.pose().position[1] < 0.65);
        assert!(!crate::player::collides(
            world.scene(),
            player.pose().position
        ));
    }

    #[test]
    fn placement_overlapping_player_is_typed_rejection_without_mutation() {
        for surface in SurfaceSelection::ALL {
            let mut world = test_world(surface, [[0, 0, 0]]);
            let player = player([0.5, 2.55, 0.5], 0.0);
            let revision = world.scene().source_revision();
            let hash = world.scene().authority_hash();
            let positions = world.presentation_mesh().positions.clone();

            let outcome = world
                .edit_from_view(
                    player.pose().position,
                    [0.0, -1.0, 0.0],
                    EditKind::Place { material_slot: 1 },
                    &player,
                )
                .unwrap();
            assert_eq!(
                outcome,
                EditOutcome::Rejected(EditRejection::PlayerOverlap { voxel: [0, 1, 0] })
            );
            assert_eq!(world.scene().source_revision(), revision);
            assert_eq!(world.scene().authority_hash(), hash);
            assert_eq!(world.presentation_mesh().positions, positions);
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
                        &walker,
                    )
                    .unwrap(),
            );
            for _ in 0..15 {
                walker.step(
                    world.scene(),
                    PlayerInput {
                        forward: 1.0,
                        ..PlayerInput::default()
                    },
                    STEP_SECONDS,
                );
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
                        &blocked,
                    )
                    .unwrap(),
            );
            assert!(world
                .scene()
                .projection_revisions()
                .is_coherent_with(world.scene().source_revision()));
            for _ in 0..15 {
                blocked.step(
                    world.scene(),
                    PlayerInput {
                        forward: 1.0,
                        ..PlayerInput::default()
                    },
                    STEP_SECONDS,
                );
            }
            assert!(blocked.pose().position[0] < 0.71);
            observations.push((opened_x, blocked.pose().position[0], placed.revision));
        }
        assert_eq!(observations[0], observations[1]);
        assert_eq!(observations[1], observations[2]);
    }

    #[test]
    fn invalid_edit_or_failed_presentation_build_preserves_prior_coherent_state() {
        let mut world = test_world(SurfaceSelection::Box, floor_with([[1, 2, 0]]));
        let mut player = player([0.0, 2.55, 0.5], 90.0);
        let revision = world.scene().source_revision();
        let hash = world.scene().authority_hash();
        let positions = world.presentation_mesh().positions.clone();

        assert!(world
            .edit_from_view(
                player.pose().position,
                [1.0, 0.0, 0.0],
                EditKind::Place { material_slot: 0 },
                &player,
            )
            .is_err());
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);

        let failed = world.edit_from_view_with_mesh_builder(
            player.pose().position,
            [1.0, 0.0, 0.0],
            EditKind::Destroy,
            &player,
            |_, _, _| Err("injected presentation failure".to_owned()),
        );
        assert_eq!(failed.unwrap_err(), "injected presentation failure");
        assert_eq!(world.scene().source_revision(), revision);
        assert_eq!(world.scene().authority_hash(), hash);
        assert_eq!(world.presentation_mesh().positions, positions);
        assert!(world
            .scene()
            .projection_revisions()
            .is_coherent_with(revision));
        for _ in 0..15 {
            player.step(
                world.scene(),
                PlayerInput {
                    forward: 1.0,
                    ..PlayerInput::default()
                },
                STEP_SECONDS,
            );
        }
        assert!(player.pose().position[0] < 0.71);
    }
}
