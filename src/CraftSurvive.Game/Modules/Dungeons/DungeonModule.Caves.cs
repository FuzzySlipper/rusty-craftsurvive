using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using CraftSurvive.Game.Modules.WorldGen;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Dungeons;

/// <summary>
/// The caves under the world's cave mouths (#10889; Patch, 2026-11-18: caves share the terrain's systems rather than keep
/// their own copies). A cave mouth's tendril is the overworld generator's; its cave is generated in the background when
/// the player comes near (#10888), then read into the walking session's own chunks under the hill, where the terrain's
/// residency streams it, its materials and surfaces draw it, its collision and navigation hold it, the world's origin
/// carries it and the overlay saves its edits. There is no loading gate and no hand-over: going down the tendril the sky's
/// light fades with depth, the overworld's far reaches go, and the lantern, the cave's own lights and its dressing come in.
/// </summary>
internal sealed partial class DungeonModule
{
    /// <summary>A cave admitted further than this from the player is let go: its box is rock again until they come back.</summary>
    internal const double CaveReleaseMetres = 640d;

    /// <summary>How near a cave's box the player comes before it is dressed (its growth, water, ruin kit) and lit.</summary>
    internal const double CaveDressMetres = 96d;

    /// <summary>How far down the player is (0 at the surface, 1 deep) before the lantern is lit.</summary>
    private const double CaveLanternBelow = 0.05d;

    // The probe volume about a player in a cave: a box of this half-extent, moved once they are a chunk from its centre.
    private static readonly Vector3 CaveProbeExtent = new(24f, 12f, 24f);

    private sealed class UnderCave
    {
        internal required CavePlacement Cave { get; init; }
        internal required string Key { get; init; }
        internal required ulong Seed { get; init; }
        internal UndergroundCave? Ready { get; set; }

        /// <summary>Whether it was read from the store rather than generated.</summary>
        internal bool FromStore { get; init; }
    }

    private readonly Dictionary<(long X, long Z), UnderCave> underCaves = [];
    private readonly List<PoiSite> caveMouths = [];
    private UnderCave? dressedCave;
    private CaveDressing? caveDressing;
    private Transform caveLitAt = new(new Vector3(float.NaN), Quaternion.Identity, Vector3.One);
    private Vector3? caveProbeCentre;
    private long nextCaveSearchStep;
    private long cavesAdmitted, cavesReleased;
    private string lastCave = "none";

    /// <summary>Whether the player is far enough down a cave under the world to carry the lantern.</summary>
    private bool CaveLanternWanted => !player.InSeparateSpace && terrain.Caves.Below > CaveLanternBelow;

    /// <summary>
    /// Follows the caves under the world each update in the open world: asks for those near the player, admits the
    /// finished ones, lets far ones go, dresses and lights the nearest, and fades the sky with depth.
    /// </summary>
    private void FollowCaves(ProductStep step)
    {
        if (player.InSeparateSpace)
        {
            return;
        }

        if (step.Step >= nextCaveSearchStep)
        {
            AskForCaves();
            ReleaseFarCaves();
            nextCaveSearchStep = step.Step + EntranceSearchIntervalSteps;
        }

        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        AdmitFinishedCaves();
        long admitted = System.Diagnostics.Stopwatch.GetTimestamp();
        DressNearestCave();
        long dressed = System.Diagnostics.Stopwatch.GetTimestamp();
        AdvanceDressing();
        long advanced = System.Diagnostics.Stopwatch.GetTimestamp();
        LightCave();
        Steer();
        long done = System.Diagnostics.Stopwatch.GetTimestamp();
        static double Ms(long from, long to) => System.Diagnostics.Stopwatch.GetElapsedTime(from, to).TotalMilliseconds;
        CaveTiming = Ms(began, done) < 1d ? "" : string.Create(CultureInfo.InvariantCulture,
            $"caves(admit={Ms(began, admitted):F1}{admitTiming} dress={Ms(admitted, dressed):F1} advance={Ms(dressed, advanced):F1} light={Ms(advanced, done):F1})");
        admitTiming = "";
    }

    /// <summary>Where this update's cave follow went (#10907): admission (its store write and the terrain's), dressing, lights.</summary>
    internal string CaveTiming { get; private set; } = "";

    private string admitTiming = "";

