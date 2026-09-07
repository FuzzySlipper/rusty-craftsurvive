using System.Numerics;
using Rusty.Engine;

namespace CraftSurvive.Game.Modules.Terrain.Recipes;

// A scoped vocabulary over Engine fields, not a second evaluator or mesh owner.
internal sealed class ImplicitRecipe(IImplicitSurfacesService service) : IDisposable
{
    internal ImplicitField Field { get; } = service.CreateField();
    internal ImplicitNode Box(Vector3 min, Vector3 max) => service.AddBox(new(Field, min, max));
    internal ImplicitNode Sphere(Vector3 center, float radius) => service.AddSphere(new(Field, center, radius));
    internal ImplicitNode Ellipsoid(Vector3 center, Vector3 radii) => service.AddEllipsoid(new(Field, center, radii));
    internal ImplicitNode Capsule(Vector3 start, Vector3 end, float radius) => service.AddCapsule(new(Field, start, end, radius));
    internal ImplicitNode Plane(Vector3 normal, float offset) => service.AddPlane(new(Field, normal, offset));
    internal ImplicitNode Union(ImplicitNode a, ImplicitNode b) => service.Union(new(Field, a, b));
    internal ImplicitNode Intersect(ImplicitNode a, ImplicitNode b) => service.Intersection(new(Field, a, b));
    internal ImplicitNode Subtract(ImplicitNode a, ImplicitNode b) => service.Difference(new(Field, a, b));
    internal ImplicitNode Offset(ImplicitNode source, float amount) => service.Offset(new(Field, source, amount));
    internal ImplicitNode Translate(ImplicitNode source, Vector3 translation) => service.Transform(new(Field, source,
        new Transform(translation, Quaternion.Identity, Vector3.One)));
    internal ImplicitNode Place(ImplicitNode source, Transform placement) => service.Transform(new(Field, source, placement));
    internal ImplicitNode Blend(ImplicitNode a, ImplicitNode b, float radius) => service.SmoothUnion(new(Field, a, b, radius));
    public void Dispose() => Field.Dispose();
}

internal readonly record struct RecipeSampling(float CellSize, float CreaseDegrees, float TextureRepeats,
    ImplicitMaterialBoundaryMode MaterialBoundaries);

// Consumed synchronously while Field is alive. Materials are borrowed from the
// caller; the runtime adapter owns every generated mesh and appearance.
internal sealed record RecipeSurface(string Name, ImplicitField Field, ImplicitNode Root,
    Vector3 Min, Vector3 Max, Material Material, ImplicitMaterialRegion[] Regions,
    RecipeSampling Sampling, Transform Placement);

internal sealed class RecipeWriter(IImplicitSurfacesService service, RecipeSampling sampling,
    Action<RecipeSurface> emit)
{
    internal ImplicitRecipe Begin() => new(service);

    internal void Surface(string name, ImplicitRecipe field, ImplicitNode root, Vector3 min, Vector3 max,
        Material material, Transform placement, ImplicitMaterialRegion[]? regions = null, float? cellSize = null)
        => emit(new(name, field.Field, root, min, max, material, regions ?? [],
            sampling with { CellSize = cellSize ?? sampling.CellSize }, placement));

    internal void Box(string name, Vector3 min, Vector3 max, Material material, Transform placement)
    {
        using ImplicitRecipe field = Begin();
        Vector3 extent = max - min;
        float narrowest = MathF.Min(extent.X, MathF.Min(extent.Y, extent.Z));
        Surface(name, field, field.Box(min, max), min, max, material, placement,
            cellSize: MathF.Min(0.5f, narrowest * 0.75f));
    }
}
