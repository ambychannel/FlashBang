using MessagePack;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Brio.Sync.Mods;

public record SyncModProgress(long HaveBytes, long TotalBytes, bool Done, int FailedFiles);

// Moves mod files between linked players over the relay's binary frames. We ask an actor's
// owner for the files we're missing, write them to the cache once their hash checks out, and
// send our own files to players who ask. Called from the framework thread (Want, Tick, peer
// changes), the relay's receive thread (OnBinary) and its own sending task.
public class SyncModTransfer(SyncModCache cache, SyncModSource source)
{
    private enum DownloadState { Waiting, Requested, Done, Failed }

    private class Download(string hash, string extension, string owner, long size)
    {
        public string Hash { get; } = hash;
        public string Extension { get; } = extension;
        public string Owner { get; } = owner;
        public long Size { get; } = size;

        public DownloadState State { get; set; }
        public long Received { get; set; }
        public FileStream? Stream { get; set; }
        public DateTime LastActivity { get; set; } = DateTime.UtcNow;
        public int Attempts { get; set; }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Download> _downloads = [];
    private readonly HashSet<string> _linked = [];

    // What linked players asked us for: (peer, hash, or null hash meaning "tell them it's missing").
    private readonly LinkedList<(string Peer, string Hash, bool Missing)> _toServe = [];
    private readonly SemaphoreSlim _serveSignal = new(0);

    private SyncRelayClient? _client;
    private CancellationTokenSource? _cts;

    // Framework thread

    public void Attach(SyncRelayClient client)
    {
        Detach();

        lock(_lock)
            _client = client;

        client.OnBinary = OnBinary;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ServeLoop(client, token));
    }

    public void Detach()
    {
        _cts?.Cancel();
        _cts = null;

        lock(_lock)
        {
            _client = null;
            _linked.Clear();
            _toServe.Clear();
            foreach(var download in _downloads.Values)
                Abort(download);
            _downloads.Clear();
        }
    }

    public void SetLinked(IEnumerable<string> peers)
    {
        lock(_lock)
        {
            _linked.Clear();
            _linked.UnionWith(peers);

            for(var node = _toServe.First; node is not null;)
            {
                var next = node.Next;
                if(!_linked.Contains(node.Value.Peer))
                    _toServe.Remove(node);
                node = next;
            }

            foreach(var download in _downloads.Values.Where(d => !_linked.Contains(d.Owner)).ToList())
            {
                Abort(download);
                _downloads.Remove(download.Hash);
            }
        }
    }

    // Registers the files an actor needs and says how far along they are.
    public SyncModProgress Want(string owner, SyncActorMods mods)
    {
        long have = 0;
        var failed = 0;
        var done = true;

        lock(_lock)
        {
            foreach(var file in mods.Files)
            {
                if(cache.TryGet(file.Hash, out _))
                {
                    have += file.Size;
                    continue;
                }

                if(!_downloads.TryGetValue(file.Hash, out var download) || (download.State == DownloadState.Failed && download.Owner != owner))
                {
                    _downloads[file.Hash] = download = new Download(file.Hash, Path.GetExtension(file.GamePaths[0]), owner, file.Size);
                }

                switch(download.State)
                {
                    case DownloadState.Failed:
                        failed++;
                        break;
                    case DownloadState.Done:
                        // Finished but evicted since; fetch again.
                        download.State = DownloadState.Waiting;
                        done = false;
                        break;
                    default:
                        have += download.Received;
                        done = false;
                        break;
                }
            }
        }

        return new SyncModProgress(have, mods.TotalBytes, done, failed);
    }

