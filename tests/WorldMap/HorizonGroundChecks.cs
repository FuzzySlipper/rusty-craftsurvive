using CraftSurvive.Game.Modules.Horizon;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;
using Rusty.Engine;

/// <summary>
/// The horizon's heightfield ground (#9848): its rings nest as the Engine requires, the coarsest reaches
/// the far edge of the map from anywhere on it, rock grades smoothly into the environment's colour (no
/// stair-stepped cells), and palette colours are decoded for the Engine's linear inputs.
/// </summary>
internal static class HorizonGroundChecks
{
    private const double FarChunkMetres = 128;
    /// <summary>The largest colour change between neighbouring rock shares a hundredth apart.</summary>
    private const float SmoothStep = 0.03f;

    internal static void Run(WorldMap continent)
    {
        HorizonGround ground = new(continent, null, null, FarChunkMetres);
        HeightfieldRing[] rings = ground.Rings;
        Check.That(rings.Length == 3, $"a continent draws three rings ({rings.Length})");
        for (int r = 1; r < rings.Length; r++)
        {
            double finer = rings[r - 1].CellSize * HorizonGround.TileCells, coarser = rings[r].CellSize * HorizonGround.TileCells;
            double ratio = coarser / finer;
            Check.That(ratio >= 2 && Math.Abs(ratio - Math.Round(ratio)) < 1e-9,
                $"ring {r}'s tile ({coarser} m) is a whole number, two or more, of ring {r - 1}'s ({finer} m)");
            Check.That(rings[r].Reach > rings[r - 1].Reach + coarser, $"ring {r} reaches past ring {r - 1} by more than one of its tiles");
        }

        Check.That(rings[^1].Reach >= 2 * continent.Radius, "the coarsest ring reaches the far edge of the map from anywhere on it");

        float largest = 0;
        MapSample bare = continent.Sample(0, 0) with { Rock = 0, Elevation = Math.Max(continent.Sample(0, 0).Elevation, GenerationConstants.WaterLevel + 50) };
        Color previous = HorizonGround.Paint(bare, null, 0);
        for (int step = 1; step <= 100; step++)
        {
            Color now = HorizonGround.Paint(bare with { Rock = step / 100.0 }, null, 0);
            largest = Math.Max(largest, Math.Max(Math.Abs(now.R - previous.R), Math.Max(Math.Abs(now.G - previous.G), Math.Abs(now.B - previous.B))));
            previous = now;
        }

        Check.That(largest > 0 && largest < SmoothStep, $"rock grades smoothly into the ground's colour (largest step {largest:F3})");

        // Vertex colours are linear; the palette is sRGB, so a mid grey decodes darker and the ends stay put.
        Color decoded = MapPalette.VertexColour(new Color(0.5f, 0, 1, 1));
        Check.That(Math.Abs(decoded.R - 0.214f) < 1e-3f && decoded.G == 0 && Math.Abs(decoded.B - 1) < 1e-6f && decoded.A == 1,
            $"a palette colour is decoded from sRGB for a vertex ({decoded.R:F3},{decoded.G:F3},{decoded.B:F3})");
    }
}
