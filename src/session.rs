use std::collections::BTreeSet;

use rusty_engine::{
    render_host_contracts::RendererCameraPose,
    render_model::{RenderDiff, RenderFrameDiff},
    render_presentation::PresentationFrameDiff,
};
use serde::{Deserialize, Serialize};

use crate::{
    block_break_debris_frame, platform_frame, terrain_texture_resource, wisp_texture_resource,
    EditKind, EditOutcome, EditReceipt, EditRejection, GameWorld, PlayerController, PlayerInput,
    PlayerPose, SurfaceSelection, TerrainConfig, TerrainProjector, MAX_BRUSH_RADIUS,
    TERRAIN_GENERATION_VERSION,
};

pub const SESSION_PROTOCOL_VERSION: u32 = 6;
pub const MAX_SESSION_MESSAGE_BYTES: usize = 16 * 1024;
pub const MAX_LOOK_DELTA_DEGREES: f64 = 45.0;
pub const MAX_INPUT_DELTA_SECONDS: f64 = 0.05;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SessionAction {
    Destroy,
    Place,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SpawnSelection {
    Route,
    MovingPlatform,
    StreamingWest,
    FarPositive,
    FarNegative,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionCommand {
    pub movement: [f64; 2],
    pub jump: bool,
    pub crouch: bool,
    pub sprint: bool,
    pub impulse: bool,
    pub look_delta_degrees: [f64; 2],
    pub delta_seconds: f64,
    pub action: Option<SessionAction>,
    pub brush_radius: u8,
}

impl Default for SessionCommand {
    fn default() -> Self {
        Self {
            movement: [0.0; 2],
            jump: false,
            crouch: false,
            sprint: false,
            impulse: false,
            look_delta_degrees: [0.0; 2],
            delta_seconds: 0.0,
            action: None,
            brush_radius: 0,
        }
    }
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(
    tag = "kind",
    rename_all = "camelCase",
    rename_all_fields = "camelCase",
    deny_unknown_fields
)]
pub enum ClientMessage {
    Input {
        protocol_version: u32,
        generation: u64,
        sequence: u64,
        command: SessionCommand,
    },
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionReadout {
    pub protocol_version: u32,
    pub generation: u64,
    pub connected: bool,
    pub accepted_sequence: u64,
    pub player_revision: u64,
    pub player: PlayerPoseReadout,
    pub player_local_position: [f64; 3],
    pub world_origin: [i64; 3],
    pub world_origin_revision: u64,
    pub local_coordinate_envelope: f64,
    pub grounded: bool,
    pub velocity: [f64; 3],
    pub stance: &'static str,
    pub blocked_stand: bool,
    pub ground_normal: Option<[f64; 3]>,
    pub ground_source: Option<&'static str>,
    pub contact_count: usize,
    pub blocks: Vec<&'static str>,
    pub step_attempted: bool,
    pub step_accepted: bool,
    pub step_rise: f64,
    pub platform_entity: Option<u64>,
    pub platform_displacement: [f64; 3],
    pub collision_world_hash: u64,
    pub cast_count: u16,
    pub recovery_passes: u8,
    pub camera: RendererCameraPose,
    pub surface: SurfaceSelectionReadout,
    pub world_revision: u64,
    pub authority_hash: u64,
    pub voxel_count: usize,
    pub targeted_voxel: Option<[i64; 3]>,
    pub brush_radius: u8,
    pub terrain_seed: String,
    pub terrain_size: u16,
    pub mesh_vertices: u32,
    pub mesh_triangles: u32,
    pub generation_ms: f64,
    pub authority_build_ms: f64,
    pub mesh_build_ms: f64,
    pub residency_center: [i64; 2],
    pub requested_chunks: usize,
    pub preparing_chunks: usize,
    pub resident_chunks: usize,
    pub pinned_chunks: usize,
    pub evictable_chunks: usize,
    pub admitted_chunks_total: u64,
    pub evicted_chunks_total: u64,
    pub residency_cache_hits: u64,
    pub residency_missed_deadlines: u64,
    pub resident_chunk_bytes: usize,
    pub residency_generation_ms: f64,
    pub residency_admission_ms: f64,
    pub residency_request_generation: u64,
    pub terrain_generation_version: u32,
    pub edit_overlay_entries: usize,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct PlayerPoseReadout {
    pub position: [f64; 3],
    pub yaw_degrees: f64,
    pub pitch_degrees: f64,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SurfaceSelectionReadout {
    Box,
    MarchingCubes,
    DualContouring,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(tag = "kind", rename_all = "camelCase")]
pub enum ServerMessage {
    Welcome {
        readout: SessionReadout,
        frame: RenderFrameDiff,
        resources: Vec<SessionResourceReadout>,
    },
    Update {
        update: Box<SessionUpdate>,
    },
    Rejected {
        code: &'static str,
        message: String,
        readout: SessionReadout,
    },
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionResourceReadout {
    pub identity: String,
    pub content_hash: String,
    pub media_type: &'static str,
    pub url: &'static str,
}

pub fn session_resources() -> Result<Vec<SessionResourceReadout>, String> {
    let terrain = terrain_texture_resource()?;
    let wisp = wisp_texture_resource()?;
    Ok(vec![
        SessionResourceReadout {
            identity: terrain.identity,
            content_hash: terrain.content_hash,
            media_type: terrain.media_type,
            url: terrain.url,
        },
        SessionResourceReadout {
            identity: wisp.identity,
            content_hash: wisp.content_hash,
            media_type: wisp.media_type,
            url: wisp.url,
        },
    ])
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionUpdate {
    pub readout: SessionReadout,
    pub action: Option<SessionAction>,
    pub edit: Option<SessionEditReadout>,
    pub edit_rejection: Option<SessionEditRejectionReadout>,
    pub frame: Option<RenderFrameDiff>,
    pub presentation: Option<PresentationFrameDiff>,
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionEditReadout {
    pub action: SessionAction,
    pub voxel: [i64; 3],
    pub revision: u64,
    pub affected_voxels: usize,
    pub authority_hash: u64,
    pub voxel_count: usize,
    pub mesh_build_ms: f64,
    pub edit_ms: f64,
    pub dirty_chunks: usize,
    pub rebuilt_chunks: usize,
    pub reused_chunks: usize,
    pub removed_chunks: usize,
    pub frame_operations: usize,
    pub encoded_bytes: usize,
    pub replacement_count: usize,
    pub destroy_count: usize,
    pub changed_handles: Vec<u64>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionEditRejectionReadout {
    pub code: &'static str,
    pub voxel: [i64; 3],
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionError {
    UnsupportedProtocol { actual: u32 },
    NotConnected,
    StaleGeneration { expected: u64, actual: u64 },
    StaleSequence { accepted: u64, actual: u64 },
    InvalidCommand,
    SessionExhausted,
}

impl SessionError {
    pub const fn code(self) -> &'static str {
        match self {
            Self::UnsupportedProtocol { .. } => "unsupportedProtocol",
            Self::NotConnected => "notConnected",
            Self::StaleGeneration { .. } => "staleGeneration",
            Self::StaleSequence { .. } => "staleSequence",
            Self::InvalidCommand => "invalidCommand",
            Self::SessionExhausted => "sessionExhausted",
        }
    }
}

impl std::fmt::Display for SessionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "session command rejected: {self:?}")
    }
}

impl std::error::Error for SessionError {}

pub struct GameSession {
    world: GameWorld,
    terrain_projector: TerrainProjector,
    player: PlayerController,
    generation: u64,
    connected: bool,
    accepted_sequence: u64,
    brush_radius: u8,
}

impl GameSession {
    pub fn new(surface: SurfaceSelection) -> Result<Self, String> {
        Self::with_terrain(surface, TerrainConfig::default())
    }

    pub fn with_terrain(surface: SurfaceSelection, terrain: TerrainConfig) -> Result<Self, String> {
        Self::with_terrain_and_spawn(surface, terrain, SpawnSelection::Route)
    }

    pub fn with_terrain_and_spawn(
        surface: SurfaceSelection,
        terrain: TerrainConfig,
        spawn: SpawnSelection,
    ) -> Result<Self, String> {
        let player = match spawn {
            SpawnSelection::Route => PlayerController::default(),
            SpawnSelection::MovingPlatform => PlayerController::new(PlayerPose {
                position: [0.0, 6.05, 9.0],
                yaw_degrees: 180.0,
                pitch_degrees: -10.0,
            })?,
            SpawnSelection::StreamingWest => PlayerController::new(PlayerPose {
                position: [-35.0, 7.0, -2.0],
                yaw_degrees: 90.0,
                pitch_degrees: -15.0,
            })?,
            SpawnSelection::FarPositive => PlayerController::new(PlayerPose {
                position: [262_080.0, 7.0, -2.0],
                yaw_degrees: 90.0,
                pitch_degrees: -15.0,
            })?,
            SpawnSelection::FarNegative => PlayerController::new(PlayerPose {
                position: [-262_080.0, 7.0, -2.0],
                yaw_degrees: 90.0,
                pitch_degrees: -15.0,
            })?,
        };
        Ok(Self {
            world: GameWorld::with_terrain(surface, terrain)?,
            terrain_projector: TerrainProjector::new(),
            player,
            generation: 0,
            connected: false,
            accepted_sequence: 0,
            brush_radius: 0,
        })
    }

    pub fn connect(&mut self) -> Result<(SessionReadout, RenderFrameDiff), String> {
        self.generation = self
            .generation
            .checked_add(1)
            .ok_or_else(|| SessionError::SessionExhausted.to_string())?;
        self.connected = true;
        self.accepted_sequence = 0;
        self.terrain_projector = TerrainProjector::new();
        self.world.rebase_player(&mut self.player)?;
        self.world.sync_residency(self.player.pose().position)?;
        let frame = self.terrain_projector.project(
            self.world.scene(),
            self.player.platform_position(),
            true,
        )?;
        Ok((self.readout(), frame))
    }

    pub fn load_overlay_bytes(&mut self, bytes: &[u8]) -> Result<(), String> {
        if self.connected {
            return Err(
                "cannot replace the terrain overlay while a session is connected".to_owned(),
            );
        }
        self.world.load_overlay_bytes(bytes)
    }

    pub fn overlay_bytes(&self) -> Result<Vec<u8>, String> {
        self.world
            .overlay_bytes()
            .map_err(|error| error.to_string())
    }

    pub fn overlay_entry_count(&self) -> usize {
        self.world.overlay_entry_count()
    }

    pub fn disconnect(&mut self, generation: u64) -> bool {
        if self.connected && self.generation == generation {
            self.connected = false;
            true
        } else {
            false
        }
    }

    pub fn submit(
        &mut self,
        message: ClientMessage,
    ) -> Result<SessionUpdate, SessionErrorOrRuntime> {
        let ClientMessage::Input {
            protocol_version,
            generation,
            sequence,
            command,
        } = message;
        if protocol_version != SESSION_PROTOCOL_VERSION {
            return Err(SessionError::UnsupportedProtocol {
                actual: protocol_version,
            }
            .into());
        }
        if !self.connected {
            return Err(SessionError::NotConnected.into());
        }
        if generation != self.generation {
            return Err(SessionError::StaleGeneration {
                expected: self.generation,
                actual: generation,
            }
            .into());
        }
        if sequence <= self.accepted_sequence {
            return Err(SessionError::StaleSequence {
                accepted: self.accepted_sequence,
                actual: sequence,
            }
            .into());
        }
        validate_command(command)?;
        self.brush_radius = command.brush_radius;

        let pose_before = self.player.pose();
        let motion_before = self.player.motion();
        let platform_before = self.player.platform_position();
        self.player
            .step(
                self.world.scene(),
                PlayerInput {
                    forward: command.movement[0],
                    right: command.movement[1],
                    jump: command.jump,
                    crouch: command.crouch,
                    sprint: command.sprint,
                    impulse: command.impulse,
                    yaw_delta_degrees: command.look_delta_degrees[0],
                    pitch_delta_degrees: command.look_delta_degrees[1],
                },
                command.delta_seconds,
            )
            .map_err(SessionErrorOrRuntime::Runtime)?;
        let rebased = self
            .world
            .rebase_player(&mut self.player)
            .map_err(SessionErrorOrRuntime::Runtime)?;
        let residency_changed = self
            .world
            .sync_residency(self.player.pose().position)
            .map_err(SessionErrorOrRuntime::Runtime)?;
        let _changed = self.player.pose() != pose_before || self.player.motion() != motion_before;

        let (edit_receipt, edit_rejection) = match command.action {
            Some(action) => match self
                .world
                .edit_from_view(
                    self.player.local_pose().position,
                    self.player.view_direction(),
                    match action {
                        SessionAction::Destroy => EditKind::Destroy,
                        SessionAction::Place => EditKind::Place { material_slot: 1 },
                    },
                    command.brush_radius,
                    &self.player,
                )
                .map_err(SessionErrorOrRuntime::Runtime)?
            {
                EditOutcome::Miss => (None, None),
                EditOutcome::Rejected(rejection) => (None, Some(session_edit_rejection(rejection))),
                EditOutcome::Applied(receipt) => (Some((action, receipt)), None),
            },
            None => (None, None),
        };
        let platform_changed = self.player.platform_position() != platform_before;
        let frame = if edit_receipt.is_some() || residency_changed || rebased {
            self.terrain_projector
                .project(
                    self.world.scene(),
                    self.player.platform_position(),
                    platform_changed,
                )
                .map(Some)
        } else if platform_changed {
            platform_frame(self.player.platform_position()).map(Some)
        } else {
            Ok(None)
        }
        .map_err(SessionErrorOrRuntime::Runtime)?;
        let presentation = edit_receipt
            .as_ref()
            .and_then(|(action, receipt)| {
                (*action == SessionAction::Destroy)
                    .then_some(receipt)
                    .and_then(|receipt| receipt.removed_material_slot.map(|slot| (receipt, slot)))
            })
            .map(|(receipt, material_slot)| {
                block_break_debris_frame(
                    self.world.scene(),
                    receipt.voxel,
                    self.player.world_origin().origin.cell(),
                    material_slot,
                    receipt.revision,
                )
            })
            .transpose()
            .map_err(SessionErrorOrRuntime::Runtime)?;
        let edit = edit_receipt
            .map(|(action, receipt)| session_edit(action, receipt, frame.as_ref()))
            .transpose()
            .map_err(SessionErrorOrRuntime::Runtime)?;
        self.accepted_sequence = sequence;
        Ok(SessionUpdate {
            readout: self.readout(),
            action: command.action,
            edit,
            edit_rejection,
            frame,
            presentation,
        })
    }

    pub fn readout(&self) -> SessionReadout {
        let pose = self.player.pose();
        let local_pose = self.player.local_pose();
        let origin = self.player.world_origin();
        let motion = self.player.motion();
        let terrain = self.world.terrain();
        let metrics = self.world.metrics();
        let mesh = self.world.mesh_stats();
        let residency = self.world.residency_readout();
        SessionReadout {
            protocol_version: SESSION_PROTOCOL_VERSION,
            generation: self.generation,
            connected: self.connected,
            accepted_sequence: self.accepted_sequence,
            player_revision: self.player.revision(),
            player: pose.into(),
            player_local_position: local_pose.position,
            world_origin: origin.origin.cell(),
            world_origin_revision: origin.revision,
            local_coordinate_envelope: f64::from(origin.local_envelope),
            grounded: motion.grounded,
            velocity: motion.velocity,
            stance: motion.stance,
            blocked_stand: motion.blocked_stand,
            ground_normal: motion.ground_normal,
            ground_source: motion.ground_source,
            contact_count: motion.contact_count,
            blocks: motion.blocks,
            step_attempted: motion.step_attempted,
            step_accepted: motion.step_accepted,
            step_rise: motion.step_rise,
            platform_entity: motion.platform_entity,
            platform_displacement: motion.platform_displacement,
            collision_world_hash: motion.collision_world_hash,
            cast_count: motion.cast_count,
            recovery_passes: motion.recovery_passes,
            camera: RendererCameraPose {
                position: local_pose.position,
                yaw_degrees: local_pose.yaw_degrees,
                pitch_degrees: local_pose.pitch_degrees,
            },
            surface: self.world.surface().into(),
            world_revision: self.world.scene().source_revision().raw(),
            authority_hash: self.world.scene().authority_hash(),
            voxel_count: self.world.scene().solid_voxel_count(),
            targeted_voxel: self
                .world
                .target_from_view(local_pose.position, self.player.view_direction()),
            brush_radius: self.brush_radius,
            terrain_seed: format!("0x{:016x}", terrain.seed),
            terrain_size: terrain.size,
            mesh_vertices: u32::try_from(mesh.vertices).unwrap_or(u32::MAX),
            mesh_triangles: u32::try_from(mesh.triangles).unwrap_or(u32::MAX),
            generation_ms: metrics.generation_ms,
            authority_build_ms: metrics.authority_build_ms,
            mesh_build_ms: metrics.mesh_build_ms,
            residency_center: residency.center,
            requested_chunks: residency.requested,
            preparing_chunks: residency.preparing,
            resident_chunks: residency.resident,
            pinned_chunks: residency.pinned,
            evictable_chunks: residency.evictable,
            admitted_chunks_total: residency.admitted_total,
            evicted_chunks_total: residency.evicted_total,
            residency_cache_hits: residency.cache_hits,
            residency_missed_deadlines: residency.missed_deadlines,
            resident_chunk_bytes: residency.resident_bytes,
            residency_generation_ms: residency.generation_ms,
            residency_admission_ms: residency.admission_ms,
            residency_request_generation: residency.request_generation,
            terrain_generation_version: TERRAIN_GENERATION_VERSION,
            edit_overlay_entries: self.world.overlay_entry_count(),
        }
    }
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum SessionErrorOrRuntime {
    Rejected(SessionError),
    Runtime(String),
}

impl SessionErrorOrRuntime {
    pub const fn code(&self) -> &'static str {
        match self {
            Self::Rejected(error) => error.code(),
            Self::Runtime(_) => "runtimeFailure",
        }
    }
}

impl std::fmt::Display for SessionErrorOrRuntime {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Rejected(error) => error.fmt(formatter),
            Self::Runtime(message) => formatter.write_str(message),
        }
    }
}

impl std::error::Error for SessionErrorOrRuntime {}

impl From<SessionError> for SessionErrorOrRuntime {
    fn from(value: SessionError) -> Self {
        Self::Rejected(value)
    }
}

fn validate_command(command: SessionCommand) -> Result<(), SessionError> {
    let finite = command
        .movement
        .into_iter()
        .chain(command.look_delta_degrees)
        .chain([command.delta_seconds])
        .all(f64::is_finite);
    let movement_bounded = command
        .movement
        .into_iter()
        .all(|value| (-1.0..=1.0).contains(&value));
    let look_bounded = command
        .look_delta_degrees
        .into_iter()
        .all(|value| value.abs() <= MAX_LOOK_DELTA_DEGREES);
    if !finite
        || !movement_bounded
        || !look_bounded
        || !(0.0..=MAX_INPUT_DELTA_SECONDS).contains(&command.delta_seconds)
        || command.brush_radius > MAX_BRUSH_RADIUS
    {
        return Err(SessionError::InvalidCommand);
    }
    Ok(())
}

fn session_edit(
    action: SessionAction,
    receipt: EditReceipt,
    frame: Option<&RenderFrameDiff>,
) -> Result<SessionEditReadout, String> {
    let mut replacement_count = 0;
    let mut destroy_count = 0;
    let mut changed_handles = BTreeSet::new();
    if let Some(frame) = frame {
        for operation in &frame.ops {
            match operation {
                RenderDiff::ReplaceMeshPayload { handle, .. } => {
                    replacement_count += 1;
                    changed_handles.insert(handle.raw());
                }
                RenderDiff::Destroy { handle } => {
                    destroy_count += 1;
                    changed_handles.insert(handle.raw());
                }
                _ => {}
            }
        }
    }
    Ok(SessionEditReadout {
        action,
        voxel: receipt.voxel,
        revision: receipt.revision,
        affected_voxels: receipt.affected_voxels,
        authority_hash: receipt.authority_hash,
        voxel_count: receipt.voxel_count,
        mesh_build_ms: receipt.mesh_build_ms,
        edit_ms: receipt.edit_ms,
        dirty_chunks: receipt.dirty_chunks,
        rebuilt_chunks: receipt.rebuilt_chunks,
        reused_chunks: receipt.reused_chunks,
        removed_chunks: receipt.removed_chunks,
        frame_operations: frame.map_or(0, |frame| frame.ops.len()),
        encoded_bytes: frame
            .map(serde_json::to_vec)
            .transpose()
            .map_err(|error| format!("encode terrain edit frame metrics: {error}"))?
            .map_or(0, |encoded| encoded.len()),
        replacement_count,
        destroy_count,
        changed_handles: changed_handles.into_iter().collect(),
    })
}

fn session_edit_rejection(rejection: EditRejection) -> SessionEditRejectionReadout {
    match rejection {
        EditRejection::PlayerOverlap { voxel } => SessionEditRejectionReadout {
            code: rejection.code(),
            voxel,
        },
        EditRejection::WorldBounds { voxel } => SessionEditRejectionReadout {
            code: rejection.code(),
            voxel,
        },
    }
}

impl From<PlayerPose> for PlayerPoseReadout {
    fn from(value: PlayerPose) -> Self {
        Self {
            position: value.position,
            yaw_degrees: value.yaw_degrees,
            pitch_degrees: value.pitch_degrees,
        }
    }
}

impl From<SurfaceSelection> for SurfaceSelectionReadout {
    fn from(value: SurfaceSelection) -> Self {
        match value {
            SurfaceSelection::Box => Self::Box,
            SurfaceSelection::MarchingCubes => Self::MarchingCubes,
            SurfaceSelection::DualContouring => Self::DualContouring,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use rusty_engine::render_presentation::{ParticleProjectionOp, ParticleVisual, PresentationOp};

    fn input(generation: u64, sequence: u64, command: SessionCommand) -> ClientMessage {
        ClientMessage::Input {
            protocol_version: SESSION_PROTOCOL_VERSION,
            generation,
            sequence,
            command,
        }
    }

    #[test]
    fn stale_generation_duplicate_sequence_and_invalid_commands_fail_closed() {
        let mut session = GameSession::new(SurfaceSelection::Box).unwrap();
        let (first, _) = session.connect().unwrap();
        let (second, _) = session.connect().unwrap();
        assert!(matches!(
            session.submit(input(first.generation, 1, SessionCommand::default())),
            Err(SessionErrorOrRuntime::Rejected(
                SessionError::StaleGeneration { .. }
            ))
        ));
        session
            .submit(input(second.generation, 1, SessionCommand::default()))
            .unwrap();
        assert!(matches!(
            session.submit(input(second.generation, 1, SessionCommand::default())),
            Err(SessionErrorOrRuntime::Rejected(
                SessionError::StaleSequence { .. }
            ))
        ));
        assert!(matches!(
            session.submit(input(
                second.generation,
                2,
                SessionCommand {
                    movement: [2.0, 0.0],
                    ..SessionCommand::default()
                }
            )),
            Err(SessionErrorOrRuntime::Rejected(
                SessionError::InvalidCommand
            ))
        ));
        assert_eq!(session.readout().accepted_sequence, 1);
    }

    #[test]
    fn disconnect_neutralizes_only_the_current_generation() {
        let mut session = GameSession::new(SurfaceSelection::Box).unwrap();
        let (first, _) = session.connect().unwrap();
        let (second, _) = session.connect().unwrap();
        assert!(!session.disconnect(first.generation));
        assert!(session.readout().connected);
        assert!(session.disconnect(second.generation));
        assert!(!session.readout().connected);
        assert!(matches!(
            session.submit(input(second.generation, 1, SessionCommand::default())),
            Err(SessionErrorOrRuntime::Rejected(SessionError::NotConnected))
        ));
    }

    #[test]
    fn neutral_input_is_noop_while_motion_updates_player_and_platform_projection() {
        let mut session = GameSession::new(SurfaceSelection::Box).unwrap();
        let (connected, _) = session.connect().unwrap();
        let neutral = session
            .submit(input(connected.generation, 1, SessionCommand::default()))
            .unwrap();
        assert_eq!(neutral.readout.authority_hash, connected.authority_hash);
        assert_eq!(neutral.readout.world_revision, connected.world_revision);
        assert_eq!(neutral.readout.player_revision, connected.player_revision);
        assert!(neutral.frame.is_none());

        let moved = session
            .submit(input(
                connected.generation,
                2,
                SessionCommand {
                    movement: [1.0, 0.0],
                    delta_seconds: 0.05,
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(moved.readout.player_revision > neutral.readout.player_revision);
        assert_eq!(moved.readout.world_revision, neutral.readout.world_revision);
        assert!(moved.frame.is_some());
    }

    #[test]
    fn destroy_and_place_publish_edits_and_replacement_meshes() {
        let mut session = GameSession::new(SurfaceSelection::Box).unwrap();
        let (connected, _) = session.connect().unwrap();
        let update = session
            .submit(input(
                connected.generation,
                1,
                SessionCommand {
                    action: Some(SessionAction::Destroy),
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(update.edit.is_some());
        assert!(update.frame.is_some());
        let debris = update.presentation.as_ref().expect("destroy emits debris");
        debris.validate().unwrap();
        let PresentationOp::Particle {
            op: ParticleProjectionOp::Emit { descriptor, .. },
            ..
        } = &debris.ops[0]
        else {
            panic!("destroy presentation must emit particles");
        };
        assert_eq!(descriptor.visual, ParticleVisual::Cube);
        assert_eq!(descriptor.burst_count, 12);
        assert!(descriptor
            .collision
            .as_ref()
            .is_some_and(|value| { !value.volumes.is_empty() && value.volumes.len() <= 6 }));
        assert!(update.readout.world_revision > connected.world_revision);
        assert_ne!(update.readout.authority_hash, connected.authority_hash);

        let placed = session
            .submit(input(
                connected.generation,
                2,
                SessionCommand {
                    action: Some(SessionAction::Place),
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(placed.edit.is_some());
        assert!(placed.frame.is_some());
        assert!(placed.presentation.is_none());
        assert!(placed.readout.world_revision > update.readout.world_revision);
    }

    #[test]
    fn every_surface_mode_connects_with_a_valid_complete_frame() {
        for surface in SurfaceSelection::ALL {
            let mut session = GameSession::new(surface).unwrap();
            let (readout, frame) = session.connect().unwrap();
            frame.validate().unwrap();
            assert_eq!(readout.surface, surface.into());
            assert!(readout.targeted_voxel.is_some());
        }
    }

    #[test]
    fn semantic_controller_actions_publish_engine_diagnostics() {
        let mut session = GameSession::new(SurfaceSelection::Box).unwrap();
        let (connected, _) = session.connect().unwrap();
        let crouched = session
            .submit(input(
                connected.generation,
                1,
                SessionCommand {
                    crouch: true,
                    delta_seconds: 0.05,
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert_eq!(crouched.readout.stance, "crouched");
        assert_ne!(crouched.readout.collision_world_hash, 0);
        assert!(crouched.readout.cast_count > 0);

        let impulse = session
            .submit(input(
                connected.generation,
                2,
                SessionCommand {
                    sprint: true,
                    impulse: true,
                    movement: [1.0, 0.0],
                    delta_seconds: 0.05,
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(impulse.readout.velocity[0] > 0.0);
        assert!(impulse.readout.velocity[2] < 0.0);
        assert!(impulse.readout.player_revision > crouched.readout.player_revision);
    }

    #[test]
    fn platform_supported_impulse_remains_a_valid_session_update() {
        let mut session = GameSession::with_terrain_and_spawn(
            SurfaceSelection::Box,
            TerrainConfig::default(),
            SpawnSelection::MovingPlatform,
        )
        .unwrap();
        let (connected, _) = session.connect().unwrap();
        let mut sequence = 0;
        for _ in 0..12 {
            sequence += 1;
            session
                .submit(input(
                    connected.generation,
                    sequence,
                    SessionCommand {
                        delta_seconds: 0.05,
                        ..SessionCommand::default()
                    },
                ))
                .unwrap();
        }
        assert_eq!(session.readout().platform_entity, Some(2));
        sequence += 1;
        let impulse = session
            .submit(input(
                connected.generation,
                sequence,
                SessionCommand {
                    impulse: true,
                    delta_seconds: 0.05,
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(impulse.readout.velocity[0].abs() > 1.0);
        assert!(impulse.readout.velocity[1] > 0.0);
        for _ in 0..80 {
            sequence += 1;
            let before = session.readout();
            session
                .submit(input(
                    connected.generation,
                    sequence,
                    SessionCommand {
                        delta_seconds: 0.05,
                        ..SessionCommand::default()
                    },
                ))
                .unwrap_or_else(|error| {
                    panic!(
                        "sequence {sequence} failed from position {:?}, velocity {:?}, platform {:?}: {error}",
                        before.player.position,
                        before.velocity,
                        before.platform_entity,
                    )
                });
        }
        let landed = session.readout();
        assert!(landed.grounded);
        assert!(landed.player.position[1] < 4.0);
    }

    #[test]
    fn far_signed_spawns_connect_in_bounded_local_frames_with_coherent_residency() {
        for (spawn, sign) in [
            (SpawnSelection::FarPositive, 1.0),
            (SpawnSelection::FarNegative, -1.0),
        ] {
            let mut session = GameSession::with_terrain_and_spawn(
                SurfaceSelection::Box,
                TerrainConfig::default(),
                spawn,
            )
            .unwrap();
            let (readout, frame) = session.connect().unwrap();
            assert!(readout.player.position[0] * sign > 262_000.0);
            assert!(readout.player_local_position[0].abs() < 1.0);
            assert_eq!(readout.world_origin[0].signum() as f64, sign);
            assert_eq!(readout.world_origin_revision, 1);
            assert!(readout.resident_chunks <= 64);
            assert!(readout.pinned_chunks > 0);
            frame.validate().unwrap();
        }
    }

    #[test]
    fn far_route_crosses_rebase_thresholds_without_losing_global_or_resident_identity() {
        let mut session = GameSession::with_terrain_and_spawn(
            SurfaceSelection::Box,
            TerrainConfig::default(),
            SpawnSelection::FarPositive,
        )
        .unwrap();
        let (connected, _) = session.connect().unwrap();
        let starting_origin_revision = connected.world_origin_revision;
        let mut last = connected;
        for sequence in 1..=240 {
            last = session
                .submit(input(
                    last.generation,
                    sequence,
                    SessionCommand {
                        movement: [1.0, 0.0],
                        sprint: true,
                        delta_seconds: 0.05,
                        ..SessionCommand::default()
                    },
                ))
                .unwrap()
                .readout;
        }
        assert!(last.player.position[0] > 262_000.0);
        assert!(last.player_local_position[0].abs() < crate::player::REBASE_THRESHOLD as f64);
        assert!(last.player_local_position[2].abs() < crate::player::REBASE_THRESHOLD as f64);
        assert!(last.world_origin_revision >= starting_origin_revision + 2);
        assert!(last.resident_chunks <= 64);
        assert!(last.pinned_chunks > 0);
    }
}
