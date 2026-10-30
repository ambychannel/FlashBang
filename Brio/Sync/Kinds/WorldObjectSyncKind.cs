using Brio.Entities;
using Brio.Entities.Core;
using Brio.Entities.WorldObjects;
using Brio.Game.WorldObjects;
using Brio.Game.WorldObjects.Objects;
using Brio.Services.Models;
using System;
using System.Numerics;

namespace Brio.Sync.Kinds;

// Props, furniture, world objects and VFX.
public class WorldObjectSyncKind(WorldObjectService worldObjectService, EntityManager entityManager) : SyncKind
{
    public override SyncEntityType Type => SyncEntityType.WorldObject;
    public override int MaxPerPlayer => SyncLimits.MaxWorldObjectsPerPlayer;
    public override byte PartCount => 1;

    // What an object was spawned as. If any of this changes it has to be spawned again.
    private record Identity(WorldObjectType Type, string Path, ushort ModelId, ushort ModelType, ushort ModelVariant, byte Stain0, byte Stain1, byte AnimationVariant)
    {
        public static Identity Of(WorldObjectDTO dto) => new(dto.ObjectType, dto.Path,
            dto.PropModel?.ModelId ?? 0, dto.PropModel?.ModelType ?? 0, dto.PropModel?.ModelVariant ?? 0,
            dto.PropModel?.Stain0 ?? 0, dto.PropModel?.Stain1 ?? 0, dto.PropModel?.AnimationVariant ?? 0);
    }

    private class SpawnedState(Identity identity)
    {
        public Identity Identity { get; } = identity;
        public (Vector4? Color, uint Stain)? Look { get; set; }
    }

    public override bool CanShare(Entity entity)
        => entity is WorldObjectEntity worldObject && worldObject.GameBgObject.IsValid;

    public override object? Capture(SyncItem item, byte part)
    {
        var worldObject = (WorldObjectEntity)item.Entity!;
        var dto = WorldObjectDTO.ToDTO(worldObject, Vector3.Zero);
        dto.RelativePosition = Vector3.Zero;

        // Mid-glide, report where it's heading so the in-between doesn't look like an edit.
        if(item.Motion is TransformMotion motion)
            dto.Transform = motion.To;

        return new SyncWorldObject { Object = dto, IsVisible = worldObject.GameBgObject.IsVisible };
    }

    public override byte[] Encode(byte part, object value) => SyncCodec.Encode((SyncWorldObject)value);

    public override object? Decode(byte part, byte[] compressed)
    {
        var dto = SyncCodec.Decode<SyncWorldObject>(compressed, SyncLimits.MaxPartBytes);
        return dto is not null && SyncValidation.IsValid(dto) ? dto : null;
    }

    public override void Adopt(SyncItem item)
    {
        var entity = (WorldObjectEntity)item.Entity!;
        var dto = WorldObjectDTO.ToDTO(entity, Vector3.Zero);
        item.State = new SpawnedState(Identity.Of(dto)) { Look = (dto.Color, dto.StainID) };
    }

    public override void Show(SyncItem item, FolderEntity folder)
    {
        var dto = (SyncWorldObject)item.Parts[0];
        var generation = ++item.Generation;
        item.Spawning = true;

        worldObjectService.SpawnFromDTO(dto.Object, null, folder, entity =>
        {
            if(!item.IsCurrent(generation))
            {
                worldObjectService.Destroy(entity.GameBgObject);
                return;
            }

            entity.IsSynced = !item.Mine;
            item.State = new SpawnedState(Identity.Of(dto.Object)) { Look = (dto.Object.Color, dto.Object.StainID) };
            item.Entity = entity;
            item.Spawning = false;
            item.MarkAllDirty();
        });
    }

    public override unsafe void Apply(SyncItem item)
    {
        item.Dirty.Clear();

        var entity = (WorldObjectEntity)item.Entity!;
        var state = (SpawnedState)item.State!;
        var dto = (SyncWorldObject)item.Parts[0];
        var obj = entity.GameBgObject;

        // Our own object can't be swapped for another model from here; everyone else's copy can.
        if(Identity.Of(dto.Object) != state.Identity && !item.Mine)
        {
            // Swapped for a different model; replace it.
            item.RespawnRequested = true;
            return;
        }

        item.Motion = new TransformMotion(obj.Transform, dto.Object.Transform, DateTime.UtcNow);
        obj.IsVisible = dto.IsVisible;
        entity.RawName = dto.Object.FriendlyName;

        var look = (dto.Object.Color, dto.Object.StainID);
        if(state.Look == look)
            return;

        switch(obj)
        {
            case StaticVfxObject vfx when vfx.IsValid && dto.Object.Color.HasValue:
                vfx.VFX->Color = dto.Object.Color.Value;
                break;
            case FurnitureObject furniture:
                if(furniture.VsualStateDirty)
                {
                    // Not drawn yet; try again next time.
                    item.Dirty.Add(0);
                    return;
                }

                if(dto.Object.Color.HasValue)
                    furniture.SetCustomColor(dto.Object.Color.Value);
                else if(dto.Object.StainID != 0)
                    furniture.SetStain((byte)dto.Object.StainID);
                else
                    furniture.ClearColor();
                break;
        }

        state.Look = look;
    }

    public override void Animate(SyncItem item, DateTime now)
    {
        if(item.Motion is not TransformMotion motion || item.Entity is not WorldObjectEntity entity || !entity.GameBgObject.IsValid)
            return;

        entity.GameBgObject.SetTransform(motion.At(now));

        if(motion.IsDone(now))
            item.Motion = null;
    }

    public override void Hide(SyncItem item)
    {
        if(item.Entity is WorldObjectEntity entity)
            worldObjectService.Destroy(entity.GameBgObject);
    }

    public override bool IsAlive(SyncItem item)
        => item.Entity is WorldObjectEntity entity && entity.GameBgObject.IsValid && entityManager.EntityExists(entity.Id);
}
