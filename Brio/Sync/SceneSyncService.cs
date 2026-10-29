using Brio.Capabilities.Folder;
using Brio.Capabilities.World;
using Brio.Config;
using Brio.Entities;
using Brio.Entities.Core;
using Brio.Entities.World;
using Brio.Game.GPose;
using Brio.Game.World;
using Brio.Services;
using Brio.Services.MediatorMessages;
using Brio.Services.Models;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;
using MessagePack;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
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
}

// Shares the lights in the Sync folder with everyone in the room who has agreed to sync,
// and shows theirs in a sub-folder per player. Runs entirely on the framework thread;
// network I/O lives in SyncRelayClient.
public class SceneSyncService : MediatorSubscriberBase
{
    private static readonly MessagePackSerializerOptions UntrustedOptions =
        MessagePackSerializer.DefaultOptions.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly EntityManager _entityManager;
    private readonly LightingService _lightingService;
    private readonly GPoseService _gPoseService;
    private readonly IClientState _clientState;
    private readonly IObjectTable _objectTable;
    private readonly IFramework _framework;
    private readonly ConfigurationService _configurationService;

    private SyncRelayClient? _client;
    private readonly List<SyncPeer> _peers = [];

    // Items we own: our entity -> stable id, and the last payload we sent for each id.
    private readonly Dictionary<EntityId, Guid> _ownedIds = [];
    private readonly Dictionary<Guid, string> _lastSent = [];
    private DateTime _nextSend = DateTime.MinValue;

