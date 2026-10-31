using Brio.Capabilities.World;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Entities.World;
using Brio.Game.World;
using Brio.Services.Models;
using System;
using System.Numerics;

namespace Brio.Sync.Kinds;

public class LightSyncKind(LightingService lightingService, EntityManager entityManager) : SyncKind
{
    public override SyncEntityType Type => SyncEntityType.Light;
    public override int MaxPerPlayer => SyncLimits.MaxLightsPerPlayer;
    public override byte PartCount => 1;

    public override bool CanShare(Entity entity)
        => entity is LightEntity light && light.GameLight.IsValid && !light.GameLight.IsWorldLight && !light.GameLight.IsGPoseLight;

    public override object? Capture(SyncItem item, byte part)
    {
        var light = (LightEntity)item.Entity!;
        var dto = lightingService.SaveLightDTO(light.GameLight, Vector3.Zero);
        if(dto is null)
            return null;

        // Mid-glide, report where it's heading so the in-between doesn't look like an edit.
        if(item.Motion is TransformMotion motion)
            dto.Transform = motion.Final(dto.Transform);

        dto.FriendlyName = light.RawName;
        dto.IsVisible = light.GameLight.IsVisible;
        return dto;
    }

    public override bool UsesValues(byte part) => true;

    public override SyncValues ToValues(byte part, object value)
    {
        var dto = (LightDTO)value;
        var t = dto.Transform;
        return new SyncValues(
            [t.Position.X, t.Position.Y, t.Position.Z, t.Scale.X, t.Scale.Y, t.Scale.Z,
             dto.Color.X, dto.Color.Y, dto.Color.Z, dto.Intensity, dto.Range, dto.Falloff,
             dto.LightAngle, dto.FalloffAngle, dto.CharacterShadowRange, dto.ShadowPlaneNear, dto.ShadowPlaneFar],
            [t.Rotation],
            [((int)dto.LightType).ToString(), dto.IsVisible ? "1" : "0", dto.FriendlyName]);
    }

    public override object? FromValues(byte part, SyncValues values)
    {
        var (n, r, c) = (values.Numbers, values.Rotations, values.Choices);
        if(n.Length != 17 || r.Length != 1 || c.Length != 3 || !int.TryParse(c[0], out var type))
            return null;

        var dto = new LightDTO
        {
            Transform = new Transform { Position = new Vector3(n[0], n[1], n[2]), Rotation = r[0], Scale = new Vector3(n[3], n[4], n[5]) },
            Color = new Vector3(n[6], n[7], n[8]),
            Intensity = n[9],
            Range = n[10],
            Falloff = n[11],
            LightAngle = n[12],
            FalloffAngle = n[13],
            CharacterShadowRange = n[14],
            ShadowPlaneNear = n[15],
            ShadowPlaneFar = n[16],
            LightType = (Game.World.Interop.LightType)type,
            IsVisible = c[1] == "1",
            FriendlyName = c[2] ?? "",
        };

        return SyncValidation.IsValid(dto) ? dto : null;
    }

    public override bool IsSame(byte part, object a, object b) => SyncCompare.Near((LightDTO)a, (LightDTO)b);

    public override byte[] Encode(byte part, object value) => SyncCodec.Encode((LightDTO)value);

    public override object? Decode(byte part, byte[] compressed)
    {
        var dto = SyncCodec.Decode<LightDTO>(compressed, SyncLimits.MaxPartBytes);
        return dto is not null && SyncValidation.IsValid(dto) ? dto : null;
    }

    public override void Show(SyncItem item, FolderEntity folder)
    {
        var dto = (LightDTO)item.Parts[0];
        var generation = ++item.Generation;
        item.Spawning = true;
        item.Dirty.Clear();

        lightingService.SpawnFromDTO(dto, null, folder, entity =>
        {
            if(!item.IsCurrent(generation))
            {
                lightingService.Destroy(entity.GameLight);
                return;
            }

            item.Spawning = false;
            item.Entity = entity;
            entity.IsSynced = !item.Mine;
            entity.GameLight.SetVisibility(dto.IsVisible);
        });
    }

    public override unsafe void Apply(SyncItem item)
    {
        item.Dirty.Clear();

        var entity = (LightEntity)item.Entity!;
        var dto = (LightDTO)item.Parts[0];
        if(!entity.GameLight.IsValid)
            return;

        if(entity.TryGetCapability<LightTransformCapability>(out var transform))
            item.Motion = new TransformMotion(transform.Transform, dto.Transform, DateTime.UtcNow);

        var gameLight = entity.GameLight.GameLight;
        var render = gameLight->RenderLight;
        if(render != null)
        {
            render->EmissionType = dto.LightType;
            render->Color = dto.Color;
            render->Intensity = dto.Intensity;
            render->Range = dto.Range;
            render->FalloffFactor = dto.Falloff;
            render->SpotLightAngleDegrees = dto.LightAngle;
            render->AngularFalloffDegrees = dto.FalloffAngle;
            render->CharacterShadowRange = dto.CharacterShadowRange;
            render->ShadowPlaneNear = dto.ShadowPlaneNear;
            render->ShadowPlaneFar = dto.ShadowPlaneFar;
        }

        entity.GameLight.SetVisibility(dto.IsVisible);
        entity.RawName = dto.FriendlyName;

        gameLight->Update();
        gameLight->UpdateTransforms(false);
    }

    public override unsafe void Animate(SyncItem item, DateTime now)
    {
        if(item.Motion is not TransformMotion motion || item.Entity is not LightEntity entity || !entity.GameLight.IsValid)
            return;

        if(entity.TryGetCapability<LightTransformCapability>(out var transform))
        {
            transform.Transform = motion.Step(transform.Transform, now);
            entity.GameLight.GameLight->UpdateTransforms(false);
        }

        if(motion.IsDone(now))
            item.Motion = null;
    }

    public override void Hide(SyncItem item)
    {
        if(item.Entity is LightEntity entity && entity.GameLight.IsValid)
            lightingService.Destroy(entity.GameLight);
    }

    public override bool IsAlive(SyncItem item)
        => item.Entity is LightEntity entity && entity.GameLight.IsValid && entityManager.EntityExists(entity.Id);
}
