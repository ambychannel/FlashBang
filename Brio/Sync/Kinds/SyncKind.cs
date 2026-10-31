using Brio.Core;
using Brio.Entities.Core;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Brio.Sync.Kinds;

// One kind of thing that can be shared through the Sync folder: how to read it from the scene,
// and how to show, update and remove a copy of it. Items are shared both ways: whoever made an
// item, anyone syncing can edit it, so Apply works on our own items as well as on copies of
// other players'. Everything runs on the framework thread.
public abstract class SyncKind
{
    public abstract SyncEntityType Type { get; }
    public abstract int MaxPerPlayer { get; }
    public abstract byte PartCount { get; }

    // Reference images can be shown anywhere; everything else is placed in the world.
    public virtual bool NeedsSameTerritory => true;

    // How often a part is read again to see if it changed.
    public virtual TimeSpan CaptureInterval(byte part) => SyncLimits.SendInterval;

    // Whether anyone but the creator may change this part. Mods and pictures stay the creator's.
    public virtual bool Editable(byte part) => true;

    // Parts the creator keeps up to date by itself once an item is shared, Live or not.
    public virtual bool AlwaysSent(byte part) => false;

    // Reading

    public abstract bool CanShare(Entity entity);

    // The part's current value, or null to skip it this time (not ready, too large...).
    public abstract object? Capture(SyncItem item, byte part);

    // Encoded separately so a part type can override the MessagePack type it is decoded as.
    public abstract byte[] Encode(byte part, object value);

    // Whether two values of a part are close enough to count as unchanged, without encoding them.
    // False just means "compare the encoded bytes instead".
    public virtual bool IsSame(byte part, object a, object b) => false;

    // What to send for a value, given what was last sent. The whole value by default.
    public virtual object ToWire(byte part, object value, object? last) => value;

    // Folds a received (or just sent) update into what we had. inSync is false if the result
    // doesn't match what the sender has.
    public virtual object Merge(byte part, object wire, object? last, out bool inSync)
    {
        inSync = true;
        return wire;
    }

    // Parts sent as differences, where every update matters and none can replace another.
    public virtual bool IsDiffed(byte part) => false;

    // Whether Sync now sends this part even if nothing about it changed.
    public virtual bool SendWhenSame(byte part) => true;

    // A digest of what we have, for asking a player for only what differs.
    public virtual byte[]? RepairDigest(byte part, object? known) => null;

    // The update that brings someone holding that digest up to what we have.
    public virtual object? RepairAnswer(byte part, object known, byte[] digest) => null;

    // Decodes and checks one part from another player. Null means it's dropped.
    public abstract object? Decode(byte part, byte[] compressed);

    // Showing

    // Sets up bookkeeping for one of our own entities, as Show would for a copy.
    public virtual void Adopt(SyncItem item) { }

    // Whether the item has enough parts to be shown.
    public virtual bool CanShow(SyncItem item) => item.Parts.ContainsKey(0);

    public abstract void Show(SyncItem item, FolderEntity folder);
    public abstract void Apply(SyncItem item);
    public abstract void Hide(SyncItem item);

    // Moves the item a little further towards its latest position, every frame, so updates
    // glide instead of jumping.
    public virtual void Animate(SyncItem item, DateTime now) { }

    // Called after one of our parts went out, e.g. so a new look also re-reads its mods.
    public virtual void OnSent(SyncItem item, byte part) { }

    // Called whenever the item's object goes away (hidden, deleted, or the game dropped it), to
    // let go of anything kept for it outside the scene. `alive` says whether the game object
    // still exists and may be touched.
    public virtual void Release(SyncItem item, bool alive) { }
    public abstract bool IsAlive(SyncItem item);
}

// Where one part of an item stands: the last version anyone sent (a Lamport counter plus who
// sent it, so every player settles on the same latest edit), and what we last saw ourselves.
public class PartTrack
{
    public uint Revision { get; set; }
    public string Writer { get; set; } = "";

