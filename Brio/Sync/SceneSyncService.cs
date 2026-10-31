using Brio.Config;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Game.GPose;
using Brio.Services;
using Brio.Services.MediatorMessages;
using Brio.Sync.Kinds;
using Brio.Sync.Mods;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Brio.Sync;

public enum SyncStatus
{
    Offline,
    Connecting,
    Connected,
}

public class SyncPeer(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;

    public bool TheyAgreed { get; set; }
    public bool IAgreed { get; set; }
    public bool Linked { get; set; }

    // Set when they asked and we haven't answered yet, so the UI can prompt.
    public bool NeedsAnswer => TheyAgreed && !IAgreed;

    internal FolderEntity? Folder { get; set; }
    internal SyncReassembler Reassembler { get; } = new();
    internal bool WarnedAboutVersion { get; set; }
}

// Shares what's in the Sync folder with everyone in the room who has agreed to sync, and shows
// theirs in a sub-folder per player. Anyone syncing can edit or delete any shared item; each
// part of an item carries a Lamport revision so everyone settles on the same latest edit. Each
// kind of item (lights, actors...) is handled by a SyncKind. Runs on the framework thread;
// network I/O lives in SyncRelayClient and file transfer in SyncModTransfer.
public class SceneSyncService : MediatorSubscriberBase
{
    // After applying someone else's change, how long to wait before reading the item again
    // to take it as the new baseline.
    private static readonly TimeSpan SettleTime = TransformMotion.Duration + TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeleteWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocalChangeWins = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoverInterval = TimeSpan.FromMilliseconds(500);
    private const double SlowUpdateMs = 2;

    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly ConfigurationService _configurationService;
    private readonly IServiceProvider _serviceProvider;
    private readonly SyncModTransfer _modTransfer;
    private readonly SyncModSource _modSource;
    private readonly SyncModCache _modCache;
    private DateTime _nextCacheTrim = DateTime.MinValue;

    private SyncRelayClient? _client;
    private string _me = "";
    private readonly List<SyncPeer> _peers = [];
    private readonly SyncOutbox _outbox = new();

    private List<SyncKind>? _kinds;
    private List<SyncKind> Kinds => _kinds ??= [
        ActivatorUtilities.CreateInstance<ActorSyncKind>(_serviceProvider),
        ActivatorUtilities.CreateInstance<LightSyncKind>(_serviceProvider),
        ActivatorUtilities.CreateInstance<WorldObjectSyncKind>(_serviceProvider),
        ActivatorUtilities.CreateInstance<CameraSyncKind>(_serviceProvider),
        ActivatorUtilities.CreateInstance<ReferenceImageSyncKind>(_serviceProvider),
    ];

    // Every item in the Sync folder, ours and other players', by SyncId. Kept even when it
    // can't be shown (outside GPose, different zone) so it comes back when it can.
    private readonly Dictionary<Guid, SyncItem> _items = [];
    private DateTime _nextCapture = DateTime.MinValue;
    private DateTime _nextDiscover = DateTime.MinValue;
    private DateTime _nextSlowWarning = DateTime.MinValue;

    // Objects waiting to be torn down, one per frame.
    private readonly Queue<SyncItem> _despawns = new();
    private readonly HashSet<FolderEntity> _orphanFolders = [];

    // Entities the player just deleted from the UI. Only these count as deletes; anything else
    // that disappears (a scene load, the game dropping it) just comes back.
    private readonly Dictionary<EntityId, DateTime> _deletedByUser = [];
    private EntityId? _syncFolderId;

