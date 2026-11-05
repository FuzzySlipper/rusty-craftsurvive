using System.Numerics;
using System.Text.Json;

namespace CraftSurvive.Game.Modules.Wildlife;

/// <summary>A body hanging from a rig joint, reaching to <see cref="End"/> or, with none, <see cref="Length"/> metres along the joint.</summary>
internal sealed record WildlifeRagdollBone(string Joint, string? End, float Length, float Radius, float Mass);

/// <summary>A limited joint between two bones: a hinge from least to most, or, with a swing, a cone that twists either way by most.</summary>
internal sealed record WildlifeRagdollLink(int Parent, int Child, Vector3 Axis, float? Swing, float Least, float Most, float Damping);

/// <summary>
/// How an animal falls (#10621): its bones and links, as `art/creatures/ragdoll.py` measured them
/// from the built mesh at a scale of 1, and the bone a killing blow lands on.
/// </summary>
internal sealed record WildlifeRagdollRig(IReadOnlyList<WildlifeRagdollBone> Bones, IReadOnlyList<WildlifeRagdollLink> Links, int HitBone)
{
    internal float Mass => Bones.Sum(bone => bone.Mass);

    internal static WildlifeRagdollRig Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        WildlifeRagdollBone[] bones = [.. root.GetProperty("bones").EnumerateArray().Select(ReadBone)];
        WildlifeRagdollLink[] links = [.. root.GetProperty("links").EnumerateArray().Select(ReadLink)];
        int hit = root.GetProperty("hitBone").GetInt32();
        if (bones.Length == 0) throw new InvalidDataException("a ragdoll needs a bone");
        if (hit < 0 || hit >= bones.Length) throw new InvalidDataException("a ragdoll's hit bone is not one of its bones");
        if (links.Any(link => link.Parent < 0 || link.Parent >= bones.Length || link.Child < 0 || link.Child >= bones.Length))
            throw new InvalidDataException("a ragdoll's link names a bone it does not have");
        return new WildlifeRagdollRig(bones, links, hit);
    }

    private static WildlifeRagdollBone ReadBone(JsonElement bone) => new(
        bone.GetProperty("joint").GetString()!,
        bone.GetProperty("end").GetString(),
        bone.GetProperty("length").GetSingle(),
        bone.GetProperty("radius").GetSingle(),
        bone.GetProperty("mass").GetSingle());

    private static WildlifeRagdollLink ReadLink(JsonElement link)
    {
        JsonElement[] axis = [.. link.GetProperty("axis").EnumerateArray()];
        Vector3 turning = new(axis[0].GetSingle(), axis[1].GetSingle(), axis[2].GetSingle());
        int parent = link.GetProperty("parent").GetInt32(), child = link.GetProperty("child").GetInt32();
        float damping = link.GetProperty("damping").GetSingle();
        if (link.TryGetProperty("swing", out JsonElement swing))
        {
            float twist = link.GetProperty("twist").GetSingle();
            return new WildlifeRagdollLink(parent, child, turning, swing.GetSingle(), -twist, twist, damping);
        }

        return new WildlifeRagdollLink(parent, child, turning, null, link.GetProperty("least").GetSingle(), link.GetProperty("most").GetSingle(), damping);
    }
}
