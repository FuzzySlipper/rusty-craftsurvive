use rusty_engine::{render_host_contracts::RendererCameraPose, render_model::RenderFrameDiff};
use serde::{Deserialize, Serialize};

use crate::{
    initial_frame, platform_frame, replacement_and_platform_frame, terrain_texture_resource,
    EditKind, EditOutcome, EditReceipt, EditRejection, GameWorld, PlayerController, PlayerInput,
    PlayerPose, SurfaceSelection, TerrainConfig, MAX_BRUSH_RADIUS,
};

pub const SESSION_PROTOCOL_VERSION: u32 = 4;
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
        update: SessionUpdate,
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
    let resource = terrain_texture_resource()?;
    Ok(vec![SessionResourceReadout {
        identity: resource.identity,
        content_hash: resource.content_hash,
        media_type: resource.media_type,
        url: resource.url,
    }])
}

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionUpdate {
    pub readout: SessionReadout,
    pub action: Option<SessionAction>,
    pub edit: Option<SessionEditReadout>,
    pub edit_rejection: Option<SessionEditRejectionReadout>,
    pub frame: Option<RenderFrameDiff>,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize)]
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
        };
        Ok(Self {
            world: GameWorld::with_terrain(surface, terrain)?,
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
        let frame = initial_frame(self.world.presentation_mesh())?;
        Ok((self.readout(), frame))
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
        let _changed = self.player.pose() != pose_before || self.player.motion() != motion_before;

        let (edit, edit_rejection) = match command.action {
            Some(action) => match self
                .world
                .edit_from_view(
                    self.player.pose().position,
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
                EditOutcome::Applied(receipt) => (Some(session_edit(action, receipt)), None),
            },
            None => (None, None),
        };
        let platform_changed = self.player.platform_position() != platform_before;
        let frame = if edit.is_some() {
            replacement_and_platform_frame(
                self.world.presentation_mesh(),
                self.player.platform_position(),
            )
            .map(Some)
        } else if platform_changed {
            platform_frame(self.player.platform_position()).map(Some)
        } else {
            Ok(None)
        }
        .map_err(SessionErrorOrRuntime::Runtime)?;
        self.accepted_sequence = sequence;
        Ok(SessionUpdate {
            readout: self.readout(),
            action: command.action,
            edit,
            edit_rejection,
            frame,
        })
    }

    pub fn readout(&self) -> SessionReadout {
        let pose = self.player.pose();
        let motion = self.player.motion();
        let terrain = self.world.terrain();
        let metrics = self.world.metrics();
        let mesh = self.world.presentation_mesh();
        SessionReadout {
            protocol_version: SESSION_PROTOCOL_VERSION,
            generation: self.generation,
            connected: self.connected,
            accepted_sequence: self.accepted_sequence,
            player_revision: self.player.revision(),
            player: pose.into(),
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
                position: pose.position,
                yaw_degrees: pose.yaw_degrees,
                pitch_degrees: pose.pitch_degrees,
            },
            surface: self.world.surface().into(),
            world_revision: self.world.scene().source_revision().raw(),
            authority_hash: self.world.scene().authority_hash(),
            voxel_count: self.world.scene().solid_voxel_count(),
            targeted_voxel: self
                .world
                .target_from_view(pose.position, self.player.view_direction()),
            brush_radius: self.brush_radius,
            terrain_seed: format!("0x{:016x}", terrain.seed),
            terrain_size: terrain.size,
            mesh_vertices: mesh.stats.vertices,
            mesh_triangles: mesh.stats.triangles,
            generation_ms: metrics.generation_ms,
            authority_build_ms: metrics.authority_build_ms,
            mesh_build_ms: metrics.mesh_build_ms,
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

fn session_edit(action: SessionAction, receipt: EditReceipt) -> SessionEditReadout {
    SessionEditReadout {
        action,
        voxel: receipt.voxel,
        revision: receipt.revision,
        affected_voxels: receipt.affected_voxels,
        authority_hash: receipt.authority_hash,
        voxel_count: receipt.voxel_count,
        mesh_build_ms: receipt.mesh_build_ms,
        edit_ms: receipt.edit_ms,
    }
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
}
