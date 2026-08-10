use rusty_engine::engine_spatial::VoxelCollisionScene;

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerPose {
    pub position: [f64; 3],
    pub yaw_degrees: f64,
    pub pitch_degrees: f64,
}

impl Default for PlayerPose {
    fn default() -> Self {
        Self {
            position: [0.0, 9.0, 18.0],
            yaw_degrees: 180.0,
            pitch_degrees: -18.0,
        }
    }
}

#[derive(Debug, Clone, Copy, Default, PartialEq)]
pub struct PlayerInput {
    pub forward: f64,
    pub right: f64,
    pub vertical: f64,
    pub yaw_delta_degrees: f64,
    pub pitch_delta_degrees: f64,
}

#[derive(Debug, Clone, Copy, PartialEq)]
pub struct PlayerController {
    pose: PlayerPose,
    speed_units_per_second: f64,
}

impl Default for PlayerController {
    fn default() -> Self {
        Self {
            pose: PlayerPose::default(),
            speed_units_per_second: 7.0,
        }
    }
}

impl PlayerController {
    pub const fn pose(&self) -> PlayerPose {
        self.pose
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

        let yaw = self.pose.yaw_degrees.to_radians();
        let (sin_yaw, cos_yaw) = yaw.sin_cos();
        let distance = self.speed_units_per_second * delta_seconds;
        let delta = [
            (sin_yaw * input.forward + cos_yaw * input.right) * distance,
            input.vertical * distance,
            (-cos_yaw * input.forward + sin_yaw * input.right) * distance,
        ];
        for axis in 0..3 {
            let mut candidate = self.pose.position;
            candidate[axis] += delta[axis];
            if !collides(scene, candidate) {
                self.pose.position = candidate;
            }
        }
    }
}

fn collides(scene: &VoxelCollisionScene, eye: [f64; 3]) -> bool {
    scene.aabb_overlaps_solid(
        [eye[0] - 0.3, eye[1] - 1.55, eye[2] - 0.3],
        [eye[0] + 0.3, eye[1] + 0.2, eye[2] + 0.3],
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn movement_is_rust_owned_and_stops_at_voxel_collision() {
        let scene = VoxelCollisionScene::from_solid_voxels(1.0, 8, [[0, 0, 0]]).unwrap();
        let mut controller = PlayerController {
            pose: PlayerPose {
                position: [0.0, 1.6, 1.0],
                yaw_degrees: 0.0,
                pitch_degrees: 0.0,
            },
            speed_units_per_second: 10.0,
        };
        controller.step(
            &scene,
            PlayerInput {
                forward: 1.0,
                ..PlayerInput::default()
            },
            0.05,
        );
        assert_eq!(controller.pose().position, [0.0, 1.6, 1.0]);
    }
}
