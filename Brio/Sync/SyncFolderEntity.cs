using Brio.Entities.Core;
using Brio.UI.Controls.Stateless;
using Brio.UI.Theming;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility.Raii;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Linq;

namespace Brio.Sync;

// The "Sync" folder in the scene list. Everything placed in it is shared with the room;
// other players' things appear in a sub-folder per player.
public class SyncFolderEntity : FolderEntity
{
    public override FontAwesomeIcon Icon => FontAwesomeIcon.Link;
    public override int ContextButtonCount => 2;

    // Not renameable, but things still have to be dropped in to be shared.
    public override bool AcceptsDrops => true;

    public SyncFolderEntity(IServiceProvider serviceProvider) : base("Sync", serviceProvider)
    {
        IsEditable = false;
    }

    public override void DrawContextButton()
    {
        var sync = _serviceProvider.GetRequiredService<SceneSyncService>();

        var (tooltip, active) = sync.Status switch
        {
            SyncStatus.Connected when sync.Peers.Any(p => p.NeedsAnswer) => ("Someone wants to sync with you. Click to answer.", true),
            SyncStatus.Connected => ($"Room {SceneSyncService.FormatCode(sync.RoomCode ?? "")}, syncing with {sync.Peers.Count(p => p.Linked)}", true),
            SyncStatus.Connecting => ("Connecting…", false),
            _ => ("Not in a room. Click to create or join one.", false),
        };

        using(ImRaii.PushColor(ImGuiCol.Text, ThemeManager.CurrentTheme.Accent.AccentColor, active))
        {
            if(ImBrio.FontIconButtonRight($"###{Id}_sync", FontAwesomeIcon.Users, 2f, tooltip, bordered: false))
                _serviceProvider.GetRequiredService<SyncWindow>().IsOpen = true;
        }

        ImGui.SameLine();

        base.DrawContextButton();
    }

    public override void OnDoubleClick()
    {
        _serviceProvider.GetRequiredService<SyncWindow>().IsOpen = true;
    }
}
