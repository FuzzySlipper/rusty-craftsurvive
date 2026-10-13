using System.Globalization;
using System.Numerics;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Water;

/// <summary>
/// An opt-in visual experiment over an actual river patch. The Engine solves the bed and boundary
/// stages; only its published current is used here. Gameplay water still comes from WaterModel.
/// </summary>
internal sealed class WaterFlowStudy(IEngineContext engine, WorldFrame frame, WaterModel model) : IDisposable
{
    internal const int Cells = 64, CellMetres = 2, Extent = Cells * CellMetres;
    private const int RefreshSteps = 12;
    private const float EdgeBlendMetres = 12, MinimumDepth = 0.04f;
    private WaterField? field;
    private long cornerX, cornerZ, nextRefresh;
    private ulong snapshot;

    internal string Start(SpatialSession session, double x, double z)
    {
        Dispose();
        cornerX = (long)Math.Floor((x - Extent / 2.0) / WaterSurfaces.TileMetres) * WaterSurfaces.TileMetres;
        cornerZ = (long)Math.Floor((z - Extent / 2.0) / WaterSurfaces.TileMetres) * WaterSurfaces.TileMetres;
        float[] bed = new float[Cells * Cells], depth = new float[Cells * Cells];
        WaterSample[] samples = new WaterSample[bed.Length];
        for (int j = 0; j < Cells; j++)
        for (int i = 0; i < Cells; i++)
        {
            int at = j * Cells + i;
            WaterSample sample = model.Sample(cornerX + (i + 0.5) * CellMetres, cornerZ + (j + 0.5) * CellMetres);
            samples[at] = sample;
            bed[at] = (float)sample.Bed;
            depth[at] = sample.Ice ? 0 : (float)sample.Depth;
        }

        field = engine.WaterField.Create(new WaterFieldCreateRequest(Cells, Cells, CellMetres, bed,
            frame.ToLocal(cornerX, 0, cornerZ))
        {
            Depth = depth,
            Session = session,
            Anchor = new WorldOriginGlobalPosition(cornerX, 0, cornerZ, 0, 0, 0),
        });
        for (uint i = 0; i < Cells; i++)
        {
            Boundary(WaterFieldSide.ZMin, i, samples[i]);
            Boundary(WaterFieldSide.ZMax, i, samples[(Cells - 1) * Cells + i]);
            Boundary(WaterFieldSide.XMin, i, samples[i * Cells]);
            Boundary(WaterFieldSide.XMax, i, samples[i * Cells + Cells - 1]);
        }
        nextRefresh = 0;
        snapshot = 0;
        return Readout();
    }

    private void Boundary(WaterFieldSide side, uint at, WaterSample sample)
    {
        if (sample.Wet && !sample.Ice)
            engine.WaterField.SetBoundary(WaterFieldBoundaryRequest.Stage(field!, side, (float)sample.Top, at, 1));
    }

    internal bool Overlaps(long x, long z, int extent) => field is not null &&
        x + extent > cornerX && x < cornerX + Extent && z + extent > cornerZ && z < cornerZ + Extent;

    internal bool Update(long step)
    {
        if (field is null || step < nextRefresh) return false;
        nextRefresh = step + RefreshSteps;
        WaterFieldReadout read = engine.WaterField.Read(field);
        if (snapshot == read.Snapshots) return false;
        snapshot = read.Snapshots;
        return true;
    }

    /// <summary>One Engine batch, preserving regional flow outside wet field cells and fading at its edge.</summary>
    internal void Apply(long x, long z, int side, int spacing, Vector2[] flow)
    {
        if (field is null || !Overlaps(x, z, (side - 1) * spacing)) return;
        WaterFieldPoint[] points = new WaterFieldPoint[flow.Length];
        for (int j = 0; j < side; j++)
        for (int i = 0; i < side; i++)
        {
            Vector3 local = frame.ToLocal(x + i * spacing, 0, z + j * spacing);
            points[j * side + i] = new(local.X, local.Z);
        }
        ReadOnlySpan<WaterFieldSample> samples = engine.WaterField.Sample(new WaterFieldSampleRequest(field, points)).Span;
        for (int j = 0; j < side; j++)
        for (int i = 0; i < side; i++)
        {
            int at = j * side + i;
            WaterFieldSample sample = samples[at];
            if (!sample.Inside || !sample.Wet || sample.Depth < MinimumDepth) continue;
            double edge = Math.Min(Math.Min(x + i * spacing - cornerX, cornerX + Extent - (x + i * spacing)),
                Math.Min(z + j * spacing - cornerZ, cornerZ + Extent - (z + j * spacing)));
            float weight = Math.Clamp((float)edge / EdgeBlendMetres, 0, 1);
            weight = weight * weight * (3 - 2 * weight);
            flow[at] = Vector2.Lerp(flow[at], sample.Velocity, weight);
        }
    }

    internal string Readout()
    {
        if (field is null) return "flowField=off (regional currents)";
        WaterFieldReadout read = engine.WaterField.Read(field);
        return string.Create(CultureInfo.InvariantCulture,
            $"flowField=on corner={cornerX},{cornerZ} extent={Extent} cellMetres={CellMetres} snapshots={read.Snapshots} age={read.Age:F3}s workerUs={read.StepMicroseconds} wetCells={read.WetCells} load={read.Load:F3} dropped={read.DroppedSeconds:F3}s visualOnly=true");
    }

    internal string At(double x, double z)
    {
        WaterSample regional = model.Sample(x, z);
        string ordinary = string.Create(CultureInfo.InvariantCulture, $"regionalVelocity={regional.CurrentX:F3},{regional.CurrentZ:F3}");
        if (field is null) return $"{ordinary} flowField=off";
        WaterFieldSample sample = engine.WaterField.Sample(field, frame.ToLocal(x, 0, z));
        return string.Create(CultureInfo.InvariantCulture,
            $"{ordinary} inside={sample.Inside} wet={sample.Wet} fieldVelocity={sample.VelocityX:F3},{sample.VelocityZ:F3} depth={sample.Depth:F3} age={sample.Age:F3}s");
    }

    public void Dispose()
    {
        field?.Dispose();
        field = null;
    }
}