    /// <summary>The cave mouths within reach of the player whose caves have not been asked for are asked for, nearest first.</summary>
    private void AskForCaves()
    {
        Vector3 feet = player.WorldFeetPosition;
        caveMouths.Clear();
        terrain.Recipe.Placement.CollectSitesNear((long)Math.Floor(feet.X), (long)Math.Floor(feet.Z), (long)Math.Ceiling(PrefetchMetres) + 1, caveMouths);
        foreach (PoiSite site in caveMouths.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X - feet.X) + Math.Abs(s.Z - feet.Z)))
        {
            if (underCaves.ContainsKey((site.CellX, site.CellZ)) || terrain.Recipe.Caves.For(site) is not CavePlacement cave)
            {
                continue;
            }

            Ask(cave);
        }
    }

    /// <summary>
    /// Asks for one cave in the background: generated by the karst generator in the placement's box, its way in at the
    /// tendril's end and nothing open to a sky, then prepared for the walking session's chunks, all off the update.
    /// </summary>
    private UnderCave Ask(CavePlacement cave)
    {
        ulong seed = seedOverride ?? DungeonSeed(terrain.SaveIdentity.Seed, cave.Site);
        CaveSetting setting = studySetting ?? terrain.CaveSettingUnder(cave.Site.X, cave.Site.Z);
        KarstCave.Settings settings = UndergroundSettings();
        string key = string.Create(CultureInfo.InvariantCulture, $"under-{seed:x}-{settings.Key}-{setting.Key}");
        ProductContent content = terrain.Content;
        // A cave this world generated before is read from the store (#10888 R10888-2) and decoded off the update; one it
        // never did is generated, prepared and encoded there, and stored once admitted.
        byte[]? stored = Generator.Asked(key) ? null : CaveStore.Read(key);
        bool fromStore = stored is not null;
        Generator.Request(key, () =>
        {
            if (stored is not null && CaveCodec.Decode(stored) is UndergroundCave kept)
            {
                CaveDressing.Preload(content, kept.Layout);
                return kept;
            }

            UndergroundCave ready = Prepare(DungeonCandidates.Generate(DungeonApproach.Karst, seed, 0, 1d, null, setting, settings), cave);
            // Its kit's and overgrowth's models are read here too, so dressing it later only makes meshes on the update.
            CaveDressing.Preload(content, ready.Layout);
            return ready with { Encoded = CaveCodec.Encode(ready) };
        });
        UnderCave asked = new() { Cave = cave, Key = key, Seed = seed, FromStore = fromStore };
        underCaves[cave.Key] = asked;
        lastCave = string.Create(CultureInfo.InvariantCulture, $"asked for the cave under the mouth at {cave.Site.X},{cave.Site.Z}");
        return asked;
    }

    /// <summary>
    /// A generated cave made ready for the walking session (off the update): each voxel of its box as the overworld's chunks
    /// read it - its rock, mud and silt the cave's own blocks, the densities a natural cave is admitted with - and the
    /// walking session's chunks holding its air, with every chunk beside them.
    /// </summary>
    internal static UndergroundCave Prepare(DungeonCandidate candidate, CavePlacement cave)
    {
        DungeonLayout all = candidate.AllVoxels;
        DungeonVolume volume = all.Volume;
        if (volume.SizeX != CavePlacement.SizeX || volume.SizeY != CavePlacement.SizeY || volume.SizeZ != CavePlacement.SizeZ)
        {
            throw new InvalidOperationException($"A cave under the world must fill its {CavePlacement.SizeX}x{CavePlacement.SizeY}x{CavePlacement.SizeZ} box.");
        }

        int edge = TerrainConstants.ChunkEdgeLength;
        ushort[] materials = new ushort[volume.SizeX * volume.SizeY * volume.SizeZ];
        float[] densities = new float[materials.Length];
        HashSet<TerrainChunkAddress> open = [];
        CaveInterior? interior = null;
        foreach ((int cx, int cy, int cz) in DungeonCollision.Chunks(volume))
        {
            (uint[] slots, float[]? values) = DungeonCollision.Prepare(volume, (cx, cy, cz), all.Densities, weathered: false, pinned: false, voxelSize: 1d);
            for (int index = 0; index < slots.Length; index++)
            {
                int i = (cx * edge) + (index % edge), j = (cy * edge) + (index / edge % edge), k = (cz * edge) + (index / (edge * edge));
                int at = (((k * volume.SizeY) + j) * volume.SizeX) + i;
                BlockId block = (BlockId)slots[index];
                materials[at] = (ushort)CaveMaterials.Underground(block);
                densities[at] = MathF.Abs(values?[index] ?? 0.5f);
                if (block == BlockId.Air)
                {
                    Vector3 world = cave.ToWorld(new Vector3(i + 0.5f, j + 0.5f, k + 0.5f));
                    open.Add(new Terrain.VoxelAddress((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y), (long)MathF.Floor(world.Z)).Chunk);
                }
            }
        }

        // The way down from the tendril's end past the cave's first sink (R10889-3): a ramp the player walks back up, and the
        // route starting with it.
        Vector3[] lead = [.. cave.Path.Select(cave.ToCanonical).Where(p => p.X >= 0f && p.X < volume.SizeX && p.Z >= 0f && p.Z < volume.SizeZ && p.Y >= 0f && p.Y < volume.SizeY)];
        CaveWayDown.Carved? down = CaveWayDown.Carve(volume.SizeX, volume.SizeY, volume.SizeZ, materials, densities, lead.Length > 0 ? lead : [cave.ToCanonical(cave.Path[^1])],
            candidate.Layout.MainRoute, CavePlacement.TendrilHalfWidth, CavePlacement.TendrilHalfHeight, CavePlacement.TendrilFloorBelowCentre,
            TerrainConstants.EmptyMaterial, (ushort)BlockId.CaveLimestone);
        foreach (int at in down?.Opened ?? [])
        {
            int i = at % volume.SizeX, j = at / volume.SizeX % volume.SizeY, k = at / (volume.SizeX * volume.SizeY);
            Vector3 world = cave.ToWorld(new Vector3(i + 0.5f, j + 0.5f, k + 0.5f));
            open.Add(new Terrain.VoxelAddress((long)MathF.Floor(world.X), (long)MathF.Floor(world.Y), (long)MathF.Floor(world.Z)).Chunk);
        }

        interior = new CaveInterior(volume.SizeX, volume.SizeY, volume.SizeZ, materials, densities);
        // What dressing it reads: the layout without the voxels it was generated in.
        DungeonLayout dressing = candidate.Layout with
        {
            Volume = new DungeonVolume(1, 1, 1, BlockId.Air), Rock = null, Densities = null, KitDrawn = null, Moisture = null,
            MainRoute = down?.Route ?? candidate.Layout.MainRoute,
        };
        if (down is not null)
        {
            // What the way down cut through: the ruin's pieces in its way and the dressing left hanging in its air.
            dressing = CaveWayDown.Clear(dressing, down, CavePlacement.TendrilHalfWidth, CavePlacement.TendrilHalfHeight, CavePlacement.TendrilFloorBelowCentre);
        }
        return new UndergroundCave(dressing, interior, TerrainCaves.Beside(open));
    }

    /// <summary>Reads the caves the background has finished into the walking session.</summary>
    private void AdmitFinishedCaves()
    {
        foreach (UnderCave cave in underCaves.Values)
        {
            if (cave.Ready is not null)
            {
                continue;
            }

            try
            {
                cave.Ready = Generator.Finished<UndergroundCave>(cave.Key);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // A cave that could not be generated stays rock behind its tendril; it is asked for again when the player returns.
                underCaves.Remove(cave.Cave.Key);
                lastCave = $"the cave under the mouth at {cave.Cave.Site.X},{cave.Cave.Site.Z} could not be generated: {failure.Message}";
                return;
            }

            if (cave.Ready is UndergroundCave ready)
            {
                Admit(cave, ready);
            }
        }
    }

    /// <summary>Reads a ready cave into the walking session, storing a freshly generated one for the world.</summary>
    private void Admit(UnderCave cave, UndergroundCave ready)
    {
        long storing = System.Diagnostics.Stopwatch.GetTimestamp();
        if (ready.Encoded is byte[] encoded)
        {
            CaveStore.Write(cave.Key, encoded);
            cave.Ready = ready with { Encoded = null };
            cavesGenerated++;
        }
        else
        {
            cavesRead++;
        }

        long admitting = System.Diagnostics.Stopwatch.GetTimestamp();
        terrain.AdmitCave(cave.Cave, ready.Interior, ready.Shell);
        cavesAdmitted++;
        admitTiming = string.Create(CultureInfo.InvariantCulture,
            $"[store={System.Diagnostics.Stopwatch.GetElapsedTime(storing, admitting).TotalMilliseconds:F1} terrain={System.Diagnostics.Stopwatch.GetElapsedTime(admitting).TotalMilliseconds:F1}]");
        (double generateMs, double queuedMs) = Generator.Timing(cave.Key) ?? (0d, 0d);
        lastCave = string.Create(CultureInfo.InvariantCulture,
            $"admitted the cave under the mouth at {cave.Cave.Site.X},{cave.Cave.Site.Z}: {(cave.FromStore ? $"generate=stored (decoded {generateMs:F0}ms)" : $"generate=background {generateMs:F0}ms")} (queued {queuedMs:F0}ms), {ready.Shell.Count} chunks with air or beside it");
    }

    private long cavesGenerated, cavesRead;

    /// <summary>The caves this world generated, kept through the Engine's persistence (#10888 R10888-2).</summary>
    private CaveStore CaveStore => caveStore ??= new CaveStore(engine, terrain.SaveIdentity.Seed, TerrainGeneratorContract.CurrentVersion);

    private CaveStore? caveStore;

    /// <summary>Lets caves far from the player go (their rock comes back); a cave kept by the generator comes back at once.</summary>
    private void ReleaseFarCaves()
    {
        Vector3 feet = player.WorldFeetPosition;
        foreach (UnderCave cave in underCaves.Values.ToArray())
        {
            if (Distance(cave.Cave, feet) <= CaveReleaseMetres)
            {
                continue;
            }

            if (cave == dressedCave)
            {
                Undress();
            }

            if (cave.Ready is not null)
            {
                terrain.ReleaseCave(cave.Cave);
                cavesReleased++;
            }

            underCaves.Remove(cave.Cave.Key);
        }
    }

    /// <summary>How far a point is from a cave's box and tendril (0 inside), across the ground.</summary>
    private static double Distance(CavePlacement cave, Vector3 at)
    {
        double Outside(double value, long minimum, long maximum) => value < minimum ? minimum - value : value > maximum + 1 ? value - maximum - 1 : 0d;
        double box = Math.Max(Outside(at.X, cave.BoxMinimum.X, cave.BoxMaximum.X), Outside(at.Z, cave.BoxMinimum.Z, cave.BoxMaximum.Z));
        double tendril = Math.Max(Outside(at.X, cave.TendrilMinimum.X, cave.TendrilMaximum.X), Outside(at.Z, cave.TendrilMinimum.Z, cave.TendrilMaximum.Z));
        return Math.Min(box, tendril);
    }

    /// <summary>Dresses the admitted cave nearest the player once they come near it; undresses one they left.</summary>
    private void DressNearestCave()
    {
        Vector3 feet = player.WorldFeetPosition;
        UnderCave? nearest = underCaves.Values.Where(c => c.Ready is not null && Distance(c.Cave, feet) <= CaveDressMetres)
            .OrderBy(c => Distance(c.Cave, feet)).FirstOrDefault();
        if (nearest == dressedCave)
        {
            return;
        }

        Undress();
        if (nearest?.Ready is not UndergroundCave ready)
        {
            return;
        }

        DungeonLayout layout = ready.Layout;
        // Built a part an update (DressingAdvance), so dressing a cave never takes one update's whole frame.
        caveDressing = new CaveDressing(engine, terrain, layout, kitDrawn: true, kitLook, kitPaint, StoneShader);
        dressedCave = nearest;
        caveLitAt = new Transform(new Vector3(float.NaN), Quaternion.Identity, Vector3.One);
        player.CaveWater = StillWater(layout.WaterZones, nearest.Cave, terrain.Frame);
    }

    /// <summary>Builds the next part of the dressed cave's dressing; once its kit is built, it joins the walking session's collision.</summary>
    private void AdvanceDressing()
    {
        if (caveDressing is not CaveDressing dressing || dressedCave is not UnderCave dressed || dressing.Built)
        {
            return;
        }

        bool done = dressing.Advance();
        if (!done)
        {
            return;
        }

        CavePlacement cave = dressed.Cave;
        // The ruin's kit collides with the walking session's other pieces, placed in world metres.
        terrain.CaveCollision = dressing.KitCount > 0
            ? (assets, vertices, triangles, instances) => caveDressing?.AddCollision(assets, vertices, triangles, instances, WorldPlace(cave))
            : null;
        caveLitAt = new Transform(new Vector3(float.NaN), Quaternion.Identity, Vector3.One);
        lastCave = string.Create(CultureInfo.InvariantCulture,
            $"dressed the cave under the mouth at {cave.Site.X},{cave.Site.Z} in {dressing.BuildMilliseconds:F0} ms (slowest update {dressing.SlowestStageMilliseconds:F0} ms): {dressing.BuiltReadout}");
    }

    private void Undress()
    {
        if (caveDressing is null)
        {
            return;
        }

        terrain.CaveCollision = null;
        player.CaveWater = null;
        // Released after the next snapshot, which no longer names its appearances.
        retiringDressing?.Dispose();
        retiringDressing = caveDressing;
        caveDressing = null;
        dressedCave = null;
        GrowthLights([]);
        RelicLights([]);
    }

    private CaveDressing? retiringDressing;

    /// <summary>Where the separate space's frame lies for a cave under the world, in world metres: its box's corner, turned to its facing.</summary>
    private static Transform WorldPlace(CavePlacement cave) =>
        new(cave.Corner - Vector3.Transform(DungeonCollision.Origin, cave.Turn), cave.Turn, Vector3.One);

    /// <summary>The same, in the walking session's local frame, which follows the world's origin.</summary>
    private Transform LocalPlace(CavePlacement cave)
    {
        Transform world = WorldPlace(cave);
        return world with { Translation = terrain.Frame.ToLocal(world.Translation.X, world.Translation.Y, world.Translation.Z) };
    }

    /// <summary>Where a point of a cave's own frame (layout metres) stands in the walking session.</summary>
    private Func<Vector3, Vector3> LocalPoint(CavePlacement cave) => layoutPoint =>
    {
        Vector3 world = cave.ToWorld(layoutPoint);
        return terrain.Frame.ToLocal(world.X, world.Y, world.Z);
    };

    /// <summary>
    /// Lights the descent: the sky's share of daylight falls with depth, the lantern is carried once the player is a way
    /// down, the dressed cave's growth and relics shine where they are (again whenever the world's origin moves), and an
    /// irradiance probe volume follows the player underground so the lantern's light bounces off the rock.
    /// </summary>
    private void LightCave()
    {
        double below = terrain.Caves.Below;
        sky.Below(1d - below, conditions.Time);
        if (dressedCave is UnderCave cave && caveDressing is CaveDressing dressing && cave.Ready is UndergroundCave ready)
        {
            Transform place = LocalPlace(cave.Cave);
            if (!place.Equals(caveLitAt))
            {
                caveLitAt = place;
                dressing.PlaceDrips(place);
                GrowthLights(ready.Layout.Ecology, LocalPoint(cave.Cave));
                RelicLights(ready.Layout.Relics, LocalPoint(cave.Cave));
            }
        }

        Vector3 here = terrain.Frame.ToLocal(player.WorldFeetPosition.X, player.WorldFeetPosition.Y, player.WorldFeetPosition.Z);
        if (below > 0d)
        {
            if (caveProbeCentre is not Vector3 centre || Vector3.Distance(centre, here) >= TerrainConstants.ChunkEdgeLength)
            {
                ProbeSpacing = ProbeSpacingMetres;
                engine.CameraView.SetIndirectLight(new IndirectLightRequest(here, CaveProbeExtent, ProbeSpacingMetres, ProbeBounces, IndirectAmbient.Floor));
                caveProbeCentre = here;
            }
        }
        else if (caveProbeCentre is not null)
        {
            engine.CameraView.SetIndirectLight(new IndirectLightRequest(Vector3.Zero, Vector3.Zero, ProbeSpacingMetres, ProbeBounces, IndirectAmbient.Floor));
            caveProbeCentre = null;
        }
    }

    /// <summary>
    /// The swim probe's still water for a cave under the world: at a place in the walking session, the surface and bed of
    /// the cave's lake or pool it lies in, or null. Its zones are in the cave's own frame.
    /// </summary>
    private static Func<Vector3, (float Surface, float Bed)?>? StillWater(IReadOnlyList<WaterZone> zones, CavePlacement cave, WorldFrame frame)
    {
        WaterZone[] still = [.. zones.Where(z => z.Kind is WaterZoneKind.Lake or WaterZoneKind.Pool)];
        if (still.Length == 0)
        {
            return null;
        }

        return local =>
        {
            Vector3 world = frame.ToWorld(local);
            Vector3 p = cave.ToCanonical(world);
            foreach (WaterZone zone in still)
            {
                if (p.Y < zone.Surface && (zone.Contains(p) || zone.Contains(p - Vector3.UnitY)) && zone.BedUnder(p) is float bed)
                {
                    return (local.Y + (zone.Surface - p.Y), local.Y + (bed - p.Y));
                }
            }

            return null;
        };
    }

    /// <summary>What the dressed cave adds to the snapshot, in the walking session; nothing while the player is in a separate space.</summary>
    private IEnumerable<AppearanceFact> CaveFacts()
    {
        if (player.InSeparateSpace || dressedCave is not UnderCave cave || caveDressing is not CaveDressing dressing)
        {
            return [];
        }

        Transform place = LocalPlace(cave.Cave);
        // The eye in the separate space's frame, for the overgrowth's distance thinning.
        Vector3 eye = Vector3.Transform(terrain.Frame.ToLocal(player.WorldEyePosition.X, player.WorldEyePosition.Y, player.WorldEyePosition.Z) - place.Translation,
            Quaternion.Inverse(place.Rotation));
        return dressing.Facts(eye, place);
    }

    /// <summary>The caves under the world: those asked for and admitted, the one dressed, and how deep the player is.</summary>
    internal string CavesReadout() => string.Create(CultureInfo.InvariantCulture,
        $"caves asked={underCaves.Count} ready={underCaves.Values.Count(c => c.Ready is not null)} admitted={cavesAdmitted} (generated {cavesGenerated}, from the store {cavesRead}) released={cavesReleased} {CaveStore.Readout()} dressed={(dressedCave is UnderCave d ? $"{d.Cave.Site.X},{d.Cave.Site.Z}" : "none")} skyShare={sky.CaveShare:F2} lantern={lanternLit} {terrain.CavesReadout()} {Generator.Readout()} way=[{SteerReadout()}] last={lastCave}");

    /// <summary>
    /// For captures and checks: the nearest cave mouth with a cave under it (within a search reach), its tendril's path and
    /// the cave's main route (world metres), asking for its cave now if it was not.
    /// </summary>
    internal (CavePlacement Cave, IReadOnlyList<Vector3> Route)? NearestCave(double reach)
    {
        Vector3 feet = player.WorldFeetPosition;
        List<PoiSite> sites = [];
        terrain.Recipe.Placement.CollectSitesNear((long)Math.Floor(feet.X), (long)Math.Floor(feet.Z), (long)Math.Ceiling(reach), sites);
        foreach (PoiSite site in sites.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X - feet.X) + Math.Abs(s.Z - feet.Z)))
        {
            if (terrain.Recipe.Caves.For(site) is not CavePlacement cave)
            {
                continue;
            }

            UnderCave under = underCaves.TryGetValue(cave.Key, out UnderCave? known) ? known : Ask(cave);
            IReadOnlyList<Vector3> route = under.Ready is UndergroundCave ready
                ? [.. ready.Layout.MainRoute.Select(cave.ToWorld)]
                : [];
            return (cave, route);
        }

        return null;
    }

    /// <summary>
    /// For a session restored standing in a cave under the world: the cave is generated now (waiting for the background)
    /// and admitted before the player is placed, so they resume where they were rather than in rock. False when the point
    /// is not in any cave's box.
    /// </summary>
    internal bool PrepareCaveAt(Vector3 world)
    {
        long edge = TerrainConstants.ChunkEdgeLength;
        foreach (CavePlacement cave in terrain.Recipe.Caves.NearChunkColumn(GridMath.FloorDivide((long)Math.Floor(world.X), edge), GridMath.FloorDivide((long)Math.Floor(world.Z), edge)))
        {
            if (!cave.InBox((long)Math.Floor(world.X), (long)Math.Floor(world.Y), (long)Math.Floor(world.Z), out _, out _, out _))
            {
                continue;
            }

            UnderCave under = underCaves.TryGetValue(cave.Key, out UnderCave? known) ? known : Ask(cave);
            // Asked for above (from the store, or generated); waited for here.
            UndergroundCave ready = under.Ready ?? Generator.Wait<UndergroundCave>(under.Key, () => throw new InvalidOperationException("the cave was asked for"));
            if (!terrain.Caves.IsAdmitted(cave))
            {
                under.Ready = ready;
                Admit(under, ready);
            }

            return true;
        }

        return false;
    }

    // The walk (for captures, #10889): the way from outside a cave's mouth down its tendril and along its main route, in
    // world metres, and how far along it the player has been moved.
    private List<Vector3>? walkWay;
    private (long X, long Z)? walkCave;
    private float walked;

    /// <summary>
    /// Assisted, for captures (#10889): stands the player on the ground a few metres outside the nearest cave mouth with a
    /// cave under it, facing in, and asks for its cave if it was not; the walk starts there.
    /// </summary>
    internal string CaveGoTo(double reach)
    {
        if (player.InSeparateSpace || NearestCave(reach) is not (CavePlacement cave, _))
        {
            return string.Create(CultureInfo.InvariantCulture, $"no cave mouth with a cave under it within {reach:F0} m");
        }

        Vector3 outside = new(cave.Site.X + 0.5f - (cave.ForwardX * OutsideMouthMetres), 0f, cave.Site.Z + 0.5f - (cave.ForwardZ * OutsideMouthMetres));
        float ground = (float)terrain.GroundAt(outside.X, outside.Z);
        if (player.Teleport(outside.X, ground + PlayerStandAbove, outside.Z) is null)
        {
            return "refused: no standing room outside the mouth";
        }

        Face(new Vector3(cave.ForwardX, 0f, cave.ForwardZ), 0d);
        walkCave = cave.Key;
        walkWay = null;
        walked = 0f;
        return string.Create(CultureInfo.InvariantCulture,
            $"outside the cave mouth at {cave.Site.X},{cave.Site.Z} facing {cave.ForwardX},{cave.ForwardZ}; tendril {cave.TendrilMinimum}..{cave.TendrilMaximum}, box {cave.BoxMinimum}..{cave.BoxMaximum}; {CavesReadout()}");
    }

    /// <summary>
    /// Assisted, for captures (#10889): moves the player this many metres on along the way into the cave that
    /// <see cref="CaveGoTo"/> chose - down its tendril, then along its main route once the cave is ready - facing the way on,
    /// as a walk would bring them (the residency and the sky follow). Negative walks back out.
    /// </summary>
    internal string CaveWalk(float metres)
    {
        if (walkCave is not (long, long) key || !underCaves.TryGetValue(key, out UnderCave? cave))
        {
            return "no cave chosen: craft.cave.goto first";
        }

        EnsureWay(cave);
        walked = Math.Clamp(walked + metres, 0f, Length(walkWay!));
        (Vector3 at, Vector3 heading) = Along(walkWay!, walked);
        // The route runs along a passage's middle: a standing body is sought a little above and below it, never on the
        // overworld's ground over the cave.
        float[] heights = [0f, -0.6f, -1.2f, 0.6f, -1.8f, 1.2f, -2.4f];
        float? placed = heights.Cast<float?>().FirstOrDefault(dy => player.TeleportExact(at.X, at.Y + dy!.Value, at.Z) is not null);
        if (placed is null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"refused: no standing room near {at.X:F1},{at.Y:F1},{at.Z:F1} ({walked:F0} m along)");
        }

        Face(heading, 0d);
        return string.Create(CultureInfo.InvariantCulture, $"walked {walked:F0}/{Length(walkWay!):F0} m to {at.X:F1},{at.Y:F1},{at.Z:F1}; {CavesReadout()}");
    }

    private const float OutsideMouthMetres = 6f, PlayerStandAbove = 1.2f, WalkAboveFloor = 1.2f;

    /// <summary>The way into the chosen cave: down its tendril, then along its main route once the cave is ready.</summary>
    private void EnsureWay(UnderCave cave)
    {
        if (walkWay is not null && (walkWay.Count > cave.Cave.Path.Count || cave.Ready is null))
        {
            return;
        }

        List<Vector3> way = [.. cave.Cave.Path.Skip(1).Select(p => p - new Vector3(0f, CavePlacement.TendrilHalfHeight - WalkAboveFloor, 0f))];
        if (cave.Ready is UndergroundCave ready)
        {
            // From the way in, along the main route as far as it goes.
            way.AddRange(ready.Layout.MainRoute.Select(p => cave.Cave.ToWorld(p + new Vector3(0f, WalkAboveFloor, 0f))));
        }

        walkWay = way;
    }

    // Steering for a walk on the player's own input (#10889 R10889-1): +1 in, -1 back out, 0 off.
    private int steer;

    /// <summary>How far ahead along the way the view is turned toward, in metres.</summary>
    private const float SteerAheadMetres = 4f;

    /// <summary>
    /// Assisted, for a walk on the player's own input (#10889 R10889-1): each update turns the view toward the chosen cave's
    /// way a few metres on from where the player stands - in, down the tendril and along the main route, or back out to the
    /// mouth - while held keys move the player through the ordinary controller, with its slopes and collision. Off stops.
    /// </summary>
    internal string CaveSteer(string mode)
    {
        steer = mode switch { "in" => 1, "out" => -1, _ => 0 };
        if (steer != 0 && (walkCave is not (long, long) key || !underCaves.ContainsKey(key)))
        {
            steer = 0;
            return "no cave chosen: craft.cave.goto first";
        }

        return steer switch { 1 => "steering in", -1 => "steering out", _ => "steering off" } + "; " + SteerReadout();
    }

    // The way's profile (#10889 R10889-3): how far under and over each point the rock is, a metre at a time.
    private const float ProfileStepMetres = 2f;
    private const int ProfileReachCells = 40;

    /// <summary>
    /// Assisted, for traversal proof: the chosen cave's way between two distances along it ("from-to" metres), each step's
    /// point with the floor under it and the ceiling over it as the terrain holds them, so a drop or a pinch shows.
    /// </summary>
    internal string CaveProfile(string range)
    {
        string[] ends = range.Split('-', 2);
        if (ends.Length != 2 || !float.TryParse(ends[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float from)
            || !float.TryParse(ends[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float to)
            || walkCave is not (long, long) key || !underCaves.TryGetValue(key, out UnderCave? cave))
        {
            return "craft.cave.profile <from>-<to> metres along, after craft.cave.goto";
        }

        EnsureWay(cave);
        bool Solid(long x, long y, long z) => BlockRegistry.TryGetBySlot(terrain.MaterialAt(new CraftSurvive.Game.Modules.Terrain.VoxelAddress(x, y, z)), out BlockDefinition block) && block.Collidable;
        System.Text.StringBuilder text = new(string.Create(CultureInfo.InvariantCulture, $"tendril {Length([.. cave.Cave.Path.Skip(1)]):F1}m of {Length(walkWay!):F0}m; "));
        for (float d = from; d <= Math.Min(to, Length(walkWay!)); d += ProfileStepMetres)
        {
            (Vector3 at, _) = Along(walkWay!, d);
            long x = (long)MathF.Floor(at.X), z = (long)MathF.Floor(at.Z), y = (long)MathF.Floor(at.Y);
            long floor = y, ceiling = y;
            while (floor > y - ProfileReachCells && !Solid(x, floor, z)) floor--;
            while (ceiling < y + ProfileReachCells && !Solid(x, ceiling, z)) ceiling++;
            text.Append(string.Create(CultureInfo.InvariantCulture, $"{d:F0}:({x},{y},{z}) floor={floor + 1} ceil={ceiling} | "));
        }

        return text.ToString();
    }

    /// <summary>
    /// Assisted, for traversal proof: a vertical slice of the terrain across x at one z ("x0,x1,y0,y1,z", world cells), a row
    /// a cell high from the top: # rock, . air.
    /// </summary>
    internal string CaveSlice(string box)
    {
        long[] v = [.. box.Split(',').Select(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : long.MinValue)];
        if (v.Length != 5 || v.Contains(long.MinValue) || v[1] < v[0] || v[3] < v[2] || v[1] - v[0] > ProfileReachCells * 2 || v[3] - v[2] > ProfileReachCells * 2)
        {
            return "craft.cave.slice x0,x1,y0,y1,z";
        }

        System.Text.StringBuilder text = new();
        for (long y = v[3]; y >= v[2]; y--)
        {
            text.Append(y.ToString(CultureInfo.InvariantCulture)).Append(' ');
            for (long x = v[0]; x <= v[1]; x++)
            {
                bool solid = BlockRegistry.TryGetBySlot(terrain.MaterialAt(new CraftSurvive.Game.Modules.Terrain.VoxelAddress(x, y, v[4])), out BlockDefinition block) && block.Collidable;
                text.Append(solid ? '#' : '.');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Assisted, for traversal proof: one world voxel ("x,y,z") as the chosen cave's box holds it and as the terrain answers.</summary>
    internal string CaveVoxel(string at)
    {
        long[] v = [.. at.Split(',').Select(t => long.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n) ? n : long.MinValue)];
        if (v.Length != 3 || v.Contains(long.MinValue) || walkCave is not (long, long) key || !underCaves.TryGetValue(key, out UnderCave? cave)
            || cave.Ready is not UndergroundCave ready)
        {
            return "craft.cave.voxel x,y,z after craft.cave.goto, once the cave is ready";
        }

        string box = cave.Cave.InBox(v[0], v[1], v[2], out int i, out int j, out int k)
            ? string.Create(CultureInfo.InvariantCulture, $"box ({i},{j},{k}) material={ready.Interior.Materials[ready.Interior.Index(i, j, k)]} magnitude={ready.Interior.Densities[ready.Interior.Index(i, j, k)]:F2}")
            : "outside the box";
        return string.Create(CultureInfo.InvariantCulture, $"{box}; terrain material={terrain.MaterialAt(new CraftSurvive.Game.Modules.Terrain.VoxelAddress(v[0], v[1], v[2]))}");
    }

    /// <summary>Where the player is along the way: the nearest point's distance along it and how far off it they stand.</summary>
    private string SteerReadout()
    {
        if (walkWay is null)
        {
            return "no way yet";
        }

        (float along, float off) = Progress(walkWay, player.WorldFeetPosition + (Vector3.UnitY * WalkAboveFloor));
        return string.Create(CultureInfo.InvariantCulture, $"along={along:F1}/{Length(walkWay):F0}m off={off:F1}m");
    }

    private void Steer()
    {
        if (steer == 0 || walkCave is not (long, long) key || !underCaves.TryGetValue(key, out UnderCave? cave))
        {
            return;
        }

        EnsureWay(cave);
        Vector3 here = player.WorldFeetPosition + (Vector3.UnitY * WalkAboveFloor);
        (float along, _) = Progress(walkWay!, here);
        Vector3 target;
        if (steer < 0 && along <= SteerAheadMetres)
        {
            // Out past the start of the tendril: on to where the walk began, outside the mouth.
            target = new Vector3(cave.Cave.Site.X + 0.5f - (cave.Cave.ForwardX * OutsideMouthMetres * 2f), here.Y, cave.Cave.Site.Z + 0.5f - (cave.Cave.ForwardZ * OutsideMouthMetres * 2f));
        }
        else
        {
            (target, _) = Along(walkWay!, Math.Clamp(along + (steer * SteerAheadMetres), 0f, Length(walkWay!)));
        }

        Face(target - here, 0d);
    }

    /// <summary>The nearest point of a way to a place: its distance along the way, and how far off the way the place is.</summary>
    private static (float Along, float Off) Progress(IReadOnlyList<Vector3> way, Vector3 at)
    {
        float best = float.PositiveInfinity, bestAlong = 0f, travelled = 0f;
        for (int i = 1; i < way.Count; i++)
        {
            Vector3 a = way[i - 1], ab = way[i] - a;
            float length = ab.Length();
            float t = length <= 0f ? 0f : Math.Clamp(Vector3.Dot(at - a, ab) / (length * length), 0f, 1f);
            float d = Vector3.Distance(at, a + (ab * t));
            if (d < best)
            {
                best = d;
                bestAlong = travelled + (t * length);
            }

            travelled += length;
        }

        return (bestAlong, best);
    }

    private void Face(Vector3 heading, double pitch)
    {
        if (heading.LengthSquared() < 1e-6f)
        {
            return;
        }

        player.LookTo(Math.Atan2(heading.X, -heading.Z), pitch);
    }

    private static float Length(IReadOnlyList<Vector3> way)
    {
        float length = 0f;
        for (int i = 1; i < way.Count; i++) length += Vector3.Distance(way[i - 1], way[i]);
        return length;
    }

    private static (Vector3 At, Vector3 Heading) Along(IReadOnlyList<Vector3> way, float distance)
    {
        for (int i = 1; i < way.Count; i++)
        {
            float segment = Vector3.Distance(way[i - 1], way[i]);
            if (distance <= segment || i == way.Count - 1)
            {
                float t = segment <= 0f ? 0f : Math.Clamp(distance / segment, 0f, 1f);
                return (Vector3.Lerp(way[i - 1], way[i], t), way[i] - way[i - 1]);
            }

            distance -= segment;
        }

        return (way[0], Vector3.UnitX);
    }

    /// <summary>
    /// The kit stone shader every cave's ruin and overgrowth draw through, opened once with the world (its compile costs
    /// about 50 ms, which a cave dressed in the open world must not pay each time, #10889).
    /// </summary>
    private RenderResource StoneShader => stoneShader ??= engine.Graphics.OpenResource(new RenderResourceRequest(DungeonKitLook.ShaderPath)).Handle;

    private RenderResource? stoneShader;

    // The generation probe (#10888 R10888-1): a cave generated afresh in the background while the player stands, not admitted.
    private int freshRuns;
    private string? freshKey;
    private long freshAsked;

    /// <summary>
    /// Assisted, for frame-time proof (#10888): generates the nearest cave mouth's cave afresh in the background - generated,
    /// prepared and encoded as a first visit does, never from the store or the kept caves - and does not admit it, so the
    /// updates measured meanwhile show the generator's cost alone. "readout" says whether it is done and how long it took.
    /// </summary>
    // The store's retention probe (R10888-4): more records than the store once kept, each a small stand-in for a cave.
    private const int StoreProbeRecords = 30;
    private const int StoreProbeBytes = 1024;
    private const string StoreProbeKey = "probe-";

    /// <summary>Assisted, for retention proof: which asked caves the store keeps, after writing probe records if asked.</summary>
    internal string CaveStoreProbe(string action)
    {
        if (action is not ("read" or "probe"))
        {
            return "craft.cave.store read|probe";
        }

        if (action == "probe")
        {
            byte[] payload = new byte[StoreProbeBytes];
            for (int i = 0; i < StoreProbeRecords; i++)
            {
                CaveStore.Write(StoreProbeKey + i.ToString(CultureInfo.InvariantCulture), payload);
            }
        }

        int probes = Enumerable.Range(0, StoreProbeRecords).Count(i => CaveStore.Has(StoreProbeKey + i.ToString(CultureInfo.InvariantCulture)));
        string caves = string.Join(" ", underCaves.Values.Select(c => string.Create(CultureInfo.InvariantCulture,
            $"{c.Cave.Site.X},{c.Cave.Site.Z}:{(CaveStore.Has(c.Key) ? "kept" : "not kept")}{(c.FromStore ? " (read from the store)" : "")}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"{CaveStore.Readout()} probes kept={probes}/{StoreProbeRecords} caves asked: {(caves.Length == 0 ? "none" : caves)}; generated {cavesGenerated}, from the store {cavesRead}");
    }

    internal string CaveGenerate(string action)
    {
        if (action == "readout")
        {
            if (freshKey is null)
            {
                return "no fresh generation asked for";
            }

            bool done = Generator.Finished<UndergroundCave>(freshKey) is not null;
            (double generateMs, double queuedMs) = Generator.Timing(freshKey) ?? (0d, 0d);
            return string.Create(CultureInfo.InvariantCulture,
                $"fresh {freshKey}: {(done ? $"done in {generateMs:F0} ms after {queuedMs:F0} ms queued" : $"generating for {System.Diagnostics.Stopwatch.GetElapsedTime(freshAsked).TotalMilliseconds:F0} ms")}; {Generator.Readout()}");
        }

        if (player.InSeparateSpace || NearestPlacement(3000d) is not CavePlacement cave)
        {
            return "no cave mouth with a cave under it within 3000 m";
        }

        ulong seed = seedOverride ?? DungeonSeed(terrain.SaveIdentity.Seed, cave.Site);
        CaveSetting setting = studySetting ?? terrain.CaveSettingUnder(cave.Site.X, cave.Site.Z);
        KarstCave.Settings settings = UndergroundSettings();
        ProductContent content = terrain.Content;
        freshKey = string.Create(CultureInfo.InvariantCulture, $"fresh-{++freshRuns}-{seed:x}-{settings.Key}-{setting.Key}");
        freshAsked = System.Diagnostics.Stopwatch.GetTimestamp();
        Generator.Request(freshKey, () =>
        {
            UndergroundCave ready = Prepare(DungeonCandidates.Generate(DungeonApproach.Karst, seed, 0, 1d, null, setting, settings), cave);
            CaveDressing.Preload(content, ready.Layout);
            return ready with { Encoded = CaveCodec.Encode(ready) };
        });
        return string.Create(CultureInfo.InvariantCulture, $"generating the cave under the mouth at {cave.Site.X},{cave.Site.Z} afresh as {freshKey}, on {CaveGenerator.Workers} workers");
    }

    /// <summary>The box and switches a cave under the world is generated with: the placement's box, its way in, nothing open to a sky.</summary>
    private static KarstCave.Settings UndergroundSettings() => KarstCave.Next with
    {
        ChunksX = CavePlacement.BoxChunksX,
        ChunksY = CavePlacement.BoxChunksY,
        ChunksZ = CavePlacement.BoxChunksZ,
        Underground = true,
        WayIn = CavePlacement.WayIn,
    };

    /// <summary>The nearest cave mouth with a cave under it, within a reach.</summary>
    private CavePlacement? NearestPlacement(double reach)
    {
        Vector3 feet = player.WorldFeetPosition;
        List<PoiSite> sites = [];
        terrain.Recipe.Placement.CollectSitesNear((long)Math.Floor(feet.X), (long)Math.Floor(feet.Z), (long)Math.Ceiling(reach), sites);
        return sites.Where(s => s.Kind == PoiKind.CaveMouth).OrderBy(s => Math.Abs(s.X - feet.X) + Math.Abs(s.Z - feet.Z))
            .Select(terrain.Recipe.Caves.For).FirstOrDefault(c => c is not null);
    }

    private void DisposeCaves()
    {
        Undress();
        retiringDressing?.Dispose();
        retiringDressing = null;
        underCaves.Clear();
        caveStore?.Dispose();
        caveStore = null;
    }

    /// <summary>Releases the shared shader after every dressing drawn through it is gone.</summary>
    private void DisposeStoneShader()
    {
        stoneShader?.Dispose();
        stoneShader = null;
    }
}
