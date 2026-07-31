using System;
using System.IO;
using System.Linq;
using System.Numerics;

using FluentAssertions;

using MapaTur.Application.Terrain;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// Smoke-test gate for the 3rd-person walk-mode avatar asset (KayKit "Adventurers" — Rogue_Hooded, CC0). It must
/// load through the SAME <see cref="SkinnedModel"/> pipeline as the ridden dragon and expose the animation clips
/// the walk-mode animation state machine binds by name (Idle / Walking / Running / Jump). This guards against a
/// re-export or asset swap silently dropping the rig, the skin weights, the base-colour texture, or a required
/// clip — the "Faza 0" loader gate from docs/PLAN-third-person-character.md.
/// </summary>
public sealed class HumanoidModelTests
{
    private static string HikerPath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "hiker.glb");

    private static SkinnedModel LoadHiker() => SkinnedModel.Load(HikerPath);

    // The clips the walk-mode FSM binds by name (mapping WalkPhysics states -> locomotion / jump).
    private static readonly string[] RequiredClips =
    {
        "Idle", "Walking_A", "Running_A", "Jump_Start", "Jump_Idle", "Jump_Land",
    };

    [Fact]
    public void Loads_AsSkinnedGeometry_WithTexture()
    {
        var model = LoadHiker();

        model.Primitives.Should().NotBeEmpty("the avatar has drawable geometry");
        model.Primitives.Should().OnlyContain(p => p.BindPositions.Length > 0 && p.Indices.Length > 0);
        model.BaseColorImageBytes.Should().NotBeNullOrEmpty("the KayKit character carries a base-colour atlas texture");
        (model.BoundsMax - model.BoundsMin).Length().Should().BeGreaterThan(0f, "the posed bind mesh has real extent");
    }

    [Fact]
    public void Exposes_TheClipsTheWalkAnimationStateMachineNeeds()
    {
        var model = LoadHiker();

        model.Animations.Should().NotBeEmpty();
        var names = model.Animations.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
        foreach (string clip in RequiredClips)
        {
            names.Should().Contain(clip, "the walk FSM plays '{0}'", clip);
        }

        model.Animations.Where(a => RequiredClips.Contains(a.Name))
            .Should().OnlyContain(a => a.Duration > 0f, "bound clips must have real duration");
    }

    [Fact]
    public void Pose_WritesFiniteSkinnedVertices()
    {
        var model = LoadHiker();
        int idle = model.Animations.Select((a, i) => (a.Name, i)).First(t => t.Name == "Idle").i;

        model.Pose(idle, seconds: 0.2f);

        foreach (SkinnedModel.Primitive p in model.Primitives)
        {
            p.PosedPositions.Should().HaveCount(p.BindPositions.Length);
            p.PosedPositions.Should().OnlyContain(v => IsFinite(v), "skinned positions must be finite");
        }
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}