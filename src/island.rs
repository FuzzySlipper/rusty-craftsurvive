use std::collections::BTreeMap;

use rusty_engine::engine_spatial::MaterialVoxel;

use crate::TerrainConfig;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct IslandConfig {
    pub radius: i64,
    pub depth: i64,
    pub summit_height: i64,
    pub seed: u64,
}

impl Default for IslandConfig {
    fn default() -> Self {
        Self::from(TerrainConfig::default())
    }
}

impl From<TerrainConfig> for IslandConfig {
    fn from(value: TerrainConfig) -> Self {
        Self {
            radius: i64::from(value.size / 2),
            depth: 9,
            summit_height: 12,
            seed: value.seed,
        }
    }
}

pub fn generate_island(config: IslandConfig) -> Vec<MaterialVoxel> {
    let mut voxels = BTreeMap::new();
    let mut heights = BTreeMap::new();
    let radius_squared = config.radius * config.radius;
    for x in -config.radius..=config.radius {
        for z in -config.radius..=config.radius {
            let distance_squared = x * x + z * z;
            if distance_squared <= radius_squared {
                heights.insert([x, z], terrain_height(config, x, z, distance_squared));
            }
        }
    }
    for (&[x, z], &top) in &heights {
        let slope = cardinal_slope(&heights, x, z, top);
        for y in -config.depth..=top {
            let depth_from_surface = top - y;
            let material_slot = if depth_from_surface == 0 && slope < 3 {
                1
            } else if depth_from_surface <= 3 && slope < 4 {
                2
            } else {
                3
            };
            voxels.insert([x, y, z], material_slot);
        }
    }
    install_playable_route(&mut voxels, config.depth);
    install_landmarks(&mut voxels, &heights, config);
    voxels
        .into_iter()
        .map(|(address, material_slot)| MaterialVoxel {
            address,
            material_slot,
        })
        .collect()
}

fn terrain_height(config: IslandConfig, x: i64, z: i64, distance_squared: i64) -> i64 {
    let radius = config.radius.max(1) as f64;
    let edge = (1.0 - (distance_squared as f64).sqrt() / radius).clamp(0.0, 1.0);
    let broad = value_noise(config.seed, x, z, 20);
    let rolling = value_noise(config.seed ^ 0xa076_1d64_78bd_642f, x, z, 9);
    let detail = value_noise(config.seed ^ 0xe703_7ed1_a0b4_28db, x, z, 4);
    let ridge = 1.0 - (rolling * 2.0 - 1.0).abs();
    let basin_center = [config.radius / 3, -config.radius / 4];
    let basin_distance =
        (((x - basin_center[0]).pow(2) + (z - basin_center[1]).pow(2)) as f64).sqrt();
    let basin = (1.0 - basin_distance / (radius * 0.22)).clamp(0.0, 1.0);
    let height = -2.0
        + edge.powf(0.55) * config.summit_height as f64
        + (broad - 0.5) * 8.0
        + ridge * 3.0
        + (detail - 0.5) * 2.0
        - basin * 4.0;
    height.round().max(-2.0) as i64
}

fn value_noise(seed: u64, x: i64, z: i64, scale: i64) -> f64 {
    let cell_x = x.div_euclid(scale);
    let cell_z = z.div_euclid(scale);
    let local_x = x.rem_euclid(scale) as f64 / scale as f64;
    let local_z = z.rem_euclid(scale) as f64 / scale as f64;
    let blend_x = smoothstep(local_x);
    let blend_z = smoothstep(local_z);
    let sample = |dx, dz| hash_unit(coordinate_hash(seed, cell_x + dx, cell_z + dz));
    let near = lerp(sample(0, 0), sample(1, 0), blend_x);
    let far = lerp(sample(0, 1), sample(1, 1), blend_x);
    lerp(near, far, blend_z)
}

fn smoothstep(value: f64) -> f64 {
    value * value * (3.0 - 2.0 * value)
}

fn lerp(left: f64, right: f64, amount: f64) -> f64 {
    left + (right - left) * amount
}

fn hash_unit(value: u64) -> f64 {
    (value >> 11) as f64 / ((1_u64 << 53) - 1) as f64
}

fn cardinal_slope(heights: &BTreeMap<[i64; 2], i64>, x: i64, z: i64, top: i64) -> i64 {
    [[x - 1, z], [x + 1, z], [x, z - 1], [x, z + 1]]
        .into_iter()
        .filter_map(|address| heights.get(&address))
        .map(|neighbor| (top - neighbor).abs())
        .max()
        .unwrap_or_default()
}

