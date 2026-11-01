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
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using System;
using System.Threading.Tasks;

namespace Brio.Sync.Kinds;

// An actor is what it looks like (part 0), the mod files it uses (part 2), which animation it's
// playing and at what frame (part 3), and the bones posed by hand on top of that (part 4). The
// other side plays the animation itself, and each part only goes out when it differs. A whole
// pose (part 1) is only for an actor whose animation can't be shared, and only on Sync now.
// Mod files themselves go through SyncModTransfer.
public class ActorSyncKind(
    EntityManager entityManager,
    IFramework framework,
    GlamourerService glamourerService,
    ActorRedrawService redrawService,
    PosingService posingService,
    SyncModSource modSource,
    SyncModTransfer modTransfer,
    SyncModApplier modApplier,
    SyncModCache modCache,
    MCDF.Game.Services.MCDFService mcdfService) : SyncKind
{
    private const byte AppearancePart = 0;
    private const byte PosePart = 1;
    private const byte ModsPart = 2;
    private const byte AnimPart = 3;
    private const byte EditsPart = 4;
    public const byte LookPart = 5;

    // How far a playing animation may stray from where it should be, and how often that's checked.
    private const float DriftSeconds = 0.25f;
    private const float ScrubSeconds = 0.02f;
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ModCheckInterval = TimeSpan.FromMilliseconds(500);

    public override SyncEntityType Type => SyncEntityType.Actor;
    public override int MaxPerPlayer => SyncLimits.MaxActorsPerPlayer;
    public override byte PartCount => 6;

    public override TimeSpan CaptureInterval(byte part) => part switch
    {
        AppearancePart => TimeSpan.FromSeconds(5),
        PosePart or AnimPart or EditsPart => TimeSpan.FromMilliseconds(200),
        _ => TimeSpan.FromSeconds(5),
    };

    // Mods are the creator's, and keep up with the creator's look once shared.
    public override bool Editable(byte part) => part != ModsPart;
    public override bool AlwaysSent(byte part) => part == ModsPart;

    private class SpawnedState(bool hasAttachments)
    {
        public bool HasAttachments { get; } = hasAttachments;
        public CompanionContainer Companion { get; set; } = CompanionContainer.None;

        public SyncModApplier.Applied? Mods { get; set; }
        public SyncActorMods? ModsShown { get; set; }
        public int ModsFailed { get; set; }
        public string? LookShown { get; set; }
        public DateTime NextModCheck { get; set; }

        // Whether a pose has been applied yet; the first one snaps into place.
        public bool HasPose { get; set; }
    }

    // Ours

    // A minion or mount travels with the actor that owns it, so it isn't shared on its own.
    public override bool CanShare(Entity entity)
        => entity is ActorEntity actor && actor.Parent is not ActorEntity
        && actor.GameObject is ICharacter && actor.GameObject.IsValid()
        && actor.HasCapability<PosingCapability>() && actor.HasCapability<ActorAppearanceCapability>();

    public override object? Capture(SyncItem item, byte part)
    {
        var actor = (ActorEntity)item.Entity!;
        return part switch
        {
            // An MCDF's look isn't ours to read back, and it's sent as a file. See UpdateLook.
            AppearancePart => HasMcdf(item) ? null : CaptureAppearance(actor),
            LookPart => null,
            // Mid-glide, report where it's heading so the in-between doesn't look like an edit.
            PosePart => CaptureWholePose(item, actor),
            AnimPart => CaptureAnim(item, actor),
            EditsPart => CaptureEdits(item, actor),
            _ => CaptureMods(item, actor),
        };
    }

    // Only when there's no animation the other side could play, and never on a timer.
    private static object? CaptureWholePose(SyncItem item, ActorEntity actor)
    {
        var usable = HasUsableTimeline(actor);
        item.Note = usable ? null : "Its animation can't be shared, so only Sync now sends its pose.";
        return !usable && item.SyncNowRequested ? CapturePose(actor) : null;
    }

    private static unsafe bool HasUsableTimeline(ActorEntity actor)
    {
        var character = actor.GetCapability<ActorAppearanceCapability>().Character;
        var id = character.Native()->Timeline.TimelineSequencer.TimelineIds[(int)ActionTimelineService.ActionTimelineSlots.Base];
        return id != 0 && SyncValidation.IsTimeline(id);
    }

    private static object? CaptureAnim(SyncItem item, ActorEntity actor)
    {
        if(ReadAnim(actor) is not { } current)
            return null;

        // A copy plays what it's told. Only something a person did to it counts as a change.
        if(!item.Mine && item.Tracks[AnimPart].LastKnown is SyncAnimState known && !ChangedByHand(actor, current, known))
            return known;

        if(actor.TryGetCapability<CompanionCapability>(out var companion) && companion.GetCompanionAsEntity() is ActorEntity companionEntity)
            current.Companion = ReadAnim(companionEntity);

        return current;
    }

    private static unsafe bool ChangedByHand(ActorEntity actor, SyncAnimState current, SyncAnimState known)
    {
        if(current.Frozen != known.Frozen || (!current.Frozen && !SyncCompare.Near(current.Speed, known.Speed)))
            return true;

        if(current.Frozen && MathF.Abs(current.Time - known.Time) > ScrubSeconds)
            return true;

        var character = actor.GetCapability<ActorAppearanceCapability>().Character;
        var chosen = character.Native()->Timeline.BaseOverride;
        if(chosen == known.Base)
            return false;

        current.Base = chosen;
        return true;
    }

    private static unsafe SyncAnimState? ReadAnim(ActorEntity actor)
    {
        if(!actor.TryGetCapability<ActionTimelineCapability>(out var timeline))
            return null;

        var sequencer = &timeline.Character.Native()->Timeline.TimelineSequencer;
        var speed = timeline.SpeedMultiplier;
        var (time, duration) = ReadTime(actor);

        return new SyncAnimState
        {
            Base = sequencer->TimelineIds[(int)ActionTimelineService.ActionTimelineSlots.Base],
            Blend = sequencer->TimelineIds[(int)ActionTimelineService.ActionTimelineSlots.UpperBody],
            Speed = speed,
            Frozen = speed == 0,
            Time = time,
            Duration = duration,
            At = DateTime.UtcNow,
        };
    }

    // The main animation's position, as Brio's scrub slider reads it.
    private static unsafe (float Time, float Duration) ReadTime(ActorEntity actor)
    {
        var skeleton = SkeletonOf(actor);
        if(skeleton == null || skeleton->PartialSkeletonCount == 0)
            return (0, 0);

        var animated = skeleton->PartialSkeletons[0].GetHavokAnimatedSkeleton(0);
        if(animated == null || animated->AnimationControls.Length == 0)
            return (0, 0);

        var control = animated->AnimationControls[0].Value;
        if(control == null)
            return (0, 0);

        var binding = control->hkaAnimationControl.Binding;
        var duration = binding.ptr != null && binding.ptr->Animation.ptr != null ? binding.ptr->Animation.ptr->Duration : 0;
        return (control->hkaAnimationControl.LocalTime, duration);
    }

    // Moves the main animation of every part of the skeleton to a time.
    private static unsafe void Seek(ActorEntity actor, float time)
    {
        var skeleton = SkeletonOf(actor);
        if(skeleton == null)
            return;

        for(var p = 0; p < skeleton->PartialSkeletonCount; p++)
        {
            var animated = skeleton->PartialSkeletons[p].GetHavokAnimatedSkeleton(0);
            if(animated == null || animated->AnimationControls.Length == 0)
                continue;

            var control = animated->AnimationControls[0].Value;
            if(control == null)
                continue;

            var binding = control->hkaAnimationControl.Binding;
            if(binding.ptr == null || binding.ptr->Animation.ptr == null)
                continue;

            control->hkaAnimationControl.LocalTime = Math.Clamp(time, 0, binding.ptr->Animation.ptr->Duration);
        }
    }

    private static unsafe FFXIVClientStructs.FFXIV.Client.Graphics.Render.Skeleton* SkeletonOf(ActorEntity actor)
    {
        if(actor.GameObject is not ICharacter character)
            return null;

        var drawObject = character.Native()->GameObject.DrawObject;
        if(drawObject == null || drawObject->Object.GetObjectType() != FFXIVClientStructs.FFXIV.Client.Graphics.Scene.ObjectType.CharacterBase)
            return null;

        return ((FFXIVClientStructs.FFXIV.Client.Graphics.Scene.CharacterBase*)drawObject)->Skeleton;
    }

    private static object? CaptureEdits(SyncItem item, ActorEntity actor)
    {
        if(!actor.TryGetCapability<PosingCapability>(out var posing))
            return null;

        var edits = new SyncActorEdits
        {
            Full = true,

            // Mid-glide, report where it's heading so the in-between doesn't look like an edit.
            Model = item.Motion is TransformMotion motion ? motion.Final(posing.ModelPosing.Transform) : posing.ModelPosing.Transform,
        };
        ReadBones(posing, edits, false);

        if(actor.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity
            && companionEntity.TryGetCapability<PosingCapability>(out var companionPosing))
        {
            edits.CompanionModel = companionPosing.ModelPosing.Transform;
            ReadBones(companionPosing, edits, true);
        }

        edits.Sort();
        return edits;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FaceBones = new();

    private static bool IsFaceBone(PosingCapability posing, BonePoseInfoId id)
    {
        if(id.Slot != PoseInfoSlot.Character)
            return false;

        if(FaceBones.TryGetValue(id.BoneName, out var known))
            return known;

        var bone = posing.SkeletonPosing.GetBone(id.BoneName, PoseInfoSlot.Character);
        if(bone is null)
            return false;

        return FaceBones[id.BoneName] = bone.IsFaceBone || id.BoneName == "j_kao";
    }

    private static void ReadBones(PosingCapability posing, SyncActorEdits edits, bool companion)
    {
        foreach(var (id, info) in posing.SkeletonPosing.PoseInfo.Poses)
        {
            if(!info.HasStacks)
                continue;

            edits.Bones.Add(new SyncBoneEdit
            {
                Name = id.BoneName,
                Partial = id.Partial,
                Slot = (byte)id.Slot,
                Companion = companion,
                Face = !companion && IsFaceBone(posing, id),
                Stacks = SyncActorEdits.StacksOf(info),
            });
        }
    }

    private SyncActorMods? CaptureMods(SyncItem item, ActorEntity actor)
    {
        if(HasMcdf(item))
            return null;

        if(!modSource.IsAvailable)
        {
            item.Status = "Mods aren't shared: Penumbra isn't available.";
            return null;
        }

        if(item.SyncNowRequested)
            modSource.Refresh(actor);

        var mods = modSource.Get(actor);
        item.Status = mods switch
        {
            null => "Reading mods...",
            { Files.Count: 0 } => "No modded files found on this actor.",
            _ => $"Sharing {mods.Files.Count} mod files ({mods.TotalBytes / 1048576} MB).",
        };
        return mods;
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

    public override bool IsSame(byte part, object a, object b) => part switch
    {
        PosePart => SyncCompare.Near((SyncActorPose)a, (SyncActorPose)b),
        AnimPart => IsSameAnim((SyncAnimState)a, (SyncAnimState)b),
        EditsPart => SyncActorEdits.Near((SyncActorEdits)a, (SyncActorEdits)b),
        ModsPart => SyncCompare.Same((SyncActorMods)a, (SyncActorMods)b),
        _ => false,
    };

    // A playing animation always moves, so what matters is whether it's where it should be by now.
    private static bool IsSameAnim(SyncAnimState a, SyncAnimState b)
    {
        if(ReferenceEquals(a, b))
            return true;

        if(a.Base != b.Base || a.Blend != b.Blend || a.Frozen != b.Frozen)
            return false;

        if((a.Companion is null) != (b.Companion is null) || (a.Companion is not null && !IsSameAnim(a.Companion, b.Companion!)))
            return false;

        if(a.Frozen)
            return MathF.Abs(a.Time - b.Time) <= ScrubSeconds;

        if(!SyncCompare.Near(a.Speed, b.Speed))
            return false;

        var elapsed = a.At - b.At;
        if(elapsed < DriftCheckInterval || b.Duration <= 0)
            return true;

        var expected = b.Time + (float)elapsed.TotalSeconds * b.Speed;
        var drift = MathF.Abs(MathF.IEEERemainder(a.Time - expected, b.Duration));
        return drift <= DriftSeconds;
    }

    public override bool IsDiffed(byte part) => part == EditsPart;

    public override bool SendWhenSame(byte part) => part != AnimPart && part != EditsPart;

    public override object ToWire(byte part, object value, object? last)
        => part == EditsPart ? SyncActorEdits.Diff((SyncActorEdits)value, last as SyncActorEdits) : value;

    public override object? Merge(byte part, object wire, object? last, out bool inSync)
    {
        inSync = true;

        if(wire is SyncAnimState anim && anim.At == default)
            anim.At = DateTime.UtcNow;

        if(part != EditsPart)
            return wire;

        var received = (SyncActorEdits)wire;
        var merged = SyncActorEdits.Merge(received, last as SyncActorEdits, out var missedSome);
        inSync = !missedSome && (!received.HasDigest || SyncActorEdits.DigestOf(merged) == received.Digest);
        return merged;
    }

    public override object Rebase(byte part, object current, object from, object to)
        => part == EditsPart ? SyncActorEdits.Rebase((SyncActorEdits)current, (SyncActorEdits)from, (SyncActorEdits)to) : to;

    // A playing animation's frame goes out of date at once, so only a frozen one has a true value.
    public override object? Checkpoint(byte part, object known) => part switch
    {
        EditsPart => SyncActorEdits.Checkpoint((SyncActorEdits)known),
        AnimPart when ((SyncAnimState)known).Frozen => known,
        _ => null,
    };

    public override byte[]? RepairDigest(byte part, object? known)
        => part == EditsPart ? SyncActorEdits.BoneDigest(known as SyncActorEdits) : null;

    public override object? RepairAnswer(byte part, object known, byte[] digest)
        => part == EditsPart ? SyncActorEdits.Repair((SyncActorEdits)known, digest) : null;

    public override byte[] Encode(byte part, object value) => part switch
    {
        AppearancePart => SyncCodec.Encode((SyncActorAppearance)value),
        PosePart => SyncCodec.Encode((SyncActorPose)value),
        AnimPart => SyncCodec.Encode((SyncAnimState)value),
        EditsPart => SyncCodec.Encode((SyncActorEdits)value),
        LookPart => SyncCodec.Encode((SyncLookSource)value),
        _ => SyncCodec.Encode((SyncActorMods)value),
    };

    // Theirs

    public override object? Decode(byte part, byte[] compressed)
    {
        if(part == LookPart)
        {
            var look = SyncCodec.Decode<SyncLookSource>(compressed, SyncLimits.MaxPartBytes);
            return look is not null && SyncValidation.IsValid(look) ? look : null;
        }

        if(part == AnimPart)
        {
            var anim = SyncCodec.Decode<SyncAnimState>(compressed, SyncLimits.MaxPartBytes);
            return anim is not null && SyncValidation.IsValid(anim) ? anim : null;
        }

        if(part == EditsPart)
        {
            var edits = SyncCodec.Decode<SyncActorEdits>(compressed, SyncLimits.MaxPartBytes * 4);
            return edits is not null && SyncValidation.IsValid(edits) ? edits : null;
        }

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

    public override void OnSent(SyncItem item, byte part)
    {
        if(part == AppearancePart && item.Mine && item.Entity is ActorEntity actor)
            modSource.Refresh(actor);
    }

    public override void Adopt(SyncItem item)
    {
        // Our own actor: never respawned from here, so it counts as having room for a companion.
        item.State = new SpawnedState(true);
    }

    public override unsafe void Show(SyncItem item, FolderEntity folder)
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

                    entity.IsSynced = !item.Mine;
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

    public override void Apply(SyncItem item)
    {
        var entity = (ActorEntity)item.Entity!;
        var state = (SpawnedState)item.State!;

        if(item.Dirty.Contains(LookPart))
        {
            UpdateLook(item, entity, state);
            if(item.Busy)
                return;
        }

        if(item.Dirty.Remove(AppearancePart) && !HasMcdf(item))
        {
            var appearance = (SyncActorAppearance)item.Parts[AppearancePart];

            // A minion or mount needs a slot reserved when the actor is spawned.
            if(appearance.Companion.Kind != CompanionKind.None && !state.HasAttachments && !item.Mine)
            {
                item.RespawnRequested = true;
                return;
            }

            entity.FriendlyName = appearance.Name;

            item.Busy = true;
            var generation = item.Generation;
            _ = ApplyAppearance(entity, appearance, state, () => item.IsCurrent(generation) && IsAlive(item)).ContinueWith(_ => framework.RunOnFrameworkThread(() =>
            {
                if(item.IsCurrent(generation))
                {
                    item.Busy = false;

                    // A redraw restarts the animation.
                    item.Dirty.Add(AnimPart);
                }
            }));
            return;
        }

        if(item.Dirty.Remove(PosePart) && item.Parts.TryGetValue(PosePart, out var poseValue))
            ApplyPose(item, entity, state, (SyncActorPose)poseValue);

        if(item.Dirty.Remove(AnimPart) && item.Parts.TryGetValue(AnimPart, out var animValue))
            ApplyAnim(item, entity, (SyncAnimState)animValue);

        if(item.Dirty.Remove(EditsPart) && item.Parts.TryGetValue(EditsPart, out var editsValue))
            ApplyEdits(item, entity, state, (SyncActorEdits)editsValue);

        if(item.Dirty.Remove(ModsPart) && !item.Mine && !HasMcdf(item) && item.Parts.TryGetValue(ModsPart, out var modsValue))
            UpdateMods(item, entity, state, (SyncActorMods)modsValue);
    }

    private static bool HasMcdf(SyncItem item)
        => item.Parts.TryGetValue(LookPart, out var look) && ((SyncLookSource)look).McdfHash is not null;

    // Fetches an MCDF someone applied, if we don't have it, and loads it on this actor. Whoever
    // applied one last decides the look, until someone goes back to the creator's own.
    private void UpdateLook(SyncItem item, ActorEntity entity, SpawnedState state)
    {
        var look = (SyncLookSource)item.Parts[LookPart];

        if(look.McdfHash == state.LookShown)
        {
            item.Dirty.Remove(LookPart);
            return;
        }

        var appearance = entity.GetCapability<ActorAppearanceCapability>();
        var generation = item.Generation;

        if(look.McdfHash is null)
        {
            item.Dirty.Remove(LookPart);
            state.LookShown = null;
            state.ModsShown = null;
            item.Status = null;
            item.Busy = true;

            _ = appearance.ResetAppearance().ContinueWith(_ => framework.RunOnFrameworkThread(() =>
            {
                if(!item.IsCurrent(generation))
                    return;

                item.Busy = false;
                item.MarkAllDirty();
                item.Dirty.Remove(LookPart);
                if(item.Mine)
                    modSource.Refresh(entity);
            }));
            return;
        }

        if(DateTime.UtcNow < state.NextModCheck)
            return;
        state.NextModCheck = DateTime.UtcNow + ModCheckInterval;

        var file = new SyncActorMods { Files = [new SyncModFile { Hash = look.McdfHash, Size = look.Size, GamePaths = ["look.mcdf"] }] };
        var progress = modTransfer.Want(item.Tracks[LookPart].Writer, file);

        if(!progress.Done)
        {
            item.Status = modTransfer.RelayRejectsFiles
                ? "The relay needs updating for mod transfer."
                : $"Downloading MCDF: {progress.HaveBytes / 1048576}/{Math.Max(1, progress.TotalBytes / 1048576)} MB.";
            return;
        }

        if(progress.FailedFiles > 0)
        {
            item.Status = "Couldn't download the MCDF. Trying again in a minute.";
            return;
        }

        if(appearance.IsSelf)
        {
            item.Dirty.Remove(LookPart);
            item.Status = "An MCDF can't go on your own character. Use a spawned actor.";
            return;
        }

        // Only one can load at a time, across all actors.
        if(appearance.IsAnyMCDFLoading)
            return;

        item.Dirty.Remove(LookPart);
        item.Busy = true;
        item.Status = $"Loading MCDF {look.Name}...";

        _ = LoadLook(item, entity, state, look, appearance).ContinueWith(task => framework.RunOnFrameworkThread(() =>
        {
            if(!item.IsCurrent(generation))
                return;

            item.Busy = false;
            state.LookShown = look.McdfHash;
            item.Status = task.IsCompletedSuccessfully && task.Result ? $"Look from MCDF {look.Name}." : "That MCDF couldn't be loaded.";

            // Loading one redraws the actor.
            item.Dirty.Add(AnimPart);
            item.Dirty.Add(EditsPart);
        }));
    }

    private async Task<bool> LoadLook(SyncItem item, ActorEntity entity, SpawnedState state, SyncLookSource look, ActorAppearanceCapability appearance)
    {
        if(!modCache.TryGet(look.McdfHash!, out var path) || !await IsSafeMcdf(path))
            return false;

        modSource.Offer(look.McdfHash!, path);
        return await appearance.LoadMCDF(path) == global::Brio.API.BrioApiResult.Success;
    }

    // Reads the file's header off the framework thread, and checks what it names is harmless.
    public Task<bool> IsSafeMcdf(string path) => Task.Run(async () =>
    {
        try
        {
            var (header, _) = await mcdfService.LoadFileHeader(path);
            return SyncValidation.IsSafe(header.CharaFileData);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: an MCDF couldn't be read.");
            return false;
        }
    });

    // Fetches whatever files are missing, then swaps the actor over to the owner's mods. Until
    // then it keeps its Glamourer look and shows download progress.
    private void UpdateMods(SyncItem item, ActorEntity entity, SpawnedState state, SyncActorMods mods)
    {
        // Shown already. If some files didn't make it, keep trying for them now and then.
        if(ReferenceEquals(mods, state.ModsShown))
        {
            if(state.ModsFailed == 0)
                return;

            if(DateTime.UtcNow >= state.NextModCheck)
            {
                state.NextModCheck = DateTime.UtcNow + ModCheckInterval;
                var retry = modTransfer.Want(item.Creator!.Id, mods);
                if(!retry.Done || retry.FailedFiles >= state.ModsFailed)
                {
                    item.Dirty.Add(ModsPart);
                    return;
                }

                state.ModsShown = null;
            }
            else
            {
                item.Dirty.Add(ModsPart);
                return;
            }
        }

        if(!modApplier.IsAvailable)
        {
            item.Status = "Can't show their mods: Penumbra isn't available.";
            return;
        }

        if(DateTime.UtcNow < state.NextModCheck)
        {
            item.Dirty.Add(ModsPart);
            return;
        }
        state.NextModCheck = DateTime.UtcNow + ModCheckInterval;

        var progress = modTransfer.Want(item.Creator!.Id, mods);
        if(!progress.Done)
        {
            entity.IsLoading = true;
            entity.LoadingDescription = $"Downloading look {progress.HaveBytes / 1048576}/{Math.Max(1, progress.TotalBytes / 1048576)} MB";
            item.Status = modTransfer.RelayRejectsFiles
                ? "The relay needs updating for mod transfer."
                : $"Downloading {mods.Files.Count} mod files: {progress.HaveBytes / 1048576}/{Math.Max(1, progress.TotalBytes / 1048576)} MB.";
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

        _ = modApplier.Apply(entity, mods, glamourerState, state.Mods, () => item.IsCurrent(generation) && IsAlive(item)).ContinueWith(task => framework.RunOnFrameworkThread(() =>
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
            state.ModsFailed = progress.FailedFiles;
            if(progress.FailedFiles > 0)
                item.Dirty.Add(ModsPart);
            item.Status = mods.Files.Count == 0
                ? "Their actor has no modded files."
                : progress.FailedFiles > 0
                    ? $"Applied {mods.Files.Count - progress.FailedFiles} of {mods.Files.Count} mod files; {progress.FailedFiles} couldn't be downloaded."
                    : $"Applied {mods.Files.Count} mod files ({mods.TotalBytes / 1048576} MB).";
            entity.IsLoading = false;
            item.Busy = false;

            // A redraw resets the skeleton and restarts the animation.
            item.Dirty.Add(PosePart);
            item.Dirty.Add(AnimPart);
            item.Dirty.Add(EditsPart);
        }));
    }

    // The actor can be removed at any await, so stillThere is checked before each game call.
    private async Task ApplyAppearance(ActorEntity entity, SyncActorAppearance dto, SpawnedState state, Func<bool> stillThere)
    {
        await framework.RunOnTick(async () =>
        {
            try
            {
                if(!stillThere())
                    return;

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

                if(!stillThere())
                    return;

                await redrawService.WaitForDrawing(entity.GameObject);

                if(stillThere() && state.Companion != dto.Companion && entity.TryGetCapability<CompanionCapability>(out var companion))
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

    // Plays what the other side is playing, then moves to the same frame once it has started.
    private void ApplyAnim(SyncItem item, ActorEntity entity, SyncAnimState dto)
    {
        var generation = item.Generation;
        Func<bool> stillThere = () => item.IsCurrent(generation) && IsAlive(item);

        PlayAnim(entity, dto, stillThere);

        if(dto.Companion is not null
            && entity.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity)
        {
            PlayAnim(companionEntity, dto.Companion, () => stillThere() && companion.GetCompanionAsEntity() == companionEntity);
        }
    }

    private unsafe void PlayAnim(ActorEntity entity, SyncAnimState dto, Func<bool> stillThere)
    {
        if(!entity.TryGetCapability<ActionTimelineCapability>(out var timeline))
            return;

        var sequencer = &timeline.Character.Native()->Timeline.TimelineSequencer;
        var currentBase = sequencer->TimelineIds[(int)ActionTimelineService.ActionTimelineSlots.Base];
        var currentBlend = sequencer->TimelineIds[(int)ActionTimelineService.ActionTimelineSlots.UpperBody];

        var starting = (dto.Base != 0 && dto.Base != currentBase) || (dto.Blend != 0 && dto.Blend != currentBlend);

        // It has to run for a moment for a new animation to take hold, even if it ends up frozen.
        timeline.SetOverallSpeedOverride(dto.Frozen ? (starting ? 1f : 0f) : dto.Speed);

        if(dto.Base != 0 && dto.Base != currentBase)
            timeline.ApplyBaseOverride(dto.Base, true);

        if(dto.Blend != 0 && dto.Blend != currentBlend)
            timeline.BlendTimeline(dto.Blend);

        if(!starting)
        {
            Seek(entity, dto.Time);
            return;
        }

        framework.RunOnTick(() =>
        {
            if(!stillThere())
                return;

            Seek(entity, dto.Time);
            if(dto.Frozen)
                timeline.SetOverallSpeedOverride(0);
        }, delayTicks: 3);
    }

    // Writes only the bones that differ, and clears any that shouldn't have changes any more.
    private void ApplyEdits(SyncItem item, ActorEntity entity, SpawnedState state, SyncActorEdits dto)
    {
        if(entity.TryGetCapability<PosingCapability>(out var posing))
        {
            WriteBones(posing, dto, false, item.WritingExact);

            if(state.HasPose)
                item.Motion = new TransformMotion(posing.ModelPosing.Transform, dto.Model, DateTime.UtcNow);
            else
                posing.ModelPosing.Transform = dto.Model;

            state.HasPose = true;
        }

        if(entity.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity
            && companionEntity.TryGetCapability<PosingCapability>(out var companionPosing))
        {
            WriteBones(companionPosing, dto, true, item.WritingExact);

            if(dto.CompanionModel.HasValue)
                companionPosing.ModelPosing.Transform = dto.CompanionModel.Value;
        }
    }

    private static void WriteBones(PosingCapability posing, SyncActorEdits dto, bool companion, bool exact)
    {
        var poseInfo = posing.SkeletonPosing.PoseInfo;
        var wanted = new HashSet<BonePoseInfoId>();

        foreach(var bone in dto.Bones)
        {
            if(bone.Companion != companion)
                continue;

            var id = new BonePoseInfoId(bone.Name, bone.Partial, (PoseInfoSlot)bone.Slot);
            wanted.Add(id);
            SyncActorEdits.Write(poseInfo.GetPoseInfo(id), bone, exact);
        }

        foreach(var (id, info) in poseInfo.Poses)
        {
            if(info.HasStacks && !wanted.Contains(id))
                info.ClearStacks();
        }
    }

    // Bones are applied once per update. Only where the actor stands glides (see Animate), since
    // re-importing a blended pose every frame is far too costly.
    private void ApplyPose(SyncItem item, ActorEntity entity, SpawnedState state, SyncActorPose dto)
    {
        Import(entity, dto.Body);

        if(entity.TryGetCapability<PosingCapability>(out var posing))
        {
            var target = new Transform { Position = dto.Body.Position, Rotation = dto.Body.Rotation, Scale = dto.Body.Scale };
            if(state.HasPose)
                item.Motion = new TransformMotion(posing.ModelPosing.Transform, target, DateTime.UtcNow);
            else
                posing.ModelPosing.Transform = target;
        }

        state.HasPose = true;

        if(dto.Companion is not null
            && entity.TryGetCapability<CompanionCapability>(out var companion)
            && companion.GetCompanionAsEntity() is ActorEntity companionEntity)
        {
            if(companionEntity.TryGetCapability<ActionTimelineCapability>(out var timeline))
                timeline.SetOverallSpeedOverride(0);

            Import(companionEntity, dto.Companion);

            if(companionEntity.TryGetCapability<PosingCapability>(out var companionPosing))
                companionPosing.ModelPosing.Transform = new Transform { Position = dto.Companion.Position, Rotation = dto.Companion.Rotation, Scale = dto.Companion.Scale };
        }
    }

    public override void Animate(SyncItem item, DateTime now)
    {
        if(item.Motion is not TransformMotion motion || item.Entity is not ActorEntity entity)
            return;

        if(entity.TryGetCapability<PosingCapability>(out var posing))
            posing.ModelPosing.Transform = motion.Step(posing.ModelPosing.Transform, now);

        if(motion.IsDone(now))
            item.Motion = null;
    }

    // Merged into the bone's last stack. A new stack per update would pile up without limit, and
    // every stack is re-applied every frame.
    private void Import(ActorEntity entity, PoseData pose)
    {
        if(entity.TryGetCapability<PosingCapability>(out var posing))
            posing.SkeletonPosing.ImportSkeletonPose(pose, posingService.SceneImporterOptions, newStack: false);
    }

    public override void Release(SyncItem item, bool alive)
    {
        if(item.State is SpawnedState { Mods: { } applied } state)
        {
            modApplier.Remove(alive ? item.Entity as ActorEntity : null, applied);
            state.Mods = null;
        }
    }

    public override void Hide(SyncItem item)
    {
        if(item.Entity is ActorEntity entity)
            entityManager.EntityManagerContainer.GetCapability<ActorContainerCapability>().DestroyCharacter(entity);
    }

    public override bool IsAlive(SyncItem item)
        => item.Entity is ActorEntity entity && entity.GameObject.IsValid() && entityManager.EntityExists(entity.Id);
}
