using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Feedback;

/// <summary>
/// Shows cues as particle bursts (<see cref="Bursts.ByCue"/>) through the Engine's one-shot
/// emission. Puffs are drawn with the dust sprite and chips with the terrain atlas. A cue raised
/// with an identity always looks the same; one without takes the next of a running count. A burst
/// the Engine refuses is counted and reported, and play goes on.
/// </summary>
internal sealed class BurstEmitter
{
    /// <summary>Mixed into a running count, so bursts without an identity of their own still differ.</summary>
    private const ulong SequenceMix = 0x9E37_79B9_7F4A_7C15UL;

    /// <summary>Separates each style's seed from the others' for the same event.</summary>
    private const ulong StyleSalt = 0xD6E8_FEB8_6659_FD93UL;

    private readonly IEngineContext engine;
    private readonly Func<RenderResourceReference> atlas;
    private RenderResourceReference puff;
    private ulong sequence;
    private long emitted;
    private long refused;
    private string lastFailure = "none";

    internal BurstEmitter(IEngineContext engine, Func<RenderResourceReference> atlas)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
    }

    /// <summary>Opens the dust sprite. The atlas is the terrain's, read when a chip burst needs it.</summary>
    internal void Start()
    {
        RenderResourceInfo resource = engine.Graphics.OpenResource(new RenderResourceRequest(Bursts.PuffContentPath));
        if (resource.Kind != RenderResourceKind.Texture || resource.ByteLength == 0)
        {
            throw new InvalidOperationException($"CraftSurvive dust sprite '{Bursts.PuffContentPath}' must be a non-empty Engine texture.");
        }

        puff = resource.Handle;
    }

    /// <summary>Shows one cue, if it has a burst and a place.</summary>
    internal void Show(RaisedCue raised)
    {
        if (raised.At is not Vector3 at || !Bursts.ByCue.TryGetValue(raised.Cue, out BurstStyle[]? styles))
        {
            return;
        }

        ulong identity = raised.Identity != 0UL ? raised.Identity : unchecked(++sequence * SequenceMix);
        for (int index = 0; index < styles.Length; index++)
        {
            BurstStyle style = styles[index];
            RenderResourceReference sprite = style.Look == BurstLook.Chips ? atlas() : puff;
            ulong seed = unchecked(identity ^ ((ulong)index * StyleSalt));
            try
            {
                engine.Presentation.EmitParticles(Bursts.Describe(style, at, sprite, seed));
                emitted++;
            }
            catch (EngineCallException exception)
            {
                refused++;
                lastFailure = exception.Message;
            }
        }
    }

    internal string Readout() => $"bursts emitted={emitted} refused={refused} failure={lastFailure}";
}