    public SyncStatus Status { get; private set; } = SyncStatus.Offline;
    public string? RoomCode { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<SyncPeer> Peers => _peers;
    public IEnumerable<SyncItem> Items => _items.Values;

    public bool RelayNeedsUpdate => _modTransfer.RelayRejectsFiles;

    // How long FlashBang takes per frame, for the Sync window.
    public double AverageUpdateMs { get; private set; }

    public string RelayUrl => string.IsNullOrWhiteSpace(_configurationService.Configuration.Sync.RelayUrl)
        ? SyncConfiguration.DefaultRelayUrl
        : _configurationService.Configuration.Sync.RelayUrl.Trim();
    public bool HasRelay => Uri.TryCreate(RelayUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    // Raised when someone joins or asks to sync, so the Sync window can open.
    public event Action? OnAttentionNeeded;

    public SceneSyncService(
        Mediator mediator,
        EntityManager entityManager,
        GPoseService gPoseService,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        ConfigurationService configurationService,
        IServiceProvider serviceProvider,
        SyncModTransfer modTransfer,
        SyncModSource modSource,
        SyncModCache modCache) : base(mediator)
    {
        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;
        _configurationService = configurationService;
        _serviceProvider = serviceProvider;
        _modTransfer = modTransfer;
        _modSource = modSource;
        _modCache = modCache;

        Mediator.Subscribe<FrameworkUpdateMessage>(this, _ => OnFrameworkUpdate());
        Mediator.Subscribe<EntityDeletedByUserMessage>(this, m => _deletedByUser[m.Id] = DateTime.UtcNow);
        _gPoseService.OnGPoseStateChange += OnGPoseChanged;
    }

    public static string FormatCode(string code)
        => code.Length == 12 ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;

    public static string NormalizeCode(string code)
        => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    // Room

    public void CreateRoom()
    {
        if(Status != SyncStatus.Offline || !HasRelay)
            return;

        Status = SyncStatus.Connecting;
        LastError = null;
        var relay = RelayUrl;

        Task.Run(async () =>
        {
            try
            {
                var code = await SyncRelayClient.CreateRoomAsync(relay);
                await _framework.RunOnFrameworkThread(() => Connect(code));
            }
            catch(Exception ex)
            {
                await _framework.RunOnFrameworkThread(() => Fail($"Couldn't create a room: {ex.Message}"));
            }
        });
    }

    public void JoinRoom(string rawCode)
    {
        if(Status != SyncStatus.Offline || !HasRelay)
            return;

        var code = NormalizeCode(rawCode);
        if(code.Length != 12)
        {
            LastError = "Room codes are 12 letters and numbers, like ABCD-EFGH-JKMN.";
            return;
        }

        Status = SyncStatus.Connecting;
        LastError = null;
        Connect(code);
    }

    private void Connect(string code)
    {
        var client = new SyncRelayClient();
        _client = client;
        _modTransfer.Attach(client);
        RoomCode = code;
        var relay = RelayUrl;
        var name = _objectTable.LocalPlayer?.Name.TextValue ?? "Player";

        Task.Run(async () =>
        {
            try
            {
                await client.ConnectAsync(relay, code, name);
            }
            catch(Exception ex)
            {
                await _framework.RunOnFrameworkThread(() =>
                {
                    if(_client == client)
                        Fail(ex.Message);
                });
            }
        });
    }

    public void LeaveRoom() => Disconnect(null);

    public void SetAgreement(SyncPeer peer, bool agree)
    {
        if(_client is null || Status != SyncStatus.Connected)
            return;

        peer.IAgreed = agree;
        _ = _client.SendConsentAsync(peer.Id, agree);
    }

    public SyncFolderEntity EnsureSyncFolder()
        => FindSyncFolder() ?? _entityManager.CreateEntityOnEntityContainer<SyncFolderEntity>();

    public SyncFolderEntity? FindSyncFolder()
        => Descendants(_entityManager.EntityManagerContainer).OfType<SyncFolderEntity>().FirstOrDefault();

    // Items, for the UI

    public SyncItem? FindItem(Entity entity) => _items.Values.FirstOrDefault(i => i.Entity?.Id == entity.Id);

    // True for anything inside the Sync folder, tracked or not, so it gets the sync controls.
    public bool IsInSyncFolder(Entity entity)
    {
        for(var e = entity.Parent; e is not null; e = e.Parent)
        {
            if(e is SyncFolderEntity)
                return true;
        }
        return false;
    }

    // True for a copy of another player's item (or part of one, like their actor's minion),
    // which isn't ours to save into a scene.
    public bool IsRemoteItem(Entity entity)
    {
        for(Entity? e = entity; e is not null; e = e.Parent)
        {
            var id = e.Id;
            if(_items.Values.Any(i => !i.Mine && i.Entity?.Id == id))
                return true;
        }
        return false;
    }

    public void SyncNow(SyncItem item) => item.SyncNowRequested = true;

    public void SetLive(SyncItem item, bool live)
    {
        item.Live = live;
        item.Note = null;
        if(live)
            item.SyncNowRequested = true;
    }

    // Deletes the item for everyone. Works on anything in the Sync folder, even if we lost
    // track of it.
    public void Delete(Entity entity)
    {
        var item = FindItem(entity);
        if(item is not null)
        {
            if(!item.Mine || item.Shared)
                Broadcast([new SyncOp { Kind = SyncOpKind.Remove, SyncId = item.Id, Type = item.Kind.Type }]);

            RemoveItem(item);
            _items.Remove(item.Id);
            return;
        }

        // Not tracked: tear down the object directly if we know its kind, then the entity.
        var kind = Kinds.FirstOrDefault(k => SafeCanShare(k, entity));
        if(kind is not null)
        {
            _despawns.Enqueue(new SyncItem(Guid.NewGuid(), kind, null) { Entity = entity });
            return;
        }

        try
        {
            _entityManager.DetachEntity(entity, true);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: couldn't remove a leftover entity.");
        }
    }

    // Mods

    public long DownloadedModBytes => _modCache.TotalBytes;

    public void ClearDownloadedMods() => _modCache.Clear(ModFilesInUse());

    // Mod files other players' actors use, plus anything still downloading.
    private HashSet<string> ModFilesInUse()
    {
        var inUse = _modTransfer.InUse();
        foreach(var item in _items.Values)
        {
            foreach(var part in item.Parts.Values)
            {
                if(part is SyncActorMods mods)
                    inUse.UnionWith(mods.Files.Select(f => f.Hash));
            }
        }
        return inUse;
    }

    //

    private void Fail(string message)
    {
        Disconnect(message);
        Brio.PopToast(message, "FlashBang Sync", NotificationType.Error);
    }

    private void Disconnect(string? error)
    {
        var client = _client;
        _client = null;
        _modTransfer.Detach();
        if(client is not null)
            Task.Run(() => client.Dispose());

        foreach(var peer in _peers.ToList())
            DropPeer(peer);

        _peers.Clear();
        _outbox.Clear();
        _me = "";
        Status = SyncStatus.Offline;
        RoomCode = null;
        LastError = error;

        // What we shared was shared with that room; in a new one it starts over.
        foreach(var item in _items.Values.Where(i => i.Mine))
        {
            item.Shared = false;
            foreach(var track in item.Tracks)
            {
                track.Baseline = null;
                track.LastKnown = null;
                track.Revision = 0;
                track.Writer = "";
            }
        }
    }

    private void OnFrameworkUpdate()
    {
        var started = Stopwatch.GetTimestamp();

        Update();

        var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        AverageUpdateMs += (ms - AverageUpdateMs) * 0.02;

        if(ms > SlowUpdateMs && DateTime.UtcNow >= _nextSlowWarning)
        {
            _nextSlowWarning = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            Brio.Log.Warning($"FlashBang: a sync update took {ms:F1} ms (average {AverageUpdateMs:F2} ms).");
        }
    }

    private void Update()
    {
        if(_client is not null)
        {
            while(_client is not null && _client.TryDequeue(out var message))
                HandleMessage(message);

            if(_client is not null && Status == SyncStatus.Connected && !_client.IsOpen)
                Fail(_client.CloseReason ?? "Disconnected from the relay.");
        }

        var now = DateTime.UtcNow;

        Reconcile(now);
        ProcessDespawns();
        Animate(now);

        if(now >= _nextDiscover)
        {
            _nextDiscover = now + DiscoverInterval;
            Discover();
        }

        if(now >= _nextCapture)
        {
            _nextCapture = now + SyncLimits.SendInterval;
            Capture(now);

            if(Status == SyncStatus.Connected)
                _modTransfer.Tick();
        }

        if(_client is not null && Status == SyncStatus.Connected)
        {
            foreach(var (data, to) in _outbox.Take(_client.Limiter))
                _ = _client.SendOpAsync(data, to);
        }

        if(now >= _nextCacheTrim)
        {
            _nextCacheTrim = now + TimeSpan.FromMinutes(1);
            _modCache.Trim(ModFilesInUse());
        }
    }

    // Relay messages

    private void HandleMessage(JsonObject msg)
    {
        try
        {
            switch(msg["t"]?.GetValue<string>())
            {
                case "welcome":
                    OnWelcome(msg);
                    break;
                case "joined":
                    {
                        var peer = AddPeer(msg["id"]!.GetValue<string>(), msg["name"]!.GetValue<string>());
                        Brio.PopToast($"{peer.Name} joined your FlashBang room. Open Sync to agree to sync.", "FlashBang Sync", NotificationType.Info);
                        OnAttentionNeeded?.Invoke();
                        break;
                    }
                case "left":
                    {
                        var peer = FindPeer(msg["id"]!.GetValue<string>());
                        if(peer is not null)
                        {
                            DropPeer(peer);
                            _peers.Remove(peer);
                            UpdateLinked();
                            Brio.PopToast($"{peer.Name} left the room.", "FlashBang Sync", NotificationType.Info);
                        }
                        break;
                    }
                case "consent":
                    {
                        var peer = FindPeer(msg["from"]!.GetValue<string>());
                        if(peer is null)
                            break;
                        peer.TheyAgreed = msg["accept"]!.GetValue<bool>();
                        if(peer.NeedsAnswer)
                        {
                            Brio.PopToast($"{peer.Name} wants to sync multiplayer GPose with you.", "FlashBang Sync", NotificationType.Info);
                            OnAttentionNeeded?.Invoke();
                        }
                        break;
                    }
                case "linked":
                    {
                        var peer = FindPeer(msg["peer"]!.GetValue<string>());
                        if(peer is null)
                            break;
                        peer.Linked = true;
                        UpdateLinked();
                        SendSnapshot(peer);
                        Brio.PopToast($"Now syncing with {peer.Name}.", "FlashBang Sync", NotificationType.Success);
                        break;
                    }
                case "unlinked":
                    {
                        var peer = FindPeer(msg["peer"]!.GetValue<string>());
                        if(peer is null)
                            break;
                        DropPeer(peer);
                        peer.Linked = false;
                        UpdateLinked();
                        break;
                    }
                case "op":
                    {
                        var peer = FindPeer(msg["from"]!.GetValue<string>());
                        if(peer is not null && peer.Linked)
                            ReceiveBatch(peer, msg["data"]!.GetValue<string>());
                        break;
                    }
                case "error":
                    {
                        // Our JSON stays well under the limit, so this means a file chunk was
                        // refused by a relay too old to carry them.
                        if(msg["code"]?.GetValue<string>() == "too_large")
                            _modTransfer.RelayRejectsFiles = true;
                        Brio.Log.Warning($"FlashBang relay: {msg["message"]?.GetValue<string>()}");
                        break;
                    }
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: ignored a malformed relay message.");
        }
    }

    private void OnWelcome(JsonObject msg)
    {
        _me = msg["you"]!.GetValue<string>();
        _peers.Clear();

        foreach(var node in msg["members"]!.AsArray())
            AddPeer(node!["id"]!.GetValue<string>(), node["name"]!.GetValue<string>());

        foreach(var node in msg["consents"]!.AsArray())
        {
            var from = node!["from"]!.GetValue<string>();
            var to = node["to"]!.GetValue<string>();
            if(to == _me && FindPeer(from) is { } asker)
                asker.TheyAgreed = true;
        }

        Status = SyncStatus.Connected;
        EnsureSyncFolder();

        if(_peers.Count > 0)
            OnAttentionNeeded?.Invoke();
    }

    private SyncPeer AddPeer(string id, string name)
    {
        var existing = FindPeer(id);
        if(existing is not null)
            return existing;

        var peer = new SyncPeer(id, string.IsNullOrWhiteSpace(name) ? "Guest" : name);
        _peers.Add(peer);
        return peer;
    }

    private SyncPeer? FindPeer(string id) => _peers.FirstOrDefault(p => p.Id == id);

    private void UpdateLinked() => _modTransfer.SetLinked(_peers.Where(p => p.Linked).Select(p => p.Id));

    // Removes everything a player made from our scene. Their edits to our things stay.
    private void DropPeer(SyncPeer peer)
    {
        foreach(var item in _items.Values.Where(i => i.Creator == peer).ToList())
        {
            RemoveItem(item);
            _items.Remove(item.Id);
        }

        peer.Reassembler.Clear();

        // Removed once its items have been torn down, one per frame.
        if(peer.Folder is not null)
            _orphanFolders.Add(peer.Folder);

        peer.Folder = null;
    }

    // Inbound

    private void ReceiveBatch(SyncPeer peer, string base64)
    {
        if(base64.Length > SyncLimits.MaxBatchBase64Chars)
            return;

        SyncBatch batch;
        try
        {
            batch = MessagePackSerializer.Deserialize<SyncBatch>(Convert.FromBase64String(base64), SyncCodec.UntrustedOptions);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read an update from {peer.Name}.");
            return;
        }

        if(batch.Version != SyncBatch.CurrentVersion)
        {
            if(!peer.WarnedAboutVersion)
            {
                peer.WarnedAboutVersion = true;
                var who = batch.Version < SyncBatch.CurrentVersion ? peer.Name : "You";
                LastError = $"{peer.Name} is on a different FlashBang version. {who} need{(who == "You" ? "" : "s")} to update FlashBang before you can sync.";
                Brio.PopToast(LastError, "FlashBang Sync", NotificationType.Warning);
            }
            return;
        }

        foreach(var op in batch.Ops.Take(SyncLimits.MaxOpsPerBatch))
        {
            _items.TryGetValue(op.SyncId, out var item);

            if(op.Kind == SyncOpKind.Remove)
            {
                peer.Reassembler.Forget(op.SyncId);
                if(item is not null)
                {
                    RemoveItem(item);
                    _items.Remove(item.Id);
                }
                continue;
            }

            if(op.Kind != SyncOpKind.Upsert)
                continue;

            var kind = Kinds.FirstOrDefault(k => k.Type == op.Type);
            if(kind is null || op.Part >= kind.PartCount)
                continue;

            if(item is not null && item.Kind != kind)
                continue;

            // Mods and pictures only ever come from whoever made the item.
            if(item is not null && !kind.Editable(op.Part) && item.Creator != peer)
                continue;

            var whole = peer.Reassembler.Add(op);
            if(whole is null)
                continue;

            object? value;
            try
            {
                value = kind.Decode(op.Part, whole);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: dropped a {op.Type} from {peer.Name}.");
                continue;
            }

            if(value is null)
                continue;

            if(item is null)
            {
                var theirs = _items.Values.Where(i => i.Creator == peer).ToList();
                if(theirs.Count >= SyncLimits.MaxItemsPerPlayer || theirs.Count(i => i.Kind == kind) >= kind.MaxPerPlayer)
                    continue;

                _items[op.SyncId] = item = new SyncItem(op.SyncId, kind, peer);
            }

            var track = item.Tracks[op.Part];
            if(!track.IsNewer(op.Revision, peer.Id))
                continue;

            track.Revision = op.Revision;
            track.Writer = peer.Id;

            if(!item.Mine && item.Creator == peer)
                item.Territory = op.Territory;

            // What we already have, give or take noise: nothing to apply.
            if(track.LastKnown is not null && kind.IsSame(op.Part, value, track.LastKnown))
                continue;

            // We're changing this right now, and our next update will win.
            if(item.Mine && item.Live && DateTime.UtcNow - track.LastLocalChange < LocalChangeWins)
                continue;

            track.LastKnown = value;
            item.Parts[op.Part] = value;
            item.Dirty.Add(op.Part);
        }
    }

    // Showing and applying

    private void Reconcile(DateTime now)
    {
        foreach(var (id, at) in _deletedByUser)
        {
            if(now - at > DeleteWindow)
                _deletedByUser.Remove(id);
        }

        var gposing = _gPoseService.IsGPosing;
        List<SyncItem>? deleted = null;

        foreach(var item in _items.Values)
        {
            var kind = item.Kind;

            var canShow = gposing
                && (item.Mine || !kind.NeedsSameTerritory || item.Territory == _clientState.TerritoryType)
                && kind.CanShow(item);

            if(item.Entity is not null && !SafeIsAlive(item))
            {
                // Deleted by the player: gone for everyone. Our own thing that was never shared has
                // nothing to come back from, and nobody else knows about it.
                if(_deletedByUser.Remove(item.Entity.Id) || (item.Mine && !item.Shared))
                {
                    (deleted ??= []).Add(item);
                    continue;
                }

                // Lost some other way. Nobody asked for that, so it just comes back.
                ForgetEntity(item);
                item.NextAttempt = now + RetryDelay;
            }

            if(item.Spawning && now - item.SpawnStarted > SpawnTimeout)
            {
                Brio.Log.Warning($"FlashBang: a {kind.Type} from {item.CreatorName} took too long to appear.");
                HideItem(item);
                item.NextAttempt = now + RetryDelay;
            }

            // Our own things are only re-created if they were shared.
            if(item.Mine && item.Entity is null && !item.Shared)
                continue;

            if(!canShow)
            {
                // Only copies are ever hidden. Our own object isn't ours to take away just because
                // it has nothing to show from yet: it has no parts until it's first shared.
                if(!item.Mine && (item.Entity is not null || item.Spawning))
                    HideItem(item);
                continue;
            }

            if(item.Entity is null)
            {
                if(!item.Spawning && now >= item.NextAttempt)
                {
                    item.Spawning = true;
                    item.SpawnStarted = now;
                    try
                    {
                        kind.Show(item, item.Mine ? EnsureSyncFolder() : EnsurePeerFolder(item.Creator!));
                    }
                    catch(Exception ex)
                    {
                        Brio.Log.Warning(ex, $"FlashBang: couldn't show a {kind.Type} from {item.CreatorName}.");
                        HideItem(item);
                        item.NextAttempt = now + RetryDelay;
                    }
                }
                continue;
            }

            if(item.Busy)
            {
                item.WasBusy = true;
                continue;
            }

            if(item.WasBusy)
            {
                // Whatever the kind just finished (a new look, mods) settles before we read it.
                item.WasBusy = false;
                Settle(item, Enumerable.Range(0, kind.PartCount).Select(p => (byte)p), now);
            }

            if(item.Dirty.Count == 0)
                continue;

            var applying = item.Dirty.ToList();
            try
            {
                kind.Apply(item);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't update a {kind.Type} from {item.CreatorName}.");
                item.Dirty.Clear();
            }

            Settle(item, applying.Where(p => !item.Dirty.Contains(p)), now);

            if(item.RespawnRequested)
            {
                item.RespawnRequested = false;
                if(!item.Mine)
                    HideItem(item);
            }
        }

        if(deleted is not null)
        {
            foreach(var item in deleted)
            {
                if(!item.Mine || item.Shared)
                    Broadcast([new SyncOp { Kind = SyncOpKind.Remove, SyncId = item.Id, Type = item.Kind.Type }]);
                ForgetEntity(item);
                item.Removed = true;
                _items.Remove(item.Id);
            }
        }
    }

    // After applying someone else's change, the next read becomes the baseline rather than
    // something to send back.
    private static void Settle(SyncItem item, IEnumerable<byte> parts, DateTime now)
    {
        foreach(var part in parts)
        {
            var track = item.Tracks[part];
            track.Absorb = true;
            track.SettleUntil = now + SettleTime;
        }
    }

    private void Animate(DateTime now)
    {
        foreach(var item in _items.Values)
        {
            if(item.Motion is null || item.Entity is null || item.Busy || item.Spawning)
                continue;

            try
            {
                if(SafeIsAlive(item))
                    item.Kind.Animate(item, now);
                else
                    item.Motion = null;
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't move a {item.Kind.Type}.");
                item.Motion = null;
            }
        }
    }

    private bool SafeIsAlive(SyncItem item)
    {
        try
        {
            return item.Kind.IsAlive(item);
        }
        catch(Exception)
        {
            return false;
        }
    }

    private static bool SafeCanShare(SyncKind kind, Entity entity)
    {
        try
        {
            return kind.CanShare(entity);
        }
        catch(Exception)
        {
            return false;
        }
    }

    // Takes the item out of our scene but keeps its data, so it can be shown again. The game
    // object itself is torn down later, one per frame (see ProcessDespawns), so a burst of
    // removals never destroys several actors in the same tick.
    private void HideItem(SyncItem item)
    {
        item.Generation++;
        item.Spawning = false;
        item.Busy = false;
        item.Motion = null;

        if(item.Entity is not null || item.State is not null)
            _despawns.Enqueue(new SyncItem(item.Id, item.Kind, item.Creator) { Entity = item.Entity, State = item.State });

        item.Entity = null;
        item.State = null;
        item.MarkAllDirty();
    }

    private void RemoveItem(SyncItem item)
    {
        HideItem(item);
        item.Removed = true;
    }

    // Cleans up one hidden item: first what's kept outside the scene (Penumbra collection,
    // Customize+ profile) while the object still exists, then the object.
    private void ProcessDespawns()
    {
        if(!_despawns.TryDequeue(out var ghost))
        {
            RemoveEmptyPeerFolders();
            return;
        }

        var alive = _gPoseService.IsGPosing && ghost.Entity is not null && SafeIsAlive(ghost);

        SafeRelease(ghost, alive);

        if(alive)
        {
            try
            {
                ghost.Kind.Hide(ghost);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't remove a {ghost.Kind.Type} from {ghost.CreatorName}.");
            }
        }

        // The game object was already gone, but its entry may still be in the scene list.
        if(!alive && _gPoseService.IsGPosing && ghost.Entity is not null && _entityManager.EntityExists(ghost.Entity.Id))
        {
            try
            {
                _entityManager.DetachEntity(ghost.Entity, true);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "FlashBang: couldn't remove a leftover entity.");
            }
        }
    }

    // Folders of players who left or stopped syncing, once everything in them is gone.
    private void RemoveEmptyPeerFolders()
    {
        foreach(var folder in _orphanFolders.ToList())
        {
            if(!_entityManager.EntityExists(folder.Id))
            {
                _orphanFolders.Remove(folder);
                continue;
            }

            if(folder.Children.Any())
                continue;

            _orphanFolders.Remove(folder);
            try
            {
                _entityManager.DetachEntity(folder, true);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, "FlashBang: couldn't remove an empty player folder.");
            }
        }
    }

    // Leaving GPose: Brio removes every actor, light and object itself. Let go of ours without
    // touching them. Shared items come back from their latest state next time; our unshared
    // ones are simply gone, as they would be without FlashBang.
    private void OnGPoseChanged(bool gposing)
    {
        if(gposing)
            return;

        foreach(var ghost in _despawns)
            SafeRelease(ghost, false);
        _despawns.Clear();

        foreach(var item in _items.Values.ToList())
        {
            item.Generation++;
            item.Spawning = false;
            item.Busy = false;
            item.WasBusy = false;
            item.Motion = null;
            SafeRelease(item, false);
            item.Entity = null;
            item.State = null;
            item.MarkAllDirty();

            if(item.Mine && !item.Shared)
                _items.Remove(item.Id);
        }
    }

    // The object went away on its own (deleted, or dropped by the game); nothing left to destroy.
    private static void ForgetEntity(SyncItem item)
    {
        SafeRelease(item, false);

        item.Entity = null;
        item.State = null;
        item.Motion = null;
        item.MarkAllDirty();
    }

    private static void SafeRelease(SyncItem item, bool alive)
    {
        try
        {
            item.Kind.Release(item, alive);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't clean up a {item.Kind.Type} from {item.CreatorName}.");
        }
    }

    private FolderEntity EnsurePeerFolder(SyncPeer peer)
    {
        var syncFolder = EnsureSyncFolder();

        if(peer.Folder is null || !_entityManager.EntityExists(peer.Folder.Id))
        {
            peer.Folder = _entityManager.CreateEntityOnEntityContainer<FolderEntity>($"{peer.Name}'s items");
            peer.Folder.IsEditable = false;
        }

        if(peer.Folder.Parent != syncFolder)
            _entityManager.MoveEntity(peer.Folder, syncFolder);

        return peer.Folder;
    }

    // Outbound

    private bool AnyLinked => _peers.Any(p => p.Linked);

    private void Broadcast(List<SyncOp> ops)
    {
        if(AnyLinked)
            _outbox.Enqueue(ops, null);
    }

    // Finds our own things in the Sync folder, and any that were taken out of it.
    private void Discover()
    {
        // Leaving GPose tears down our scene, but our things stay shared: nothing is removed for
        // others until we delete them, take them out of Sync, stop syncing or leave the room.
        if(!_gPoseService.IsGPosing)
            return;

        var folder = FindSyncFolder();
        var seen = new HashSet<EntityId>();

        if(folder is not null)
        {
            var copies = _items.Values.Where(i => !i.Mine && i.Entity is not null).Select(i => i.Entity!.Id).ToHashSet();
            var mineByEntity = _items.Values.Where(i => i.Mine && i.Entity is not null).ToDictionary(i => i.Entity!.Id);
            var counts = _items.Values.Where(i => i.Mine).GroupBy(i => i.Kind).ToDictionary(g => g.Key, g => g.Count());

            foreach(var entity in Descendants(folder, copies))
            {
                if(mineByEntity.ContainsKey(entity.Id))
                {
                    seen.Add(entity.Id);
                    continue;
                }

                var kind = Kinds.FirstOrDefault(k => SafeCanShare(k, entity));
                if(kind is null)
                    continue;

                counts.TryGetValue(kind, out var count);
                if(count >= kind.MaxPerPlayer || counts.Values.Sum() >= SyncLimits.MaxItemsPerPlayer)
                    continue;
                counts[kind] = count + 1;

                // New in the Sync folder: tracked, but not shared until Live or Sync now.
                var item = new SyncItem(Guid.NewGuid(), kind, null) { Entity = entity };
                try
                {
                    kind.Adopt(item);
                }
                catch(Exception ex)
                {
                    Brio.Log.Warning(ex, $"FlashBang: couldn't track {entity.FriendlyName}.");
                    continue;
                }

                _items[item.Id] = item;
                seen.Add(entity.Id);
            }
        }

        _modSource.Keep(seen);

        // Our things that were taken out of the Sync folder. Reconcile deals with any that died. A
        // missing folder only counts if the player deleted it: a scene load removes it too.
        var folderIsTrustworthy = folder is not null || (_syncFolderId is { } folderId && _deletedByUser.ContainsKey(folderId));
        _syncFolderId = folder?.Id ?? _syncFolderId;

        foreach(var item in _items.Values.Where(i => folderIsTrustworthy && i.Mine && !i.Spawning && i.Entity is not null && !seen.Contains(i.Entity.Id) && SafeIsAlive(i)).ToList())
        {
            if(item.Shared)
                Broadcast([new SyncOp { Kind = SyncOpKind.Remove, SyncId = item.Id, Type = item.Kind.Type }]);

            SafeRelease(item, false);
            item.Removed = true;
            _items.Remove(item.Id);
        }

    }

    // Sends whatever changed on any item that's Live or asked to Sync now.
    private void Capture(DateTime now)
    {
        if(!_gPoseService.IsGPosing)
            return;

        var territory = _clientState.TerritoryType;

        foreach(var item in _items.Values)
        {
            if(item.Entity is null || item.Spawning || item.Busy || item.WasBusy || !SafeIsAlive(item))
                continue;

            if(item.Mine)
                item.Territory = territory;

            var force = item.SyncNowRequested;
            var sent = false;

            for(byte part = 0; part < item.Kind.PartCount; part++)
                sent |= CapturePart(item, part, force, now);

            if(force)
                item.SyncNowRequested = false;

            if(sent && item.Mine)
                item.Shared = true;
        }
    }

    // Reads one part and sends it if it changed (or if forced). Returns whether it was sent.
    private bool CapturePart(SyncItem item, byte part, bool force, DateTime now)
    {
        var kind = item.Kind;

        if(!item.Mine && !kind.Editable(part))
            return false;

        var automatic = item.Live || (item.Mine && item.Shared && kind.AlwaysSent(part));
        if(!force && !automatic)
            return false;

        var track = item.Tracks[part];
        if(!force && (now < track.NextCapture || now < track.SettleUntil))
            return false;

        track.NextCapture = now + kind.CaptureInterval(part);

        object? value;
        byte[] bytes;
        try
        {
            value = kind.Capture(item, part);
            if(value is null)
                return false;

            // Unchanged, give or take noise: done, without encoding anything.
            var same = track.LastKnown is not null && kind.IsSame(part, value, track.LastKnown);
            if(same && !force && !track.Absorb)
                return false;

            bytes = same && track.Baseline is not null ? track.Baseline : kind.Encode(part, value);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read {item.Entity?.FriendlyName} to share it.");
            return false;
        }

        if(bytes.Length > SyncReassembler.MaxPartBytes(kind.Type))
        {
            if(!track.WarnedTooLarge)
            {
                track.WarnedTooLarge = true;
                Brio.PopToast($"{item.Entity?.FriendlyName} is too large to share.", "FlashBang Sync", NotificationType.Warning);
            }
            return false;
        }

        if(track.Absorb)
        {
            track.Absorb = false;
            track.Baseline = bytes;
            track.LastKnown = value;
            if(!force)
                return false;
        }

        if(!force && track.Baseline is not null && track.Baseline.AsSpan().SequenceEqual(bytes))
            return false;

        track.Baseline = bytes;
        track.LastKnown = value;
        track.LastLocalChange = now;
        item.Parts[part] = value;
        track.Revision++;
        track.Writer = _me;

        Broadcast(SyncCodec.Split(item.Id, kind.Type, part, item.Territory, track.Revision, bytes));

        try
        {
            kind.OnSent(item, part);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: after sending {item.Entity?.FriendlyName}.");
        }

        return true;
    }

    // Sends everything we've shared to one player who just agreed.
    private void SendSnapshot(SyncPeer peer)
    {
        foreach(var item in _items.Values.Where(i => i.Mine && i.Shared))
        {
            for(byte part = 0; part < item.Tracks.Length; part++)
            {
                var track = item.Tracks[part];
                if(track.Baseline is not null)
                    _outbox.Enqueue(SyncCodec.Split(item.Id, item.Kind.Type, part, item.Territory, track.Revision, track.Baseline), peer.Id);
            }
        }
    }

    // Everything under root, skipping other players' copies and whatever hangs off them.
    private static IEnumerable<Entity> Descendants(Entity root, IReadOnlySet<EntityId>? skip = null)
    {
        foreach(var child in root.Children)
        {
            if(skip is not null && skip.Contains(child.Id))
                continue;

            yield return child;
            foreach(var descendant in Descendants(child, skip))
                yield return descendant;
        }
    }

    public override void Dispose()
    {
        _gPoseService.OnGPoseStateChange -= OnGPoseChanged;
        Disconnect(null);
        base.Dispose();
    }
}
