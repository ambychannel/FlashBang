using Brio.Files;
using Brio.Game.Types;
using Brio.Services.Models;
using MessagePack;
using System;

namespace Brio.Sync;

// What's shared for an actor that rarely changes: who they are and what they look like.
// Penumbra collections, Glamourer design ids and Customize+ profiles are left out because
// they only mean something on the sender's PC.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncActorAppearance
{
    public string Name { get; set; } = "";
    public required AnamnesisCharaFile Appearance { get; set; }
    public string? GlamourerState { get; set; }
    public CompanionContainer Companion { get; set; } = CompanionContainer.None;
}

// What changes while posing.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncActorPose
{
    public required PoseData Body { get; set; }
    public PoseData? Companion { get; set; }
}

// A prop, piece of furniture, world object or VFX.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncWorldObject
{
    public required WorldObjectDTO Object { get; set; }
    public bool IsVisible { get; set; } = true;
}
