using Brio.Core;
using Brio.Game.World.Interop;
using Brio.Services.Models;
using System;
using System.Numerics;

namespace Brio.Sync;

// Checks data from other players before it touches the game. Anything odd is dropped.
public static class SyncValidation
{
    private const float MaxCoordinate = 100_000f;
    private const float MaxValue = 100_000f;
    private const int MaxNameLength = 64;

    public static bool IsValid(LightDTO dto)
    {
        if(!Enum.IsDefined(dto.LightType))
            return false;

        if((dto.FriendlyName?.Length ?? 0) > MaxNameLength)
            return false;

        return IsValid(dto.Transform)
            && IsFinite(dto.Color, MaxValue)
            && IsFinite(dto.Intensity, MaxValue)
            && IsFinite(dto.Range, MaxValue)
            && IsFinite(dto.Falloff, MaxValue)
            && IsFinite(dto.LightAngle, 360f)
            && IsFinite(dto.FalloffAngle, 360f)
            && IsFinite(dto.CharacterShadowRange, MaxValue)
            && IsFinite(dto.ShadowPlaneNear, MaxValue)
            && IsFinite(dto.ShadowPlaneFar, MaxValue);
    }

    public static bool IsValid(Transform transform)
        => IsFinite(transform.Position, MaxCoordinate)
        && IsFinite(transform.Scale, MaxValue)
        && float.IsFinite(transform.Rotation.X) && float.IsFinite(transform.Rotation.Y)
        && float.IsFinite(transform.Rotation.Z) && float.IsFinite(transform.Rotation.W)
        && transform.Rotation.Length() is > 0.5f and < 1.5f;

    private static bool IsFinite(float value, float max)
        => float.IsFinite(value) && MathF.Abs(value) <= max;

    private static bool IsFinite(Vector3 value, float max)
        => IsFinite(value.X, max) && IsFinite(value.Y, max) && IsFinite(value.Z, max);
}
