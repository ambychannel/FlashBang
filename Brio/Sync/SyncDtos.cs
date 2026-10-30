using Brio.Entities.Camera;
using Brio.Files;
using Brio.Game.Types;
using Brio.Services.Models;
using MessagePack;
using System;
using System.Numerics;

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

// A Brio camera, as the values a viewer would need to look through it. Cutscene cameras aren't
// shared because they play a file from the owner's PC.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncCamera
{
    public string Name { get; set; } = "";
    public CameraType Type { get; set; }

    public Vector3 Position { get; set; }
    public Vector3 PositionOffset { get; set; }
    public Vector3 TargetOffset { get; set; }
    public Vector3 Rotation { get; set; }
    public Vector2 Pan { get; set; }
    public Vector2 Angle { get; set; }
    public float PivotRotation { get; set; }
    public float Zoom { get; set; }
    public float FoV { get; set; }
    public bool DisableCollision { get; set; }
}

// A reference image's picture. Only the bytes travel, never the owner's file name or path.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncReferenceImage
{
    public string Name { get; set; } = "";
    public byte[] Image { get; set; } = [];
}

// How a reference image's window is set up. Sent separately so moving a slider doesn't resend the picture.
[Serializable]
[MessagePackObject(keyAsPropertyName: true)]
public class SyncReferenceImageView
{
    public bool IsWindowOpen { get; set; } = true;
    public float Opacity { get; set; } = 1f;
    public float Zoom { get; set; } = 1f;
    public float Rotation { get; set; }
    public Vector2 PanOffset { get; set; }
}
