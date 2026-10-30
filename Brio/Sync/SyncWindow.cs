using Brio.Config;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
using System.Linq;
using System.Numerics;

namespace Brio.Sync;

// Create or join a room, and agree (or not) to sync with each person in it.
public class SyncWindow : Window, IDisposable
{
    private readonly SceneSyncService _sync;
    private readonly ConfigurationService _configurationService;

    private string _joinCode = string.Empty;
    private string _relayUrl = string.Empty;

    public SyncWindow(SceneSyncService sync, ConfigurationService configurationService)
        : base($"{Brio.Name} SYNC###flashbang_sync_window", ImGuiWindowFlags.AlwaysAutoResize)
    {
        Namespace = "flashbang_sync_window";

        _sync = sync;
        _configurationService = configurationService;
        _relayUrl = configurationService.Configuration.Sync.RelayUrl;

        _sync.OnAttentionNeeded += Open;
    }

    private void Open() => IsOpen = true;

    public override void Draw()
    {
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 26);
        try
        {
            DrawContents();
        }
        finally
        {
            ImGui.PopTextWrapPos();
        }
    }

    private void DrawContents()
    {
        if(!_sync.HasRelay)
        {
            DrawRelaySetup();
            return;
        }

        switch(_sync.Status)
        {
            case SyncStatus.Offline:
                DrawLobby();
                break;
            case SyncStatus.Connecting:
                ImGui.TextUnformatted("Connecting to the room…");
                break;
            case SyncStatus.Connected:
                DrawRoom();
                break;
        }

        if(!string.IsNullOrEmpty(_sync.LastError))
        {
            ImGui.Spacing();
            ImGui.TextColored(ImGuiColors.DalamudRed, _sync.LastError);
        }
    }

    private void DrawRelaySetup()
    {
        ImGui.TextUnformatted("FlashBang needs a relay address before you can sync. Paste the one you were given.");
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 22);
        ImGui.InputTextWithHint("###flashbang_relay_url", "https://…workers.dev", ref _relayUrl, 256);
        if(ImGui.Button("Save###flashbang_relay_save"))
        {
            _configurationService.Configuration.Sync.RelayUrl = _relayUrl.Trim();
            _configurationService.ApplyChange();
        }
    }

    private void DrawLobby()
    {
        ImGui.TextUnformatted("Share your GPose scene with a friend. Create a room and send them the code, or join theirs.");
        ImGui.Spacing();

        if(ImBrio.Button("Create a room", FontAwesomeIcon.Plus))
            _sync.CreateRoom();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 10);
        ImGui.InputTextWithHint("###flashbang_join_code", "ABCD-EFGH-JKMN", ref _joinCode, 20);
        ImGui.SameLine();
        if(ImBrio.Button("Join", FontAwesomeIcon.ArrowRight))
            _sync.JoinRoom(_joinCode);

        DrawModCache();
    }

    private void DrawRoom()
    {
        var code = SceneSyncService.FormatCode(_sync.RoomCode ?? string.Empty);

        ImGui.TextUnformatted("Room code");
        using(ImRaii.PushFont(UiBuilder.MonoFont))
            ImGui.TextUnformatted(code);
        ImGui.SameLine();
        if(ImBrio.FontIconButton("flashbang_copy_code", FontAwesomeIcon.Copy, "Copy the room code"))
            ImGui.SetClipboardText(code);

        ImGui.Spacing();
        ImGui.TextDisabled("Put actors, lights, props, furniture, VFX, cameras and reference images in the Sync folder, then press Sync now (the arrows) to send one, or turn on Live (the signal) to send changes as you make them. Anyone syncing can edit or delete anything in the folder. Actors bring their creator's mods: the files download the first time, and the actor shows its plain look until then.");
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if(_sync.Peers.Count == 0)
            ImGui.TextUnformatted("Nobody else is here yet. Send them the room code.");

        foreach(var peer in _sync.Peers)
        {
            using var id = ImRaii.PushId(peer.Id);

            ImGui.TextUnformatted(peer.Name);
            ImGui.SameLine();

            if(peer.Linked)
            {
                ImGui.TextColored(ImGuiColors.HealerGreen, "Syncing");
                ImGui.SameLine();
                if(ImGui.SmallButton("Stop syncing"))
                    _sync.SetAgreement(peer, false);
            }
            else if(peer.NeedsAnswer)
            {
                ImGui.TextColored(ImGuiColors.DalamudYellow, "wants to sync multiplayer GPose with you");
                if(ImGui.Button("Agree"))
                    _sync.SetAgreement(peer, true);
                ImGui.SameLine();
                if(ImGui.Button("Decline"))
                    _sync.SetAgreement(peer, false);
            }
            else if(peer.IAgreed)
            {
                ImGui.TextDisabled("Waiting for them to agree");
                ImGui.SameLine();
                if(ImGui.SmallButton("Cancel"))
                    _sync.SetAgreement(peer, false);
            }
            else
            {
                ImGui.TextDisabled("Not syncing");
                ImGui.SameLine();
                if(ImGui.SmallButton("Agree to sync"))
                    _sync.SetAgreement(peer, true);
            }
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        DrawItems();

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        if(ImBrio.Button("Leave room", FontAwesomeIcon.DoorOpen))
            _sync.LeaveRoom();

        DrawModCache();
    }

    private void DrawItems()
    {
        if(_sync.RelayNeedsUpdate)
            ImGui.TextColored(ImGuiColors.DalamudRed, "The relay needs updating for mod transfer. Run 'npx wrangler deploy' in FlashBang's relay folder.");

        var items = _sync.Items.ToList();
        if(items.Count == 0)
        {
            ImGui.TextDisabled("Nothing in the Sync folder yet.");
            return;
        }

        ImGui.TextUnformatted("In the Sync folder");

        foreach(var item in items)
        {
            using var id = ImRaii.PushId(item.Id.ToString());

            var name = item.Entity?.FriendlyName ?? "(not shown right now)";
            ImGui.TextUnformatted($"{name}  ({item.Kind.Type}, made by {item.CreatorName})");

            if(item.Entity is not null)
            {
                ImGui.SameLine();
                if(ImGui.SmallButton("Sync now"))
                    _sync.SyncNow(item);
                ImGui.SameLine();
                var live = item.Live;
                if(ImGui.Checkbox("Live", ref live))
                    _sync.SetLive(item, live);
            }

            if(item.Mine && !item.Shared)
                ImGui.TextDisabled("    Not shared yet. Press Sync now or turn on Live.");
            if(!string.IsNullOrEmpty(item.Status))
                ImGui.TextDisabled($"    {item.Status}");
        }
    }

    private void DrawModCache()
    {
        ImGui.Spacing();
        ImGui.TextDisabled($"Downloaded mods: {_sync.DownloadedModBytes / 1048576} MB (up to 10 GB is kept)");
        if(ImBrio.Button("Clear downloaded mods", FontAwesomeIcon.Trash))
            _sync.ClearDownloadedMods();
    }

    public void Dispose()
    {
        _sync.OnAttentionNeeded -= Open;
    }
}
