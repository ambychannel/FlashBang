using Brio.Entities.Actor;
using Brio.Game.Actor;
using Brio.IPC;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Brio.Sync.Mods;

// Puts another player's mods on our copy of their actor: a temporary Penumbra collection with
// their files (from our cache) and meta, assigned to just that actor, then their Glamourer
// state and Customize+ profile on top.
public class SyncModApplier(
    PenumbraService penumbraService,
    GlamourerService glamourerService,
    CustomizePlusService customizePlusService,
    ActorRedrawService redrawService,
    SyncModCache cache,
    IFramework framework)
{
    public class Applied
    {
        public Guid Collection { get; init; }
        public bool HasProfile { get; set; }
    }

    public bool IsAvailable => penumbraService.IsAvailable;

    // The actor can be removed at any await, so stillThere is checked before each game call.
    public async Task<Applied?> Apply(ActorEntity entity, SyncActorMods mods, string? glamourerState, Applied? previous, Func<bool> stillThere)
    {
        if(!penumbraService.IsAvailable || !await framework.RunOnFrameworkThread(stillThere))
            return previous;

        var gameObject = entity.GameObject;
        var index = await framework.RunOnFrameworkThread(() => gameObject.ObjectIndex);

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach(var file in mods.Files)
        {
            if(!cache.TryGet(file.Hash, out var local))
                continue;
            foreach(var gamePath in file.GamePaths)
                paths[gamePath] = local;
        }
        foreach(var swap in mods.Swaps)
            paths.TryAdd(swap.GamePath, swap.SwapPath);

        var collection = previous?.Collection ?? await penumbraService.CreateTemporaryCollectionAsync($"FlashBang_{index}");
        if(collection == Guid.Empty)
            return previous;

        var applied = previous ?? new Applied { Collection = collection };
        var applicationId = Guid.NewGuid();

        if(!await framework.RunOnFrameworkThread(stillThere))
            return applied;

        await penumbraService.AssignTemporaryCollectionAsync(collection, index);
        await penumbraService.SetTemporaryModsAsync(applicationId, collection, paths);
        await penumbraService.SetManipulationDataAsync(applicationId, collection, mods.Meta);

        if(!await framework.RunOnFrameworkThread(stillThere))
            return applied;

        if(glamourerState is not null && glamourerService.IsAvailable)
            await framework.RunOnFrameworkThread(() => { if(stillThere()) glamourerService.SetState(gameObject, glamourerState); });

        if(!await framework.RunOnFrameworkThread(stillThere))
            return applied;

        await redrawService.RedrawAndWait(gameObject);

        if(customizePlusService.IsAvailable)
        {
            await framework.RunOnFrameworkThread(() =>
            {
                if(!stillThere())
                    return;

                if(!string.IsNullOrEmpty(mods.CustomizePlus))
                {
                    customizePlusService.SetProfile(gameObject, mods.CustomizePlus);
                    applied.HasProfile = true;
                }
                else if(applied.HasProfile)
                {
                    customizePlusService.RemoveTemporaryProfile(gameObject);
                    applied.HasProfile = false;
                }
            });
        }

        return applied;
    }

    // Framework thread. The actor may already be gone.
    public void Remove(ActorEntity? entity, Applied applied)
    {
        _ = penumbraService.RemoveTemporaryCollectionAsync(Guid.Empty, applied.Collection);

        if(applied.HasProfile && entity is not null && entity.GameObject.IsValid() && customizePlusService.IsAvailable)
            customizePlusService.RemoveTemporaryProfile(entity.GameObject);
    }
}
