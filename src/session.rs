use rusty_engine::{render_host_contracts::RendererCameraPose, render_model::RenderFrameDiff};
use serde::{Deserialize, Serialize};

use crate::{
    initial_frame, replacement_frame, EditKind, EditReceipt, GameWorld, PlayerController,
    PlayerInput, PlayerPose, SurfaceSelection,
};

pub const SESSION_PROTOCOL_VERSION: u32 = 1;
pub const MAX_SESSION_MESSAGE_BYTES: usize = 16 * 1024;
pub const MAX_LOOK_DELTA_DEGREES: f64 = 45.0;
pub const MAX_INPUT_DELTA_SECONDS: f64 = 0.05;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub enum SessionAction {
    Destroy,
    Place,
}

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionCommand {
    pub movement: [f64; 3],
    pub look_delta_degrees: [f64; 2],
    pub delta_seconds: f64,
    pub action: Option<SessionAction>,
}

impl Default for SessionCommand {
    fn default() -> Self {
        Self {
            movement: [0.0; 3],
            look_delta_degrees: [0.0; 2],
            delta_seconds: 0.0,
            action: None,
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

#[derive(Debug, Clone, Copy, PartialEq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase", deny_unknown_fields)]
pub struct SessionReadout {
    pub protocol_version: u32,
    pub generation: u64,
    pub connected: bool,
    pub accepted_sequence: u64,
    pub player_revision: u64,
    pub player: PlayerPoseReadout,
    pub camera: RendererCameraPose,
    pub surface: SurfaceSelectionReadout,
    pub world_revision: u64,
    pub authority_hash: u64,
    pub voxel_count: usize,
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

#[derive(Debug, Clone, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionUpdate {
    pub readout: SessionReadout,
    pub edit: Option<SessionEditReadout>,
    pub frame: Option<RenderFrameDiff>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize)]
#[serde(rename_all = "camelCase")]
pub struct SessionEditReadout {
    pub action: SessionAction,
    pub voxel: [i64; 3],
    pub revision: u64,
    pub authority_hash: u64,
    pub voxel_count: usize,
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
    player_revision: u64,
}

impl GameSession {
    pub fn new(surface: SurfaceSelection) -> Result<Self, String> {
        Ok(Self {
            world: GameWorld::new(surface)?,
            player: PlayerController::default(),
            generation: 0,
            connected: false,
            accepted_sequence: 0,
            player_revision: 0,
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

        let pose_before = self.player.pose();
        self.player.step(
            self.world.scene(),
            PlayerInput {
                forward: command.movement[0],
                right: command.movement[1],
                vertical: command.movement[2],
                yaw_delta_degrees: command.look_delta_degrees[0],
                pitch_delta_degrees: command.look_delta_degrees[1],
            },
            command.delta_seconds,
        );
        if self.player.pose() != pose_before {
            self.player_revision = self.player_revision.saturating_add(1);
        }

        let edit = match command.action {
            Some(action) => self
                .world
                .edit_from_view(
                    self.player.pose().position,
                    self.player.view_direction(),
                    match action {
                        SessionAction::Destroy => EditKind::Destroy,
                        SessionAction::Place => EditKind::Place { material_slot: 1 },
                    },
                )
                .map_err(SessionErrorOrRuntime::Runtime)?
                .map(|receipt| session_edit(action, receipt)),
            None => None,
        };
        let frame = if edit.is_some() {
            Some(
                replacement_frame(self.world.presentation_mesh())
                    .map_err(SessionErrorOrRuntime::Runtime)?,
            )
        } else {
            None
        };
        self.accepted_sequence = sequence;
        Ok(SessionUpdate {
            readout: self.readout(),
            edit,
            frame,
        })
    }

    pub fn readout(&self) -> SessionReadout {
        let pose = self.player.pose();
        SessionReadout {
            protocol_version: SESSION_PROTOCOL_VERSION,
            generation: self.generation,
            connected: self.connected,
            accepted_sequence: self.accepted_sequence,
            player_revision: self.player_revision,
            player: pose.into(),
            camera: RendererCameraPose {
                position: pose.position,
                yaw_degrees: pose.yaw_degrees,
                pitch_degrees: pose.pitch_degrees,
            },
            surface: self.world.surface().into(),
            world_revision: self.world.scene().source_revision().raw(),
            authority_hash: self.world.scene().authority_hash(),
            voxel_count: self.world.scene().solid_voxel_count(),
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
        authority_hash: receipt.authority_hash,
        voxel_count: receipt.voxel_count,
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
                    movement: [2.0, 0.0, 0.0],
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
    fn neutral_input_is_authoritative_noop_while_motion_changes_only_player() {
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
                    movement: [1.0, 0.0, 0.0],
                    delta_seconds: 0.05,
                    ..SessionCommand::default()
                },
            ))
            .unwrap();
        assert!(moved.readout.player_revision > neutral.readout.player_revision);
        assert_eq!(moved.readout.world_revision, neutral.readout.world_revision);
        assert!(moved.frame.is_none());
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
        }
    }
}