    // Sends requests for waiting files and retries stalled ones.
    public void Tick()
    {
        List<(string Owner, List<string> Hashes)> requests = [];
        SyncRelayClient? client;

        lock(_lock)
        {
            client = _client;
            if(client is null)
                return;

            var now = DateTime.UtcNow;
            foreach(var download in _downloads.Values)
            {
                if(download.State == DownloadState.Requested && now - download.LastActivity > SyncModLimits.StallTimeout)
                    Retry(download);
            }

            foreach(var group in _downloads.Values.GroupBy(d => d.Owner))
            {
                var inFlight = group.Where(d => d.State == DownloadState.Requested).Sum(d => d.Size - d.Received);
                var batch = new List<string>();

                foreach(var download in group.Where(d => d.State == DownloadState.Waiting))
                {
                    if(batch.Count >= SyncModLimits.MaxHashesPerRequest || (inFlight > 0 && inFlight + download.Size > SyncModLimits.MaxBytesInFlight))
                        break;

                    batch.Add(download.Hash);
                    inFlight += download.Size;
                }

                if(batch.Count > 0)
                    requests.Add((group.Key, batch));
            }

            foreach(var (owner, hashes) in requests.ToList())
            {
                if(!client.Limiter.TryTake())
                {
                    requests.Remove((owner, hashes));
                    continue;
                }

                foreach(var hash in hashes)
                {
                    var download = _downloads[hash];
                    download.State = DownloadState.Requested;
                    download.LastActivity = now;
                }
            }
        }

        foreach(var (owner, hashes) in requests)
        {
            var list = MessagePackSerializer.Serialize(hashes);
            var payload = new byte[1 + list.Length];
            payload[0] = (byte)SyncModMessage.Request;
            list.CopyTo(payload, 1);
            _ = client.SendBinaryAsync(owner, payload);
        }
    }

    // Hashes of files any current download or the given actors need, so the cache keeps them.
    public HashSet<string> InUse()
    {
        lock(_lock)
            return [.. _downloads.Keys];
    }

    // Receive thread

    private void OnBinary(string from, ReadOnlyMemory<byte> payload)
    {
        if(payload.Length < 1)
            return;

        var body = payload[1..];
        switch((SyncModMessage)payload.Span[0])
        {
            case SyncModMessage.Request:
                OnRequest(from, body);
                break;
            case SyncModMessage.Chunk:
                OnChunk(from, body.Span);
                break;
            case SyncModMessage.Missing:
                if(body.Length == 32)
                {
                    lock(_lock)
                    {
                        if(_downloads.TryGetValue(Convert.ToHexString(body.Span).ToLowerInvariant(), out var download) && download.Owner == from)
                        {
                            Abort(download);
                            download.State = DownloadState.Failed;
                        }
                    }
                }
                break;
        }
    }

    private void OnRequest(string from, ReadOnlyMemory<byte> body)
    {
        List<string> hashes;
        try
        {
            hashes = MessagePackSerializer.Deserialize<List<string>>(body, SyncCodec.UntrustedOptions);
        }
        catch(Exception)
        {
            return;
        }

        lock(_lock)
        {
            if(!_linked.Contains(from))
                return;

            foreach(var hash in hashes.Take(SyncModLimits.MaxHashesPerRequest))
            {
                if(!SyncValidation.IsHash(hash) || _toServe.Count >= SyncModLimits.MaxFiles)
                    continue;
                if(_toServe.Any(e => e.Peer == from && e.Hash == hash))
                    continue;

                _toServe.AddLast((from, hash, !source.TryGetPath(hash, out _)));
            }
        }

        _serveSignal.Release();
    }

