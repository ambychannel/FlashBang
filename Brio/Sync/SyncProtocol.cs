using MessagePack;
using System;
using System.Collections.Generic;

namespace Brio.Sync;

public enum SyncEntityType : byte
{
    Light = 1,
}

public enum SyncOpKind : byte
{
    Upsert = 1,
    Remove = 2,
}

// One change to one shared item. Payload is the item's existing Brio DTO (e.g. LightDTO)
// serialized with MessagePack, so scene files and sync share one format.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncOp
{
    public SyncOpKind Kind { get; set; }
    public Guid SyncId { get; set; }
    public SyncEntityType Type { get; set; }
    public uint Territory { get; set; }
    public byte[]? Payload { get; set; }
}

// What travels in one relay "op" message: everything that changed since the last send.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncBatch
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SyncOp> Ops { get; set; } = [];
}

public static class SyncLimits
{
    public const int MaxItemsPerPlayer = 32;
    public const int MaxOpsPerBatch = 24;
    public const int MaxPayloadBytes = 2048;
    public const int MaxBatchBase64Chars = 15 * 1024;

    public static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(100);
}