    // Items other players own, keyed by (owner, id). Kept even when they can't be shown
    // (outside GPose, different zone) so they reappear when they can.
    private readonly Dictionary<(string Owner, Guid Id), RemoteItem> _remote = [];
    private readonly HashSet<EntityId> _remoteEntityIds = [];

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
        LightingService lightingService,
        GPoseService gPoseService,
        IClientState clientState,
        IObjectTable objectTable,
        IFramework framework,
        ConfigurationService configurationService) : base(mediator)
    {
        _entityManager = entityManager;
        _lightingService = lightingService;
        _gPoseService = gPoseService;
        _clientState = clientState;
        _objectTable = objectTable;
        _framework = framework;
        _configurationService = configurationService;

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

    public bool IsRemoteItem(Entity entity) => _remoteEntityIds.Contains(entity.Id);

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
        _lastSent.Clear();
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

        if(Status == SyncStatus.Connected && DateTime.UtcNow >= _nextSend)
        {
            _nextSend = DateTime.UtcNow + SyncLimits.SendInterval;
            SendChanges();
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
            DespawnRemote(_remote[key]);
            _remote.Remove(key);
        }

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
            batch = MessagePackSerializer.Deserialize<SyncBatch>(Convert.FromBase64String(base64), UntrustedOptions);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read an update from {peer.Name}.");
            return;
        }

        if(batch.Version != SyncBatch.CurrentVersion)
        {
            LastError = $"{peer.Name} is on a different FlashBang version. Both of you should update.";
            return;
        }

        foreach(var op in batch.Ops.Take(SyncLimits.MaxOpsPerBatch))
        {
            var key = (peer.Id, op.SyncId);

            if(op.Kind == SyncOpKind.Remove)
            {
                if(_remote.Remove(key, out var gone))
                    DespawnRemote(gone);
                continue;
            }

            if(op.Kind != SyncOpKind.Upsert || op.Type != SyncEntityType.Light || op.Payload is null || op.Payload.Length > SyncLimits.MaxPayloadBytes)
                continue;

            LightDTO dto;
            try
            {
                dto = MessagePackSerializer.Deserialize<LightDTO>(op.Payload, UntrustedOptions);
            }
            catch(Exception)
            {
                continue;
            }

            if(!SyncValidation.IsValid(dto))
                continue;

            if(_remote.TryGetValue(key, out var item))
            {
                item.Dto = dto;
                item.Territory = op.Territory;
                item.Dirty = true;
            }
            else if(_remote.Keys.Count(k => k.Owner == peer.Id) < SyncLimits.MaxItemsPerPlayer)
            {
                _remote[key] = new RemoteItem(peer, dto, op.Territory);
            }
        }
    }

    private void ReconcileRemote()
    {
        foreach(var item in _remote.Values)
        {
            if(item.Entity is not null && !IsAlive(item.Entity))
                ForgetEntity(item);

            var canShow = _gPoseService.IsGPosing && item.Territory == _clientState.TerritoryType;

            if(!canShow)
            {
                if(item.Entity is not null)
                    DespawnRemote(item);
                continue;
            }

            if(item.Entity is null)
            {
                if(!item.Spawning)
                    SpawnRemote(item);
            }
            else if(item.Dirty)
            {
                ApplyToLight(item.Entity, item.Dto);
                item.Dirty = false;
            }
        }
    }

    private void SpawnRemote(RemoteItem item)
    {
        item.Spawning = true;
        item.Dirty = false;
        var folder = EnsurePeerFolder(item.Owner);

        _lightingService.SpawnFromDTO(item.Dto, null, folder, entity =>
        {
            item.Spawning = false;

            // The item was removed or its owner left while it was spawning.
            if(!_remote.ContainsValue(item))
            {
                _lightingService.Destroy(entity.GameLight);
                return;
            }

            item.Entity = entity;
            _remoteEntityIds.Add(entity.Id);
            entity.IsSynced = true;
            entity.GameLight.SetVisibility(item.Dto.IsVisible);

            if(item.Dirty)
            {
                ApplyToLight(entity, item.Dto);
                item.Dirty = false;
            }
        });
    }

    private void DespawnRemote(RemoteItem item)
    {
        var entity = item.Entity;
        ForgetEntity(item);

        if(entity is not null && IsAlive(entity))
            _lightingService.Destroy(entity.GameLight);
    }

    private void ForgetEntity(RemoteItem item)
    {
        if(item.Entity is not null)
            _remoteEntityIds.Remove(item.Entity.Id);
        item.Entity = null;
        item.Dirty = true;
    }

    private bool IsAlive(LightEntity entity)
        => entity.GameLight.IsValid && _entityManager.EntityExists(entity.Id);

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

    private static unsafe void ApplyToLight(LightEntity entity, LightDTO dto)
    {
        if(!entity.GameLight.IsValid)
            return;

        if(entity.TryGetCapability<LightTransformCapability>(out var transform))
            transform.Transform = dto.Transform;

        var gameLight = entity.GameLight.GameLight;
        var render = gameLight->RenderLight;
        if(render != null)
        {
            render->EmissionType = dto.LightType;
            render->Color = dto.Color;
            render->Intensity = dto.Intensity;
            render->Range = dto.Range;
            render->FalloffFactor = dto.Falloff;
            render->SpotLightAngleDegrees = dto.LightAngle;
            render->AngularFalloffDegrees = dto.FalloffAngle;
            render->CharacterShadowRange = dto.CharacterShadowRange;
            render->ShadowPlaneNear = dto.ShadowPlaneNear;
            render->ShadowPlaneFar = dto.ShadowPlaneFar;
        }

        entity.GameLight.SetVisibility(dto.IsVisible);
        entity.RawName = dto.FriendlyName;

        gameLight->Update();
        gameLight->UpdateTransforms(false);
    }

    // Outbound

    private void SendChanges()
    {
        if(_client is null || !_peers.Any(p => p.Linked))
            return;

        var current = CaptureOwned();
        var ops = new List<SyncOp>();

        foreach(var (id, payload) in current)
        {
            var encoded = Convert.ToBase64String(payload);
            if(_lastSent.TryGetValue(id, out var previous) && previous == encoded)
                continue;

            _lastSent[id] = encoded;
            ops.Add(new SyncOp { Kind = SyncOpKind.Upsert, SyncId = id, Type = SyncEntityType.Light, Territory = _clientState.TerritoryType, Payload = payload });
        }

        foreach(var id in _lastSent.Keys.Where(id => !current.ContainsKey(id)).ToList())
        {
            _lastSent.Remove(id);
            ops.Add(new SyncOp { Kind = SyncOpKind.Remove, SyncId = id, Type = SyncEntityType.Light });
        }

        Send(ops, null);
    }

    // Sends everything we currently share to one player who just agreed.
    private void SendSnapshot(SyncPeer peer)
    {
        if(_client is null)
            return;

        var ops = CaptureOwned()
            .Select(kv => new SyncOp { Kind = SyncOpKind.Upsert, SyncId = kv.Key, Type = SyncEntityType.Light, Territory = _clientState.TerritoryType, Payload = kv.Value })
            .ToList();

        Send(ops, peer.Id);
    }

    private void Send(List<SyncOp> ops, string? to)
    {
        if(_client is null || ops.Count == 0)
            return;

        foreach(var chunk in ops.Chunk(SyncLimits.MaxOpsPerBatch))
        {
            var batch = new SyncBatch { Ops = [.. chunk] };
            var data = Convert.ToBase64String(MessagePackSerializer.Serialize(batch));
            if(data.Length > SyncLimits.MaxBatchBase64Chars)
            {
                Brio.Log.Warning("FlashBang: an update was too large to send and was skipped.");
                continue;
            }
            _ = _client.SendOpAsync(data, to);
        }
    }

    // Lights in our Sync folder that we own, as (stable id -> serialized LightDTO).
    private Dictionary<Guid, byte[]> CaptureOwned()
    {
        var result = new Dictionary<Guid, byte[]>();
        var folder = FindSyncFolder();
        if(folder is null || !_gPoseService.IsGPosing)
        {
            _ownedIds.Clear();
            return result;
        }

        var seen = new HashSet<EntityId>();
        foreach(var light in Descendants(folder).OfType<LightEntity>())
        {
            if(_remoteEntityIds.Contains(light.Id) || !light.GameLight.IsValid || light.GameLight.IsWorldLight || light.GameLight.IsGPoseLight)
                continue;
            if(result.Count >= SyncLimits.MaxItemsPerPlayer)
                break;

            var dto = _lightingService.SaveLightDTO(light.GameLight, Vector3.Zero);
            if(dto is null)
                continue;

            dto.FriendlyName = light.RawName;
            dto.IsVisible = light.GameLight.IsVisible;

            if(!_ownedIds.TryGetValue(light.Id, out var id))
                _ownedIds[light.Id] = id = Guid.NewGuid();

            seen.Add(light.Id);
            result[id] = MessagePackSerializer.Serialize(dto);
        }

        foreach(var stale in _ownedIds.Keys.Where(k => !seen.Contains(k)).ToList())
            _ownedIds.Remove(stale);

        return result;
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

    private class RemoteItem(SyncPeer owner, LightDTO dto, uint territory)
    {
        public SyncPeer Owner { get; } = owner;
        public LightDTO Dto { get; set; } = dto;
        public uint Territory { get; set; } = territory;
        public LightEntity? Entity { get; set; }
        public bool Spawning { get; set; }
        public bool Dirty { get; set; }
    }
}
