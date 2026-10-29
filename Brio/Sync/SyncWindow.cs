using Brio.Config;
using Brio.UI.Controls.Stateless;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using System;
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
        ImGui.TextUnformatted("Share lights with a friend in GPose. Create a room and send them the code, or join theirs.");
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
        ImGui.TextDisabled("Lights you put in the Sync folder are shared with everyone you've agreed to sync with. Their lights appear in the Sync folder too.");
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

        if(ImBrio.Button("Leave room", FontAwesomeIcon.DoorOpen))
            _sync.LeaveRoom();
    }

    public void Dispose()
    {
        _sync.OnAttentionNeeded -= Open;
    }
}
