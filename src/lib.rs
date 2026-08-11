#![forbid(unsafe_code)]

mod config;
mod island;
mod player;
mod projection;
mod session;
mod terrain_texture;
mod world;

pub use config::{DemoConfig, SurfaceSelection};
pub use island::{generate_island, IslandConfig};
pub use player::{PlayerController, PlayerInput, PlayerMotionReadout, PlayerPose};
pub use projection::{initial_frame, replacement_frame, telemetry_frame};
pub use session::{
    session_resources, ClientMessage, GameSession, ServerMessage, SessionAction, SessionCommand,
    SessionEditRejectionReadout, SessionError, SessionErrorOrRuntime, SessionReadout,
    SessionResourceReadout, SessionUpdate, MAX_SESSION_MESSAGE_BYTES, SESSION_PROTOCOL_VERSION,
};
pub use terrain_texture::{terrain_texture_resource, TerrainTextureResource, TERRAIN_ATLAS_URL};
pub use world::{EditKind, EditOutcome, EditReceipt, EditRejection, GameWorld};
