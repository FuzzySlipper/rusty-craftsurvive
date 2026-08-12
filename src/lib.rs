#![forbid(unsafe_code)]

mod config;
mod island;
mod player;
mod projection;
mod session;
mod terrain_texture;
mod world;

pub use config::{
    parse_seed, DemoConfig, SurfaceSelection, TerrainConfig, DEFAULT_TERRAIN_SEED,
    DEFAULT_TERRAIN_SIZE, MAX_TERRAIN_SIZE, MIN_TERRAIN_SIZE,
};
pub use island::{generate_island, IslandConfig};
pub use player::{
    craftsurvive_controller_config, PlayerController, PlayerInput, PlayerMotionReadout, PlayerPose,
    PLATFORM_HALF_EXTENTS, PLATFORM_INITIAL_CENTER,
};
pub use projection::{
    initial_frame, platform_frame, replacement_and_platform_frame, replacement_frame,
    telemetry_frame,
};
pub use session::{
    session_resources, ClientMessage, GameSession, ServerMessage, SessionAction, SessionCommand,
    SessionEditRejectionReadout, SessionError, SessionErrorOrRuntime, SessionReadout,
    SessionResourceReadout, SessionUpdate, MAX_SESSION_MESSAGE_BYTES, SESSION_PROTOCOL_VERSION,
};
pub use terrain_texture::{terrain_texture_resource, TerrainTextureResource, TERRAIN_ATLAS_URL};
pub use world::{
    brush_addresses, EditKind, EditOutcome, EditReceipt, EditRejection, GameWorld, WorldMetrics,
    MAX_BRUSH_RADIUS,
};
