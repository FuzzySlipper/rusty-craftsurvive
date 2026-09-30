using Rusty.Engine;

namespace CraftSurvive.Game.Modules.World;

/// <summary>
/// The Engine's fixed-step time as the product's modules see it. <see cref="Step"/> is the
/// simulation step this update brings the world to, so a tick-based rule counts Engine steps,
/// and <see cref="ElapsedSeconds"/> covers every step the Engine admitted - more than one when
/// it catches up.
/// </summary>
internal readonly record struct ProductStep(long Step, uint AdmittedSteps, double FixedDeltaSeconds)
{
    internal double ElapsedSeconds => AdmittedSteps * FixedDeltaSeconds;

    internal static ProductStep From(ProductUpdateFacts facts) => new(
        checked((long)(facts.SimulationStep + facts.AdmittedStepCount)),
        facts.AdmittedStepCount,
        facts.FixedDeltaSeconds);
}

/// <summary>
/// The lifecycle every gameplay module follows, so the product root composes them one way:
/// start once, advance on Engine step time, return to a fresh session on restart, release
/// Engine resources on dispose.
/// </summary>
internal interface IProductModule : IDisposable
{
    void Start();

    void Update(ProductStep step);

    /// <summary>Begins a fresh play session over the persisted world.</summary>
    void Restart();
}
