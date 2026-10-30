using Brio.Entities;
using Brio.Entities.Camera;
using Brio.Entities.Core;
using Brio.Game.Camera;

namespace Brio.Sync.Kinds;

// Free and game cameras. Another player's camera can be selected to look through it, and it
// follows them as they move it.
public class CameraSyncKind(VirtualCameraManager cameraManager, EntityManager entityManager) : SyncKind
{
    public override SyncEntityType Type => SyncEntityType.Camera;
    public override int MaxPerPlayer => SyncLimits.MaxCamerasPerPlayer;
    public override byte PartCount => 1;

    public override bool CanShare(Entity entity)
        => entity is CameraEntity camera && !camera.IsDefaultCamera && camera.CameraType is CameraType.Free or CameraType.Game;

    public override object? Capture(Entity entity, byte part)
    {
        var camera = (CameraEntity)entity;
        var virtualCamera = camera.VirtualCamera;

        return new SyncCamera
        {
            Name = camera.RawName,
            Type = camera.CameraType,
            Position = virtualCamera.Position,
            PositionOffset = virtualCamera.PositionOffset,
            TargetOffset = virtualCamera.TargetOffset,
            Rotation = virtualCamera.Rotation,
            Pan = virtualCamera.Pan,
            Angle = virtualCamera.Angle,
            PivotRotation = virtualCamera.PivotRotation,
            Zoom = virtualCamera.Zoom,
            FoV = virtualCamera.FoV,
            DisableCollision = virtualCamera.DisableCollision,
        };
    }

    public override byte[] Encode(byte part, object value) => SyncCodec.Encode((SyncCamera)value);

    public override object? Decode(byte part, byte[] compressed)
    {
        var dto = SyncCodec.Decode<SyncCamera>(compressed, SyncLimits.MaxPartBytes);
        return dto is not null && SyncValidation.IsValid(dto) ? dto : null;
    }

    public override void Show(RemoteItem item, FolderEntity folder)
    {
        var dto = (SyncCamera)item.Parts[0];
        item.Generation++;

        var virtualCamera = new VirtualCamera { IsFreeCamera = dto.Type == CameraType.Free };
        CopyTo(dto, virtualCamera);

        var (created, cameraId) = cameraManager.CreateCamera(dto.Type, selectCamera: false, targetNewInHierarch: false, virtualCamera: virtualCamera);
        if(!created || !entityManager.TryGetEntity<CameraEntity>(new CameraId(cameraId), out var entity))
            return;

        entityManager.MoveEntity(entity, folder);
        entity.IsSynced = true;
        entity.RawName = dto.Name;

        item.State = dto.Type;
        item.Entity = entity;
        item.Spawning = false;
        item.Dirty.Clear();
    }

    public override void Apply(RemoteItem item)
    {
        item.Dirty.Clear();

        var entity = (CameraEntity)item.Entity!;
        var dto = (SyncCamera)item.Parts[0];

        if((CameraType)item.State! != dto.Type)
        {
            item.RespawnRequested = true;
            return;
        }

        entity.RawName = dto.Name;
        CopyTo(dto, entity.VirtualCamera);
    }

    // The camera's own setters write through to the game camera while it's the one being looked through.
    private static void CopyTo(SyncCamera dto, VirtualCamera camera)
    {
        camera.Position = dto.Position;
        camera.PositionOffset = dto.PositionOffset;
        camera.TargetOffset = dto.TargetOffset;
        camera.Rotation = dto.Rotation;
        camera.Pan = dto.Pan;
        camera.Angle = dto.Angle;
        camera.PivotRotation = dto.PivotRotation;
        camera.Zoom = dto.Zoom;
        camera.FoV = dto.FoV;
        camera.DisableCollision = dto.DisableCollision;
    }

    public override void Hide(RemoteItem item)
    {
        if(item.Entity is CameraEntity entity)
            cameraManager.DestroyCamera(entity.CameraID);
    }

    public override bool IsAlive(RemoteItem item)
        => item.Entity is CameraEntity entity && entityManager.EntityExists(entity.Id);
}
