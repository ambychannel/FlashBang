using Brio.Core;
using Brio.Entities.Camera;
using Brio.Files;
using Brio.Game.Types;
using Brio.Game.World.Interop;
using Brio.Game.WorldObjects;
using Brio.Resources;
using Brio.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
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
    private const int MaxPathLength = 256;
    private const int MaxBoneEdits = 4096;
    private const int MaxStacksPerBone = 32;

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
        if((dto.Name?.Length ?? 0) > MaxNameLength || (dto.GlamourerState?.Length ?? 0) > MaxGlamourerStateLength)
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

    public static bool IsValid(SyncWorldObject dto)
    {
        var obj = dto.Object;
        if(obj is null || !Enum.IsDefined(obj.ObjectType) || (obj.FriendlyName?.Length ?? 0) > MaxNameLength || obj.StainID > byte.MaxValue)
            return false;

        if(obj.ObjectType == WorldObjectType.Prop ? obj.PropModel is null : !IsGamePath(obj.Path))
            return false;

        obj.ParentFolderId = null;

        return IsValid(obj.Transform)
            && (!obj.Color.HasValue || IsFinite(obj.Color.Value, MaxValue));
    }

    public static bool IsValid(SyncCamera dto)
        => (dto.Name?.Length ?? 0) <= MaxNameLength
        && dto.Type is CameraType.Free or CameraType.Game
        && IsFinite(dto.Position, MaxCoordinate) && IsFinite(dto.PositionOffset, MaxCoordinate)
        && IsFinite(dto.TargetOffset, MaxCoordinate) && IsFinite(dto.Rotation, MaxValue)
        && IsFinite(dto.Pan.X, MaxValue) && IsFinite(dto.Pan.Y, MaxValue)
        && IsFinite(dto.Angle.X, MaxValue) && IsFinite(dto.Angle.Y, MaxValue)
        && IsFinite(dto.PivotRotation, MaxValue) && IsFinite(dto.Zoom, MaxValue) && IsFinite(dto.FoV, MaxValue);

    public static bool IsValid(SyncReferenceImage dto)
        => (dto.Name?.Length ?? 0) <= MaxNameLength
        && dto.Image is not null && dto.Image.Length <= SyncLimits.MaxImageBytes
        && ImageExtension(dto.Image) is not null;

    public static bool IsValid(SyncReferenceImageView dto)
        => IsFinite(dto.Opacity, 1f) && IsFinite(dto.Zoom, 1000f) && IsFinite(dto.Rotation, MaxValue)
        && IsFinite(dto.PanOffset.X, MaxValue) && IsFinite(dto.PanOffset.Y, MaxValue);

    // ".png" or ".jpg" going by the first bytes, or null for anything else.
    public static string? ImageExtension(ReadOnlySpan<byte> image)
    {
        if(image.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ".png";
        if(image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
            return ".jpg";
        return null;
    }

    public static bool IsValid(Mods.SyncActorMods dto)
    {
        if(dto.Files is null || dto.Swaps is null || dto.Meta is null)
            return false;

        if(dto.Files.Count > Mods.SyncModLimits.MaxFiles || dto.Swaps.Count > Mods.SyncModLimits.MaxFiles
            || dto.Meta.Length > Mods.SyncModLimits.MaxMetaLength || (dto.CustomizePlus?.Length ?? 0) > Mods.SyncModLimits.MaxCustomizePlusLength)
            return false;

        long total = 0;
        foreach(var file in dto.Files)
        {
            if(!IsHash(file.Hash) || file.Size <= 0 || file.Size > Mods.SyncModLimits.MaxFileBytes)
                return false;
            if(file.GamePaths is null || file.GamePaths.Count == 0 || file.GamePaths.Count > Mods.SyncModLimits.MaxGamePathsPerFile)
                return false;
            foreach(var path in file.GamePaths)
            {
                if(!IsModGamePath(path))
                    return false;
            }
            total += file.Size;
        }

        if(total > Mods.SyncModLimits.MaxActorBytes)
            return false;

        foreach(var swap in dto.Swaps)
        {
            if(!IsModGamePath(swap.GamePath) || !IsModGamePath(swap.SwapPath))
                return false;
        }

        return true;
    }

    public static bool IsHash(string? hash)
        => hash is { Length: 64 } && hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    // A lowercase game path with a file type mods are allowed to replace.
    public static bool IsModGamePath(string? path)
        => IsGamePath(path) && path == path.ToLowerInvariant()
        && Mods.SyncModLimits.AllowedExtensions.Any(e => path.EndsWith(e, StringComparison.Ordinal));

    // A path inside the game's data, like "bgcommon/hou/indoor/.../asset.sgb". Never a file on disk.
    public static bool IsGamePath(string? path)
    {
        if(string.IsNullOrEmpty(path) || path.Length > MaxPathLength || !path.Contains('/') || path.StartsWith('/') || path.Contains(".."))
            return false;

        foreach(var c in path)
        {
            if(!(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '.'))
                return false;
        }

        return true;
    }

    public static bool IsValid(SyncAnimState dto)
    {
        if(!IsFinite(dto.Speed, 100f) || !IsFinite(dto.Time, MaxValue) || !IsFinite(dto.Duration, MaxValue))
            return false;

        if(!IsTimeline(dto.Base) || !IsTimeline(dto.Blend))
            return false;

        return dto.Companion is null || IsValid(dto.Companion);
    }

    // A real animation, or none.
    public static bool IsTimeline(ushort id)
        => id == 0 || GameDataProvider.Instance.ActionTimelines.HasRow(id);

    public static bool IsValid(SyncActorEdits dto)
    {
        if(dto.Bones is null || dto.Removed is null || dto.Bones.Count > MaxBoneEdits || dto.Removed.Count > MaxBoneEdits)
            return false;

        if(!IsValid(dto.Model) || (dto.CompanionModel.HasValue && !IsValid(dto.CompanionModel.Value)))
            return false;

        foreach(var bone in dto.Bones)
        {
            if(string.IsNullOrEmpty(bone.Name) || bone.Name.Length > MaxNameLength || bone.Partial is < 0 or > 255 || bone.Slot > (byte)Game.Posing.PoseInfoSlot.Ornament)
                return false;

            if(bone.Stacks is null || bone.Stacks.Count > MaxStacksPerBone)
                return false;

            foreach(var stack in bone.Stacks)
            {
                var t = stack.Transform;
                if(stack.Propagate > 7 || !IsFinite(t.Position, MaxCoordinate) || !IsFinite(t.Scale, MaxValue) || !IsFinite(t.Rotation))
                    return false;
            }
        }

        return true;
    }

    public static bool IsValid(SyncLookSource dto)
        => (dto.Name?.Length ?? 0) <= MaxNameLength
        && (dto.McdfHash is null || (IsHash(dto.McdfHash) && dto.Size > 0 && dto.Size <= Mods.SyncModLimits.MaxMcdfBytes));

    // An MCDF names the files it unpacks and what they replace. Brio builds file paths from those
    // names, so one from another player is only used if they can't point anywhere they shouldn't.
    public static bool IsSafe(MCDF.API.Data.MareCharaFileData data)
    {
        foreach(var file in data.Files)
        {
            if(string.IsNullOrEmpty(file.Hash) || file.Hash.Length > 128 || !file.Hash.All(char.IsAsciiHexDigit) || file.Length < 0)
                return false;

            foreach(var path in file.GamePaths)
            {
                if(!IsGamePath(path))
                    return false;
            }
        }

        foreach(var swap in data.FileSwaps)
        {
            if(!IsGamePath(swap.FileSwapPath))
                return false;

            foreach(var path in swap.GamePaths)
            {
                if(!IsGamePath(path))
                    return false;
            }
        }

        return true;
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
