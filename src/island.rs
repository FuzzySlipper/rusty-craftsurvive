use rusty_engine::engine_spatial::MaterialVoxel;
use std::collections::BTreeMap;

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
    let mut voxels = BTreeMap::new();
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
                voxels.insert([x, y, z], material_slot);
            }
        }
    }
    install_playable_route(&mut voxels);
    voxels
        .into_iter()
        .map(|(address, material_slot)| MaterialVoxel {
            address,
            material_slot,
        })
        .collect()
}

fn install_playable_route(voxels: &mut BTreeMap<[i64; 3], u16>) {
    // An ordinary, visible part of the island: the fixed spawn lane gives
    // players and black-box playtests a repeatable trench, wall, and landmarks.
    voxels.retain(|address, _| {
        !((-3..=3).contains(&address[0]) && (2..=10).contains(&address[2]) && address[1] >= -4)
    });
    for x in -3..=3 {
        for z in 2..=10 {
            for y in -4..=3 {
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
    // One-voxel trench across the center lane. A grounded jump cleanly clears it.
    for x in -1..=1 {
        voxels.remove(&[x, 3, 5]);
    }
    // The cell before the wall is a thin bridge over a two-voxel drop. Removing
    // its visible top support exercises gravity rather than ordinary step-down.
    for x in -1..=1 {
        voxels.remove(&[x, 2, 4]);
    }
    // A wall beyond the trench provides an unambiguous collision stop.
    for x in -1..=1 {
        for y in 4..=6 {
            voxels.insert([x, y, 3], 3);
        }
    }
    // Unequal side pillars make spawn orientation visible without HUD knowledge.
    for y in 4..=5 {
        voxels.insert([-3, y, 8], 2);
    }
    for y in 4..=7 {
        voxels.insert([3, y, 8], 3);
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
        assert!(first.iter().any(|voxel| voxel.material_slot == 3));
        assert!(!first.iter().any(|voxel| voxel.address == [0, 3, 5]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 2, 5]));
        assert!(!first.iter().any(|voxel| voxel.address == [0, 2, 4]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 1, 4]));
        assert!(first.iter().any(|voxel| voxel.address == [0, 6, 3]));
    }
}
