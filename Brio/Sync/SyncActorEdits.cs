using Brio.Core;
using Brio.Game.Posing;
using MessagePack;
using System;
using System.Collections.Generic;

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
    public List<SyncBoneStack> Stacks { get; set; } = [];

    [IgnoreMember] public uint Key => SyncActorEdits.KeyOf(Name, Partial, Slot, Companion);
}

// An actor's hand-posed bones and where it stands. Kept whole on each side, sorted by bone key,
// and sent as just the bones that differ from what was last sent.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncActorEdits
{
    public Transform Model { get; set; } = Transform.Identity;
    public Transform? CompanionModel { get; set; }

    public bool Full { get; set; }
    public List<SyncBoneEdit> Bones { get; set; } = [];
    public List<uint> Removed { get; set; } = [];

    // Of the whole set after this update, so the other side can tell if it has fallen out of step.
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
            if(a.Bones[i].Key != b.Bones[i].Key || !Near(a.Bones[i], b.Bones[i]))
                return false;
        }

        return true;
    }

    public static bool Near(SyncBoneEdit a, SyncBoneEdit b) => Near(a.Stacks, b.Stacks);

    private static bool Near(List<SyncBoneStack> a, List<SyncBoneStack> b)
    {
        if(a.Count != b.Count)
            return false;

        for(var i = 0; i < a.Count; i++)
        {
            if(a[i].Propagate != b[i].Propagate || a[i].IK != b[i].IK || !SyncCompare.Near(a[i].Transform, b[i].Transform))
                return false;
        }

        return true;
    }

    // Just what changed from `last`; everything if there's no `last`.
    public static SyncActorEdits Diff(SyncActorEdits current, SyncActorEdits? last)
    {
        var wire = new SyncActorEdits
        {
            Model = current.Model,
            CompanionModel = current.CompanionModel,
            Full = last is null,
        };

        if(last is null)
        {
            wire.Bones = current.Bones;
            wire.Digest = DigestOf(current);
            return wire;
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

            var (currentKey, lastKey) = (current.Bones[i].Key, last.Bones[j].Key);
            if(currentKey < lastKey)
            {
                wire.Bones.Add(current.Bones[i++]);
            }
            else if(lastKey < currentKey)
            {
                wire.Removed.Add(lastKey);
                j++;
            }
            else
            {
                if(!Near(current.Bones[i], last.Bones[j]))
                    wire.Bones.Add(current.Bones[i]);
                i++;
                j++;
            }
        }

        // Of what the other side ends up with: bones within tolerance stay as last sent.
        wire.Digest = Merge(wire, last).Digest;
        return wire;
    }

    // The whole set after applying an update to what we had.
    public static SyncActorEdits Merge(SyncActorEdits wire, SyncActorEdits? last)
    {
        var merged = new SyncActorEdits { Model = wire.Model, CompanionModel = wire.CompanionModel, Full = true };

        if(wire.Full || last is null)
        {
            merged.Bones = wire.Bones;
        }
        else
        {
            var bones = new Dictionary<uint, SyncBoneEdit>(last.Bones.Count + wire.Bones.Count);
            foreach(var bone in last.Bones)
                bones[bone.Key] = bone;
            foreach(var key in wire.Removed)
                bones.Remove(key);
            foreach(var bone in wire.Bones)
                bones[bone.Key] = bone;
            merged.Bones = [.. bones.Values];
        }

        merged.Sort();
        merged.Digest = DigestOf(merged);
        return merged;
    }

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
            Add(r.X, 100000); Add(r.Y, 100000); Add(r.Z, 100000); Add(r.W, 100000);
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

    // The update that brings someone holding that digest up to `current`.
    public static SyncActorEdits Repair(SyncActorEdits current, ReadOnlySpan<byte> digest)
    {
        var theirs = new Dictionary<uint, uint>(digest.Length / 8);
        for(var i = 0; i + 8 <= digest.Length; i += 8)
            theirs[BitConverter.ToUInt32(digest.Slice(i, 4))] = BitConverter.ToUInt32(digest.Slice(i + 4, 4));

        var wire = new SyncActorEdits { Model = current.Model, CompanionModel = current.CompanionModel, Digest = DigestOf(current) };

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
    public static void Write(BonePoseInfo info, SyncBoneEdit bone)
    {
        if(Near(StacksOf(info), bone.Stacks))
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
