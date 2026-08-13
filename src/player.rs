use rusty_engine::{
    core_ids::EntityId,
    core_math::{Vec2, Vec3},
    engine_spatial::{
        CharacterBlockKind, CharacterControllerCommand, CharacterControllerConfig,
        CharacterControllerReceipt, CharacterControllerService, FirstPersonLookCommand,
        FirstPersonLookConfig, FirstPersonLookService, FirstPersonLookState, GlobalPosition,
        VoxelCollisionScene, WorldOrigin, WorldOriginEntity, WorldOriginReadout,
        WorldOriginRebaseRequest, WorldOriginRebaseService, WorldOriginState,
    },
    entity_state::{CharacterMotionComponent, CharacterStance, EntityDefinition, EntityState},
    svc_collision::CharacterCollisionSource,
};

const FIXED_STEP_SECONDS: f32 = 1.0 / 120.0;
const PLAYER_ENTITY: EntityId = EntityId::new(1);
const PLATFORM_ENTITY: EntityId = EntityId::new(2);
pub const PLATFORM_INITIAL_CENTER: [f32; 3] = [0.0, 4.25, 9.0];
pub const PLATFORM_HALF_EXTENTS: [f32; 3] = [1.5, 0.25, 0.9];
const STANDING_EYE_HEIGHT: f32 = 1.55;
const CROUCHED_EYE_HEIGHT: f32 = 0.85;
const IMPULSE_SPEED: f32 = 5.5;
const IMPULSE_LIFT: f32 = 2.5;
pub(crate) const REBASE_THRESHOLD: f32 = 32.0;
const CRAFTSURVIVE_LOCAL_ENVELOPE: f32 = 1_000_000.0;
const PLATFORM_ACTIVITY_RADIUS: f64 = 32.0;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerPose {
    pub position: [f64; 3],
    pub yaw_degrees: f64,
    pub pitch_degrees: f64,
}

