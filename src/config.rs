use std::str::FromStr;

use rusty_engine::svc_mesh::SurfaceMode;

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
}

impl DemoConfig {
    pub fn from_args(arguments: impl IntoIterator<Item = String>) -> Result<Self, String> {
        let mut surface = SurfaceSelection::Box;
        let mut summary_only = false;
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
                "--help" | "-h" => {
                    return Err(
                        "usage: rusty-craftsurvive [--surface box|mc|dc] [--summary]".into(),
                    );
                }
                _ => return Err(format!("unknown argument '{argument}'")),
            }
        }
        Ok(Self {
            surface,
            summary_only,
        })
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
}
