using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Brio.Sync.Mods;

// Mod files downloaded from other players, stored as <hash><extension> in FlashBang's cache
// folder. Nothing a sender says is ever used as a path here; only the hash (checked to be hex)
// and an extension from the allowed list. Thread-safe.
public class SyncModCache
{
    private readonly object _lock = new();
    private readonly string _folder;
    private readonly Dictionary<string, (string Path, long Size, DateTime LastUsed)> _files = [];
    private long _totalBytes;
    private bool _scanned;

    public SyncModCache(IDalamudPluginInterface pluginInterface)
    {
        _folder = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "cache");
    }

    public string Folder => _folder;

    public long TotalBytes
    {
        get
        {
            lock(_lock)
            {
                Scan();
                return _totalBytes;
            }
        }
    }

    // Whether we have a file, without touching the disk, for checks that run often.
    public bool Has(string hash)
    {
        lock(_lock)
        {
            Scan();
            return _files.ContainsKey(hash);
        }
    }

    public bool TryGet(string hash, out string path)
    {
        lock(_lock)
        {
            Scan();
            if(_files.TryGetValue(hash, out var entry) && File.Exists(entry.Path))
            {
                _files[hash] = entry with { LastUsed = DateTime.UtcNow };
                path = entry.Path;
                return true;
            }

            if(_files.Remove(hash, out var gone))
                _totalBytes -= gone.Size;

            path = "";
            return false;
        }
    }

    public string TempPathFor(string hash)
    {
        Directory.CreateDirectory(_folder);
        return Path.Combine(_folder, $"{hash}.part");
    }

    // Moves a finished, verified download into place.
    public string Add(string hash, string extension, string tempPath, long size)
    {
        var finalPath = Path.Combine(_folder, hash + extension);
        File.Move(tempPath, finalPath, true);

        lock(_lock)
        {
            Scan();
            if(_files.Remove(hash, out var old))
                _totalBytes -= old.Size;
            _files[hash] = (finalPath, size, DateTime.UtcNow);
            _totalBytes += size;
        }

        return finalPath;
    }

    // Keeps a copy of a file we already have, under its hash. Not on the framework thread.
    public string AddCopy(string hash, string extension, string sourcePath, long size)
    {
        var temp = TempPathFor(hash);
        File.Copy(sourcePath, temp, true);
        return Add(hash, extension, temp, size);
    }

    // Drops the least recently used files until the cache fits, keeping anything in use.
    public void Trim(IReadOnlySet<string> inUse)
    {
        lock(_lock)
        {
            Scan();
            if(_totalBytes <= SyncModLimits.MaxCacheBytes)
                return;

            foreach(var (hash, entry) in _files.OrderBy(kv => kv.Value.LastUsed).ToList())
            {
                if(_totalBytes <= SyncModLimits.MaxCacheBytes)
                    break;
                if(inUse.Contains(hash))
                    continue;

                TryDelete(entry.Path);
                _files.Remove(hash);
                _totalBytes -= entry.Size;
            }
        }
    }

    public void Clear(IReadOnlySet<string> inUse)
    {
        lock(_lock)
        {
            Scan();
            foreach(var (hash, entry) in _files.ToList())
            {
                if(inUse.Contains(hash))
                    continue;

                TryDelete(entry.Path);
                _files.Remove(hash);
                _totalBytes -= entry.Size;
            }

            if(Directory.Exists(_folder))
            {
                foreach(var part in Directory.EnumerateFiles(_folder, "*.part"))
                    TryDelete(part);
            }
        }
    }

    private void Scan()
    {
        if(_scanned)
            return;
        _scanned = true;

        if(!Directory.Exists(_folder))
            return;

        foreach(var file in new DirectoryInfo(_folder).EnumerateFiles())
        {
            var hash = Path.GetFileNameWithoutExtension(file.Name);
            if(file.Extension == ".part" || !SyncValidation.IsHash(hash) || (file.Extension != ".mcdf" && !SyncModLimits.AllowedExtensions.Contains(file.Extension)))
            {
                TryDelete(file.FullName);
                continue;
            }

            _files[hash] = (file.FullName, file.Length, file.LastWriteTimeUtc);
            _totalBytes += file.Length;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch(Exception)
        {
            // In use by the game; it goes next time.
        }
    }
}
