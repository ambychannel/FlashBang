using Brio.Capabilities.Folder;
using Brio.Config;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Game.GPose;
using Brio.Services;
using Brio.Services.MediatorMessages;
using Brio.Sync.Kinds;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
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

// Shares everything in the Sync folder with everyone in the room who has agreed to sync, and
// shows theirs in a sub-folder per player. Each kind of item (lights, actors...) is handled by a
// SyncKind. Runs entirely on the framework thread; network I/O lives in SyncRelayClient.
public class SceneSyncService : MediatorSubscriberBase
{
    private readonly EntityManager _entityManager;
    private readonly GPoseService _gPoseService;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly ConfigurationService _configurationService;
    private readonly IServiceProvider _serviceProvider;

    private SyncRelayClient? _client;
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

    // Items we own, by entity.
    private readonly Dictionary<EntityId, OwnedItem> _owned = [];
    private DateTime _nextCapture = DateTime.MinValue;

    // Items other players own, keyed by (owner, id). Kept even when they can't be shown
    // (outside GPose, different zone) so they reappear when they can.
    private readonly Dictionary<(string Owner, Guid Id), RemoteItem> _remote = [];

    public SyncStatus Status { get; private set; } = SyncStatus.Offline;
    public string? RoomCode { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<SyncPeer> Peers => _peers;

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
        IServiceProvider serviceProvider) : base(mediator)
    {
        _entityManager = entityManager;
        _gPoseService = gPoseService;
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;
        _configurationService = configurationService;
        _serviceProvider = serviceProvider;

        Mediator.Subscribe<FrameworkUpdateMessage>(this, _ => OnFrameworkUpdate());
    }

    public static string FormatCode(string code)
        => code.Length == 12 ? $"{code[..4]}-{code[4..8]}-{code[8..]}" : code;

    public static string NormalizeCode(string code)
        => new string(code.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    //

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

    // True for anything another player shared with us, which isn't ours to save or re-share.
    public bool IsRemoteItem(Entity entity) => _remote.Values.Any(i => i.Entity?.Id == entity.Id);

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
        if(client is not null)
            Task.Run(() => client.Dispose());

        foreach(var peer in _peers.ToList())
            DropPeer(peer);

        _peers.Clear();
        _outbox.Clear();
        Status = SyncStatus.Offline;
        RoomCode = null;
        LastError = error;
    }

