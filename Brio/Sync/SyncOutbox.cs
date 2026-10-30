using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Brio.Sync;

// Queues outgoing ops and sends them at a steady rate the relay accepts. Small, live updates go
// before bulk transfers (a reference image, an actor joining), and a newer version of a part
// replaces whatever older version is still waiting.
public class SyncOutbox
{
    private record Entry(SyncOp Op, string? To);

    private readonly LinkedList<Entry> _live = [];
    private readonly LinkedList<Entry> _bulk = [];

    public int Waiting => _live.Count + _bulk.Count;

    public void Enqueue(List<SyncOp> ops, string? to)
    {
        if(ops.Count == 0)
            return;

        var first = ops[0];
        Drop(first.SyncId, first.Kind == SyncOpKind.Remove ? null : first.Part, to);

        var queue = ops.Count > 2 ? _bulk : _live;
        foreach(var op in ops)
            queue.AddLast(new Entry(op, to));
    }

    public void Clear()
    {
        _live.Clear();
        _bulk.Clear();
    }

    // Drops queued ops for an item (or one of its parts) that a newer op makes pointless.
    private void Drop(Guid id, byte? part, string? to)
    {
        foreach(var queue in new[] { _live, _bulk })
        {
            var node = queue.First;
            while(node is not null)
            {
                var next = node.Next;
                var entry = node.Value;
                if(entry.Op.SyncId == id && (part is null || entry.Op.Part == part) && (to is null || entry.To == to))
                    queue.Remove(node);
                node = next;
            }
        }
    }

    // Packs queued ops into relay messages, as many as the rate allows right now.
    public List<(string Data, string? To)> Take(SyncRateLimiter limiter)
    {
        var messages = new List<(string, string?)>();

        while((_live.Count > 0 || _bulk.Count > 0) && limiter.TryTake())
        {
            var queue = _live.Count > 0 ? _live : _bulk;
            var to = queue.First!.Value.To;

            var batch = new SyncBatch();
            var bytes = 0;
            while(queue.First is { } node && node.Value.To == to && batch.Ops.Count < SyncLimits.MaxOpsPerBatch)
            {
                var size = node.Value.Op.Data?.Length ?? 0;
                if(batch.Ops.Count > 0 && bytes + size > SyncLimits.ChunkBytes)
                    break;

                batch.Ops.Add(node.Value.Op);
                bytes += size;
                queue.RemoveFirst();
            }

            var data = Convert.ToBase64String(MessagePackSerializer.Serialize(batch));
            if(data.Length > SyncLimits.MaxBatchBase64Chars)
            {
                Brio.Log.Warning("FlashBang: an update was too large to send and was skipped.");
                continue;
            }

            messages.Add((data, to));
        }

        return messages;
    }
}
