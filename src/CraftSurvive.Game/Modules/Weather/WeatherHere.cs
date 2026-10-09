using System.Numerics;

namespace CraftSurvive.Game.Modules.Weather;

/// <summary>
/// The weather over a moving point as of the clock (#9738): sampled again whenever it is read and the
/// clock or the point has moved enough since, so it is never older than what reads it - even while
/// the map is open and gameplay updates are skipped, as travel and camp still turn the clock.
/// </summary>
internal sealed class WeatherHere
{
    /// <summary>The sample stands until the clock moves this far, in game hours, or the point this far.</summary>
    internal const double RefreshHours = 1.0 / 60, RefreshMetres = 25;

    private readonly WeatherField field;
    private readonly Func<double> hours;
    private readonly Func<Vector2> at;
    private EnvironmentSample sample = EnvironmentSample.Clear;
    private double sampledHours = double.NegativeInfinity;
    private Vector2 sampledAt;

    internal WeatherHere(WeatherField field, Func<double> hours, Func<Vector2> at)
    {
        this.field = field ?? throw new ArgumentNullException(nameof(field));
        this.hours = hours ?? throw new ArgumentNullException(nameof(hours));
        this.at = at ?? throw new ArgumentNullException(nameof(at));
    }

    /// <summary>The weather over the point now, to within <see cref="RefreshHours"/> and <see cref="RefreshMetres"/>.</summary>
    internal EnvironmentSample Current => Refresh(force: false);

    /// <summary>The weather over the point exactly now: for a readout, or after the field itself changed.</summary>
    internal EnvironmentSample Fresh() => Refresh(force: true);

    private EnvironmentSample Refresh(bool force)
    {
        double now = hours();
        Vector2 here = at();
        if (force || Math.Abs(now - sampledHours) >= RefreshHours || Vector2.Distance(here, sampledAt) >= RefreshMetres)
        {
            sample = field.Sample(here.X, here.Y, now);
            sampledHours = now;
            sampledAt = here;
        }

        return sample;
    }
}
