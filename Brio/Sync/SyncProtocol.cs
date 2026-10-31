using MessagePack;
using System;
using System.Collections.Generic;

namespace Brio.Sync;

public enum SyncEntityType : byte
{
    Light = 1,
    Actor = 2,
    WorldObject = 3,
    Camera = 4,
    ReferenceImage = 5,
}

public enum SyncOpKind : byte
{
    Upsert = 1,
    Remove = 2,

    // Carries a digest of what we have, asking a player for only what differs.
    Repair = 3,
}

// One chunk of one part of one shared item. An item is sent as one or more parts (an actor's
// appearance and its pose, say), so a part that changes often doesn't resend the others. Each
// part is MessagePack, Brotli-compressed, then split into chunks small enough for the relay.
// A Remove op removes the whole item and carries no data.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncOp
{
    public SyncOpKind Kind { get; set; }
    public Guid SyncId { get; set; }
    public SyncEntityType Type { get; set; }
    public byte Part { get; set; }
    public uint Territory { get; set; }

    // Bumped each time the part changes, so chunks of an older version are never mixed in.
    public uint Revision { get; set; }
    public ushort Chunk { get; set; }
    public ushort ChunkCount { get; set; }
    public byte[]? Data { get; set; }

    // An answer to a Repair. Not a new edit, so it doesn't compete on revision.
    public bool IsRepair { get; set; }
}

// What travels in one relay "op" message.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncBatch
{
    public const int CurrentVersion = 5;

    public int Version { get; set; } = CurrentVersion;
    public List<SyncOp> Ops { get; set; } = [];
}

public static class SyncLimits
{
    public const int MaxOpsPerBatch = 24;

    // Compressed bytes per chunk. A full batch stays under ~12 KB of base64, inside the relay's 16 KB.
    public const int ChunkBytes = 9000;
    public const int MaxBatchBase64Chars = 15 * 1024;

    // Bytes a part may take once reassembled, before and after decompression.
    public const int MaxPartBytes = 512 * 1024;
    public const int MaxImagePartBytes = 2 * 1024 * 1024 + 64 * 1024;
    public const int MaxPendingBytesPerPeer = 6 * 1024 * 1024;
    public static readonly TimeSpan PendingTimeout = TimeSpan.FromSeconds(30);

    public const int MaxItemsPerPlayer = 48;
    public const int MaxLightsPerPlayer = 32;
    public const int MaxActorsPerPlayer = 4;
    public const int MaxWorldObjectsPerPlayer = 32;
    public const int MaxCamerasPerPlayer = 4;
    public const int MaxReferenceImagesPerPlayer = 4;

    public const int MaxImageBytes = 2 * 1024 * 1024;

    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(100);

    // The relay allows 30 messages a second with a burst of 60; stay under it. Scene updates and
    // file transfers share this.
    public const double MessagesPerSecond = 25;
    public const double MessageBurst = 40;
}