fn install_playable_route(voxels: &mut BTreeMap<[i64; 3], u16>, depth: i64) {
    voxels.retain(|address, _| {
        !((-3..=3).contains(&address[0]) && (2..=10).contains(&address[2]) && address[1] >= -depth)
    });
    for x in -3..=3 {
        for z in 2..=10 {
            for y in -depth..=3 {
                let material_slot = if y == 3 {
                    1
                } else if y >= 1 {
                    2
                } else {
                    3
                };
                voxels.insert([x, y, z], material_slot);
            }
        }
    }
    for x in -1..=1 {
        voxels.remove(&[x, 3, 5]);
        voxels.remove(&[x, 2, 4]);
    }
    // The moving-platform station is ordinary product geometry. The active platform bridges a
    // shallow recess immediately beyond the original spawn lane and remains visible/playable in
    // every presentation mode.
    for x in -3..=3 {
        for z in 8..=10 {
            for y in 2..=24 {
                voxels.remove(&[x, y, z]);
            }
            // Keep a full standing-capsule clearance below the platform sweep so an actor
            // displaced off its deck cannot be trapped between the lower floor and moving side.
            for y in -depth..=1 {
                voxels.insert([x, y, z], if y == 1 { 2 } else { 3 });
            }
        }
    }
    for x in -1..=1 {
        for y in 4..=6 {
            voxels.insert([x, y, 3], 3);
        }
    }
    for y in 4..=5 {
        voxels.insert([-3, y, 8], 2);
    }
    for y in 4..=7 {
        voxels.insert([3, y, 8], 3);
    }
}

fn install_landmarks(
    voxels: &mut BTreeMap<[i64; 3], u16>,
    heights: &BTreeMap<[i64; 2], i64>,
    config: IslandConfig,
) {
    let distance = config.radius * 2 / 3;
    for (x, z, material_slot, height) in [
        (-distance, 0, 3, 8),
        (distance, 0, 2, 6),
        (0, -distance, 3, 10),
    ] {
        let Some(&surface) = heights.get(&[x, z]) else {
            continue;
        };
        for y in surface + 1..=surface + height {
            voxels.insert([x, y, z], material_slot);
        }
    }
}

fn coordinate_hash(seed: u64, x: i64, z: i64) -> u64 {
    let mut value = seed ^ (x as u64).wrapping_mul(0x9e37_79b9_7f4a_7c15);
    value ^= (z as u64)
        .rotate_left(29)
        .wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value ^= value >> 30;
    value = value.wrapping_mul(0xbf58_476d_1ce4_e5b9);
    value ^= value >> 27;
    value.wrapping_mul(0x94d0_49bb_1331_11eb) ^ (value >> 31)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn island_generation_is_bounded_deterministic_and_materialized() {
        let config = IslandConfig::default();
        let first = generate_island(config);
        let second = generate_island(config);
        assert_eq!(first, second);
        assert!(!first.is_empty());
        assert!(first.iter().all(|voxel| {
            voxel.address[0].abs() <= config.radius
                && voxel.address[2].abs() <= config.radius
                && voxel.material_slot > 0
        }));
        assert!(first.iter().any(|voxel| voxel.material_slot == 1));
        assert!(first.iter().any(|voxel| voxel.material_slot == 2));
        assert!(first.iter().any(|voxel| voxel.material_slot == 3));
        assert!(!first.iter().any(|voxel| voxel.address == [0, 3, 5]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 2, 5]));
        assert!(!first.iter().any(|voxel| voxel.address == [0, 2, 4]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 1, 4]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 6, 3]));
    }

    #[test]
    fn alternate_seed_changes_bounded_terrain_without_changing_spawn_route() {
        let first_config = IslandConfig::from(TerrainConfig::new(1, 64).unwrap());
        let second_config = IslandConfig::from(TerrainConfig::new(2, 64).unwrap());
        let first = generate_island(first_config);
        let second = generate_island(second_config);
        assert_ne!(first, second);
        for terrain in [&first, &second] {
            assert!(terrain.iter().all(|voxel| {
                voxel.address[0].abs() <= first_config.radius
                    && voxel.address[2].abs() <= first_config.radius
            }));
            assert!(terrain.iter().any(|voxel| voxel.address == [0, 3, 7]));
            assert!(!terrain.iter().any(|voxel| {
                voxel.address[0] == 0 && voxel.address[2] == 7 && voxel.address[1] > 3
            }));
        }
    }
}