impl Default for PlayerPose {
    fn default() -> Self {
        Self {
            position: [0.5, 7.0, 7.0],
            yaw_degrees: 0.0,
            pitch_degrees: -20.0,
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct PlayerInput {
    pub forward: f64,
    pub right: f64,
    pub jump: bool,
    pub crouch: bool,
    pub sprint: bool,
    pub impulse: bool,
    pub yaw_delta_degrees: f64,
    pub pitch_delta_degrees: f64,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PlayerMotionReadout {
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
}

/// Product-owned orchestration around the Engine-owned character and look services.
///
/// This type deliberately contains no collision solver or movement integration. It translates
/// CraftSurvive input and tuning into direct Engine service calls, then projects the canonical
/// entity center into the downstream camera-eye pose.
pub struct PlayerController {
    entities: EntityState,
    origin: WorldOriginState,
    player_global: GlobalPosition,
    platform_global: GlobalPosition,
    controller: CharacterControllerService,
    config: CharacterControllerConfig,
    look_config: FirstPersonLookConfig,
    look: FirstPersonLookState,
    fixed_step_accumulator: f32,
    jump_held: bool,
    impulse_held: bool,
    command_sequence: u64,
    last_receipt: Option<CharacterControllerReceipt>,
    platform_direction: f32,
}

impl std::fmt::Debug for PlayerController {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter
            .debug_struct("PlayerController")
            .field("pose", &self.pose())
            .field("motion", &self.motion())
            .field("command_sequence", &self.command_sequence)
            .finish_non_exhaustive()
    }
}

impl Default for PlayerController {
    fn default() -> Self {
        Self::new(PlayerPose::default()).expect("default player definition is valid")
    }
}

impl PlayerController {
    pub fn new(pose: PlayerPose) -> Result<Self, String> {
        let config = craftsurvive_controller_config();
        config
            .validate()
            .map_err(|error| format!("validate CraftSurvive controller tuning: {error}"))?;
        let center_y = pose.position[1] - f64::from(eye_offset(&config, CharacterStance::Standing));
        let origin = WorldOriginState::new(CRAFTSURVIVE_LOCAL_ENVELOPE)
            .map_err(|error| format!("create CraftSurvive world origin: {error}"))?;
        let player_global =
            GlobalPosition::from_world([pose.position[0], center_y, pose.position[2]])
                .map_err(|error| format!("create global player position: {error}"))?;
        let platform_global = GlobalPosition::from_world(PLATFORM_INITIAL_CENTER.map(f64::from))
            .map_err(|error| format!("create global platform position: {error}"))?;
        let center = vec3(
            origin
                .local_from_global(player_global)
                .map_err(|error| format!("project initial player position: {error}"))?,
        );
        let entities = EntityState::from_definitions([
            EntityDefinition::new(PLAYER_ENTITY, "craftsurvive-player")
                .with_transform(center)
                .with_character_motion(CharacterMotionComponent::at_rest(center.y)),
            EntityDefinition::new(PLATFORM_ENTITY, "craftsurvive-moving-platform")
                .with_transform(Vec3::new(
                    PLATFORM_INITIAL_CENTER[0],
                    PLATFORM_INITIAL_CENTER[1],
                    PLATFORM_INITIAL_CENTER[2],
                ))
                .with_bounds(
                    Vec3::new(
                        -PLATFORM_HALF_EXTENTS[0],
                        -PLATFORM_HALF_EXTENTS[1],
                        -PLATFORM_HALF_EXTENTS[2],
                    ),
                    Vec3::new(
                        PLATFORM_HALF_EXTENTS[0],
                        PLATFORM_HALF_EXTENTS[1],
                        PLATFORM_HALF_EXTENTS[2],
                    ),
                )
                .with_collision(true, false),
        ])
        .map_err(|error| format!("create Engine player entity: {error}"))?;
        Ok(Self {
            entities,
            origin,
            player_global,
            platform_global,
            controller: CharacterControllerService::default(),
            config,
            look_config: FirstPersonLookConfig::default(),
            look: FirstPersonLookState {
                yaw_radians: finite_f32(pose.yaw_degrees.to_radians())?,
                pitch_radians: finite_f32(pose.pitch_degrees.to_radians())?,
            },
            fixed_step_accumulator: 0.0,
            jump_held: false,
            impulse_held: false,
            command_sequence: 0,
            last_receipt: None,
            platform_direction: 1.0,
        })
    }

    pub fn pose(&self) -> PlayerPose {
        let center = self.player_global.to_world();
        let stance = self
            .entities
            .character_motion(PLAYER_ENTITY)
            .expect("player always retains its Engine motion")
            .stance;
        let offset = f64::from(eye_offset(&self.config, stance));
        PlayerPose {
            position: [center[0], center[1] + offset, center[2]],
            yaw_degrees: f64::from(self.look.yaw_radians.to_degrees()),
            pitch_degrees: f64::from(self.look.pitch_radians.to_degrees()),
        }
    }

    pub fn local_pose(&self) -> PlayerPose {
        let transform = self
            .entities
            .transform(PLAYER_ENTITY)
            .expect("player always retains its Engine transform");
        let stance = self
            .entities
            .character_motion(PLAYER_ENTITY)
            .expect("player always retains its Engine motion")
            .stance;
        let offset = f64::from(eye_offset(&self.config, stance));
        PlayerPose {
            position: [
                f64::from(transform.translation.x),
                f64::from(transform.translation.y) + offset,
                f64::from(transform.translation.z),
            ],
            yaw_degrees: f64::from(self.look.yaw_radians.to_degrees()),
            pitch_degrees: f64::from(self.look.pitch_radians.to_degrees()),
        }
    }

    pub fn motion(&self) -> PlayerMotionReadout {
        let motion = self
            .entities
            .character_motion(PLAYER_ENTITY)
            .expect("player always retains its Engine motion");
        let receipt = self.last_receipt.as_ref();
        let velocity = motion.controlled_velocity + motion.external_velocity;
        let step = receipt.and_then(|value| value.step);
        let platform = receipt.and_then(|value| value.platform);
        PlayerMotionReadout {
            grounded: motion.grounded,
            velocity: vec3_array(velocity),
            stance: stance_name(motion.stance),
            blocked_stand: receipt.is_some_and(|value| value.stance.blocked),
            ground_normal: receipt
                .and_then(|value| value.ground)
                .map(|ground| vec3_array(ground.normal)),
            ground_source: receipt
                .and_then(|value| value.ground)
                .map(|ground| collision_source_name(ground.source)),
            contact_count: receipt.map_or(0, |value| value.contacts.len()),
            blocks: receipt
                .map(|value| value.blocks.iter().copied().map(block_name).collect())
                .unwrap_or_default(),
            step_attempted: step.is_some_and(|value| value.attempted),
            step_accepted: step.is_some_and(|value| value.accepted),
            step_rise: step.map_or(0.0, |value| f64::from(value.rise)),
            platform_entity: platform.map(|value| value.entity.raw()),
            platform_displacement: platform
                .map(|value| vec3_array(value.carried_displacement))
                .unwrap_or([0.0; 3]),
            collision_world_hash: motion.collision_world_hash,
            cast_count: receipt.map_or(0, |value| value.cast_count),
            recovery_passes: receipt.map_or(0, |value| value.recovery_passes),
        }
    }

    pub fn revision(&self) -> u64 {
        self.entities.revision()
    }

    pub fn platform_position(&self) -> [f64; 3] {
        let transform = self
            .entities
            .transform(PLATFORM_ENTITY)
            .expect("moving platform always retains its Engine transform");
        vec3_array(transform.translation)
    }

    pub const fn world_origin(&self) -> WorldOriginReadout {
        self.origin.readout()
    }

    pub fn rebase_world(&mut self, scene: &mut VoxelCollisionScene) -> Result<bool, String> {
        let local = self
            .entities
            .transform(PLAYER_ENTITY)
            .expect("player always retains its Engine transform")
            .translation;
        if local.x.abs() < REBASE_THRESHOLD && local.z.abs() < REBASE_THRESHOLD {
            return Ok(false);
        }
        let player_cell = self.player_global.cell();
        let target = WorldOrigin::new([player_cell[0], 0, player_cell[2]]);
        let request = WorldOriginRebaseRequest {
            expected_origin_revision: self.origin.revision(),
            expected_entity_revision: self.entities.revision(),
            expected_voxel_source_revision: scene.source_revision().raw(),
            expected_static_mesh_revision: scene.static_mesh_collision_revision(),
            target_origin: target,
            entities: vec![
                WorldOriginEntity {
                    entity: PLAYER_ENTITY,
                    global_position: self.player_global,
                },
                WorldOriginEntity {
                    entity: PLATFORM_ENTITY,
                    global_position: self.platform_global,
                },
            ],
        };
        WorldOriginRebaseService
            .apply(&mut self.origin, &mut self.entities, scene, request)
            .map_err(|error| format!("rebase CraftSurvive local world frame: {error}"))?;
        Ok(true)
    }

    pub fn overlaps_voxel(&self, address: [i64; 3], voxel_size: f64) -> bool {
        let (player_min, player_max) = self.global_capsule_bounds();
        let voxel_min = address.map(|coordinate| coordinate as f64 * voxel_size);
        let voxel_max = voxel_min.map(|coordinate| coordinate + voxel_size);
        aabb_intersects(player_min, player_max, voxel_min, voxel_max)
    }

    #[cfg(test)]
    pub(crate) fn overlaps_scene(&self, scene: &VoxelCollisionScene) -> bool {
        let (mut min, mut max) = self.capsule_bounds();
        for axis in 0..3 {
            min[axis] += 1.0e-6;
            max[axis] -= 1.0e-6;
        }
        scene.aabb_overlaps_solid(min, max)
    }

    pub fn view_direction(&self) -> [f64; 3] {
        let receipt = FirstPersonLookService
            .integrate(
                &self.look_config,
                self.look,
                FirstPersonLookCommand::default(),
            )
            .expect("stored look state and product config remain valid");
        vec3_array(receipt.forward)
    }

    pub fn step(
        &mut self,
        scene: &VoxelCollisionScene,
        input: PlayerInput,
        delta_seconds: f64,
    ) -> Result<(), String> {
        let look = FirstPersonLookService
            .integrate(
                &self.look_config,
                self.look,
                FirstPersonLookCommand {
                    delta: Vec2::new(
                        finite_f32(input.yaw_delta_degrees.to_radians())?,
                        finite_f32(input.pitch_delta_degrees.to_radians())?,
                    ),
                },
            )
            .map_err(|error| format!("apply first-person look: {error}"))?;
        self.look = look.after;

        let jump_pressed = input.jump && !self.jump_held;
        let impulse_pressed = input.impulse && !self.impulse_held;
        self.jump_held = input.jump;
        self.impulse_held = input.impulse;
        self.fixed_step_accumulator += finite_f32(delta_seconds.clamp(0.0, 0.05))?;
        let mut first_step = true;
        while self.fixed_step_accumulator + f32::EPSILON >= FIXED_STEP_SECONDS {
            self.advance_platform()?;
            self.command_sequence = self
                .command_sequence
                .checked_add(1)
                .ok_or_else(|| "player command sequence exhausted".to_owned())?;
            let mut config = self.config.clone();
            if input.sprint && !input.crouch {
                config.ground.forward_speed = 8.0;
                config.ground.backward_speed = 8.0;
                config.ground.strafe_speed = 8.0;
            }
            let receipt = self
                .controller
                .step(
                    &mut self.entities,
                    scene,
                    PLAYER_ENTITY,
                    &config,
                    CharacterControllerCommand {
                        planar_intent: Vec2::new(
                            finite_f32(input.right)?,
                            finite_f32(input.forward)?,
                        ),
                        heading_yaw_radians: self.look.yaw_radians,
                        jump_pressed: first_step && jump_pressed,
                        jump_held: input.jump,
                        crouch_requested: input.crouch,
                        external_impulse: if first_step && impulse_pressed {
                            look.right * IMPULSE_SPEED + Vec3::new(0.0, IMPULSE_LIFT, 0.0)
                        } else {
                            Vec3::ZERO
                        },
                        ..CharacterControllerCommand::idle(
                            FIXED_STEP_SECONDS,
                            self.command_sequence,
                        )
                    },
                )
                .map_err(|error| format!("step Engine character controller: {error}"))?;
            self.last_receipt = Some(receipt);
            self.refresh_global_positions()?;
            self.fixed_step_accumulator -= FIXED_STEP_SECONDS;
            first_step = false;
        }
        Ok(())
    }

    fn advance_platform(&mut self) -> Result<(), String> {
        let player = self.player_global.to_world();
        let platform = self.platform_global.to_world();
        let distance_squared = (player[0] - platform[0]).powi(2)
            + (player[1] - platform[1]).powi(2)
            + (player[2] - platform[2]).powi(2);
        if distance_squared > PLATFORM_ACTIVITY_RADIUS.powi(2) {
            return Ok(());
        }
        let x = platform[0];
        if x >= 1.5 {
            self.platform_direction = -1.0;
        } else if x <= -1.5 {
            self.platform_direction = 1.0;
        }
        let revision = self.entities.revision();
        self.entities
            .apply_transform(
                revision,
                rusty_engine::entity_state::TransformCommand::Translate {
                    entity: PLATFORM_ENTITY,
                    delta: Vec3::new(self.platform_direction * 0.8 * FIXED_STEP_SECONDS, 0.0, 0.0),
                },
            )
            .map_err(|error| format!("advance CraftSurvive moving platform: {error}"))?;
        self.refresh_platform_global()?;
        Ok(())
    }

    fn refresh_global_positions(&mut self) -> Result<(), String> {
        let player = self
            .entities
            .transform(PLAYER_ENTITY)
            .expect("player always retains its Engine transform")
            .translation;
        self.player_global = self
            .origin
            .global_from_local(player.to_array())
            .map_err(|error| format!("update global player position: {error}"))?;
        self.refresh_platform_global()
    }

    fn refresh_platform_global(&mut self) -> Result<(), String> {
        let platform = self
            .entities
            .transform(PLATFORM_ENTITY)
            .expect("moving platform always retains its Engine transform")
            .translation;
        self.platform_global = self
            .origin
            .global_from_local(platform.to_array())
            .map_err(|error| format!("update global platform position: {error}"))?;
        Ok(())
    }

    #[cfg(test)]
    fn capsule_bounds(&self) -> ([f64; 3], [f64; 3]) {
        let transform = self
            .entities
            .transform(PLAYER_ENTITY)
            .expect("player always retains its Engine transform");
        let stance = self
            .entities
            .character_motion(PLAYER_ENTITY)
            .expect("player always retains its Engine motion")
            .stance;
        let height = match stance {
            CharacterStance::Standing => self.config.shape.standing_height,
            CharacterStance::Crouched => self.config.shape.crouched_height,
        };
        let center = vec3_array(transform.translation);
        let radius = f64::from(self.config.shape.radius);
        let half_height = f64::from(height) * 0.5;
        (
            [
                center[0] - radius,
                center[1] - half_height,
                center[2] - radius,
            ],
            [
                center[0] + radius,
                center[1] + half_height,
                center[2] + radius,
            ],
        )
    }

    fn global_capsule_bounds(&self) -> ([f64; 3], [f64; 3]) {
        let stance = self
            .entities
            .character_motion(PLAYER_ENTITY)
            .expect("player always retains its Engine motion")
            .stance;
        let height = match stance {
            CharacterStance::Standing => self.config.shape.standing_height,
            CharacterStance::Crouched => self.config.shape.crouched_height,
        };
        let center = self.player_global.to_world();
        let radius = f64::from(self.config.shape.radius);
        let half_height = f64::from(height) * 0.5;
        (
            [
                center[0] - radius,
                center[1] - half_height,
                center[2] - radius,
            ],
            [
                center[0] + radius,
                center[1] + half_height,
                center[2] + radius,
            ],
        )
    }
}

pub fn craftsurvive_controller_config() -> CharacterControllerConfig {
    let mut config = CharacterControllerConfig::responsive_fps();
    config.shape.standing_height = 1.75;
    config.shape.crouched_height = 1.0;
    config.shape.radius = 0.3;
    config.shape.contact_skin = 0.015;
    config.ground.forward_speed = 7.0;
    config.ground.backward_speed = 7.0;
    config.ground.strafe_speed = 7.0;
    config.ground.acceleration = 48.0;
    config.ground.braking = 58.0;
    config.ground.friction = 9.0;
    config.air.maximum_speed = 7.0;
    config.air.acceleration = 10.0;
    config.air.wish_speed_cap = 7.0;
    config.vertical.gravity = 24.0;
    config.vertical.jump_speed = 8.5;
    config.vertical.terminal_fall_speed = 24.0;
    config.surface.maximum_slope_radians = 50.0_f32.to_radians();
    config.surface.maximum_step_height = 1.05;
    config.surface.floor_snap_distance = 0.25;
    config.surface.floor_snap_speed_limit = 10.0;
    config.external_motion.external_decay_per_second = 3.0;
    config
}

fn eye_offset(config: &CharacterControllerConfig, stance: CharacterStance) -> f32 {
    let eye_height = match stance {
        CharacterStance::Standing => STANDING_EYE_HEIGHT,
        CharacterStance::Crouched => CROUCHED_EYE_HEIGHT,
    };
    let height = match stance {
        CharacterStance::Standing => config.shape.standing_height,
        CharacterStance::Crouched => config.shape.crouched_height,
    };
    eye_height - height * 0.5
}

fn finite_f32(value: f64) -> Result<f32, String> {
    let narrowed = value as f32;
    if value.is_finite() && narrowed.is_finite() {
        Ok(narrowed)
    } else {
        Err("player input or pose is outside the finite f32 range".to_owned())
    }
}

fn vec3_array(value: Vec3) -> [f64; 3] {
    [f64::from(value.x), f64::from(value.y), f64::from(value.z)]
}

fn vec3(value: [f32; 3]) -> Vec3 {
    Vec3::new(value[0], value[1], value[2])
}

fn stance_name(value: CharacterStance) -> &'static str {
    match value {
        CharacterStance::Standing => "standing",
        CharacterStance::Crouched => "crouched",
    }
}

fn collision_source_name(value: CharacterCollisionSource) -> &'static str {
    match value {
        CharacterCollisionSource::VoxelChunk(_) => "voxel",
        CharacterCollisionSource::StaticMesh { .. } => "staticMesh",
        CharacterCollisionSource::ActiveEntity(_) => "activeEntity",
    }
}

fn block_name(value: CharacterBlockKind) -> &'static str {
    match value {
        CharacterBlockKind::Wall => "wall",
        CharacterBlockKind::Ceiling => "ceiling",
        CharacterBlockKind::SteepSlope => "steepSlope",
        CharacterBlockKind::StartSolid => "startSolid",
        CharacterBlockKind::SolverBudget => "solverBudget",
    }
}

