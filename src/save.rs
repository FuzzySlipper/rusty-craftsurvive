use std::collections::BTreeMap;

use rusty_engine::engine_spatial::{MAX_VOXEL_COORDINATE_ABS, MAX_VOXEL_MATERIAL_SLOT};
use serde::{Deserialize, Serialize};

use crate::island::TERRAIN_GENERATION_VERSION;

pub const TERRAIN_OVERLAY_SCHEMA_VERSION: u32 = 1;
pub const MAX_TERRAIN_OVERLAY_ENTRIES: usize = 65_536;
pub const MAX_TERRAIN_OVERLAY_BYTES: usize = 8 * 1024 * 1024;

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct TerrainOverlayDocument {
    schema_version: u32,
    generation_version: u32,
    seed: String,
    entries: Vec<TerrainOverlayEntry>,
    fingerprint: String,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields, rename_all = "camelCase")]
struct TerrainOverlayEntry {
    address: [i64; 3],
    material_slot: Option<u16>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TerrainOverlayError {
    TooLarge { limit: usize, actual: usize },
    Malformed(String),
    UnsupportedSchema { expected: u32, actual: u32 },
    UnsupportedGeneration { expected: u32, actual: u32 },
    SeedMismatch { expected: u64, actual: u64 },
    InvalidSeed,
    EntriesNotCanonical,
    CoordinateOutOfRange { address: [i64; 3] },
    InvalidMaterial { material_slot: u16 },
    FingerprintMismatch,
}

impl std::fmt::Display for TerrainOverlayError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{self:?}")
    }
}

impl std::error::Error for TerrainOverlayError {}

pub(crate) fn encode_overlay(
    seed: u64,
    overlay: &BTreeMap<[i64; 3], Option<u16>>,
) -> Result<Vec<u8>, TerrainOverlayError> {
    if overlay.len() > MAX_TERRAIN_OVERLAY_ENTRIES {
        return Err(TerrainOverlayError::TooLarge {
            limit: MAX_TERRAIN_OVERLAY_ENTRIES,
            actual: overlay.len(),
        });
    }
    let entries = overlay
        .iter()
        .map(|(address, material_slot)| TerrainOverlayEntry {
            address: *address,
            material_slot: *material_slot,
        })
        .collect::<Vec<_>>();
    let document = TerrainOverlayDocument {
        schema_version: TERRAIN_OVERLAY_SCHEMA_VERSION,
        generation_version: TERRAIN_GENERATION_VERSION,
        seed: format!("0x{seed:016x}"),
        fingerprint: format!("0x{:016x}", overlay_fingerprint(seed, &entries)),
        entries,
    };
    let encoded = serde_json::to_vec_pretty(&document)
        .map_err(|error| TerrainOverlayError::Malformed(error.to_string()))?;
    if encoded.len() > MAX_TERRAIN_OVERLAY_BYTES {
        return Err(TerrainOverlayError::TooLarge {
            limit: MAX_TERRAIN_OVERLAY_BYTES,
            actual: encoded.len(),
        });
    }
    Ok(encoded)
}

