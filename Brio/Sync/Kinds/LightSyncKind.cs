using Brio.Capabilities.World;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Entities.World;
using Brio.Game.World;
using Brio.Services.Models;
using System.Numerics;

namespace Brio.Sync.Kinds;

public class LightSyncKind(LightingService lightingService, EntityManager entityManager) : SyncKind
{
    public override SyncEntityType Type => SyncEntityType.Light;
    public override int MaxPerPlayer => SyncLimits.MaxLightsPerPlayer;
    public override byte PartCount => 1;

    public override bool CanShare(Entity entity)
        => entity is LightEntity light && light.GameLight.IsValid && !light.GameLight.IsWorldLight && !light.GameLight.IsGPoseLight;

    public override object? Capture(Entity entity, byte part)
    {
        var light = (LightEntity)entity;
        var dto = lightingService.SaveLightDTO(light.GameLight, Vector3.Zero);
        if(dto is null)
            return null;

        dto.FriendlyName = light.RawName;
        dto.IsVisible = light.GameLight.IsVisible;
        return dto;
    }

    public override byte[] Encode(byte part, object value) => SyncCodec.Encode((LightDTO)value);

    public override object? Decode(byte part, byte[] compressed)
    {
        var dto = SyncCodec.Decode<LightDTO>(compressed, SyncLimits.MaxPartBytes);
        return dto is not null && SyncValidation.IsValid(dto) ? dto : null;
    }

    public override void Show(RemoteItem item, FolderEntity folder)
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
            entity.IsSynced = true;
            entity.GameLight.SetVisibility(dto.IsVisible);
        });
    }

    public override unsafe void Apply(RemoteItem item)
    {
        item.Dirty.Clear();

        var entity = (LightEntity)item.Entity!;
        var dto = (LightDTO)item.Parts[0];
        if(!entity.GameLight.IsValid)
            return;

        if(entity.TryGetCapability<LightTransformCapability>(out var transform))
            transform.Transform = dto.Transform;

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

    public override void Hide(RemoteItem item)
    {
        if(item.Entity is LightEntity entity && entity.GameLight.IsValid)
            lightingService.Destroy(entity.GameLight);
    }

    public override bool IsAlive(RemoteItem item)
        => item.Entity is LightEntity entity && entity.GameLight.IsValid && entityManager.EntityExists(entity.Id);
}
