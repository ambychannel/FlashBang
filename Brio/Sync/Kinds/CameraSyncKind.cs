using Brio.Entities;
using Brio.Entities.Camera;
using Brio.Entities.Core;
using Brio.Game.Camera;
using System;
using System.Numerics;

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

    public override object? Capture(SyncItem item, byte part)
    {
        var camera = (CameraEntity)item.Entity!;
        var virtualCamera = camera.VirtualCamera;
        var motion = item.Motion as CameraMotion;
        var position = virtualCamera.Position + (motion is null ? Vector3.Zero : motion.ToPosition - motion.ReachedPosition);
        var rotation = virtualCamera.Rotation + (motion is null ? Vector3.Zero : motion.ToRotation - motion.ReachedRotation);

        // Mid-glide, report where it's heading so the in-between doesn't look like an edit.
        return new SyncCamera
        {
            Name = camera.RawName,
            Type = camera.CameraType,
            Position = position,
            PositionOffset = virtualCamera.PositionOffset,
            TargetOffset = virtualCamera.TargetOffset,
            Rotation = rotation,
            Pan = virtualCamera.Pan,
            Angle = virtualCamera.Angle,
            PivotRotation = virtualCamera.PivotRotation,
            Zoom = virtualCamera.Zoom,
            FoV = virtualCamera.FoV,
            DisableCollision = virtualCamera.DisableCollision,
        };
    }

    public override bool UsesValues(byte part) => true;

    public override SyncValues ToValues(byte part, object value)
    {
        var d = (SyncCamera)value;
        return new SyncValues(
            [d.Position.X, d.Position.Y, d.Position.Z, d.PositionOffset.X, d.PositionOffset.Y, d.PositionOffset.Z,
             d.TargetOffset.X, d.TargetOffset.Y, d.TargetOffset.Z, d.Rotation.X, d.Rotation.Y, d.Rotation.Z,
             d.Pan.X, d.Pan.Y, d.Angle.X, d.Angle.Y, d.PivotRotation, d.Zoom, d.FoV],
            [],
            [d.Name, ((int)d.Type).ToString(), d.DisableCollision ? "1" : "0"]);
    }

    public override object? FromValues(byte part, SyncValues values)
    {
        var (n, c) = (values.Numbers, values.Choices);
        if(n.Length != 19 || c.Length != 3 || !int.TryParse(c[1], out var type))
            return null;

        var dto = new SyncCamera
        {
            Name = c[0] ?? "",
            Type = (CameraType)type,
            DisableCollision = c[2] == "1",
            Position = new Vector3(n[0], n[1], n[2]),
            PositionOffset = new Vector3(n[3], n[4], n[5]),
            TargetOffset = new Vector3(n[6], n[7], n[8]),
            Rotation = new Vector3(n[9], n[10], n[11]),
            Pan = new Vector2(n[12], n[13]),
            Angle = new Vector2(n[14], n[15]),
            PivotRotation = n[16],
            Zoom = n[17],
            FoV = n[18],
        };

        return SyncValidation.IsValid(dto) ? dto : null;
    }

    public override bool IsSame(byte part, object a, object b) => SyncCompare.Near((SyncCamera)a, (SyncCamera)b);

    public override byte[] Encode(byte part, object value) => SyncCodec.Encode((SyncCamera)value);

    public override object? Decode(byte part, byte[] compressed)
    {
        var dto = SyncCodec.Decode<SyncCamera>(compressed, SyncLimits.MaxPartBytes);
        return dto is not null && SyncValidation.IsValid(dto) ? dto : null;
    }

    public override void Adopt(SyncItem item) => item.State = ((CameraEntity)item.Entity!).CameraType;

    public override void Show(SyncItem item, FolderEntity folder)
    {
        var dto = (SyncCamera)item.Parts[0];
        item.Generation++;

        var virtualCamera = new VirtualCamera { IsFreeCamera = dto.Type == CameraType.Free };
        CopyTo(dto, virtualCamera);

        var (created, cameraId) = cameraManager.CreateCamera(dto.Type, selectCamera: false, targetNewInHierarch: false, virtualCamera: virtualCamera);
        if(!created || !entityManager.TryGetEntity<CameraEntity>(new CameraId(cameraId), out var entity))
            return;

        entityManager.MoveEntity(entity, folder);
        entity.IsSynced = !item.Mine;
        entity.RawName = dto.Name;

        item.State = dto.Type;
        item.Entity = entity;
        item.Spawning = false;
        item.Dirty.Clear();
    }

    public override void Apply(SyncItem item)
    {
        item.Dirty.Clear();

        var entity = (CameraEntity)item.Entity!;
        var dto = (SyncCamera)item.Parts[0];

        if((CameraType)item.State! != dto.Type && !item.Mine)
        {
            item.RespawnRequested = true;
            return;
        }

        entity.RawName = dto.Name;

        var camera = entity.VirtualCamera;
        var motion = new CameraMotion(camera.Position, dto.Position, camera.Rotation, dto.Rotation, DateTime.UtcNow);
        CopyTo(dto, camera);
        camera.Position = motion.FromPosition;
        camera.Rotation = motion.FromRotation;
        item.Motion = motion;
    }

    // Only the change is added, a little each frame, so someone moving the camera still counts.
    private class CameraMotion(Vector3 fromPosition, Vector3 toPosition, Vector3 fromRotation, Vector3 toRotation, DateTime start)
    {
        public Vector3 FromPosition { get; } = fromPosition;
        public Vector3 FromRotation { get; } = fromRotation;
        public Vector3 ToPosition { get; } = toPosition;
        public Vector3 ToRotation { get; } = toRotation;
        public DateTime Start { get; } = start;
        public Vector3 ReachedPosition { get; set; } = fromPosition;
        public Vector3 ReachedRotation { get; set; } = fromRotation;
    }

    public override void Animate(SyncItem item, DateTime now)
    {
        if(item.Motion is not CameraMotion motion || item.Entity is not CameraEntity entity)
            return;

        var t = (float)Math.Clamp((now - motion.Start) / TransformMotion.Duration, 0, 1);

        var position = Vector3.Lerp(motion.FromPosition, motion.ToPosition, t);
        var rotation = new Vector3(
            LerpAngle(motion.FromRotation.X, motion.ToRotation.X, t),
            LerpAngle(motion.FromRotation.Y, motion.ToRotation.Y, t),
            LerpAngle(motion.FromRotation.Z, motion.ToRotation.Z, t));

        entity.VirtualCamera.Position += position - motion.ReachedPosition;
        entity.VirtualCamera.Rotation += rotation - motion.ReachedRotation;
        motion.ReachedPosition = position;
        motion.ReachedRotation = rotation;

        if(t >= 1)
            item.Motion = null;
    }

    // Radians, the short way round.
    private static float LerpAngle(float from, float to, float t)
    {
        var delta = MathF.IEEERemainder(to - from, MathF.Tau);
        return from + delta * t;
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

    public override void Hide(SyncItem item)
    {
        if(item.Entity is CameraEntity entity)
            cameraManager.DestroyCamera(entity.CameraID);
    }

    public override bool IsAlive(SyncItem item)
        => item.Entity is CameraEntity entity && entityManager.EntityExists(entity.Id);
}
