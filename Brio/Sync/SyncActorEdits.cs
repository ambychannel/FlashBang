using Brio.Core;
using Brio.Game.Posing;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Sync;

// What an actor's animation is doing. The other side plays the same one itself.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncAnimState
{
    public ushort Base { get; set; }
    public ushort Blend { get; set; }
    public float Speed { get; set; }
    public bool Frozen { get; set; }
    public float Time { get; set; }
    public float Duration { get; set; }
    public SyncAnimState? Companion { get; set; }

    // When this was read or received, on this PC. Only used to work out where it should be by now.
    [IgnoreMember] public DateTime At { get; set; }
}

[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncBoneStack
{
    public byte Propagate { get; set; }
    public bool IK { get; set; }
    public Transform Transform { get; set; }
}

// One bone someone has posed by hand: Brio's stacks of changes on top of the animation.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncBoneEdit
{
    public string Name { get; set; } = "";
    public int Partial { get; set; }
    public byte Slot { get; set; }
    public bool Companion { get; set; }

    // In an update, whether the stacks are plain values rather than how much each one changed.
    public bool Absolute { get; set; } = true;
    public List<SyncBoneStack> Stacks { get; set; } = [];

    [IgnoreMember] public uint Key => SyncActorEdits.KeyOf(Name, Partial, Slot, Companion);
}

// An actor's hand-posed bones and where it stands. Kept whole on each side, sorted by bone key.
// Updates hold only how much the bones that changed have changed, so two people moving things at
// once add up, even on the same bone.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncActorEdits
{
    public Transform Model { get; set; } = Transform.Identity;
    public Transform? CompanionModel { get; set; }

    // In an update: plain values rather than how much they changed.
    public bool ModelAbsolute { get; set; }
    public bool CompanionAbsolute { get; set; }

    public bool Full { get; set; }
    public List<SyncBoneEdit> Bones { get; set; } = [];
    public List<uint> Removed { get; set; } = [];

    // From a checkpoint: what the creator has, for the receiver to compare with its own.
    public bool HasDigest { get; set; }
    public ulong Digest { get; set; }

    public static uint KeyOf(string name, int partial, byte slot, bool companion)
    {
        var hash = 2166136261u;
        foreach(var c in name)
            hash = (hash ^ c) * 16777619u;
        hash = (hash ^ (uint)partial) * 16777619u;
        hash = (hash ^ slot) * 16777619u;
        return (hash ^ (companion ? 1u : 0u)) * 16777619u;
    }

    public void Sort() => Bones.Sort((a, b) => a.Key.CompareTo(b.Key));

    public static bool Near(SyncActorEdits a, SyncActorEdits b)
    {
        if(!SyncCompare.Near(a.Model, b.Model) || a.Bones.Count != b.Bones.Count || a.CompanionModel.HasValue != b.CompanionModel.HasValue)
            return false;

        if(a.CompanionModel.HasValue && !SyncCompare.Near(a.CompanionModel.Value, b.CompanionModel!.Value))
            return false;

        for(var i = 0; i < a.Bones.Count; i++)
        {
            if(a.Bones[i].Key != b.Bones[i].Key || !Near(a.Bones[i].Stacks, b.Bones[i].Stacks))
                return false;
        }

        return true;
    }

    private static bool Near(List<SyncBoneStack> a, List<SyncBoneStack> b)
    {
        if(!SameShape(a, b))
            return false;

        for(var i = 0; i < a.Count; i++)
        {
            if(!SyncCompare.Near(a[i].Transform, b[i].Transform))
                return false;
        }

        return true;
    }

    // Whether one bone's stacks can be added to the other's, stack by stack.
    private static bool SameShape(List<SyncBoneStack> a, List<SyncBoneStack> b)
    {
        if(a.Count != b.Count)
            return false;

        for(var i = 0; i < a.Count; i++)
        {
            if(a[i].Propagate != b[i].Propagate || a[i].IK != b[i].IK)
                return false;
        }

        return true;
    }

    private static Transform Unchanged => new() { Position = Vector3.Zero, Rotation = Quaternion.Identity, Scale = Vector3.Zero };

    // How much `to` differs from `from`.
    private static Transform Change(Transform from, Transform to) => new()
    {
        Position = to.Position - from.Position,
        Rotation = SyncValues.IsSameRotation(from.Rotation, to.Rotation) ? Quaternion.Identity : SyncValues.RotationFrom(from.Rotation, to.Rotation),
        Scale = to.Scale - from.Scale,
    };

    private static Transform Add(Transform value, Transform change) => new()
    {
        Position = value.Position + change.Position,
        Rotation = Quaternion.Normalize(change.Rotation * value.Rotation),
        Scale = value.Scale + change.Scale,
    };

    private static List<SyncBoneStack> Add(List<SyncBoneStack> stacks, List<SyncBoneStack> change)
    {
        var result = new List<SyncBoneStack>(stacks.Count);
        for(var i = 0; i < stacks.Count; i++)
            result.Add(new SyncBoneStack { Propagate = stacks[i].Propagate, IK = stacks[i].IK, Transform = Add(stacks[i].Transform, change[i].Transform) });
        return result;
    }

    // What changed from `last`; everything, as plain values, if there's no `last`.
    public static SyncActorEdits Diff(SyncActorEdits current, SyncActorEdits? last)
    {
        if(last is null)
            return new SyncActorEdits { Model = current.Model, CompanionModel = current.CompanionModel, ModelAbsolute = true, CompanionAbsolute = true, Full = true, Bones = current.Bones };

        var wire = new SyncActorEdits { Model = SyncCompare.Near(current.Model, last.Model) ? Unchanged : Change(last.Model, current.Model) };

        if(current.CompanionModel is { } companion)
        {
            if(last.CompanionModel is { } lastCompanion)
            {
                wire.CompanionModel = SyncCompare.Near(companion, lastCompanion) ? Unchanged : Change(lastCompanion, companion);
            }
            else
            {
                wire.CompanionModel = companion;
                wire.CompanionAbsolute = true;
            }
        }

        var (i, j) = (0, 0);
        while(i < current.Bones.Count || j < last.Bones.Count)
        {
            if(j >= last.Bones.Count)
            {
                wire.Bones.Add(current.Bones[i++]);
                continue;
            }

            if(i >= current.Bones.Count)
            {
                wire.Removed.Add(last.Bones[j++].Key);
                continue;
            }

            var (bone, lastBone) = (current.Bones[i], last.Bones[j]);
            var (currentKey, lastKey) = (bone.Key, lastBone.Key);
            if(currentKey < lastKey)
            {
                wire.Bones.Add(bone);
                i++;
            }
            else if(lastKey < currentKey)
            {
                wire.Removed.Add(lastKey);
                j++;
            }
            else
            {
                if(!Near(bone.Stacks, lastBone.Stacks))
                    wire.Bones.Add(ChangeOf(bone, lastBone));
                i++;
                j++;
            }
        }

        return wire;
    }

    private static SyncBoneEdit ChangeOf(SyncBoneEdit bone, SyncBoneEdit last)
    {
        // Stacks were added or removed, so there's nothing to add them to.
        if(!SameShape(bone.Stacks, last.Stacks))
            return bone;

        var change = new SyncBoneEdit { Name = bone.Name, Partial = bone.Partial, Slot = bone.Slot, Companion = bone.Companion, Absolute = false };
        for(var s = 0; s < bone.Stacks.Count; s++)
        {
            var stack = bone.Stacks[s];
            change.Stacks.Add(new SyncBoneStack { Propagate = stack.Propagate, IK = stack.IK, Transform = Change(last.Stacks[s].Transform, stack.Transform) });
        }
        return change;
    }

    // The whole set after an update.
    public static SyncActorEdits Merge(SyncActorEdits wire, SyncActorEdits? last)
    {
        var merged = new SyncActorEdits
        {
            Model = wire.ModelAbsolute || last is null ? wire.Model : Add(last.Model, wire.Model),
            CompanionModel = wire.CompanionModel is not { } companion
                ? last?.CompanionModel
                : wire.CompanionAbsolute || last?.CompanionModel is null ? companion : Add(last.CompanionModel.Value, companion),
            Full = true,
        };

        var bones = new Dictionary<uint, SyncBoneEdit>((last?.Bones.Count ?? 0) + wire.Bones.Count);
        if(last is not null && !wire.Full)
        {
            foreach(var bone in last.Bones)
                bones[bone.Key] = bone;
        }

        foreach(var key in wire.Removed)
            bones.Remove(key);

        foreach(var bone in wire.Bones)
        {
            if(bone.Absolute)
            {
                bones[bone.Key] = bone;
            }
            else if(bones.TryGetValue(bone.Key, out var existing) && SameShape(existing.Stacks, bone.Stacks))
            {
                bones[bone.Key] = new SyncBoneEdit { Name = bone.Name, Partial = bone.Partial, Slot = bone.Slot, Companion = bone.Companion, Stacks = Add(existing.Stacks, bone.Stacks) };
            }

            // A change to stacks we don't have is put right by the next checkpoint.
        }

        merged.Bones = [.. bones.Values];
        merged.Sort();
        return merged;
    }

    // `current` with the change from `from` to `to` added on top, so anything changed locally
    // and not sent yet is kept.
    public static SyncActorEdits Rebase(SyncActorEdits current, SyncActorEdits from, SyncActorEdits to)
    {
        var rebased = new SyncActorEdits
        {
            Model = Add(current.Model, Change(from.Model, to.Model)),
            CompanionModel = to.CompanionModel,
            Full = true,
        };

        if(current.CompanionModel is { } companion && from.CompanionModel is { } fromCompanion && to.CompanionModel is { } toCompanion)
            rebased.CompanionModel = Add(companion, Change(fromCompanion, toCompanion));

        var fromBones = new Dictionary<uint, SyncBoneEdit>(from.Bones.Count);
        foreach(var bone in from.Bones)
            fromBones[bone.Key] = bone;

        var toBones = new Dictionary<uint, SyncBoneEdit>(to.Bones.Count);
        foreach(var bone in to.Bones)
            toBones[bone.Key] = bone;

        var bones = new Dictionary<uint, SyncBoneEdit>(current.Bones.Count + to.Bones.Count);

        // Ours, unless the update took it away.
        foreach(var bone in current.Bones)
        {
            if(!fromBones.ContainsKey(bone.Key) || toBones.ContainsKey(bone.Key))
                bones[bone.Key] = bone;
        }

        foreach(var target in to.Bones)
        {
            var key = target.Key;
            if(!bones.TryGetValue(key, out var mine) || !fromBones.TryGetValue(key, out var before))
            {
                bones[key] = target;
            }
            else if(Near(before.Stacks, target.Stacks))
            {
                // Untouched by the update: whatever we have stays.
            }
            else if(SameShape(mine.Stacks, before.Stacks) && SameShape(before.Stacks, target.Stacks))
            {
                var change = ChangeOf(target, before);
                bones[key] = new SyncBoneEdit { Name = mine.Name, Partial = mine.Partial, Slot = mine.Slot, Companion = mine.Companion, Stacks = Add(mine.Stacks, change.Stacks) };
            }
            else
            {
                bones[key] = target;
            }
        }

        rebased.Bones = [.. bones.Values];
        rebased.Sort();
        return rebased;
    }

    // What the creator sends once things go quiet: where the actor really is, and a digest of
    // the bones. Anyone whose bones don't match asks for the ones that differ.
    public static SyncActorEdits Checkpoint(SyncActorEdits known) => new()
    {
        Model = known.Model,
        ModelAbsolute = true,
        CompanionModel = known.CompanionModel,
        CompanionAbsolute = true,
        HasDigest = true,
        Digest = DigestOf(known),
    };

    public static ulong DigestOf(SyncActorEdits edits)
    {
        ulong digest = 0;
        foreach(var bone in edits.Bones)
            digest += (((ulong)bone.Key << 32) | ValueHash(bone)) * 0x9E3779B97F4A7C15UL;
        return digest;
    }

    // Quantised, so a bone reads the same as long as nothing has really changed.
    public static uint ValueHash(SyncBoneEdit bone)
    {
        var hash = 2166136261u;

        void Add(float value, float scale)
            => hash = (hash ^ (uint)(int)MathF.Round(value * scale)) * 16777619u;

        foreach(var stack in bone.Stacks)
        {
            hash = (hash ^ stack.Propagate ^ (stack.IK ? 256u : 0u)) * 16777619u;
            var (p, r, s) = (stack.Transform.Position, stack.Transform.Rotation, stack.Transform.Scale);
            Add(p.X, 1000); Add(p.Y, 1000); Add(p.Z, 1000);
            Add(r.X, 10000); Add(r.Y, 10000); Add(r.Z, 10000); Add(r.W, 10000);
            Add(s.X, 1000); Add(s.Y, 1000); Add(s.Z, 1000);
        }

        return hash;
    }

    // 8 bytes per bone: what a player has, so the other side can send only what differs.
    public static byte[] BoneDigest(SyncActorEdits? edits)
    {
        if(edits is null)
            return [];

        var data = new byte[edits.Bones.Count * 8];
        for(var i = 0; i < edits.Bones.Count; i++)
        {
            BitConverter.TryWriteBytes(data.AsSpan(i * 8, 4), edits.Bones[i].Key);
            BitConverter.TryWriteBytes(data.AsSpan(i * 8 + 4, 4), ValueHash(edits.Bones[i]));
        }
        return data;
    }

    // The update that brings someone holding that digest up to `current`, in plain values.
    public static SyncActorEdits Repair(SyncActorEdits current, ReadOnlySpan<byte> digest)
    {
        var theirs = new Dictionary<uint, uint>(digest.Length / 8);
        for(var i = 0; i + 8 <= digest.Length; i += 8)
            theirs[BitConverter.ToUInt32(digest.Slice(i, 4))] = BitConverter.ToUInt32(digest.Slice(i + 4, 4));

        var wire = new SyncActorEdits { Model = current.Model, ModelAbsolute = true, CompanionModel = current.CompanionModel, CompanionAbsolute = true };

        foreach(var bone in current.Bones)
        {
            if(!theirs.Remove(bone.Key, out var value) || value != ValueHash(bone))
                wire.Bones.Add(bone);
        }

        wire.Removed.AddRange(theirs.Keys);
        return wire;
    }

    public static List<SyncBoneStack> StacksOf(BonePoseInfo info)
    {
        var stacks = new List<SyncBoneStack>(info.Stacks.Count);
        foreach(var stack in info.Stacks)
            stacks.Add(new SyncBoneStack { Propagate = (byte)stack.PropagateComponents, IK = stack.IKInfo.Enabled, Transform = stack.Transform });
        return stacks;
    }

    // Writes a bone's stacks, unless they're already that. IK bones get their default solver.
    public static void Write(BonePoseInfo info, SyncBoneEdit bone, bool exact = false)
    {
        if(!exact && Near(StacksOf(info), bone.Stacks))
            return;

        info.ClearStacks();
        foreach(var stack in bone.Stacks)
        {
            var ik = BoneIKInfo.Disabled;
            if(stack.IK)
            {
                ik = BoneIKInfo.CalculateDefault(bone.Name);
                ik.Enabled = true;
            }
            info.Stacks.Add(new BonePoseTransformInfo((TransformComponents)stack.Propagate, ik, stack.Transform));
        }
    }
}
