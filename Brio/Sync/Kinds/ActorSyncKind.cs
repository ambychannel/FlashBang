using Brio.Capabilities.Actor;
using Brio.Capabilities.Posing;
using Brio.Core;
using Brio.Entities;
using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.Files;
using Brio.Game.Actor;
using Brio.Game.Actor.Appearance;
using Brio.Game.Actor.Extensions;
using Brio.Game.Core;
using Brio.Game.Posing;
using Brio.Game.Types;
using Brio.IPC;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

namespace Brio.Sync.Kinds;

// Actors are sent as two parts: appearance (part 0, checked every couple of seconds) and pose
// (part 1, at most twice a second), so posing doesn't resend the whole character.
public class ActorSyncKind(
    EntityManager entityManager,
    IFramework framework,
    GlamourerService glamourerService,
    ActorRedrawService redrawService,
    PosingService posingService) : SyncKind
{
    private const byte AppearancePart = 0;
    private const byte PosePart = 1;

    public override SyncEntityType Type => SyncEntityType.Actor;
    public override int MaxPerPlayer => SyncLimits.MaxActorsPerPlayer;
    public override byte PartCount => 2;

    public override TimeSpan CaptureInterval(byte part)
        => part == AppearancePart ? TimeSpan.FromSeconds(2) : TimeSpan.FromMilliseconds(500);

    private class SpawnedState(bool hasAttachments)
    {
        public bool HasAttachments { get; } = hasAttachments;
        public CompanionContainer Companion { get; set; } = CompanionContainer.None;
    }

    // Ours

    // A minion or mount travels with the actor that owns it, so it isn't shared on its own.
    public override bool CanShare(Entity entity)
        => entity is ActorEntity actor && actor.Parent is not ActorEntity
        && actor.GameObject is ICharacter && actor.GameObject.IsValid()
        && actor.HasCapability<PosingCapability>() && actor.HasCapability<ActorAppearanceCapability>();

    public override object? Capture(Entity entity, byte part)
    {
        var actor = (ActorEntity)entity;
        return part == AppearancePart ? CaptureAppearance(actor) : CapturePose(actor);
    }

    private static unsafe SyncActorAppearance CaptureAppearance(ActorEntity actor)
    {
        var appearance = actor.GetCapability<ActorAppearanceCapability>();

        ActorAppearanceExtended extended = new() { Appearance = appearance.CurrentAppearance };
        var shaderParams = appearance.Character.GetShaderParams();
        if(shaderParams is not null)
            extended.ShaderParams = *shaderParams;

        var dto = new SyncActorAppearance
        {
            Name = actor.FriendlyName,
            Appearance = extended,
        };

        if(appearance.HasGlamourerIntegration)
        {
            var state = appearance.GetCurrentDesign();
            if(!string.IsNullOrEmpty(state))
                dto.GlamourerState = state;
        }

        if(appearance.Character.HasSpawnedCompanion())
            dto.Companion = appearance.Character.GetCompanionInfo();

        return dto;
    }

    private static SyncActorPose CapturePose(ActorEntity actor)
    {
        var dto = new SyncActorPose { Body = actor.GetCapability<PosingCapability>().GeneratePoseData() };

        if(actor.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity
            && companionEntity.TryGetCapability<PosingCapability>(out var companionPosing))
        {
            dto.Companion = companionPosing.GeneratePoseData();
        }

        return dto;
    }

    public override byte[] Encode(byte part, object value)
        => part == AppearancePart ? SyncCodec.Encode((SyncActorAppearance)value) : SyncCodec.Encode((SyncActorPose)value);

    // Theirs

    public override object? Decode(byte part, byte[] compressed)
    {
        if(part == AppearancePart)
        {
            var appearance = SyncCodec.Decode<SyncActorAppearance>(compressed, SyncLimits.MaxPartBytes);
            return appearance is not null && SyncValidation.IsValid(appearance) ? appearance : null;
        }

        var pose = SyncCodec.Decode<SyncActorPose>(compressed, SyncLimits.MaxPartBytes);
        return pose is not null && SyncValidation.IsValid(pose) ? pose : null;
    }

    public override unsafe void Show(RemoteItem item, FolderEntity folder)
    {
        var appearance = (SyncActorAppearance)item.Parts[AppearancePart];
        var hasAttachments = appearance.Companion.Kind != CompanionKind.None;
        var generation = ++item.Generation;
        item.Spawning = true;

        var actors = entityManager.EntityManagerContainer.GetCapability<ActorContainerCapability>();
        var (actorId, character) = actors.CreateCharacter(hasAttachments, false, forceSpawnActorWithoutCompanion: !hasAttachments);

        framework.RunUntilSatisfied(
            () => !character.IsValid() || character.Native()->IsReadyToDraw(),
            _ =>
            {
                framework.RunOnTick(() =>
                {
                    var entity = entityManager.GetEntity<ActorEntity>(actorId);

                    if(!item.IsCurrent(generation))
                    {
                        if(entity is not null)
                            actors.DestroyCharacter(entity);
                        return;
                    }

                    if(entity is null)
                    {
                        // The engine notices the spawn never finished and tries again.
                        return;
                    }

                    if(entityManager.EntityExists(folder.Id))
                        entityManager.MoveEntity(entity, folder);

                    entity.IsSynced = true;
                    entity.FriendlyName = appearance.Name;

                    if(entity.TryGetCapability<ActionTimelineCapability>(out var timeline))
                        timeline.SetOverallSpeedOverride(0);

                    item.State = new SpawnedState(hasAttachments);
                    item.Entity = entity;
                    item.Spawning = false;
                    item.MarkAllDirty();
                });
            },
            100,
            dontStartFor: 3);
    }

    public override void Apply(RemoteItem item)
    {
        var entity = (ActorEntity)item.Entity!;
        var state = (SpawnedState)item.State!;

        if(item.Dirty.Remove(AppearancePart))
        {
            var appearance = (SyncActorAppearance)item.Parts[AppearancePart];

            // A minion or mount needs a slot reserved when the actor is spawned.
            if(appearance.Companion.Kind != CompanionKind.None && !state.HasAttachments)
            {
                Respawn(item);
                return;
            }

            entity.FriendlyName = appearance.Name;

            item.Busy = true;
            var generation = item.Generation;
            _ = ApplyAppearance(entity, appearance, state).ContinueWith(_ => framework.RunOnFrameworkThread(() =>
            {
                if(item.IsCurrent(generation))
                    item.Busy = false;
            }));
            return;
        }

        if(item.Dirty.Remove(PosePart) && item.Parts.TryGetValue(PosePart, out var poseValue))
            ApplyPose(entity, (SyncActorPose)poseValue);
    }

    private async Task ApplyAppearance(ActorEntity entity, SyncActorAppearance dto, SpawnedState state)
    {
        await framework.RunOnTick(async () =>
        {
            try
            {
                var appearance = entity.GetCapability<ActorAppearanceCapability>();

                if(dto.GlamourerState is not null && glamourerService.IsAvailable)
                {
                    glamourerService.SetState(entity.GameObject, dto.GlamourerState);
                }
                else
                {
                    if(dto.Appearance.IsExtendedAppearanceValid)
                        BrioUtilities.ImportShadersFromFile(ref appearance._modelShaderOverride, dto.Appearance);
                    await appearance.SetAppearance(dto.Appearance, AppearanceImportOptions.All);
                }

                await redrawService.WaitForDrawing(entity.GameObject);

                if(state.Companion != dto.Companion && entity.TryGetCapability<CompanionCapability>(out var companion))
                {
                    companion.SetCompanion(dto.Companion);
                    state.Companion = dto.Companion;
                }
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't apply the look of {dto.Name}.");
            }
        });
    }

    private void ApplyPose(ActorEntity entity, SyncActorPose dto)
    {
        Import(entity, dto.Body);

        if(dto.Companion is not null
            && entity.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity)
        {
            if(companionEntity.TryGetCapability<ActionTimelineCapability>(out var timeline))
                timeline.SetOverallSpeedOverride(0);

            Import(companionEntity, dto.Companion);
        }
    }

    private void Import(ActorEntity entity, PoseData pose)
    {
        if(!entity.TryGetCapability<PosingCapability>(out var posing))
            return;

        posing.SkeletonPosing.ImportSkeletonPose(pose, posingService.SceneImporterOptions);
        posing.ModelPosing.Transform = new Transform { Position = pose.Position, Rotation = pose.Rotation, Scale = pose.Scale };
    }

    private void Respawn(RemoteItem item)
    {
        Hide(item);
        item.Generation++;
        item.Entity = null;
        item.State = null;
        item.MarkAllDirty();
    }

    public override void Hide(RemoteItem item)
    {
        if(item.Entity is ActorEntity entity)
            entityManager.EntityManagerContainer.GetCapability<ActorContainerCapability>().DestroyCharacter(entity);
    }

    public override bool IsAlive(RemoteItem item)
        => item.Entity is ActorEntity entity && entity.GameObject.IsValid() && entityManager.EntityExists(entity.Id);
}
