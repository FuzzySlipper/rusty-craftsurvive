use rusty_engine::{
    core_math::Vec3,
    engine_spatial::{KinematicCollisionQuery, VoxelCollisionScene},
};

const FIXED_STEP_SECONDS: f64 = 1.0 / 120.0;
const GRAVITY_UNITS_PER_SECOND_SQUARED: f64 = -24.0;
const JUMP_SPEED_UNITS_PER_SECOND: f64 = 8.5;
const TERMINAL_FALL_SPEED_UNITS_PER_SECOND: f64 = -24.0;
const CAPSULE_RADIUS: f64 = 0.3;
const COLLISION_SKIN: f64 = 0.001;
const EYE_TO_FOOT: f64 = 1.55;
const EYE_TO_HEAD: f64 = 0.2;
const STEP_HEIGHT: f64 = 1.05;
const STEP_INCREMENT: f64 = 0.05;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerPose {
    pub position: [f64; 3],
    pub yaw_degrees: f64,
    pub pitch_degrees: f64,
}

impl Default for PlayerPose {
    fn default() -> Self {
        Self {
            position: [0.0, 7.0, 7.0],
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
    pub yaw_delta_degrees: f64,
    pub pitch_delta_degrees: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerMotionReadout {
    pub grounded: bool,
    pub velocity: [f64; 3],
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerController {
    pose: PlayerPose,
    velocity: [f64; 3],
    grounded: bool,
    jump_held: bool,
    fixed_step_accumulator: f64,
    speed_units_per_second: f64,
}

impl Default for PlayerController {
    fn default() -> Self {
        Self::new(PlayerPose::default())
    }
}

impl PlayerController {
    pub const fn new(pose: PlayerPose) -> Self {
        Self {
            pose,
            velocity: [0.0; 3],
            grounded: false,
            jump_held: false,
            fixed_step_accumulator: 0.0,
            speed_units_per_second: 7.0,
        }
    }

    pub const fn pose(&self) -> PlayerPose {
        self.pose
    }

    pub const fn motion(&self) -> PlayerMotionReadout {
        PlayerMotionReadout {
            grounded: self.grounded,
            velocity: self.velocity,
        }
    }

    pub fn overlaps_voxel(&self, address: [i64; 3], voxel_size: f64) -> bool {
        let (player_min, player_max) = capsule_bounds(self.pose.position);
        let voxel_min = address.map(|coordinate| coordinate as f64 * voxel_size);
        let voxel_max = voxel_min.map(|coordinate| coordinate + voxel_size);
        aabb_intersects(player_min, player_max, voxel_min, voxel_max)
    }

    pub fn view_direction(&self) -> [f64; 3] {
        let yaw = self.pose.yaw_degrees.to_radians();
        let pitch = self.pose.pitch_degrees.to_radians();
        let (sin_yaw, cos_yaw) = yaw.sin_cos();
        let (sin_pitch, cos_pitch) = pitch.sin_cos();
        [sin_yaw * cos_pitch, sin_pitch, -cos_yaw * cos_pitch]
    }

    pub fn step(&mut self, scene: &VoxelCollisionScene, input: PlayerInput, delta_seconds: f64) {
        let delta_seconds = delta_seconds.clamp(0.0, 0.05);
        self.pose.yaw_degrees += input.yaw_delta_degrees;
        self.pose.pitch_degrees =
            (self.pose.pitch_degrees + input.pitch_delta_degrees).clamp(-89.0, 89.0);
        if !input.jump {
            self.jump_held = false;
        }

        self.fixed_step_accumulator += delta_seconds;
        while self.fixed_step_accumulator + f64::EPSILON >= FIXED_STEP_SECONDS {
            self.fixed_step(scene, input);
            self.fixed_step_accumulator -= FIXED_STEP_SECONDS;
        }
    }

    fn fixed_step(&mut self, scene: &VoxelCollisionScene, input: PlayerInput) {
        let (forward, right) = normalized_axes(input.forward, input.right);
        let yaw = self.pose.yaw_degrees.to_radians();
        let (sin_yaw, cos_yaw) = yaw.sin_cos();
        self.velocity[0] = (sin_yaw * forward + cos_yaw * right) * self.speed_units_per_second;
        self.velocity[2] = (-cos_yaw * forward + sin_yaw * right) * self.speed_units_per_second;

        let was_grounded = self.grounded;
        let jump_admitted = input.jump && !self.jump_held && self.grounded;
        if input.jump {
            self.jump_held = true;
        }
        if jump_admitted {
            self.velocity[1] = JUMP_SPEED_UNITS_PER_SECOND;
            self.grounded = false;
        }

        let horizontal_delta = [
            self.velocity[0] * FIXED_STEP_SECONDS,
            0.0,
            self.velocity[2] * FIXED_STEP_SECONDS,
        ];
        self.move_horizontal_axis(
            scene,
            0,
            horizontal_delta[0],
            was_grounded && !jump_admitted,
        );
        self.move_horizontal_axis(
            scene,
            2,
            horizontal_delta[2],
            was_grounded && !jump_admitted,
        );

        if was_grounded && !jump_admitted && self.snap_down(scene, STEP_HEIGHT) {
            self.grounded = true;
            self.velocity[1] = 0.0;
        } else {
            self.velocity[1] = (self.velocity[1]
                + GRAVITY_UNITS_PER_SECOND_SQUARED * FIXED_STEP_SECONDS)
                .max(TERMINAL_FALL_SPEED_UNITS_PER_SECOND);
            let vertical_delta = self.velocity[1] * FIXED_STEP_SECONDS;
            if self.sweep_blocked(scene, [0.0, vertical_delta, 0.0]) {
                self.grounded = vertical_delta < 0.0;
                self.velocity[1] = 0.0;
            } else {
                self.pose.position[1] += vertical_delta;
                self.grounded = false;
            }
        }

        debug_assert!(!collides(scene, self.pose.position));
    }

    fn move_horizontal_axis(
        &mut self,
        scene: &VoxelCollisionScene,
        axis: usize,
        distance: f64,
        may_step: bool,
    ) {
        if distance == 0.0 {
            return;
        }
        let mut translation = [0.0; 3];
        translation[axis] = distance;
        if !self.sweep_blocked(scene, translation) {
            self.pose.position[axis] += distance;
            return;
        }
        self.velocity[axis] = 0.0;
        if !may_step {
            return;
        }

        let original = self.pose.position;
        let mut height = STEP_INCREMENT;
        while height <= STEP_HEIGHT + f64::EPSILON {
            self.pose.position = original;
            if self.sweep_blocked(scene, [0.0, height, 0.0]) {
                break;
            }
            self.pose.position[1] += height;
            if !self.sweep_blocked(scene, translation) {
                self.pose.position[axis] += distance;
                if self.snap_down(scene, height + STEP_INCREMENT) {
                    self.velocity[axis] = distance / FIXED_STEP_SECONDS;
                    self.grounded = true;
                    return;
                }
            }
            height += STEP_INCREMENT;
        }
        self.pose.position = original;
    }

    fn snap_down(&mut self, scene: &VoxelCollisionScene, max_distance: f64) -> bool {
        if !self.sweep_blocked(scene, [0.0, -max_distance, 0.0]) {
            return false;
        }
        let mut remaining = max_distance;
        while remaining > f64::EPSILON {
            let distance = remaining.min(STEP_INCREMENT);
            if self.sweep_blocked(scene, [0.0, -distance, 0.0]) {
                return true;
            }
            self.pose.position[1] -= distance;
            remaining -= distance;
        }
        true
    }

    fn sweep_blocked(&self, scene: &VoxelCollisionScene, translation: [f64; 3]) -> bool {
        let (min, max) = capsule_bounds(self.pose.position);
        scene.axis_sweep_blocked(
            Vec3::new(min[0] as f32, min[1] as f32, min[2] as f32),
            Vec3::new(max[0] as f32, max[1] as f32, max[2] as f32),
            Vec3::new(
                translation[0] as f32,
                translation[1] as f32,
                translation[2] as f32,
            ),
        )
    }
}

fn normalized_axes(forward: f64, right: f64) -> (f64, f64) {
    let length = forward.hypot(right);
    if length > 1.0 {
        (forward / length, right / length)
    } else {
        (forward, right)
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

fn capsule_bounds(eye: [f64; 3]) -> ([f64; 3], [f64; 3]) {
    (
        [
            eye[0] - CAPSULE_RADIUS + COLLISION_SKIN,
            eye[1] - EYE_TO_FOOT + COLLISION_SKIN,
            eye[2] - CAPSULE_RADIUS + COLLISION_SKIN,
        ],
        [
            eye[0] + CAPSULE_RADIUS - COLLISION_SKIN,
            eye[1] + EYE_TO_HEAD - COLLISION_SKIN,
            eye[2] + CAPSULE_RADIUS - COLLISION_SKIN,
        ],
    )
}

pub(crate) fn collides(scene: &VoxelCollisionScene, eye: [f64; 3]) -> bool {
    let (min, max) = capsule_bounds(eye);
    scene.aabb_overlaps_solid(min, max)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn floor(extra: impl IntoIterator<Item = [i64; 3]>) -> VoxelCollisionScene {
        let mut voxels = Vec::new();
        for x in -6..=6 {
            for z in -6..=6 {
                voxels.push([x, 0, z]);
            }
        }
        voxels.extend(extra);
        VoxelCollisionScene::from_solid_voxels(1.0, 16, voxels).unwrap()
    }

    fn controller(position: [f64; 3], yaw_degrees: f64) -> PlayerController {
        PlayerController {
            pose: PlayerPose {
                position,
                yaw_degrees,
                pitch_degrees: 0.0,
            },
            velocity: [0.0; 3],
            grounded: true,
            jump_held: false,
            fixed_step_accumulator: 0.0,
            speed_units_per_second: 7.0,
        }
    }

    fn run(
        controller: &mut PlayerController,
        scene: &VoxelCollisionScene,
        input: PlayerInput,
        steps: usize,
    ) {
        for _ in 0..steps {
            controller.step(scene, input, FIXED_STEP_SECONDS);
        }
    }

    #[test]
    fn level_walking_and_screen_relative_heading_are_deterministic() {
        let scene = floor([]);
        for (yaw, expected_axis, expected_sign) in [
            (0.0, 2, -1.0),
            (90.0, 0, 1.0),
            (180.0, 2, 1.0),
            (-90.0, 0, -1.0),
        ] {
            let mut player = controller([0.0, 2.55, 0.0], yaw);
            run(
                &mut player,
                &scene,
                PlayerInput {
                    forward: 1.0,
                    ..PlayerInput::default()
                },
                10,
            );
            assert!(player.pose().position[expected_axis] * expected_sign > 0.5);
            assert!(player.motion().grounded);
            assert!(!collides(&scene, player.pose().position));
        }
    }

    #[test]
    fn wall_collision_slides_without_penetration() {
        let scene = floor((1..=3).flat_map(|y| (-6..=6).map(move |z| [1, y, z])));
        let mut player = controller([0.0, 2.55, 0.0], 90.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                right: 1.0,
                ..PlayerInput::default()
            },
            40,
        );
        assert!(player.pose().position[0] < 0.71);
        assert!(player.pose().position[2] > 0.5);
        assert!(!collides(&scene, player.pose().position));
    }

    #[test]
    fn falling_lands_and_terminal_speed_is_bounded() {
        let scene = floor([]);
        let mut player = controller([0.0, 8.0, 0.0], 0.0);
        player.grounded = false;
        run(&mut player, &scene, PlayerInput::default(), 240);
        assert!(player.motion().grounded);
        assert_eq!(player.motion().velocity[1], 0.0);
        assert!(player.pose().position[1] >= 2.55);
        assert!(!collides(&scene, player.pose().position));

        let empty = VoxelCollisionScene::from_solid_voxels(1.0, 16, []).unwrap();
        player.pose.position = [0.0, 100.0, 0.0];
        player.grounded = false;
        run(&mut player, &empty, PlayerInput::default(), 1000);
        assert_eq!(
            player.motion().velocity[1],
            TERMINAL_FALL_SPEED_UNITS_PER_SECOND
        );
    }

    #[test]
    fn jump_is_grounded_edge_triggered_and_ceiling_is_rejected() {
        let scene = floor([]);
        let mut player = controller([0.0, 2.55, 0.0], 0.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                jump: true,
                ..PlayerInput::default()
            },
            20,
        );
        let first_apex_velocity = player.motion().velocity[1];
        assert!(player.pose().position[1] > 2.7);
        assert!(first_apex_velocity < JUMP_SPEED_UNITS_PER_SECOND);
        run(
            &mut player,
            &scene,
            PlayerInput {
                jump: true,
                ..PlayerInput::default()
            },
            140,
        );
        assert!(player.motion().grounded);
        assert_eq!(player.motion().velocity[1], 0.0);
        player.step(
            &scene,
            PlayerInput {
                jump: false,
                ..PlayerInput::default()
            },
            FIXED_STEP_SECONDS,
        );
        player.step(
            &scene,
            PlayerInput {
                jump: true,
                ..PlayerInput::default()
            },
            FIXED_STEP_SECONDS,
        );
        assert!(player.motion().velocity[1] > 0.0);

        let ceiling = floor((-1..=1).flat_map(|x| (-1..=1).map(move |z| [x, 3, z])));
        let mut under_ceiling = controller([0.0, 2.55, 0.0], 0.0);
        run(
            &mut under_ceiling,
            &ceiling,
            PlayerInput {
                jump: true,
                ..PlayerInput::default()
            },
            20,
        );
        assert!(under_ceiling.pose().position[1] < 2.81);
        assert!(!collides(&ceiling, under_ceiling.pose().position));
    }

    #[test]
    fn one_voxel_step_up_and_down_stays_grounded() {
        let scene = floor([[1, 1, 0]]);
        let mut player = controller([0.0, 2.55, 0.0], 90.0);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            30,
        );
        assert!(player.pose().position[0] > 1.0);
        assert!(player.pose().position[1] > 3.45);
        assert!(player.motion().grounded);
        run(
            &mut player,
            &scene,
            PlayerInput {
                forward: -1.0,
                ..PlayerInput::default()
            },
            30,
        );
        assert!(player.pose().position[1] < 2.65);
        assert!(player.motion().grounded);
        assert!(!collides(&scene, player.pose().position));
    }

    #[test]
    fn view_ray_matches_the_canonical_engine_camera_basis() {
        let mut player = controller([0.0; 3], 90.0);
        let rightward = player.view_direction();
        assert!(rightward[0] > 0.999);
        assert!(rightward[2].abs() < 0.001);
        player.pose.yaw_degrees = -90.0;
        assert!(player.view_direction()[0] < -0.999);
    }
}
