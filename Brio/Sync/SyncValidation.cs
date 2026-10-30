using Brio.Core;
using Brio.Files;
using Brio.Game.Types;
using Brio.Game.World.Interop;
using Brio.Services.Models;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Sync;

// Checks data from other players before it touches the game. Anything odd is dropped.
public static class SyncValidation
{
    private const float MaxCoordinate = 100_000f;
    private const float MaxValue = 100_000f;
    private const int MaxNameLength = 64;
    private const int MaxBonesPerSkeleton = 1024;
    private const int MaxGlamourerStateLength = 64 * 1024;

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

    public static bool IsValid(SyncActorAppearance dto)
    {
        if(dto.Name.Length > MaxNameLength || (dto.GlamourerState?.Length ?? 0) > MaxGlamourerStateLength)
            return false;

        if(!Enum.IsDefined(dto.Companion.Kind))
            return false;

        var chara = dto.Appearance;
        if(chara is null || chara.ModelType > ushort.MaxValue)
            return false;

        // Library metadata has no business here.
        chara.Author = null;
        chara.Description = null;
        chara.Version = null;
        chara.Base64Image = null;
        chara.Tags = null;

        return IsFinite(chara.Transparency, 1f)
            && IsFinite(chara.HeightMultiplier, 100f)
            && IsFinite(chara.MuscleTone, 100f)
            && IsFinite(chara.SkinColor) && IsFinite(chara.SkinGloss)
            && IsFinite(chara.LeftEyeColor) && IsFinite(chara.RightEyeColor)
            && IsFinite(chara.LimbalRingColor) && IsFinite(chara.HairColor)
            && IsFinite(chara.HairGloss) && IsFinite(chara.HairHighlight)
            && IsFinite(chara.BustScale)
            && (!chara.MouthColor.HasValue || IsFinite(chara.MouthColor.Value, MaxValue));
    }

    public static bool IsValid(SyncActorPose dto)
        => IsValid(dto.Body) && (dto.Companion is null || IsValid(dto.Companion));

    public static bool IsValid(PoseData pose)
    {
        pose.Author = null;
        pose.Description = null;
        pose.Version = null;
        pose.Base64Image = null;
        pose.Tags = null;

        return IsValid(pose.Bones) && IsValid(pose.MainHand) && IsValid(pose.OffHand)
            && IsValid(pose.Prop) && IsValid(pose.Ornament)
            && IsValid(pose.ModelDifference) && IsValid(pose.ModelAbsoluteValues)
            && IsValid(new Transform { Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale });
    }

    private static bool IsValid(Dictionary<string, PoseData.Bone>? bones)
    {
        if(bones is null)
            return true;

        if(bones.Count > MaxBonesPerSkeleton)
            return false;

        foreach(var (name, bone) in bones)
        {
            if(name.Length > MaxNameLength || !IsValid(bone))
                return false;
        }

        return true;
    }

    private static bool IsValid(PoseData.Bone? bone)
        => bone is not null
        && IsFinite(bone.Position, MaxCoordinate)
        && IsFinite(bone.Scale, MaxValue)
        && IsFinite(bone.Rotation);

    public static bool IsValid(Transform transform)
        => IsFinite(transform.Position, MaxCoordinate)
        && IsFinite(transform.Scale, MaxValue)
        && IsFinite(transform.Rotation)
        && transform.Rotation.Length() is > 0.5f and < 1.5f;

    private static bool IsFinite(Quaternion value)
        => IsFinite(value.X, 2f) && IsFinite(value.Y, 2f) && IsFinite(value.Z, 2f) && IsFinite(value.W, 2f);

    private static bool IsFinite(float value, float max)
        => float.IsFinite(value) && MathF.Abs(value) <= max;

    private static bool IsFinite(Vector3 value, float max)
        => IsFinite(value.X, max) && IsFinite(value.Y, max) && IsFinite(value.Z, max);

    private static bool IsFinite(Vector3? value)
        => !value.HasValue || IsFinite(value.Value, MaxValue);

    private static bool IsFinite(Vector4 value, float max)
        => IsFinite(value.X, max) && IsFinite(value.Y, max) && IsFinite(value.Z, max) && IsFinite(value.W, max);
}
