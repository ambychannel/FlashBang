using Brio.Entities.Core;
using System;
using System.Collections.Generic;

namespace Brio.Sync.Kinds;

// One kind of thing that can be shared through the Sync folder: how to read it from our scene,
// and how to show, update and remove another player's copy of it. Everything runs on the
// framework thread.
public abstract class SyncKind
{
    public abstract SyncEntityType Type { get; }
    public abstract int MaxPerPlayer { get; }
    public abstract byte PartCount { get; }

    // Reference images can be shown anywhere; everything else is placed in the world.
    public virtual bool NeedsSameTerritory => true;

    // How often a part of one of our items is read again to see if it changed.
    public virtual TimeSpan CaptureInterval(byte part) => SyncLimits.SendInterval;

    // Ours

    public abstract bool CanShare(Entity entity);

    // The part's current value, or null to skip it this time (not ready, too large...).
    public abstract object? Capture(Entity entity, byte part);

    // Encoded separately so a part type can override the MessagePack type it is decoded as.
    public abstract byte[] Encode(byte part, object value);

    // Theirs

    // Decodes and checks one part from another player. Null means it's dropped.
    public abstract object? Decode(byte part, byte[] compressed);

    // Whether the remote item has enough parts to be shown.
    public virtual bool CanShow(RemoteItem item) => item.Parts.ContainsKey(0);

    public abstract void Show(RemoteItem item, FolderEntity folder);
    public abstract void Apply(RemoteItem item);
    public abstract void Hide(RemoteItem item);
    public abstract bool IsAlive(RemoteItem item);
}

public class RemoteItem(SyncPeer owner, Guid id, SyncKind kind)
{
    public SyncPeer Owner { get; } = owner;
    public Guid Id { get; } = id;
    public SyncKind Kind { get; } = kind;
    public uint Territory { get; set; }

    public Dictionary<byte, object> Parts { get; } = [];
    public HashSet<byte> Dirty { get; } = [];

    public Entity? Entity { get; set; }
    public bool Spawning { get; set; }
    public DateTime SpawnStarted { get; set; }
    public DateTime NextAttempt { get; set; }

    // Set by a kind while it has work in flight (an actor loading its appearance) so updates wait.
    public bool Busy { get; set; }

    // Kind-specific bookkeeping, like what an actor was spawned with.
    public object? State { get; set; }

    // Bumped on every spawn and hide, so a spawn that finishes late can tell it's no longer wanted.
    public int Generation { get; set; }
    public bool Removed { get; set; }

    public bool IsCurrent(int generation) => !Removed && Generation == generation;

    public void MarkAllDirty()
    {
        foreach(var part in Parts.Keys)
            Dirty.Add(part);
    }
}
