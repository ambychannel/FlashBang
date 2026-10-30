using Brio.Entities;
using Brio.Entities.Core;
using Brio.Services;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Brio.Sync.Kinds;

// Reference images are sent as the picture (part 0, PNG or JPEG up to 2 MB) and the window
// settings (part 1). Other players' pictures are written to a cache folder under a hashed name.
public class ReferenceImageSyncKind : SyncKind
{
    private const byte ImagePart = 0;
    private const byte ViewPart = 1;

    private readonly ReferenceImageService _referenceImageService;
    private readonly EntityManager _entityManager;
    private readonly string _cacheFolder;

    // Our pictures, read and encoded once per file.
    private readonly Dictionary<string, (SyncReferenceImage? Dto, byte[]? Encoded)> _ours = [];

    public ReferenceImageSyncKind(ReferenceImageService referenceImageService, EntityManager entityManager, IDalamudPluginInterface pluginInterface)
    {
        _referenceImageService = referenceImageService;
        _entityManager = entityManager;
        _cacheFolder = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "Data", "SyncCache");

        // Pictures from an earlier session aren't needed any more.
        try
        {
            if(Directory.Exists(_cacheFolder))
                Directory.Delete(_cacheFolder, true);
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, "FlashBang: couldn't clear the sync image cache.");
        }
    }

    public override SyncEntityType Type => SyncEntityType.ReferenceImage;
    public override int MaxPerPlayer => SyncLimits.MaxReferenceImagesPerPlayer;
    public override byte PartCount => 2;
    public override bool NeedsSameTerritory => false;

    public override TimeSpan CaptureInterval(byte part)
        => part == ImagePart ? TimeSpan.FromSeconds(5) : SyncLimits.SendInterval;

    // Ours

    public override bool CanShare(Entity entity) => entity is ReferenceImageEntity;

    // The picture is the creator's; others can move and resize the window.
    public override bool Editable(byte part) => part == ViewPart;
    public override bool AlwaysSent(byte part) => part == ImagePart;

    public override object? Capture(SyncItem item, byte part)
    {
        var image = (ReferenceImageEntity)item.Entity!;

        if(part == ViewPart)
        {
            return new SyncReferenceImageView
            {
                IsWindowOpen = image.IsWindowOpen,
                Opacity = image.Opacity,
                Zoom = image.Zoom,
                Rotation = image.Rotation,
                PanOffset = image.PanOffset,
            };
        }

        if(!_ours.TryGetValue(image.Path, out var cached))
        {
            cached = (ReadImage(image), null);
            _ours[image.Path] = cached;
        }

        return cached.Dto;
    }

    private static SyncReferenceImage? ReadImage(ReferenceImageEntity image)
    {
        try
        {
            var info = new FileInfo(image.Path);
            if(!info.Exists || info.Length > SyncLimits.MaxImageBytes)
            {
                Brio.PopToast($"{image.FriendlyName} is too large to share. Reference images can be up to 2 MB.", "FlashBang Sync", Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
                return null;
            }

            var bytes = File.ReadAllBytes(image.Path);
            if(SyncValidation.ImageExtension(bytes) is null)
            {
                Brio.PopToast($"{image.FriendlyName} can't be shared. Only PNG and JPEG reference images can be.", "FlashBang Sync", Dalamud.Interface.ImGuiNotification.NotificationType.Warning);
                return null;
            }

            return new SyncReferenceImage { Name = image.FriendlyName, Image = bytes };
        }
        catch(Exception ex)
        {
            Brio.Log.Warning(ex, $"FlashBang: couldn't read reference image {image.Path}.");
            return null;
        }
    }

    public override byte[] Encode(byte part, object value)
    {
        if(part == ViewPart)
            return SyncCodec.Encode((SyncReferenceImageView)value);

        var dto = (SyncReferenceImage)value;
        foreach(var (path, cached) in _ours)
        {
            if(ReferenceEquals(cached.Dto, dto))
            {
                if(cached.Encoded is null)
                    _ours[path] = cached with { Encoded = SyncCodec.Encode(dto) };
                return _ours[path].Encoded!;
            }
        }

        return SyncCodec.Encode(dto);
    }

    // Theirs

    public override object? Decode(byte part, byte[] compressed)
    {
        if(part == ViewPart)
        {
            var view = SyncCodec.Decode<SyncReferenceImageView>(compressed, SyncLimits.MaxPartBytes);
            return view is not null && SyncValidation.IsValid(view) ? view : null;
        }

        var image = SyncCodec.Decode<SyncReferenceImage>(compressed, SyncLimits.MaxImagePartBytes);
        return image is not null && SyncValidation.IsValid(image) ? image : null;
    }

    public override void Show(SyncItem item, FolderEntity folder)
    {
        var dto = (SyncReferenceImage)item.Parts[ImagePart];
        item.Generation++;

        // Named by content and item, never by anything the sender chose.
        var hash = Convert.ToHexString(SHA256.HashData(dto.Image))[..32];
        var path = Path.Combine(_cacheFolder, $"{hash}-{item.Id:N}{SyncValidation.ImageExtension(dto.Image)}");

        Directory.CreateDirectory(_cacheFolder);
        if(!File.Exists(path))
            File.WriteAllBytes(path, dto.Image);

        var entity = _referenceImageService.Spawn(path);
        _entityManager.MoveEntity(entity, folder);
        entity.IsSynced = !item.Mine;
        entity.FriendlyName = string.IsNullOrWhiteSpace(dto.Name) ? "Reference image" : dto.Name;

        item.State = dto;
        item.Entity = entity;
        item.Spawning = false;
        item.Dirty.Remove(ImagePart);
    }

    public override void Apply(SyncItem item)
    {
        var entity = (ReferenceImageEntity)item.Entity!;

        if(item.Dirty.Remove(ImagePart) && !item.Mine && !ReferenceEquals(item.State, item.Parts[ImagePart]))
        {
            // A different picture; replace the window.
            item.RespawnRequested = true;
            return;
        }

        if(item.Dirty.Remove(ViewPart) && item.Parts.TryGetValue(ViewPart, out var value))
        {
            var view = (SyncReferenceImageView)value;
            entity.IsWindowOpen = view.IsWindowOpen;
            entity.Opacity = view.Opacity;
            entity.Zoom = view.Zoom;
            entity.Rotation = view.Rotation;
            entity.PanOffset = view.PanOffset;
        }
    }

    public override void Hide(SyncItem item)
    {
        if(item.Entity is not ReferenceImageEntity entity)
            return;

        _referenceImageService.Destroy(entity);

        try
        {
            if(entity.Path.StartsWith(_cacheFolder, StringComparison.OrdinalIgnoreCase))
                File.Delete(entity.Path);
        }
        catch(Exception)
        {
            // Still open somewhere; it's cleared next time FlashBang starts.
        }
    }

    public override bool IsAlive(SyncItem item)
        => item.Entity is ReferenceImageEntity entity && entity.IsAttached && _entityManager.EntityExists(entity.Id);
}
