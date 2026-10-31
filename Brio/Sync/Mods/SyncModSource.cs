using Brio.Entities.Actor;
using Brio.Entities.Core;
using Brio.IPC;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Brio.Sync.Mods;

// Our side: works out which mod files our shared actors use (through Penumbra) and lets the
// transfer serve exactly those files, and nothing else, to linked players.
public class SyncModSource(PenumbraService penumbraService, CustomizePlusService customizePlusService)
{
    private static readonly TimeSpan RebuildInterval = TimeSpan.FromSeconds(30);

    private class Entry
    {
        public DateTime NextBuild { get; set; }
        public Task? Building { get; set; }
        public SyncActorMods? Latest { get; set; }
        public Dictionary<string, string> Paths { get; set; } = [];
    }

    private readonly Dictionary<EntityId, Entry> _actors = [];
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, string Hash)> _hashes = new(StringComparer.OrdinalIgnoreCase);

    // hash -> local file, for everything we currently share. Read from the transfer thread.
    private volatile Dictionary<string, string> _served = [];

    // Whole MCDF files in use, also offered to linked players by hash.
    private readonly Dictionary<string, string> _extraFiles = [];

    public bool IsAvailable => penumbraService.IsAvailable;

    // Framework thread. Returns the latest known list, rebuilding it in the background now and then.
    public SyncActorMods? Get(ActorEntity actor)
    {
        if(!penumbraService.IsAvailable)
            return null;

        Entry? entry;
        lock(_actors)
        {
            if(!_actors.TryGetValue(actor.Id, out entry))
                _actors[actor.Id] = entry = new Entry();
        }

        if(entry.Building is null && DateTime.UtcNow >= entry.NextBuild)
        {
            entry.NextBuild = DateTime.UtcNow + RebuildInterval;
            Start(actor, entry);
        }

        return entry.Latest;
    }

    // Framework thread. Reads the actor's mods again on the next Get (Sync now, or its look changed).
    public void Refresh(ActorEntity actor)
    {
        lock(_actors)
        {
            if(_actors.TryGetValue(actor.Id, out var entry))
                entry.NextBuild = DateTime.MinValue;
        }
    }

    // Framework thread. Forgets actors we no longer share.
    public void Keep(IReadOnlySet<EntityId> shared)
    {
        var removed = false;
        lock(_actors)
        {
            foreach(var id in _actors.Keys.Where(id => !shared.Contains(id)).ToList())
            {
                _actors.Remove(id);
                removed = true;
            }
        }

        if(removed)
            RebuildServed();
    }

    public bool TryGetPath(string hash, out string path) => _served.TryGetValue(hash, out path!);

    public void Offer(string hash, string path)
    {
        lock(_actors)
            _extraFiles[hash] = path;
        RebuildServed();
    }

    private void Start(ActorEntity actor, Entry entry)
    {
        Dictionary<string, HashSet<string>>? resources;
        string meta;
        string? customizePlus = null;

        try
        {
            // Already on the framework thread, so these run inline.
            resources = penumbraService.GetCharacterData(actor.GameObject).GetAwaiter().GetResult();
            meta = penumbraService.GetMetaManipulations(actor.GameObject.ObjectIndex);

            if(customizePlusService.IsAvailable)
            {
                var (profile, _) = customizePlusService.GetActiveProfile(actor.GameObject);
                if(!string.IsNullOrEmpty(profile) && profile.Length <= SyncModLimits.MaxCustomizePlusLength)
                    customizePlus = profile;
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read the mods on {actor.FriendlyName}.");
            return;
        }

        if(resources is null)
            return;

        var name = actor.FriendlyName;
        entry.Building = Task.Run(() =>
        {
            try
            {
                var (mods, paths) = Build(resources, meta, customizePlus, name);
                entry.Latest = mods;
                entry.Paths = paths;
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't list the mods on {name}.");
            }
        }).ContinueWith(_ =>
        {
            entry.Building = null;
            RebuildServed();
        });
    }

    private (SyncActorMods, Dictionary<string, string>) Build(Dictionary<string, HashSet<string>> resources, string meta, string? customizePlus, string name)
    {
        var mods = new SyncActorMods
        {
            Meta = meta.Length <= SyncModLimits.MaxMetaLength ? meta : "",
            CustomizePlus = customizePlus,
        };
        var paths = new Dictionary<string, string>();
        var byHash = new Dictionary<string, SyncModFile>();
        long total = 0;
        var skipped = 0;

        // Penumbra gives local file (or redirected game path) -> the game paths it replaces.
        foreach(var (resolved, gamePathSet) in resources.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var gamePaths = gamePathSet
                .Select(p => p.Replace('\\', '/').ToLowerInvariant())
                .Where(SyncValidation.IsModGamePath)
                .Distinct()
                .Take(SyncModLimits.MaxGamePathsPerFile)
                .ToList();
            if(gamePaths.Count == 0)
                continue;

            if(Path.IsPathRooted(resolved))
            {
                var info = new FileInfo(resolved);
                if(!info.Exists || info.Length == 0 || info.Length > SyncModLimits.MaxFileBytes || total + info.Length > SyncModLimits.MaxActorBytes || byHash.Count >= SyncModLimits.MaxFiles)
                {
                    skipped++;
                    continue;
                }

                var hash = HashOf(info);
                if(byHash.TryGetValue(hash, out var existing))
                {
                    existing.GamePaths = [.. existing.GamePaths.Union(gamePaths).Take(SyncModLimits.MaxGamePathsPerFile)];
                    continue;
                }

                var file = new SyncModFile { Hash = hash, Size = info.Length, GamePaths = gamePaths };
                byHash[hash] = file;
                mods.Files.Add(file);
                paths[hash] = info.FullName;
                total += info.Length;
            }
            else
            {
                // Redirected to another game file, not a mod file.
                var swapTo = resolved.Replace('\\', '/').ToLowerInvariant();
                if(!SyncValidation.IsModGamePath(swapTo))
                    continue;

                foreach(var gamePath in gamePaths.Where(p => p != swapTo))
                {
                    if(mods.Swaps.Count < SyncModLimits.MaxFiles)
                        mods.Swaps.Add(new SyncModSwap { GamePath = gamePath, SwapPath = swapTo });
                }
            }
        }

        if(skipped > 0)
            Brio.Log.Warning($"FlashBang: {skipped} mod files on {name} are too large to share and were left out.");

        return (mods, paths);
    }

    private string HashOf(FileInfo info)
    {
        if(_hashes.TryGetValue(info.FullName, out var cached) && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc)
            return cached.Hash;

        using var stream = info.OpenRead();
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashes[info.FullName] = (info.Length, info.LastWriteTimeUtc, hash);
        return hash;
    }

    private void RebuildServed()
    {
        var served = new Dictionary<string, string>();
        List<Entry> entries;
        lock(_actors)
        {
            entries = [.. _actors.Values];
            foreach(var (hash, path) in _extraFiles)
                served[hash] = path;
        }

        foreach(var entry in entries)
        {
            foreach(var (hash, path) in entry.Paths)
                served[hash] = path;
        }
        _served = served;
    }
}
