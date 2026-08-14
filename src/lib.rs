#![forbid(unsafe_code)]

mod config;
mod island;
mod player;
mod projection;
mod save;
mod session;
mod sky_background;
mod sprite_scene;
mod terrain_texture;
mod world;

pub use config::{
    parse_seed, DemoConfig, SurfaceSelection, TerrainConfig, DEFAULT_TERRAIN_SEED,
    DEFAULT_TERRAIN_SIZE, MAX_TERRAIN_SIZE, MIN_TERRAIN_SIZE,
};
pub use island::{generate_island, IslandConfig, TERRAIN_GENERATION_VERSION};
pub use player::{
    craftsurvive_controller_config, PlayerController, PlayerInput, PlayerMotionReadout, PlayerPose,
    PLATFORM_HALF_EXTENTS, PLATFORM_INITIAL_CENTER,
};
pub use projection::{block_break_debris_frame, platform_frame, telemetry_frame, TerrainProjector};
pub use save::{
    TerrainOverlayError, MAX_TERRAIN_OVERLAY_BYTES, MAX_TERRAIN_OVERLAY_ENTRIES,
    TERRAIN_OVERLAY_SCHEMA_VERSION,
};
pub use session::{
    session_resources, ClientMessage, GameSession, ServerMessage, SessionAction, SessionCommand,
    SessionEditRejectionReadout, SessionError, SessionErrorOrRuntime, SessionReadout,
    SessionResourceReadout, SessionUpdate, SpawnSelection, MAX_SESSION_MESSAGE_BYTES,
    SESSION_PROTOCOL_VERSION,
};
pub use sky_background::{
    sky_background_ops, sky_background_resource, SkyBackgroundResource, SKY_URL,
};
pub use sprite_scene::{
    wisp_light_update_op, wisp_scene_ops, wisp_texture_resource, WispTextureResource, WISP_URL,
};
pub use terrain_texture::{
    terrain_material_ops, terrain_materials, terrain_texture_op, terrain_texture_resource,
    TerrainTextureResource, TERRAIN_ATLAS_URL,
};
pub use world::{
    brush_addresses, EditKind, EditOutcome, EditReceipt, EditRejection, GameWorld, WorldMeshStats,
    WorldMetrics, CERTIFIED_WORLD_COORDINATE_ABS, MAX_BRUSH_RADIUS,
};