fn aabb_intersects(
    left_min: [f64; 3],
    left_max: [f64; 3],
    right_min: [f64; 3],
    right_max: [f64; 3],
) -> bool {
    (0..3).all(|axis| left_min[axis] < right_max[axis] && left_max[axis] > right_min[axis])
}

#[cfg(test)]
mod tests {
    use super::*;

    fn floor(extra: impl IntoIterator<Item = [i64; 3]>) -> VoxelCollisionScene {
        let mut voxels = Vec::new();
        for x in -8..=8 {
            for z in -8..=8 {
                voxels.push([x, 0, z]);
            }
        }
        voxels.extend(extra);
        VoxelCollisionScene::from_solid_voxels(1.0, 16, voxels).unwrap()
    }

    fn ramp(rise: f64) -> VoxelCollisionScene {
        use rusty_engine::svc_collision::{
            StaticMeshAssetId, StaticMeshColliderAsset, StaticMeshColliderInstance,
            StaticMeshInstanceId, StaticMeshTransform,
        };

        let mut scene = floor([]);
        let asset = StaticMeshColliderAsset::new(
            StaticMeshAssetId(1),
            vec![
                [-2.0, 1.0, 0.0],
                [2.0, 1.0, 0.0],
                [-2.0, 1.0 + rise, -4.0],
                [2.0, 1.0 + rise, -4.0],
            ],
            vec![[0, 1, 2], [1, 3, 2]],
        )
        .unwrap();
        let geometry_hash = asset.geometry_hash;
        scene
            .replace_static_mesh_colliders(
                0,
                [asset],
                [StaticMeshColliderInstance {
                    id: StaticMeshInstanceId(1),
                    asset: StaticMeshAssetId(1),
                    expected_geometry_hash: geometry_hash,
                    transform: StaticMeshTransform::IDENTITY,
                }],
            )
            .unwrap();
        scene
    }

