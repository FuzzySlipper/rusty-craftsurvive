using Rusty.Engine.Debugging;

namespace CraftSurvive.Game.Modules.Chemistry;

/// <summary>Live-debug adapter over the chemistry field around the player (#10293): read it, light it, and read a cell.</summary>
public sealed class ChemistryDebugModule : IDebugCommandModule
{
    private readonly Func<ChemistryModule> chemistrySource;
    private readonly Func<(double X, double Z)> playerColumn;

    internal ChemistryDebugModule(Func<ChemistryModule> chemistry, Func<(double X, double Z)> playerColumn)
    {
        chemistrySource = chemistry ?? throw new ArgumentNullException(nameof(chemistry));
        this.playerColumn = playerColumn ?? throw new ArgumentNullException(nameof(playerColumn));
    }

    [DebugCommand("craft.chem.readout", Description = "Reads the chemistry field (#10293): chunks loaded and active, step cost, fires shown, and burnt ground written, waiting and refused against the terrain overlay's reserve.")]
    public string Readout() => chemistrySource().Readout();

    [DebugCommand("craft.chem.weather", Description = "Reads the ambient the field took from the weather: air heat, the wind's drift in cells a second, and rain.")]
    public string Weather() => chemistrySource().AmbientReadout();

    [DebugCommand("craft.chem.ignite", Description = "Assisted: heats the ground at the surface of world column x z, as a torch would. Whether it catches is the field's.")]
    public string Ignite(double x, double z) => chemistrySource().IgniteAt(x, z);

    [DebugCommand("craft.chem.ignite.near", Description = "Assisted: heats the ground at the surface this many metres east and south of the player.")]
    public string IgniteNear(double east, double south)
    {
        (double x, double z) = playerColumn();
        return chemistrySource().IgniteAt(x + east, z + south);
    }

    [DebugCommand("craft.chem.cell", Description = "Reads a world cell in the field: its material, whether it is open to the sky, and every quantity.")]
    public string Cell(long x, long y, long z) => chemistrySource().CellReadout(x, y, z);
}
