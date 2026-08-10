#![forbid(unsafe_code)]

mod config;
mod island;
mod player;
mod projection;
mod session;
mod world;

pub use config::{DemoConfig, SurfaceSelection};
pub use island::{generate_island, IslandConfig};
pub use player::{PlayerController, PlayerInput, PlayerPose};
pub use projection::{initial_frame, replacement_frame, telemetry_frame, view_composition};
pub use session::{
    ClientMessage, GameSession, ServerMessage, SessionAction, SessionCommand, SessionError,
    SessionErrorOrRuntime, SessionReadout, SessionUpdate, MAX_SESSION_MESSAGE_BYTES,
    SESSION_PROTOCOL_VERSION,
};
pub use world::{EditKind, EditReceipt, GameWorld};