    // The part's bytes as last sent or settled. A capture that differs is a local edit.
    public byte[]? Baseline { get; set; }

    // Set after applying someone else's update: the next capture becomes the new baseline
    // instead of being sent back, so updates don't bounce between players.
    public bool Absorb { get; set; }
    public DateTime SettleUntil { get; set; }

    // The value last sent or applied: what this part is believed to be for everyone.
    public object? LastKnown { get; set; }

    // When we last sent a change of our own.
    public DateTime LastLocalChange { get; set; }

    public DateTime NextRepair { get; set; }

    public DateTime NextCapture { get; set; }
    public bool WarnedTooLarge { get; set; }

    // True if a version (revision, writer) is newer than what we have.
    public bool IsNewer(uint revision, string writer)
        => revision > Revision || (revision == Revision && string.CompareOrdinal(writer, Writer) > 0);
}

public class SyncItem(Guid id, SyncKind kind, SyncPeer? creator)
{
    public Guid Id { get; } = id;
    public SyncKind Kind { get; } = kind;

    // Who made it; null for our own items.
    public SyncPeer? Creator { get; } = creator;
    public bool Mine => Creator is null;
    public string CreatorName => Creator?.Name ?? "you";

    public uint Territory { get; set; }

    public Dictionary<byte, object> Parts { get; } = [];
    public HashSet<byte> Dirty { get; } = [];
    public PartTrack[] Tracks { get; } = CreateTracks(kind.PartCount);

    public Entity? Entity { get; set; }
    public bool Spawning { get; set; }
    public DateTime SpawnStarted { get; set; }
    public DateTime NextAttempt { get; set; }

    // Set by a kind when the item has to be spawned again (a different model, say); the engine
    // tears the old one down safely and shows it again.
    public bool RespawnRequested { get; set; }

    // Set by a kind while it has work in flight (an actor loading its appearance) so updates wait.
    public bool Busy { get; set; }
    public bool WasBusy { get; set; }

    // Kind-specific bookkeeping, like what an actor was spawned with.
    public object? State { get; set; }

    // Kind-specific movement towards the latest update (see SyncKind.Animate).
    public object? Motion { get; set; }

    // Bumped on every spawn and hide, so a spawn that finishes late can tell it's no longer wanted.
    public int Generation { get; set; }
    public bool Removed { get; set; }

    // Kept on this PC only. Live sends our changes as they happen; otherwise they go out only
    // when Sync now is pressed. Our own items aren't shared at all until one of the two.
    public bool Live { get; set; }
    public bool SyncNowRequested { get; set; }
    public bool Shared { get; set; }

    // A line for the Sync window, like mod download progress.
    public string? Status { get; set; }

    // Why Live isn't sending something right now.
    public string? Note { get; set; }

    public bool IsCurrent(int generation) => !Removed && Generation == generation;

    public void MarkAllDirty()
    {
        foreach(var part in Parts.Keys)
            Dirty.Add(part);
    }

    private static PartTrack[] CreateTracks(byte count)
    {
        var tracks = new PartTrack[count];
        for(var i = 0; i < count; i++)
            tracks[i] = new PartTrack();
        return tracks;
    }
}

// A transform gliding from where it was to where it's going.
public class TransformMotion(Transform from, Transform to, DateTime start)
{
    public static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(180);

    public Transform From { get; } = from;
    public Transform To { get; } = to;
    public DateTime Start { get; } = start;

    public bool IsDone(DateTime now) => now - Start >= Duration;

    public Transform At(DateTime now)
    {
        var t = (float)Math.Clamp((now - Start) / Duration, 0, 1);
        return new Transform
        {
            Position = Vector3.Lerp(From.Position, To.Position, t),
            Rotation = Quaternion.Slerp(From.Rotation, To.Rotation, t),
            Scale = Vector3.Lerp(From.Scale, To.Scale, t),
        };
    }
}