    fn controller(position: [f64; 3], yaw_degrees: f64) -> PlayerController {
        let mut player = PlayerController::new(PlayerPose {
            position,
            yaw_degrees,
            pitch_degrees: 0.0,
        })
        .unwrap();
        for _ in 0..2 {
            player
                .step(
                    &floor([]),
                    PlayerInput::default(),
                    f64::from(FIXED_STEP_SECONDS),
                )
                .unwrap();
        }
        player
    }

    fn run(
        controller: &mut PlayerController,
        scene: &VoxelCollisionScene,
        input: PlayerInput,
        steps: usize,
    ) {
        for _ in 0..steps {
            controller
                .step(scene, input, f64::from(FIXED_STEP_SECONDS))
                .unwrap();
        }
    }

    #[test]
    fn checked_product_tuning_is_valid_and_motion_is_engine_owned() {
        craftsurvive_controller_config().validate().unwrap();
        let scene = floor([]);
        let mut player = controller([0.5, 2.55, 0.5], 0.0);
        let before_revision = player.revision();
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            24,
        );
        assert!(player.pose().position[2] < 0.0);
        assert!(player.motion().grounded, "{:?}", player.motion());
        assert!(player.revision() > before_revision);
        assert_ne!(player.motion().collision_world_hash, 0);
    }

    #[test]
    fn far_global_motion_stays_exact_while_repeated_rebases_bound_local_space() {
        let mut scene = VoxelCollisionScene::from_solid_voxels(1.0, 16, []).unwrap();
        let mut player = PlayerController::new(PlayerPose {
            position: [262_080.5, 7.0, 7.0],
            yaw_degrees: 0.0,
            pitch_degrees: 0.0,
        })
        .unwrap();
        assert!(player.rebase_world(&mut scene).unwrap());
        let first_origin = player.world_origin();
        assert_eq!(first_origin.origin.cell()[0], 262_080);
        assert!(player.local_pose().position[0].abs() < 1.0);

        let start = player.pose().position;
        for _ in 0..1_200 {
            player
                .step(
                    &scene,
                    PlayerInput {
                        forward: 1.0,
                        sprint: true,
                        ..PlayerInput::default()
                    },
                    f64::from(FIXED_STEP_SECONDS),
                )
                .unwrap();
            player.rebase_world(&mut scene).unwrap();
        }

        let global = player.pose().position;
        let local = player.local_pose().position;
        assert_eq!(global[0], start[0]);
        assert!(global[2] < start[2] - 60.0);
        assert!(local[0].abs() < REBASE_THRESHOLD as f64);
        assert!(local[2].abs() < REBASE_THRESHOLD as f64);
        assert!(player.world_origin().revision >= first_origin.revision + 2);
        assert_eq!(player.revision(), player.entities.revision());
    }

    #[test]
    fn diagonal_input_is_normalized_and_look_uses_canonical_basis() {
        let scene = floor([]);
        let mut straight = controller([0.5, 2.55, 0.5], 0.0);
        let mut diagonal = controller([0.5, 2.55, 0.5], 0.0);
        run(
            &mut straight,
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            60,
        );
        run(
            &mut diagonal,
            &scene,
            PlayerInput {
                forward: 1.0,
                right: 1.0,
                ..PlayerInput::default()
            },
            60,
        );
        let straight_distance = (straight.pose().position[2] - 0.5).abs();
        let diagonal_pose = diagonal.pose().position;
        let diagonal_distance = (diagonal_pose[0] - 0.5).hypot(diagonal_pose[2] - 0.5);
        assert!(diagonal_distance <= straight_distance + 0.08);
        assert!(diagonal_distance >= straight_distance * 0.75);

        diagonal
            .step(
                &scene,
                PlayerInput {
                    yaw_delta_degrees: 90.0,
                    ..PlayerInput::default()
                },
                0.0,
            )
            .unwrap();
        assert!(diagonal.view_direction()[0] > 0.999);
    }

    #[test]
    fn jump_crouch_blocked_stand_and_external_impulse_are_reported() {
        let low_ceiling = floor((-1..=1).flat_map(|x| (-1..=1).map(move |z| [x, 2, z])));
        let mut player = controller([0.5, 2.55, 0.5], 0.0);
        run(
            &mut player,
            &low_ceiling,
            PlayerInput {
                crouch: true,
                ..PlayerInput::default()
            },
            2,
        );
        assert_eq!(player.motion().stance, "crouched");
        run(&mut player, &low_ceiling, PlayerInput::default(), 2);
        assert_eq!(player.motion().stance, "crouched");
        assert!(player.motion().blocked_stand);

        let scene = floor([]);
        let mut jumping = controller([0.5, 2.55, 0.5], 0.0);
        run(
            &mut jumping,
            &scene,
            PlayerInput {
                jump: true,
                ..PlayerInput::default()
            },
            2,
        );
        assert!(!jumping.motion().grounded);
        assert!(jumping.motion().velocity[1] > 0.0);
        let before_x = jumping.pose().position[0];
        run(
            &mut jumping,
            &scene,
            PlayerInput {
                impulse: true,
                ..PlayerInput::default()
            },
            2,
        );
        assert!(jumping.pose().position[0] > before_x);
    }

    #[test]
    fn wall_and_step_facts_come_from_engine_receipts_without_penetration() {
        let scene = floor((1..=3).flat_map(|y| (-8..=8).map(move |z| [1, y, z])));
        let mut player = controller([0.5, 2.55, 0.5], 90.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                right: 1.0,
                ..PlayerInput::default()
            },
            60,
        );
        assert!(player.pose().position[0] < 0.72);
        assert!(player.motion().blocks.contains(&"wall"));

        let step_scene = floor([[1, 1, 0]]);
        let mut stepper = controller([0.5, 2.55, 0.5], 90.0);
        let mut accepted = false;
        let mut maximum_eye_y = stepper.pose().position[1];
        for _ in 0..120 {
            stepper
                .step(
                    &step_scene,
                    PlayerInput {
                        forward: 1.0,
                        ..PlayerInput::default()
                    },
                    f64::from(FIXED_STEP_SECONDS),
                )
                .unwrap();
            accepted |= stepper.motion().step_accepted;
            maximum_eye_y = maximum_eye_y.max(stepper.pose().position[1]);
        }
        assert!(accepted);
        assert!(maximum_eye_y > 3.4);
    }

    #[test]
    fn product_platform_is_an_active_engine_support_and_carries_the_player() {
        let scene = VoxelCollisionScene::from_solid_voxels(1.0, 16, []).unwrap();
        let mut player = PlayerController::new(PlayerPose {
            position: [0.0, 6.05, 9.0],
            yaw_degrees: 0.0,
            pitch_degrees: 0.0,
        })
        .unwrap();
        let before = player.pose().position;
        run(&mut player, &scene, PlayerInput::default(), 8);
        let motion = player.motion();
        assert!(motion.grounded);
        assert_eq!(motion.ground_source, Some("activeEntity"));
        assert_eq!(motion.platform_entity, Some(PLATFORM_ENTITY.raw()));
        assert!(player.pose().position[0] > before[0]);
        assert!(motion.platform_displacement[0] > 0.0);
    }

    #[test]
    fn product_slope_limit_accepts_a_ramp_and_rejects_an_over_limit_face() {
        let mut legal = controller([0.5, 2.55, 0.0], 0.0);
        run(
            &mut legal,
            &ramp(2.0),
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            90,
        );
        assert!(legal.pose().position[2] < -2.0);
        assert!(legal.pose().position[1] > 3.2);

        let mut illegal = controller([0.5, 2.55, 0.0], 0.0);
        run(
            &mut illegal,
            &ramp(8.0),
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            90,
        );
        assert!(illegal.pose().position[2] > -0.5);
        assert!(illegal.motion().blocks.contains(&"steepSlope"));
    }

    #[test]
    fn open_ledge_loses_support_without_phantom_floor_snap() {
        let voxels = (-4..=4).flat_map(|x| (0..=4).map(move |z| [x, 0, z]));
        let scene = VoxelCollisionScene::from_solid_voxels(1.0, 16, voxels).unwrap();
        let mut player = controller([0.5, 2.55, 0.8], 0.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            70,
        );
        assert!(player.pose().position[2] < -0.5);
        assert!(!player.motion().grounded);
        assert!(player.motion().velocity[1] < 0.0);
    }

    #[test]
    fn inside_corner_stops_without_snagging_or_penetration() {
        let walls = (1..=3)
            .flat_map(|y| (-8..=8).map(move |z| [1, y, z]))
            .chain((1..=3).flat_map(|y| (-8..=8).map(move |x| [x, y, -2])));
        let scene = floor(walls);
        let mut player = controller([0.5, 2.55, 0.5], 45.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            90,
        );
        let position = player.pose().position;
        assert!(position[0] < 0.72);
        assert!(position[2] > -1.72);
        assert!(player.motion().blocks.contains(&"wall"));
        assert!(!player.overlaps_scene(&scene));
    }
}
