using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Brio.Sync.Mods;

// The mod files an actor uses on the owner's PC, by content. The files themselves are fetched
// from the owner separately and only when we don't have them yet.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncActorMods
{
    public List<SyncModFile> Files { get; set; } = [];
    public List<SyncModSwap> Swaps { get; set; } = [];
    public string Meta { get; set; } = "";
    public string? CustomizePlus { get; set; }

    public long TotalBytes => Files.Sum(f => f.Size);
}

[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncModFile
{
    // SHA-256 of the file, lowercase hex.
    public string Hash { get; set; } = "";
    public long Size { get; set; }
    public List<string> GamePaths { get; set; } = [];
}

// A game path that's redirected to another game file rather than a mod file.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncModSwap
{
    public string GamePath { get; set; } = "";
    public string SwapPath { get; set; } = "";
}

public static class SyncModLimits
{
    public const long MaxFileBytes = 256L * 1024 * 1024;
    public const long MaxMcdfBytes = 2048L * 1024 * 1024;
    public const long MaxActorBytes = 1536L * 1024 * 1024;
    public const int MaxFiles = 4096;
    public const int MaxGamePathsPerFile = 64;
    public const int MaxMetaLength = 1024 * 1024;
    public const int MaxCustomizePlusLength = 64 * 1024;

    public const long MaxCacheBytes = 10L * 1024 * 1024 * 1024;

    // Data bytes per binary chunk; the frame stays under the relay's 256 KB.
    public const int ChunkBytes = 240 * 1024;

    // How much one download keeps requested at once, per owner.
    public const long MaxBytesInFlight = 48L * 1024 * 1024;
    public const int MaxHashesPerRequest = 256;

    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(30);
    public const int MaxAttempts = 3;
    public static readonly TimeSpan RetryFailedAfter = TimeSpan.FromMinutes(1);

    public static readonly string[] AllowedExtensions =
        [".mdl", ".tex", ".mtrl", ".sklb", ".skp", ".pbd", ".phyb", ".pap", ".tmb", ".avfx", ".atex", ".eid", ".scd", ".shpk"];
}

// Binary messages for file transfer. First byte says which.
public enum SyncModMessage : byte
{
    // Payload: MessagePack list of hashes the sender wants.
    Request = 1,

    // Payload: 32-byte hash, 8-byte offset, 8-byte total size, then file data.
    Chunk = 2,

    // Payload: 32-byte hash the owner can't send.
    Missing = 3,
}