    private void OnChunk(string from, ReadOnlySpan<byte> body)
    {
        if(body.Length < 48)
            return;

        var hash = Convert.ToHexString(body[..32]).ToLowerInvariant();
        var offset = BinaryPrimitives.ReadInt64BigEndian(body[32..40]);
        var total = BinaryPrimitives.ReadInt64BigEndian(body[40..48]);
        var data = body[48..];

        Download? download;
        FileStream? finished = null;

        lock(_lock)
        {
            if(!_downloads.TryGetValue(hash, out download) || download.Owner != from || download.State != DownloadState.Requested || total != download.Size)
                return;

            // A resend after a retry starts over.
            if(offset == 0 && download.Received > 0)
                Abort(download);

            if(offset != download.Received || download.Received + data.Length > download.Size)
                return;

            try
            {
                download.Stream ??= new FileStream(cache.TempPathFor(hash), FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                download.Stream.Write(data);
                download.Received += data.Length;
                download.LastActivity = DateTime.UtcNow;

                if(download.Received < download.Size)
                    return;

                // Complete. Check it outside the lock so the game never waits on hashing.
                finished = download.Stream;
                download.Stream = null;
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "FlashBang: couldn't save a downloaded mod file.");
                Retry(download);
                return;
            }
        }

        string actual;
        try
        {
            finished.Position = 0;
            actual = Convert.ToHexString(SHA256.HashData(finished)).ToLowerInvariant();
        }
        catch(Exception)
        {
            actual = "";
        }
        finally
        {
            finished.Dispose();
        }

        lock(_lock)
        {
            if(!_downloads.TryGetValue(hash, out var current) || current != download)
            {
                TryDelete(cache.TempPathFor(hash));
                return;
            }

            if(actual != hash)
            {
                Brio.Log.Warning("FlashBang: a mod file from another player didn't match its hash and was dropped.");
                TryDelete(cache.TempPathFor(hash));
                Retry(download);
                return;
            }

            try
            {
                cache.Add(hash, download.Extension, cache.TempPathFor(hash), download.Size);
                _downloads.Remove(hash);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "FlashBang: couldn't save a downloaded mod file.");
                TryDelete(cache.TempPathFor(hash));
                Retry(download);
            }
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
        }
    }

    // Must hold _lock.
    private void Retry(Download download)
    {
        Abort(download);
        download.Attempts++;
        download.State = download.Attempts >= SyncModLimits.MaxAttempts ? DownloadState.Failed : DownloadState.Waiting;
    }

    // Must hold _lock.
    private void Abort(Download download)
    {
        download.Received = 0;
        if(download.Stream is not null)
        {
            try
            {
                download.Stream.Dispose();
                File.Delete(cache.TempPathFor(download.Hash));
            }
            catch(Exception)
            {
            }
            download.Stream = null;
        }
    }

    // Sending task

    private async Task ServeLoop(SyncRelayClient client, CancellationToken token)
    {
        var buffer = new byte[1 + 48 + SyncModLimits.ChunkBytes];

        while(!token.IsCancellationRequested)
        {
            (string Peer, string Hash, bool Missing) next;
            lock(_lock)
            {
                if(_toServe.First is { } node)
                {
                    next = node.Value;
                    _toServe.RemoveFirst();
                }
                else
                {
                    next = default;
                }
            }

            if(next.Peer is null)
            {
                try
                {
                    await _serveSignal.WaitAsync(TimeSpan.FromSeconds(1), token);
                }
                catch(OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                if(next.Missing || !source.TryGetPath(next.Hash, out var path))
                {
                    var missing = new byte[33];
                    missing[0] = (byte)SyncModMessage.Missing;
                    Convert.FromHexString(next.Hash).CopyTo(missing, 1);
                    await WaitForBudget(client, token);
                    await client.SendBinaryAsync(next.Peer, missing);
                    continue;
                }

                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var total = stream.Length;
                long offset = 0;

                buffer[0] = (byte)SyncModMessage.Chunk;
                Convert.FromHexString(next.Hash).CopyTo(buffer, 1);
                BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(41, 8), total);

                while(offset < total)
                {
                    if(!IsLinked(next.Peer))
                        break;

                    var read = await stream.ReadAsync(buffer.AsMemory(49, (int)Math.Min(SyncModLimits.ChunkBytes, total - offset)), token);
                    if(read == 0)
                        break;

                    BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(33, 8), offset);
                    await WaitForBudget(client, token);
                    await client.SendBinaryAsync(next.Peer, buffer.AsMemory(0, 49 + read));
                    offset += read;
                }
            }
            catch(OperationCanceledException)
            {
                return;
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "FlashBang: couldn't send a mod file.");
            }
        }
    }

    private bool IsLinked(string peer)
    {
        lock(_lock)
            return _linked.Contains(peer);
    }

    // File chunks leave a few messages a second free for scene updates.
    private static async Task WaitForBudget(SyncRelayClient client, CancellationToken token)
    {
        while(!client.Limiter.TryTake(keep: 8))
            await Task.Delay(20, token);
    }
}
