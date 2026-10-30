using MessagePack;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Brio.Sync;

// Turns item parts into chunked ops and back.
public static class SyncCodec
{
    public static readonly MessagePackSerializerOptions UntrustedOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    public static byte[] Encode<T>(T value)
    {
        var raw = MessagePackSerializer.Serialize(value);

        using var output = new MemoryStream();
        using(var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
            brotli.Write(raw);

        return output.ToArray();
    }

    // Decompresses at most maxBytes, so a small message can't expand into something huge.
    public static T? Decode<T>(byte[] compressed, int maxBytes) where T : class
    {
        try
        {
            using var input = new MemoryStream(compressed);
            using var brotli = new BrotliStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();

            var buffer = new byte[16 * 1024];
            int read;
            while((read = brotli.Read(buffer, 0, buffer.Length)) > 0)
            {
                if(output.Length + read > maxBytes)
                    return null;
                output.Write(buffer, 0, read);
            }

            return MessagePackSerializer.Deserialize<T>(output.ToArray(), UntrustedOptions);
        }
        catch(Exception)
        {
            return null;
        }
    }

    public static List<SyncOp> Split(Guid id, SyncEntityType type, byte part, uint territory, uint revision, byte[] data)
    {
        var count = Math.Max(1, (data.Length + SyncLimits.ChunkBytes - 1) / SyncLimits.ChunkBytes);
        var ops = new List<SyncOp>(count);

        for(int i = 0; i < count; i++)
        {
            var start = i * SyncLimits.ChunkBytes;
            var length = Math.Min(SyncLimits.ChunkBytes, data.Length - start);

            ops.Add(new SyncOp
            {
                Kind = SyncOpKind.Upsert,
                SyncId = id,
                Type = type,
                Part = part,
                Territory = territory,
                Revision = revision,
                Chunk = (ushort)i,
                ChunkCount = (ushort)count,
                Data = data.AsSpan(start, length).ToArray(),
            });
        }

        return ops;
    }
}

// Collects chunks from one player until a part is complete. Incomplete parts are dropped
// when they get too old or the player has too much in flight.
public class SyncReassembler
{
    private class Pending(SyncOp first)
    {
        public uint Revision { get; } = first.Revision;
        public byte[]?[] Chunks { get; } = new byte[first.ChunkCount][];
        public int Received { get; set; }
        public int Bytes { get; set; }
        public DateTime Started { get; } = DateTime.UtcNow;
    }

    private readonly Dictionary<(Guid Id, byte Part), Pending> _pending = [];

    public static int MaxPartBytes(SyncEntityType type)
        => type == SyncEntityType.ReferenceImage ? SyncLimits.MaxImagePartBytes : SyncLimits.MaxPartBytes;

    // Returns the whole compressed part once its last chunk arrives.
    public byte[]? Add(SyncOp op)
    {
        if(op.Data is null || op.ChunkCount == 0 || op.Chunk >= op.ChunkCount)
            return null;

        var maxBytes = MaxPartBytes(op.Type);
        if((long)op.ChunkCount * SyncLimits.ChunkBytes > maxBytes + SyncLimits.ChunkBytes || op.Data.Length > SyncLimits.ChunkBytes)
            return null;

        if(op.ChunkCount == 1)
        {
            _pending.Remove((op.SyncId, op.Part));
            return op.Data;
        }

        var key = (op.SyncId, op.Part);
        if(!_pending.TryGetValue(key, out var pending) || pending.Revision != op.Revision || pending.Chunks.Length != op.ChunkCount)
        {
            _pending[key] = pending = new Pending(op);
            Trim();
        }

        if(pending.Chunks[op.Chunk] is not null)
            return null;

        pending.Chunks[op.Chunk] = op.Data;
        pending.Received++;
        pending.Bytes += op.Data.Length;

        if(pending.Bytes > maxBytes)
        {
            _pending.Remove(key);
            return null;
        }

        if(pending.Received < pending.Chunks.Length)
            return null;

        _pending.Remove(key);

        var whole = new byte[pending.Bytes];
        var offset = 0;
        foreach(var chunk in pending.Chunks)
        {
            chunk!.CopyTo(whole, offset);
            offset += chunk.Length;
        }
        return whole;
    }

    public void Forget(Guid id)
    {
        foreach(var key in _pending.Keys.Where(k => k.Id == id).ToList())
            _pending.Remove(key);
    }

    public void Clear() => _pending.Clear();

    private void Trim()
    {
        var now = DateTime.UtcNow;
        foreach(var (key, pending) in _pending.ToList())
        {
            if(now - pending.Started > SyncLimits.PendingTimeout)
                _pending.Remove(key);
        }

        while(_pending.Values.Sum(p => p.Bytes) > SyncLimits.MaxPendingBytesPerPeer && _pending.Count > 0)
            _pending.Remove(_pending.MinBy(kv => kv.Value.Started).Key);
    }
}
