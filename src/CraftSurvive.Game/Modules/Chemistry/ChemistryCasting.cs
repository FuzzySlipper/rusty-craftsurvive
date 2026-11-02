using System.Globalization;
using CraftSurvive.Game.Modules.Content;
using CraftSurvive.Game.Modules.Terrain;
using CraftSurvive.Game.Modules.World;
using Rusty.Engine;
using RustyChemistry.Casting;
using RustyChemistry.Simulation;
using VoxelAddress = CraftSurvive.Game.Modules.Terrain.VoxelAddress;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>
/// The development magic compiler (#10529; atlas, 2026-11-01): a hexagram composed from the Bagua grammar, compiled
/// into a formula and cast where the player aims, alongside the compiled formulas the game carries. It is a design
/// and testing instrument: the finished game may carry only compiled formulas. It owns the composition and what the
/// last cast did; the field and its casts stay the chemistry module's.
/// </summary>
internal sealed class ChemistryCasting : IProductModule
{
    /// <summary>The composition a world starts with: fire under heaven, 13 同人 Fellowship.</summary>
    private const string FirstUpper = "heaven", FirstLower = "fire";

    private readonly ChemistryModule chemistry;
    private readonly Func<TerrainPick> aim;
    private readonly Func<bool> castRequested;
    private readonly ProductUiPublisher ui;
    private string upper = FirstUpper, lower = FirstLower;
    private string last = "nothing cast yet";

    /// <param name="aim">What the player is aiming at, within reach.</param>
    /// <param name="castRequested">Whether the player pressed the cast key this update.</param>
    internal ChemistryCasting(ProductContent content, ChemistryModule chemistry, Func<TerrainPick> aim, Func<bool> castRequested,
        ProductUiPublisher ui)
    {
        ArgumentNullException.ThrowIfNull(content);
        this.chemistry = chemistry ?? throw new ArgumentNullException(nameof(chemistry));
        this.aim = aim ?? throw new ArgumentNullException(nameof(aim));
        this.castRequested = castRequested ?? throw new ArgumentNullException(nameof(castRequested));
        this.ui = ui ?? throw new ArgumentNullException(nameof(ui));
        string path = chemistry.Settings.Casting.GrammarPath;
        Grammar = BaguaGrammar.Read(content.ReadText(path), chemistry.Pack, path);
        Composed = Grammar.Compile(Grammar.Hexagram(upper, lower));
    }

    internal BaguaGrammar Grammar { get; }

    /// <summary>The composition compiled: what a cast now casts.</summary>
    internal Formula Composed { get; private set; }

    public void Start() => Publish();

    public void Update(ProductStep step)
    {
        if (castRequested())
        {
            Cast();
        }
    }

    public void Restart()
    {
        last = "nothing cast yet";
        Publish();
    }

    public void Dispose()
    {
    }

    /// <summary>Composes a hexagram: <paramref name="outer"/> over <paramref name="inner"/>.</summary>
    internal string Compose(string outer, string inner)
    {
        if (!Grammar.Trigrams.ContainsKey(outer) || !Grammar.Trigrams.ContainsKey(inner))
        {
            return Refused($"no trigram '{(Grammar.Trigrams.ContainsKey(outer) ? inner : outer)}'; trigrams: {string.Join(", ", Grammar.Trigrams.Keys)}");
        }

        (upper, lower) = (outer, inner);
        Composed = Grammar.Compile(Grammar.Hexagram(upper, lower));
        Publish();
        return $"{Composed.Label}: {Composed.Detail}: {FormulaTool.Describe(Composed)}";
    }

    /// <summary>Casts the composition where the player aims.</summary>
    internal string Cast() => CastAtAim(Composed);

    /// <summary>Casts the composition at a world cell, as aiming at its top face would.</summary>
    internal string CastAt(long x, long y, long z) => Land(Composed, Above(x, y, z));

    /// <summary>Casts one of the formulas the game carries where the player aims, or at a world cell.</summary>
    internal string CastFormula(string id, (long X, long Y, long Z)? at = null)
    {
        Formula? formula = chemistry.Formulas.FirstOrDefault(known => known.Id == id);
        if (formula is null)
        {
            return Refused($"no formula '{id}'; formulas: {string.Join(", ", chemistry.Formulas.Select(known => known.Id))}");
        }

        return at is (long x, long y, long z) ? Land(formula, Above(x, y, z)) : CastAtAim(formula);
    }

    internal string ShowOverlay(string quantity)
    {
        string outcome = chemistry.ShowOverlay(quantity);
        Publish();
        return outcome;
    }

    private string CastAtAim(Formula formula)
    {
        TerrainPick pick = aim();
        if (pick.Outcome != TerrainPickOutcome.Picked)
        {
            return Refused("aim at the ground within reach to cast");
        }

        return Land(formula, new CastTarget(CellOf(pick.Target), CellOf(pick.Adjacent)));
    }

    private string Land(Formula formula, CastTarget target)
    {
        last = chemistry.Cast(new FormulaTool(formula, chemistry.Pack), target);
        Publish();
        return last;
    }

    private string Refused(string why)
    {
        last = $"refused: {why}";
        Publish();
        return last;
    }

    private static CastTarget Above(long x, long y, long z)
    {
        Cell hit = CellOf(new VoxelAddress(x, y, z));
        return new CastTarget(hit, hit.Offset(0, 1, 0));
    }

    /// <summary>A field cell is a world voxel: the field keeps world coordinates within its window.</summary>
    private static Cell CellOf(VoxelAddress address) => new((int)address.X, (int)address.Y, (int)address.Z);

    private void Publish()
    {
        string trigrams = string.Join(";", Grammar.Trigrams.Select(pair => $"{pair.Key}|{pair.Value.Glyph}|{pair.Value.Name}"));
        string formulas = string.Join(";", chemistry.Formulas.Select(formula => $"{formula.Id}|{formula.Label}"));
        string overlays = string.Join(";", chemistry.Settings.Overlays.Select(overlay => $"{overlay.Quantity}|{overlay.Label}"));
        ui.PublishChemistry(new ChemistryUiFacts(trigrams, upper, lower, Composed.Label, Composed.Detail ?? "", FormulaTool.Describe(Composed),
            Composed.WriteIndented(), formulas, overlays, chemistry.Overlay?.Quantity ?? "none", last));
    }

    /// <summary>The compiled composition as JSON, to keep as a shipped formula.</summary>
    internal string FormulaJson() => Composed.WriteIndented();

    /// <summary>What every hexagram compiles to in this grammar, one line each, for reading the grammar whole.</summary>
    internal string Table() => string.Join("\n", Grammar.Hexagrams.OrderBy(hexagram => hexagram.Number).Select(hexagram =>
    {
        Formula formula = Grammar.Compile(hexagram);
        return string.Create(CultureInfo.InvariantCulture, $"{formula.Label} ({formula.Detail}): {FormulaTool.Describe(formula)}");
    }));
}
