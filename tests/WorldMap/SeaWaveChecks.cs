using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using CraftSurvive.Game.Modules.Travel;
using CraftSurvive.Game.Modules.Water;
using CraftSurvive.Game.Modules.WorldGen;
using CraftSurvive.Game.Tests;

/// <summary>
/// The sea state (#9902): one wave definition, drawn by the water shader and ridden by boats and swimmers.
/// The shader's constants are C#'s; the waves grow with wind and fetch, stay within the mesh's bounds and
/// above the bed; the surface read at a point is the one drawn there; a boat pitches and rolls with it; and
/// a boat is windbound at a lake whose waves pass its limit.
/// </summary>
internal static class SeaWaveChecks
{
    internal static void Run()
    {
        ShaderAgrees();
        Growth();
        Bounds();
        Surface();
        Riding();
        Windbinding();
    }

    /// <summary>Every wave constant the shader declares is the one SeaWaves holds.</summary>
    private static void ShaderAgrees()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "content", "game", "shaders", "water-flow.wgsl"))) directory = Path.GetDirectoryName(directory);
        Check.That(directory is not null, "the water shader is found");
        if (directory is null) return;
        string wgsl = File.ReadAllText(Path.Combine(directory, "content", "game", "shaders", "water-flow.wgsl"));
        double Scalar(string name)
        {
            Match m = Regex.Match(wgsl, $@"const {name}: (?:f32|i32) = ([-0-9.e]+);");
            return m.Success ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
        }

        double[] Array(string name)
        {
            Match m = Regex.Match(wgsl, $@"const {name}: array<f32, \d+> = array<f32, \d+>\(([^)]*)\);");
            return m.Success ? [.. m.Groups[1].Value.Split(',').Select(v => double.Parse(v.Trim(), CultureInfo.InvariantCulture))] : [];
        }

        (string Name, double Value)[] scalars =
        [
            ("TRAIN_COUNT", SeaWaves.Trains.Length), ("SWELL_TRAIN", SeaWaves.SwellTrain), ("GRAVITY", SeaWaves.Gravity),
            ("HEIGHT_GROWTH", SeaWaves.HeightGrowth), ("PERIOD_GROWTH", SeaWaves.PeriodGrowth), ("DEVELOPED_HEIGHT", SeaWaves.DevelopedHeight),
            ("DEVELOPED_PERIOD", SeaWaves.DevelopedPeriod), ("MAXIMUM_HEIGHT", SeaWaves.MaximumHeight), ("SPECTRUM_WIDTH", SeaWaves.SpectrumWidth), ("TAIL_POWER", SeaWaves.TailPower),
            ("STEEPNESS", SeaWaves.Steepness), ("CHOPPINESS", SeaWaves.Choppiness), ("SWELL_AMPLITUDE", SeaWaves.SwellAmplitude),
            ("EDGE_FADE_METRES", SeaWaves.EdgeFadeMetres), ("LEAST_FETCH", SeaWaves.LeastFetch),
        ];
        string[] differing = [.. scalars.Where(c => Scalar(c.Name) != c.Value).Select(c => $"{c.Name}={Scalar(c.Name)} (C# {c.Value})")];
        Check.That(differing.Length == 0, $"the shader's wave constants are SeaWaves' ({string.Join(", ", differing)})");
        Check.That(Array("WAVELENGTHS").SequenceEqual(SeaWaves.Trains.Select(t => t.Wavelength))
            && Array("ANGLES").SequenceEqual(SeaWaves.Trains.Select(t => t.Angle)),
            "the shader's trains have SeaWaves' wavelengths and bearings");
    }

    /// <summary>Waves grow with wind and fetch: a calm pond is flat, a gale over a great lake is not, a river stays a chop.</summary>
    private static void Growth()
    {
        double pond = SeaWaves.SignificantHeight(SeaState.CalmWind, 300), gale = SeaState.WindFor(1);
        double smallLake = SeaWaves.SignificantHeight(gale, SeaWaves.LakeFetchMetres(1));
        double greatLake = SeaWaves.SignificantHeight(gale, SeaWaves.LakeFetchMetres(286));
        double river = SeaWaves.SignificantHeight(gale, WaterModel.RiverFetchWidths * 30);
        Console.WriteLine(FormattableString.Invariant(
            $"sea state: calm pond {pond:F2} m; gale over 1 km2 {smallLake:F2} m, 286 km2 {greatLake:F2} m, a 30 m river {river:F2} m; peak {SeaWaves.PeakWavelength(gale, SeaWaves.LakeFetchMetres(286)):F0} m"));
        Check.That(pond < 0.05, "a calm pond stands flat");
        Check.That(smallLake < greatLake && greatLake == SeaWaves.MaximumHeight && river < 0.2,
            "a gale raises waves with the fetch: a river keeps to a chop, a great lake runs as high as is drawn");
        Check.That(SeaWaves.PeakWavelength(gale, 1000) < SeaWaves.PeakWavelength(gale, 20_000)
            && SeaWaves.PeakWavelength(SeaState.CalmWind, 20_000) < SeaWaves.PeakWavelength(gale, 20_000),
            "longer fetch and stronger wind lengthen the waves");
        Span<double> amplitudes = stackalloc double[SeaWaves.Trains.Length];
        SeaWaves.Amplitudes(gale, new(SeaWaves.LakeFetchMetres(286), 0, 1), amplitudes);
        double variance = 0;
        foreach (double a in amplitudes) variance += a * a / 2;
        Check.That(Math.Abs((4 * Math.Sqrt(variance)) - SeaWaves.MaximumHeight) < 0.05,
            $"the trains together stand at the significant height asked ({4 * Math.Sqrt(variance):F2} m)");
    }

    /// <summary>The highest possible waves stay within a metre of the mesh, and every trough above the bed.</summary>
    private static void Bounds()
    {
        Span<double> amplitudes = stackalloc double[SeaWaves.Trains.Length];
        SeaWaves.Amplitudes(SeaState.WindFor(1), new(WaterModel.SeaFetchMetres, 1, 1), amplitudes);
        double rise = 0;
        foreach (double a in amplitudes) rise += a;
        Console.WriteLine(FormattableString.Invariant($"sea state: the most the trains together can lift is {rise:F2} m"));
        Check.That(rise <= 1, $"even a gale at sea with its swell displaces the water by at most a metre ({rise:F2} m)");
        bool aboveBed = true;
        foreach (double depth in (double[])[0.1, 0.2, 0.3, 0.5, 0.75, 1, 1.5, 2, 4])
            aboveBed &= WaterWaveSettings.Coverage(depth, false) * rise < depth;
        Check.That(aboveBed, "even the deepest trough stays above the bed, the waves fading into shallows");
        double[] depths = [-1, 0, 0.1, WaterWaveSettings.ShoreClearance, 0.25, 0.5, 1, 2, 4];
        float previous = 0;
        bool smooth = true, ice = true, pinned = true;
        foreach (double depth in depths)
        {
            float coverage = WaterWaveSettings.Coverage(depth, false);
            smooth &= coverage >= previous && coverage <= 1;
            ice &= WaterWaveSettings.Coverage(depth, true) == 0;
            pinned &= depth > WaterWaveSettings.ShoreClearance || coverage == 0;
            previous = coverage;
        }

        Check.That(smooth && ice && pinned && WaterWaveSettings.Coverage(WaterWaveSettings.FullDepth, false) == 1,
            "wave coverage rises smoothly into deep water; ice and the shoreline never move");
    }

    /// <summary>The surface read at a point is the one the waves carry there, and the shader's phases measure the same waves from its own origin.</summary>
    private static void Surface()
    {
        SeaState sea = new() { HeldWind = 14 };
        sea.Advance(37.25, 0, 0, new(0.6f, 0.8f), 120_345, -64_210);
        WaveExposure lake = new(8000, 0, 1);
        double worst = 0;
        for (int k = 0; k < 64; k++)
        {
            double sx = 120_300 + (k * 1.37), sz = -64_200 + (k * 0.91);
            Vector3 moved = sea.Displacement(sx, sz, lake, 1);
            double lift = sea.Lift(sx + moved.X, sz + moved.Z, lake, 1);
            worst = Math.Max(worst, Math.Abs(lift - moved.Y));
        }

        Check.That(worst < 0.01, $"the surface read at a point is the water the waves carried there (within {worst * 100:F2} cm)");

        // The shader measures each train from the frame's origin with the phases it is handed: the same angle.
        (double ox, double oz) = (119_808, -64_512);
        Vector4 low = sea.ShaderPhases(0, ox, oz), high = sea.ShaderPhases(4, ox, oz);
        double drift = 0;
        for (int i = 0; i < SeaWaves.Trains.Length; i++)
        {
            (double ux, double uz) = SeaWaves.Direction(sea.Wind, SeaWaves.Trains[i].Angle);
            double k = SeaWaves.Trains[i].Number, px = 120_401.5, pz = -64_177.25;
            double cpu = (k * ((ux * (px - sea.Anchor.X)) + (uz * (pz - sea.Anchor.Z)))) + sea.Phases[i];
            float phase = i < 4 ? low[i] : high[i - 4];
            double shader = (k * ((ux * (float)(px - ox)) + (uz * (float)(pz - oz)))) + phase;
            drift = Math.Max(drift, Math.Abs(Math.IEEERemainder(cpu - shader, 2 * Math.PI)));
        }

        Check.That(drift < 1e-3, $"the shader's phases, measured from the frame's origin, are the C# waves' ({drift:E1} rad)");
        double before = sea.Phases[0];
        sea.Advance(0, 0, 0, new(0.6f, 0.8f), 120_345, -64_210);
        Check.That(sea.Phases[0] == before, "the waves hold while no simulation time passes");
    }

    /// <summary>A boat rides the surface under it: bow up where the water rises ahead, rolled toward the lower side, heaving as it rises.</summary>
    private static void Riding()
    {
        FloatWater Sloped(double x, double z, double alongX, double alongZ) => new(10, 2, 0, 0, false, (alongX * x) + (alongZ * z));
        BoatFloat bow = new(Boats.Canoe, 0, 0, 0) { Moored = false };
        bow.Step(0.01, (x, z) => Sloped(x, z, 0.1, 0), Vector2.Zero, laden: false);
        Check.That(bow.Pitch > 0 && bow.Pitch < Math.Atan(0.1) / 2, "it leans into the water's slope with a little inertia, not at once");
        for (int i = 0; i < 300; i++) bow.Step(0.01, (x, z) => Sloped(x, z, 0.1, 0), Vector2.Zero, laden: false);
        Check.That(Math.Abs(bow.Pitch - Math.Atan(0.1)) < 1e-3 && Math.Abs(bow.Roll) < 1e-9,
            $"water rising ahead lifts the bow ({bow.Pitch * 180 / Math.PI:F1} deg)");
        BoatFloat side = new(Boats.Raft, 0, 0, 0) { Moored = false };
        for (int i = 0; i < 600; i++) side.Step(0.01, (x, z) => Sloped(x, z, 0, 0.1), Vector2.Zero, laden: false);
        Check.That(Math.Abs(side.Roll - Math.Atan(0.1)) < 1e-3 && Math.Abs(side.Pitch) < 1e-9,
            "water rising to the bow's left rolls it up on that side");
        Vector3 left = Vector3.Transform(Vector3.UnitZ, side.Attitude), nose = Vector3.Transform(Vector3.UnitX, bow.Attitude);
        Check.That(left.Y > 0 && nose.Y > 0, "its drawn attitude lifts the side and the bow the water lifts");
        BoatFloat rising = new(Boats.Canoe, 0, 0, 0);
        double lift = 0;
        rising.Step(0.1, (_, _) => new(10, 2, 0, 0, false, lift), Vector2.Zero, laden: false);
        lift = 0.05;
        rising.Step(0.1, (_, _) => new(10, 2, 0, 0, false, lift), Vector2.Zero, laden: false);
        Check.That(Math.Abs(rising.Y - 10.05) < 1e-9 && Math.Abs(rising.Heave - 0.5) < 1e-9,
            "even moored it heaves with the waves, saying how fast so what stands aboard is carried");
        rising.Step(0.1, (_, _) => new(10, 2, 0, 0, false, lift), Vector2.Zero, laden: true);
        Check.That(rising.Y < 10.05 && rising.Heave == 0, "settling as someone boards is not heave: nothing aboard is flung");
    }

    /// <summary>A boat is windbound where the wind raises the lake's waves past its limit, and waits for the wind to drop.</summary>
    private static void Windbinding()
    {
        Lake small = Synthetic(1), great = Synthetic(286);
        Check.That(!Windbound.Holds(Boats.Canoe, small, 1), "a gale does not hold a canoe off a small lake");
        Check.That(Windbound.Holds(Boats.Canoe, great, 0.4) && !Windbound.Holds(Boats.Canoe, great, 0),
            "a rain front's wind holds a canoe off a great lake; still air does not");
        Check.That(Boats.Raft.WaveLimit > Boats.Canoe.WaveLimit, "a raft rides higher waves than a canoe");
        Check.That(Windbound.Wait(Boats.Canoe, great, h => h < 6 ? 0.8 : 0.05, 48) == 6 && Windbound.Wait(Boats.Canoe, great, _ => 1, 48) is null,
            "the party waits until the wind drops, or is told it holds");
        Check.That(Windbound.Name(small) == "the lake" && Windbound.Name(great with { Name = "Greatwater" }) == "Greatwater", "a lake is named to the party");
    }

    private static Lake Synthetic(double km2) => new(0, LakeKind.Basin, "", [0], [0], 10, 10, false, -1, km2, 5, 1, 1, [(10, km2)]);
}
