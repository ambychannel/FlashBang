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
using Brio.Sync.Mods;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

namespace Brio.Sync.Kinds;

// Actors are sent as three parts: appearance (part 0, checked every couple of seconds), pose
// (part 1, at most twice a second) and the list of mod files they use (part 2), so posing
// doesn't resend the whole character. Mod files themselves go through SyncModTransfer.
public class ActorSyncKind(
    EntityManager entityManager,
    IFramework framework,
    GlamourerService glamourerService,
    ActorRedrawService redrawService,
    PosingService posingService,
    SyncModSource modSource,
    SyncModTransfer modTransfer,
    SyncModApplier modApplier) : SyncKind
{
    private const byte AppearancePart = 0;
    private const byte PosePart = 1;
    private const byte ModsPart = 2;

    private static readonly TimeSpan ModCheckInterval = TimeSpan.FromMilliseconds(500);

    public override SyncEntityType Type => SyncEntityType.Actor;
    public override int MaxPerPlayer => SyncLimits.MaxActorsPerPlayer;
    public override byte PartCount => 3;

    public override TimeSpan CaptureInterval(byte part) => part switch
    {
        AppearancePart => TimeSpan.FromSeconds(2),
        PosePart => TimeSpan.FromMilliseconds(500),
        _ => TimeSpan.FromSeconds(5),
    };

    private class SpawnedState(bool hasAttachments)
    {
        public bool HasAttachments { get; } = hasAttachments;
        public CompanionContainer Companion { get; set; } = CompanionContainer.None;

        public SyncModApplier.Applied? Mods { get; set; }
        public SyncActorMods? ModsShown { get; set; }
        public DateTime NextModCheck { get; set; }
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
        return part switch
        {
            AppearancePart => CaptureAppearance(actor),
            PosePart => CapturePose(actor),
            _ => modSource.Get(actor),
        };
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

    public override byte[] Encode(byte part, object value) => part switch
    {
        AppearancePart => SyncCodec.Encode((SyncActorAppearance)value),
        PosePart => SyncCodec.Encode((SyncActorPose)value),
        _ => SyncCodec.Encode((SyncActorMods)value),
    };

    // Theirs

    public override object? Decode(byte part, byte[] compressed)
    {
        if(part == ModsPart)
        {
            var mods = SyncCodec.Decode<SyncActorMods>(compressed, SyncLimits.MaxPartBytes * 4);
            return mods is not null && SyncValidation.IsValid(mods) ? mods : null;
        }

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

        if(item.Dirty.Remove(ModsPart) && item.Parts.TryGetValue(ModsPart, out var modsValue))
            UpdateMods(item, entity, state, (SyncActorMods)modsValue);
    }

    // Fetches whatever files are missing, then swaps the actor over to the owner's mods. Until
    // then it keeps its Glamourer look and shows download progress.
    private void UpdateMods(RemoteItem item, ActorEntity entity, SpawnedState state, SyncActorMods mods)
    {
        if(ReferenceEquals(mods, state.ModsShown) || !modApplier.IsAvailable)
            return;

        if(DateTime.UtcNow < state.NextModCheck)
        {
            item.Dirty.Add(ModsPart);
            return;
        }
        state.NextModCheck = DateTime.UtcNow + ModCheckInterval;

        var progress = modTransfer.Want(item.Owner.Id, mods);
        if(!progress.Done)
        {
            entity.IsLoading = true;
            entity.LoadingDescription = $"Downloading look {progress.HaveBytes / 1048576}/{Math.Max(1, progress.TotalBytes / 1048576)} MB";
            item.Dirty.Add(ModsPart);
            return;
        }

        if(progress.FailedFiles > 0)
            Brio.Log.Warning($"FlashBang: {progress.FailedFiles} mod files for {entity.FriendlyName} couldn't be downloaded; showing the rest.");

        entity.IsLoading = true;
        entity.LoadingDescription = "Applying look...";
        item.Busy = true;

        var generation = item.Generation;
        var glamourerState = item.Parts.TryGetValue(AppearancePart, out var appearance) ? ((SyncActorAppearance)appearance).GlamourerState : null;

        _ = modApplier.Apply(entity, mods, glamourerState, state.Mods).ContinueWith(task => framework.RunOnFrameworkThread(() =>
        {
            if(task.Exception is not null)
                Brio.Log.Warning(task.Exception, $"FlashBang: couldn't apply the mods on {entity.FriendlyName}.");

            var applied = task.IsCompletedSuccessfully ? task.Result : state.Mods;
            if(!item.IsCurrent(generation))
            {
                // Hidden while applying; don't leave the collection behind.
                if(applied is not null && applied != state.Mods)
                    modApplier.Remove(null, applied);
                return;
            }

            state.Mods = applied;
            state.ModsShown = mods;
            entity.IsLoading = false;
            item.Busy = false;

            // A redraw resets the skeleton.
            item.Dirty.Add(PosePart);
        }));
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
        Release(item);
        item.Generation++;
        item.Entity = null;
        item.State = null;
        item.MarkAllDirty();
    }

    public override void Release(RemoteItem item)
    {
        if(item.State is SpawnedState { Mods: { } applied } state)
        {
            modApplier.Remove(item.Entity as ActorEntity, applied);
            state.Mods = null;
        }
    }

    public override void Hide(RemoteItem item)
    {
        if(item.Entity is ActorEntity entity)
            entityManager.EntityManagerContainer.GetCapability<ActorContainerCapability>().DestroyCharacter(entity);
    }

    public override bool IsAlive(RemoteItem item)
        => item.Entity is ActorEntity entity && entity.GameObject.IsValid() && entityManager.EntityExists(entity.Id);
}
