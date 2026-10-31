using Brio.Core;
using Brio.Files;
using Brio.Services.Models;
using Brio.Sync.Mods;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Sync;

// "Close enough to be the same". A pose or transform read back after applying it is never quite
// identical, and treating that noise as an edit makes updates bounce between players forever.
public static class SyncCompare
{
    private const float PositionTolerance = 0.001f;
    private const float RotationTolerance = 0.000001f;

    public static bool Near(float a, float b) => MathF.Abs(a - b) <= PositionTolerance;

    public static bool Near(Vector2 a, Vector2 b) => Near(a.X, b.X) && Near(a.Y, b.Y);

    public static bool Near(Vector3 a, Vector3 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z);

    public static bool Near(Vector4 a, Vector4 b) => Near(a.X, b.X) && Near(a.Y, b.Y) && Near(a.Z, b.Z) && Near(a.W, b.W);

    // q and -q are the same rotation.
    public static bool Near(Quaternion a, Quaternion b) => 1f - MathF.Abs(Quaternion.Dot(a, b)) <= RotationTolerance;

    public static bool Near(Transform a, Transform b)
        => Near(a.Position, b.Position) && Near(a.Rotation, b.Rotation) && Near(a.Scale, b.Scale);

    public static bool Near(LightDTO a, LightDTO b)
        => Near(a.Transform, b.Transform) && a.LightType == b.LightType && Near(a.Color, b.Color)
        && Near(a.Intensity, b.Intensity) && Near(a.Range, b.Range) && Near(a.Falloff, b.Falloff)
        && Near(a.LightAngle, b.LightAngle) && Near(a.FalloffAngle, b.FalloffAngle)
        && Near(a.CharacterShadowRange, b.CharacterShadowRange)
        && Near(a.ShadowPlaneNear, b.ShadowPlaneNear) && Near(a.ShadowPlaneFar, b.ShadowPlaneFar)
        && a.IsVisible == b.IsVisible && a.FriendlyName == b.FriendlyName;

    public static bool Near(SyncWorldObject a, SyncWorldObject b)
        => a.IsVisible == b.IsVisible && Near(a.Object.Transform, b.Object.Transform)
        && a.Object.FriendlyName == b.Object.FriendlyName && a.Object.ObjectType == b.Object.ObjectType
        && a.Object.Path == b.Object.Path && a.Object.StainID == b.Object.StainID
        && a.Object.Color.HasValue == b.Object.Color.HasValue
        && (!a.Object.Color.HasValue || Near(a.Object.Color.Value, b.Object.Color!.Value));

    public static bool Near(SyncCamera a, SyncCamera b)
        => a.Name == b.Name && a.Type == b.Type && a.DisableCollision == b.DisableCollision
        && Near(a.Position, b.Position) && Near(a.PositionOffset, b.PositionOffset)
        && Near(a.TargetOffset, b.TargetOffset) && Near(a.Rotation, b.Rotation)
        && Near(a.Pan, b.Pan) && Near(a.Angle, b.Angle) && Near(a.PivotRotation, b.PivotRotation)
        && Near(a.Zoom, b.Zoom) && Near(a.FoV, b.FoV);

    public static bool Near(SyncActorPose a, SyncActorPose b)
        => Near(a.Body, b.Body)
        && (a.Companion is null) == (b.Companion is null)
        && (a.Companion is null || Near(a.Companion, b.Companion!));

    public static bool Near(PoseData a, PoseData b)
        => Near(a.Position, b.Position) && Near(a.Rotation, b.Rotation) && Near(a.Scale, b.Scale)
        && Near(a.Bones, b.Bones) && Near(a.MainHand, b.MainHand) && Near(a.OffHand, b.OffHand)
        && Near(a.Prop, b.Prop) && Near(a.Ornament, b.Ornament);

    // Only bones both have: the two sides can differ, e.g. with different mods.
    private static bool Near(Dictionary<string, PoseData.Bone>? a, Dictionary<string, PoseData.Bone>? b)
    {
        if(a is null || b is null)
            return true;

        foreach(var (name, bone) in a)
        {
            if(b.TryGetValue(name, out var other)
                && !(Near(bone.Position, other.Position) && Near(bone.Rotation, other.Rotation) && Near(bone.Scale, other.Scale)))
                return false;
        }

        return true;
    }

    public static bool Same(SyncActorMods a, SyncActorMods b)
    {
        if(ReferenceEquals(a, b))
            return true;

        if(a.Files.Count != b.Files.Count || a.Swaps.Count != b.Swaps.Count || a.Meta != b.Meta || a.CustomizePlus != b.CustomizePlus)
            return false;

        for(var i = 0; i < a.Files.Count; i++)
        {
            var (x, y) = (a.Files[i], b.Files[i]);
            if(x.Hash != y.Hash || x.GamePaths.Count != y.GamePaths.Count)
                return false;
            for(var p = 0; p < x.GamePaths.Count; p++)
            {
                if(x.GamePaths[p] != y.GamePaths[p])
                    return false;
            }
        }

        for(var i = 0; i < a.Swaps.Count; i++)
        {
            if(a.Swaps[i].GamePath != b.Swaps[i].GamePath || a.Swaps[i].SwapPath != b.Swaps[i].SwapPath)
                return false;
        }

        return true;
    }
}
