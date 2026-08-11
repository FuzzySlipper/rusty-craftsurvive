use std::str::FromStr;

use rusty_engine::svc_mesh::SurfaceMode;

pub const DEFAULT_TERRAIN_SEED: u64 = 0x4352_4146_5453_5552;
pub const DEFAULT_TERRAIN_SIZE: u16 = 96;
pub const MIN_TERRAIN_SIZE: u16 = 32;
pub const MAX_TERRAIN_SIZE: u16 = 128;

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SurfaceSelection {
    Box,
    MarchingCubes,
    DualContouring,
}

impl SurfaceSelection {
    pub const ALL: [Self; 3] = [Self::Box, Self::MarchingCubes, Self::DualContouring];

    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Box => "box",
            Self::MarchingCubes => "mc",
            Self::DualContouring => "dc",
        }
    }

    pub const fn engine_mode(self) -> SurfaceMode {
        match self {
            Self::Box => SurfaceMode::GreedyCubes,
            Self::MarchingCubes => SurfaceMode::MarchingCubes,
            Self::DualContouring => SurfaceMode::DualContouring,
        }
    }
}

impl FromStr for SurfaceSelection {
    type Err = String;

    fn from_str(value: &str) -> Result<Self, Self::Err> {
        match value {
            "box" | "greedy" | "greedy-cubes" => Ok(Self::Box),
            "mc" | "marching-cubes" => Ok(Self::MarchingCubes),
            "dc" | "dual-contouring" => Ok(Self::DualContouring),
            _ => Err(format!(
                "unsupported surface '{value}'; expected box, mc, or dc"
            )),
        }
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DemoConfig {
    pub surface: SurfaceSelection,
    pub summary_only: bool,
    pub terrain: TerrainConfig,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TerrainConfig {
    pub seed: u64,
    pub size: u16,
}

impl Default for TerrainConfig {
    fn default() -> Self {
        Self {
            seed: DEFAULT_TERRAIN_SEED,
            size: DEFAULT_TERRAIN_SIZE,
        }
    }
}

impl TerrainConfig {
    pub fn new(seed: u64, size: u16) -> Result<Self, String> {
        if !(MIN_TERRAIN_SIZE..=MAX_TERRAIN_SIZE).contains(&size) {
            return Err(format!(
                "terrain size {size} is outside {MIN_TERRAIN_SIZE}..={MAX_TERRAIN_SIZE}"
            ));
        }
        if !size.is_multiple_of(2) {
            return Err("terrain size must be even".to_owned());
        }
        Ok(Self { seed, size })
    }
}

impl DemoConfig {
    pub fn from_args(arguments: impl IntoIterator<Item = String>) -> Result<Self, String> {
        let mut surface = SurfaceSelection::Box;
        let mut summary_only = false;
        let mut terrain = TerrainConfig::default();
        let mut arguments = arguments.into_iter();
        while let Some(argument) = arguments.next() {
            match argument.as_str() {
                "--surface" => {
                    surface = arguments
                        .next()
                        .ok_or_else(|| "--surface requires box, mc, or dc".to_owned())?
                        .parse()?;
                }
                "--summary" => summary_only = true,
                "--seed" => {
                    terrain.seed = parse_seed(
                        &arguments
                            .next()
                            .ok_or_else(|| "--seed requires an unsigned integer".to_owned())?,
                    )?;
                }
                "--size" => {
                    terrain.size = arguments
                        .next()
                        .ok_or_else(|| "--size requires an even integer".to_owned())?
                        .parse()
                        .map_err(|_| "--size requires an even integer".to_owned())?;
                }
                "--help" | "-h" => {
                    return Err(
                        "usage: rusty-craftsurvive [--surface box|mc|dc] [--seed N|0xHEX] [--size 32..128 even] [--summary]".into(),
                    );
                }
                _ => return Err(format!("unknown argument '{argument}'")),
            }
        }
        terrain = TerrainConfig::new(terrain.seed, terrain.size)?;
        Ok(Self {
            surface,
            summary_only,
            terrain,
        })
    }
}

pub fn parse_seed(value: &str) -> Result<u64, String> {
    if let Some(hex) = value.strip_prefix("0x") {
        u64::from_str_radix(hex, 16).map_err(|_| format!("invalid hexadecimal seed '{value}'"))
    } else {
        value
            .parse()
            .map_err(|_| format!("invalid unsigned seed '{value}'"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_surface_spellings_select_an_engine_mode() {
        for (name, expected) in [
            ("box", SurfaceMode::GreedyCubes),
            ("mc", SurfaceMode::MarchingCubes),
            ("dc", SurfaceMode::DualContouring),
        ] {
            assert_eq!(
                name.parse::<SurfaceSelection>().unwrap().engine_mode(),
                expected
            );
        }
    }

    #[test]
    fn terrain_configuration_is_bounded_and_reproducible() {
        let config = DemoConfig::from_args([
            "--seed".to_owned(),
            "0x2a".to_owned(),
            "--size".to_owned(),
            "64".to_owned(),
        ])
        .unwrap();
        assert_eq!(config.terrain, TerrainConfig { seed: 42, size: 64 });
        assert!(TerrainConfig::new(1, 31).is_err());
        assert!(TerrainConfig::new(1, 33).is_err());
        assert!(TerrainConfig::new(1, 130).is_err());
    }
}