    private void OnFrameworkUpdate()
    {
        if(_client is not null)
        {
            while(_client is not null && _client.TryDequeue(out var message))
                HandleMessage(message);

            if(_client is not null && Status == SyncStatus.Connected && !_client.IsOpen)
                Fail(_client.CloseReason ?? "Disconnected from the relay.");
        }

        ReconcileRemote();

        if(Status == SyncStatus.Connected && DateTime.UtcNow >= _nextCapture)
        {
            _nextCapture = DateTime.UtcNow + SyncLimits.SendInterval;
            CaptureOwned();
        }

        if(_client is not null && Status == SyncStatus.Connected)
        {
            foreach(var (data, to) in _outbox.Take())
                _ = _client.SendOpAsync(data, to);
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
                    Brio.Log.Warning($"FlashBang relay: {msg["message"]?.GetValue<string>()}");
                    break;
            }
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: ignored a malformed relay message.");
        }
    }

    private void OnWelcome(JsonObject msg)
    {
        var me = msg["you"]!.GetValue<string>();
        _peers.Clear();

        foreach(var node in msg["members"]!.AsArray())
            AddPeer(node!["id"]!.GetValue<string>(), node["name"]!.GetValue<string>());

        foreach(var node in msg["consents"]!.AsArray())
        {
            var from = node!["from"]!.GetValue<string>();
            var to = node["to"]!.GetValue<string>();
            if(to == me && FindPeer(from) is { } asker)
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

    // Removes everything a player shared with us from our scene.
    private void DropPeer(SyncPeer peer)
    {
        foreach(var key in _remote.Keys.Where(k => k.Owner == peer.Id).ToList())
        {
            RemoveItem(_remote[key]);
            _remote.Remove(key);
        }

        peer.Reassembler.Clear();

        if(peer.Folder is not null && _entityManager.EntityExists(peer.Folder.Id))
        {
            if(peer.Folder.TryGetCapability<FolderCapability>(out var folderCapability))
                folderCapability.DeleteFolderDestroyChildren();
            else
                _entityManager.DetachEntity(peer.Folder, true);
        }

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
            var key = (peer.Id, op.SyncId);

            if(op.Kind == SyncOpKind.Remove)
            {
                peer.Reassembler.Forget(op.SyncId);
                if(_remote.Remove(key, out var gone))
                    RemoveItem(gone);
                continue;
            }

            if(op.Kind != SyncOpKind.Upsert)
                continue;

            var kind = Kinds.FirstOrDefault(k => k.Type == op.Type);
            if(kind is null || op.Part >= kind.PartCount)
                continue;

            if(_remote.TryGetValue(key, out var existing) && existing.Kind != kind)
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

            if(existing is null)
            {
                var theirs = _remote.Values.Where(i => i.Owner == peer).ToList();
                if(theirs.Count >= SyncLimits.MaxItemsPerPlayer || theirs.Count(i => i.Kind == kind) >= kind.MaxPerPlayer)
                    continue;

                _remote[key] = existing = new RemoteItem(peer, op.SyncId, kind);
            }

            existing.Territory = op.Territory;
            existing.Parts[op.Part] = value;
            existing.Dirty.Add(op.Part);
        }
    }

    private void ReconcileRemote()
    {
        var now = DateTime.UtcNow;

        foreach(var item in _remote.Values)
        {
            var kind = item.Kind;

            // Deleted on our side (or the game dropped it). It comes back on the next pass.
            if(item.Entity is not null && !SafeIsAlive(item))
                ForgetEntity(item);

            if(item.Spawning && now - item.SpawnStarted > SpawnTimeout)
            {
                Brio.Log.Warning($"FlashBang: a {kind.Type} from {item.Owner.Name} took too long to appear.");
                HideItem(item);
                item.NextAttempt = now + RetryDelay;
            }

            var canShow = _gPoseService.IsGPosing
                && (!kind.NeedsSameTerritory || item.Territory == _clientState.TerritoryType)
                && kind.CanShow(item);

            if(!canShow)
            {
                if(item.Entity is not null || item.Spawning)
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
                        kind.Show(item, EnsurePeerFolder(item.Owner));
                    }
                    catch(Exception ex)
                    {
                        Brio.Log.Warning(ex, $"FlashBang: couldn't show a {kind.Type} from {item.Owner.Name}.");
                        HideItem(item);
                        item.NextAttempt = now + RetryDelay;
                    }
                }
            }
            else if(item.Dirty.Count > 0 && !item.Busy)
            {
                try
                {
                    kind.Apply(item);
                }
                catch(Exception ex)
                {
                    Brio.Log.Warning(ex, $"FlashBang: couldn't update a {kind.Type} from {item.Owner.Name}.");
                    item.Dirty.Clear();
                }
            }
        }
    }

    private static readonly TimeSpan SpawnTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private bool SafeIsAlive(RemoteItem item)
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

    // Takes the item out of our scene but keeps its data, so it can be shown again.
    private void HideItem(RemoteItem item)
    {
        item.Generation++;
        item.Spawning = false;
        item.Busy = false;

        if(item.Entity is not null && SafeIsAlive(item))
        {
            try
            {
                item.Kind.Hide(item);
            }
            catch(Exception ex)
            {
                Brio.Log.Warning(ex, $"FlashBang: couldn't remove a {item.Kind.Type} from {item.Owner.Name}.");
            }
        }

        ForgetEntity(item);
    }

    private void RemoveItem(RemoteItem item)
    {
        HideItem(item);
        item.Removed = true;
    }

    private static void ForgetEntity(RemoteItem item)
    {
        item.Entity = null;
        item.State = null;
        item.MarkAllDirty();
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

    // Reads everything we share and queues whatever changed.
    private void CaptureOwned()
    {
        var folder = FindSyncFolder();
        var seen = new HashSet<EntityId>();

        if(folder is not null && _gPoseService.IsGPosing)
        {
            var now = DateTime.UtcNow;
            var remoteIds = _remote.Values.Where(i => i.Entity is not null).Select(i => i.Entity!.Id).ToHashSet();
            var counts = new Dictionary<SyncKind, int>();
            var territory = _clientState.TerritoryType;

            foreach(var entity in Descendants(folder))
            {
                if(remoteIds.Contains(entity.Id))
                    continue;

                var kind = Kinds.FirstOrDefault(k => SafeCanShare(k, entity));
                if(kind is null)
                    continue;

                counts.TryGetValue(kind, out var count);
                if(count >= kind.MaxPerPlayer || seen.Count >= SyncLimits.MaxItemsPerPlayer)
                    continue;
                counts[kind] = count + 1;

                if(!_owned.TryGetValue(entity.Id, out var owned) || owned.Kind != kind)
                {
                    if(owned is not null)
                        QueueRemove(owned);
                    _owned[entity.Id] = owned = new OwnedItem(kind);
                }

                seen.Add(entity.Id);

                for(byte part = 0; part < kind.PartCount; part++)
                    CapturePart(entity, owned, part, territory, now);
            }
        }

        foreach(var (entityId, owned) in _owned.Where(kv => !seen.Contains(kv.Key)).ToList())
        {
            _owned.Remove(entityId);
            QueueRemove(owned);
        }
    }

    private void CapturePart(Entity entity, OwnedItem owned, byte part, uint territory, DateTime now)
    {
        var state = owned.Parts[part];
        if(now < state.NextCapture && state.Territory == territory)
            return;

        state.NextCapture = now + owned.Kind.CaptureInterval(part);

        byte[] bytes;
        try
        {
            var value = owned.Kind.Capture(entity, part);
            if(value is null)
                return;
            bytes = owned.Kind.Encode(part, value);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read {entity.FriendlyName} to share it.");
            return;
        }

        if(bytes.Length > SyncReassembler.MaxPartBytes(owned.Kind.Type))
        {
            if(!state.WarnedTooLarge)
            {
                state.WarnedTooLarge = true;
                Brio.PopToast($"{entity.FriendlyName} is too large to share.", "FlashBang Sync", NotificationType.Warning);
            }
            return;
        }

        if(state.Bytes is not null && state.Territory == territory && state.Bytes.AsSpan().SequenceEqual(bytes))
            return;

        state.Bytes = bytes;
        state.Territory = territory;
        state.Revision++;

        if(AnyLinked)
            _outbox.Enqueue(SyncCodec.Split(owned.Id, owned.Kind.Type, part, territory, state.Revision, bytes), null);
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

    private void QueueRemove(OwnedItem owned)
    {
        if(!AnyLinked || owned.Parts.All(p => p.Bytes is null))
            return;

        _outbox.Enqueue([new SyncOp { Kind = SyncOpKind.Remove, SyncId = owned.Id, Type = owned.Kind.Type }], null);
    }

    // Sends everything we currently share to one player who just agreed.
    private void SendSnapshot(SyncPeer peer)
    {
        CaptureOwned();

        foreach(var owned in _owned.Values)
        {
            for(byte part = 0; part < owned.Parts.Length; part++)
            {
                var state = owned.Parts[part];
                if(state.Bytes is not null)
                    _outbox.Enqueue(SyncCodec.Split(owned.Id, owned.Kind.Type, part, state.Territory, state.Revision, state.Bytes), peer.Id);
            }
        }
    }

    private static IEnumerable<Entity> Descendants(Entity root)
    {
        foreach(var child in root.Children)
        {
            yield return child;
            foreach(var descendant in Descendants(child))
                yield return descendant;
        }
    }

    public override void Dispose()
    {
        Disconnect(null);
        base.Dispose();
    }

    private class OwnedItem(SyncKind kind)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public SyncKind Kind { get; } = kind;
        public PartState[] Parts { get; } = Enumerable.Range(0, kind.PartCount).Select(_ => new PartState()).ToArray();
    }

    private class PartState
    {
        public byte[]? Bytes { get; set; }
        public uint Revision { get; set; }
        public uint Territory { get; set; }
        public DateTime NextCapture { get; set; }
        public bool WarnedTooLarge { get; set; }
    }
}
