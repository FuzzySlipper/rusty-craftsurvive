use rusty_engine::engine_spatial::MaterialVoxel;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct IslandConfig {
    pub radius: i64,
    pub depth: i64,
    pub summit_height: i64,
    pub seed: u64,
}

impl Default for IslandConfig {
    fn default() -> Self {
        Self {
            radius: 12,
            depth: 4,
            summit_height: 5,
            seed: 0x4352_4146_5453_5552,
        }
    }
}

pub fn generate_island(config: IslandConfig) -> Vec<MaterialVoxel> {
    let mut voxels = Vec::new();
    let radius_squared = config.radius * config.radius;
    for x in -config.radius..=config.radius {
        for z in -config.radius..=config.radius {
            let distance_squared = x * x + z * z;
            if distance_squared > radius_squared {
                continue;
            }
            let radial_height = ((radius_squared - distance_squared) * config.summit_height)
                / radius_squared.max(1);
            let variation = (coordinate_hash(config.seed, x, z) % 3) as i64 - 1;
            let top = (radial_height + variation).max(0);
            for y in -config.depth..=top {
                let material_slot = if y == top {
                    1
                } else if y >= top - 2 {
                    2
                } else {
                    3
                };
                voxels.push(MaterialVoxel {
                    address: [x, y, z],
                    material_slot,
                });
            }
        }
    }
    voxels
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
        assert!(first.iter().any(|voxel| voxel.material_slot == 3));
    }
}