pub(crate) fn decode_overlay(
    expected_seed: u64,
    bytes: &[u8],
) -> Result<BTreeMap<[i64; 3], Option<u16>>, TerrainOverlayError> {
    if bytes.len() > MAX_TERRAIN_OVERLAY_BYTES {
        return Err(TerrainOverlayError::TooLarge {
            limit: MAX_TERRAIN_OVERLAY_BYTES,
            actual: bytes.len(),
        });
    }
    let document: TerrainOverlayDocument = serde_json::from_slice(bytes)
        .map_err(|error| TerrainOverlayError::Malformed(error.to_string()))?;
    if document.schema_version != TERRAIN_OVERLAY_SCHEMA_VERSION {
        return Err(TerrainOverlayError::UnsupportedSchema {
            expected: TERRAIN_OVERLAY_SCHEMA_VERSION,
            actual: document.schema_version,
        });
    }
    if document.generation_version != TERRAIN_GENERATION_VERSION {
        return Err(TerrainOverlayError::UnsupportedGeneration {
            expected: TERRAIN_GENERATION_VERSION,
            actual: document.generation_version,
        });
    }
    let seed = document
        .seed
        .strip_prefix("0x")
        .and_then(|value| u64::from_str_radix(value, 16).ok())
        .ok_or(TerrainOverlayError::InvalidSeed)?;
    if seed != expected_seed {
        return Err(TerrainOverlayError::SeedMismatch {
            expected: expected_seed,
            actual: seed,
        });
    }
    if document.entries.len() > MAX_TERRAIN_OVERLAY_ENTRIES {
        return Err(TerrainOverlayError::TooLarge {
            limit: MAX_TERRAIN_OVERLAY_ENTRIES,
            actual: document.entries.len(),
        });
    }
    if document
        .entries
        .windows(2)
        .any(|pair| pair[0].address >= pair[1].address)
    {
        return Err(TerrainOverlayError::EntriesNotCanonical);
    }
    for entry in &document.entries {
        if entry
            .address
            .iter()
            .any(|coordinate| coordinate.unsigned_abs() > MAX_VOXEL_COORDINATE_ABS as u64)
        {
            return Err(TerrainOverlayError::CoordinateOutOfRange {
                address: entry.address,
            });
        }
        if let Some(material_slot) = entry.material_slot {
            if material_slot == 0 || material_slot > MAX_VOXEL_MATERIAL_SLOT {
                return Err(TerrainOverlayError::InvalidMaterial { material_slot });
            }
        }
    }
    let fingerprint = document
        .fingerprint
        .strip_prefix("0x")
        .and_then(|value| u64::from_str_radix(value, 16).ok())
        .ok_or(TerrainOverlayError::FingerprintMismatch)?;
    if fingerprint != overlay_fingerprint(seed, &document.entries) {
        return Err(TerrainOverlayError::FingerprintMismatch);
    }
    Ok(document
        .entries
        .into_iter()
        .map(|entry| (entry.address, entry.material_slot))
        .collect())
}

fn overlay_fingerprint(seed: u64, entries: &[TerrainOverlayEntry]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64 ^ seed;
    for entry in entries {
        for coordinate in entry.address {
            hash ^= coordinate as u64;
            hash = hash.wrapping_mul(0x100_0000_01b3);
        }
        hash ^= entry.material_slot.map_or(0, |slot| u64::from(slot) + 1);
        hash = hash.wrapping_mul(0x100_0000_01b3);
    }
    hash
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn overlay_round_trip_is_canonical_and_detects_corruption() {
        let overlay = BTreeMap::from([([-50, 4, 70], None), ([12, 3, -9], Some(2))]);
        let encoded = encode_overlay(42, &overlay).unwrap();
        assert_eq!(decode_overlay(42, &encoded).unwrap(), overlay);
        assert!(matches!(
            decode_overlay(43, &encoded),
            Err(TerrainOverlayError::SeedMismatch { .. })
        ));
        let mut corrupt = encoded;
        let offset = corrupt.iter().position(|byte| *byte == b'2').unwrap();
        corrupt[offset] = b'3';
        assert!(decode_overlay(42, &corrupt).is_err());

        let mut unsupported: serde_json::Value =
            serde_json::from_slice(&encode_overlay(42, &overlay).unwrap()).unwrap();
        unsupported["schemaVersion"] = 99.into();
        assert!(matches!(
            decode_overlay(42, &serde_json::to_vec(&unsupported).unwrap()),
            Err(TerrainOverlayError::UnsupportedSchema { actual: 99, .. })
        ));
        unsupported["schemaVersion"] = TERRAIN_OVERLAY_SCHEMA_VERSION.into();
        unsupported["generationVersion"] = 1.into();
        assert!(matches!(
            decode_overlay(42, &serde_json::to_vec(&unsupported).unwrap()),
            Err(TerrainOverlayError::UnsupportedGeneration { actual: 1, .. })
        ));
    }
}
