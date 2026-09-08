using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

public static class DropboxPublishCommand
{
    private static readonly JsonSerializerOptions JsonRead = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions StateJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    public static async Task<int> RunAsync(string[] args)
    {
        string outputPath = Path.GetFullPath(args.Length >= 1 ? args[0] : "./EagleSiteOutput");
        string publishTag = args.Length >= 2 ? args[1] : "发布";

        string appKey = RequiredEnv("DROPBOX_APP_KEY");
        string appSecret = RequiredEnv("DROPBOX_APP_SECRET");
        string refreshToken = RequiredEnv("DROPBOX_REFRESH_TOKEN");
        string rootPath = EnvOrDefault("DROPBOX_LIBRARY_PATH", "/Eagle");
        string sinceRaw = EnvOrDefault("DROPBOX_SINCE", "2026-09-01T00:00:00+08:00");
        string statePath = Path.GetFullPath(EnvOrDefault("DROPBOX_STATE_PATH", "./.eagle-sync/state.json"));
        string cachePath = Path.GetFullPath(EnvOrDefault("DROPBOX_CACHE_PATH", "./.eagle-sync/cache"));
        string stagingPath = NormalizeRoot(EnvOrDefault("DROPBOX_STAGING_PATH", ""));

        if (!DateTimeOffset.TryParse(sinceRaw, out DateTimeOffset since))
        {
            Console.Error.WriteLine($"Invalid DROPBOX_SINCE: {sinceRaw}");
            return 1;
        }

        rootPath = NormalizeRoot(rootPath);
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        Directory.CreateDirectory(cachePath);
        Directory.CreateDirectory(outputPath);

        DropboxSyncState state = LoadState(statePath);
        state.RootPath = rootPath;
        state.Since = since.ToString("o");
        state.Staged ??= new Dictionary<string, StagedMediaState>(StringComparer.OrdinalIgnoreCase);
        state.KnownImageIds ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        state.PendingStageInfoDirs ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Dropbox root: {rootPath}");
        Console.WriteLine($"Since: {since:o}");
        Console.WriteLine($"Publish tag: {publishTag}");
        Console.WriteLine($"State: {statePath}");
        if (!string.IsNullOrWhiteSpace(stagingPath))
        {
            Console.WriteLine($"Staging path: {stagingPath} (images/videos only)");
        }

        using var dbx = new DropboxApiClient(appKey, appSecret, refreshToken);
        await dbx.EnsureAccessTokenAsync();

        var touchedLibraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touchedImageMetaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touchedSourceMediaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newInfoDirPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Dropbox cannot "list only files after a date". Listing a huge library takes hours.
        // Default bootstrap only takes the latest cursor ("from now on"). Optional since-scan is slow.
        string bootstrapMode = EnvOrDefault("DROPBOX_BOOTSTRAP_MODE", "cursor").ToLowerInvariant();
        bool hadCursorAlready = state.BootstrapCompleted && !string.IsNullOrWhiteSpace(state.Cursor);

        if (!hadCursorAlready)
        {
            if (bootstrapMode == "since-scan")
            {
                Console.WriteLine("Bootstrap(since-scan): listing ALL entries under root, then filtering by date (SLOW)...");
                string? cursor = null;
                bool hasMore = true;
                int page = 0;
                int matched = 0;
                while (hasMore)
                {
                    page++;
                    var (entries, nextCursor, more) = await dbx.ListFolderAsync(rootPath, cursor);
                    cursor = nextCursor;
                    hasMore = more;
                    foreach (var entry in entries)
                    {
                        if (!entry.IsFile || entry.IsDeleted)
                        {
                            continue;
                        }

                        if (entry.ServerModified is null || entry.ServerModified < since)
                        {
                            continue;
                        }

                        var info = ParseEaglePath(rootPath, entry.PathDisplay);
                        if (info is null)
                        {
                            if (TryParseEagleInfoMediaPath(rootPath, entry.PathDisplay, out _, out _))
                            {
                                TrackTouchedSourceMedia(entry.PathDisplay, touchedSourceMediaPaths);
                            }

                            continue;
                        }

                        matched++;
                        if (info.IsLibraryMetadata)
                        {
                            touchedLibraryPaths.Add(info.LibraryPath);
                        }
                        else if (info.IsImageMetadata)
                        {
                            touchedImageMetaPaths.Add(entry.PathDisplay);
                            touchedLibraryPaths.Add(info.LibraryPath);
                            newInfoDirPaths.Add(info.InfoDirPath!);
                        }
                    }

                    Console.WriteLine($"  listed page {page}, matched-since-date so far: {matched}");
                }

                state.Cursor = cursor;
                state.BootstrapCompleted = true;
                Console.WriteLine($"Bootstrap listing done. Matched files since date: {matched}");
            }
            else
            {
                Console.WriteLine("Bootstrap(cursor): take latest cursor only — no full library listing.");
                Console.WriteLine("Only files changed AFTER this moment are published. Re-save '发布' in Eagle to include older items.");
                state.Cursor = await dbx.GetLatestCursorAsync(rootPath);
                state.BootstrapCompleted = true;
                Console.WriteLine("Cursor acquired. This run publishes an empty/current state; later runs are incremental.");
            }
        }
        else
        {
            Console.WriteLine("Incremental: applying Dropbox cursor changes...");
            string cursor = state.Cursor!;
            bool hasMore = true;
            int page = 0;
            while (hasMore)
            {
                page++;
                var (entries, nextCursor, more) = await dbx.ListFolderAsync(rootPath, cursor);
                cursor = nextCursor;
                hasMore = more;
                foreach (var entry in entries)
                {
                    if (entry.IsDeleted)
                    {
                        // Keep raw deleted paths even when they are .info folders / image files,
                        // so RemoveByDeletedDropboxPath can unpublish matching state entries.
                        deletedPaths.Add(entry.PathDisplay);

                        var deletedInfo = ParseEaglePath(rootPath, entry.PathDisplay);
                        if (deletedInfo is not null)
                        {
                            touchedLibraryPaths.Add(deletedInfo.LibraryPath);
                            if (deletedInfo.IsImageMetadata)
                            {
                                touchedImageMetaPaths.Add(entry.PathDisplay);
                            }
                        }
                        else
                        {
                            // e.g. /Eagle/Lib.library/images/XXX.info or a file inside it
                            string p = entry.PathDisplay.Replace('\\', '/');
                            foreach (SyncImageState img in state.Images.Values)
                            {
                                string infoDir = img.InfoDirPath.TrimEnd('/');
                                if (string.Equals(p.TrimEnd('/'), infoDir, StringComparison.OrdinalIgnoreCase) ||
                                    p.StartsWith(infoDir + "/", StringComparison.OrdinalIgnoreCase))
                                {
                                    touchedLibraryPaths.Add(img.LibraryPath);
                                    touchedImageMetaPaths.Add(img.MetadataPath);
                                }
                            }
                        }

                        continue;
                    }

                    if (entry.IsFolder)
                    {
                        if (TryParseEagleInfoDirPath(rootPath, entry.PathDisplay, out string newInfoDir))
                        {
                            newInfoDirPaths.Add(newInfoDir);
                        }

                        continue;
                    }

                    if (!entry.IsFile)
                    {
                        continue;
                    }

                    var info = ParseEaglePath(rootPath, entry.PathDisplay);
                    if (info is null)
                    {
                        if (TryParseEagleInfoMediaPath(rootPath, entry.PathDisplay, out _, out _))
                        {
                            TrackTouchedSourceMedia(entry.PathDisplay, touchedSourceMediaPaths);
                        }

                        continue;
                    }

                    if (info.IsLibraryMetadata)
                    {
                        touchedLibraryPaths.Add(info.LibraryPath);
                    }
                    else if (info.IsImageMetadata)
                    {
                        touchedImageMetaPaths.Add(entry.PathDisplay);
                        touchedLibraryPaths.Add(info.LibraryPath);
                    }
                    else if (TryParseEagleInfoMediaPath(rootPath, entry.PathDisplay, out _, out _))
                    {
                        TrackTouchedSourceMedia(entry.PathDisplay, touchedSourceMediaPaths);
                    }
                }

                Console.WriteLine($"  delta page {page}, entries={entries.Count}");
            }

            state.Cursor = cursor;
        }

        foreach (string deleted in deletedPaths)
        {
            RemoveByDeletedDropboxPath(state, deleted, cachePath);
            var deletedInfo = ParseEaglePath(state.RootPath, deleted);
            if (deletedInfo?.IsImageMetadata == true)
            {
                RemoveStagedMedia(state, deleted);
            }
        }

        foreach (string libraryPath in touchedLibraryPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            bool hasImageChange = touchedImageMetaPaths.Any(p =>
                p.StartsWith(libraryPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase));
            bool alreadyPublished = state.Images.Values.Any(i =>
                string.Equals(i.LibraryPath, libraryPath, StringComparison.OrdinalIgnoreCase));
            if (!hasImageChange && !alreadyPublished)
            {
                Console.WriteLine($"Skip library metadata (no published images): {libraryPath}");
                continue;
            }

            await EnsureLibraryAsync(dbx, state, libraryPath);
        }

        foreach (string metaPath in touchedImageMetaPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (deletedPaths.Contains(metaPath))
            {
                continue;
            }

            await ProcessImageMetadataAsync(dbx, state, metaPath, publishTag, cachePath);
        }

        int stagedThisRun = 0;
        if (!string.IsNullOrWhiteSpace(stagingPath))
        {
            var stagingMetaPaths = new HashSet<string>(touchedImageMetaPaths, StringComparer.OrdinalIgnoreCase);
            foreach (string mediaPath in touchedSourceMediaPaths)
            {
                if (TryParseEagleInfoMediaPath(state.RootPath, mediaPath, out string metaFromMedia, out _))
                {
                    stagingMetaPaths.Add(metaFromMedia);
                }
            }

            foreach (string metaPath in stagingMetaPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (deletedPaths.Contains(metaPath))
                {
                    RemoveStagedMedia(state, metaPath);
                    continue;
                }

                if (await TryStageMediaAsync(
                        dbx, state, metaPath, stagingPath, touchedSourceMediaPaths, newInfoDirPaths))
                {
                    stagedThisRun++;
                }
            }
        }

        // Drop libraries that currently have zero published images.
        foreach (string libraryPath in state.Libraries.Keys.ToList())
        {
            bool hasImages = state.Images.Values.Any(i =>
                string.Equals(i.LibraryPath, libraryPath, StringComparison.OrdinalIgnoreCase));
            if (!hasImages)
            {
                state.Libraries.Remove(libraryPath);
            }
        }

        // Ensure assets exist for all published images (cache may be cold on CI).
        foreach (var image in state.Images.Values.ToList())
        {
            BackfillThumbnailFields(image);
            await EnsureCachedAssetAsync(dbx, image, cachePath, original: true);
            await EnsureCachedAssetAsync(dbx, image, cachePath, original: false);
        }

        SaveState(statePath, state);
        SiteGenerator.WriteFromSyncState(state, cachePath, outputPath, publishTag);

        Console.WriteLine($"Published images in state: {state.Images.Count}");
        Console.WriteLine($"Libraries in state: {state.Libraries.Count}");
        if (!string.IsNullOrWhiteSpace(stagingPath))
        {
            Console.WriteLine($"Staged media this run: {stagedThisRun}");
            Console.WriteLine($"Total staged in state: {state.Staged.Count}");
        }
        return 0;
    }

    private static async Task<bool> TryStageMediaAsync(
        DropboxApiClient dbx,
        DropboxSyncState state,
        string metadataPath,
        string stagingRoot,
        IReadOnlySet<string> touchedSourceMediaPaths,
        IReadOnlySet<string> newInfoDirPaths)
    {
        var info = ParseEaglePath(state.RootPath, metadataPath);
        if (info is null || !info.IsImageMetadata)
        {
            return false;
        }

        string json;
        try
        {
            json = await dbx.DownloadTextAsync(metadataPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Staging skip (metadata missing): {metadataPath} ({ex.Message})");
            RemoveStagedMedia(state, metadataPath);
            return false;
        }

        ImageMetadata? imageMeta;
        try
        {
            imageMeta = JsonSerializer.Deserialize<ImageMetadata>(json, JsonRead);
        }
        catch
        {
            Console.WriteLine($"Staging skip (invalid metadata): {metadataPath}");
            return false;
        }

        if (imageMeta is null || string.IsNullOrWhiteSpace(imageMeta.Id) || imageMeta.IsDeleted)
        {
            RemoveStagedMedia(state, metadataPath);
            return false;
        }

        if (!IsImageOrVideo(imageMeta.Ext))
        {
            Console.WriteLine($"Staging skip (not image/video): {metadataPath} ext={imageMeta.Ext}");
            return false;
        }

        var probe = new SyncImageState
        {
            ImageId = imageMeta.Id,
            Metadata = imageMeta,
            InfoDirPath = info.InfoDirPath!
        };
        var (sourceImage, _) = await ResolveSourcePathsAsync(dbx, probe);
        if (string.IsNullOrWhiteSpace(sourceImage))
        {
            Console.WriteLine($"Staging skip (no source file): {metadataPath}");
            return false;
        }

        if (IsThumbnailPath(sourceImage))
        {
            Console.WriteLine($"Staging skip (thumbnail only): {metadataPath}");
            return false;
        }

        string infoDir = info.InfoDirPath!.TrimEnd('/');

        if (state.KnownImageIds.Contains(imageMeta.Id))
        {
            Console.WriteLine($"Staging skip (known image, e.g. rename): {metadataPath}");
            return false;
        }

        StagedMediaState? stagedByImageId = state.Staged.Values.FirstOrDefault(s =>
            string.Equals(s.ImageId, imageMeta.Id, StringComparison.OrdinalIgnoreCase));
        if (stagedByImageId is not null)
        {
            state.KnownImageIds.Add(imageMeta.Id);
            Console.WriteLine($"Staging skip (already staged): {stagedByImageId.StagingPath}");
            return false;
        }

        bool isNewInfoDir = newInfoDirPaths.Contains(infoDir) ||
            state.PendingStageInfoDirs.Contains(infoDir);
        if (!isNewInfoDir)
        {
            state.KnownImageIds.Add(imageMeta.Id);
            Console.WriteLine($"Staging skip (pre-existing library item): {metadataPath}");
            return false;
        }

        if (!touchedSourceMediaPaths.Contains(sourceImage))
        {
            state.PendingStageInfoDirs.Add(infoDir);
            Console.WriteLine($"Staging pending (waiting for source file): {metadataPath}");
            return false;
        }

        if (state.Staged.TryGetValue(metadataPath, out StagedMediaState? existing) &&
            string.Equals(existing.SourcePath, sourceImage, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Staging skip (already staged): {existing.StagingPath}");
            return false;
        }

        string safeName = SanitizeDropboxFileName(imageMeta.Name ?? imageMeta.Id);
        string ext = NormalizeExt(imageMeta.Ext);
        string targetPath = $"{stagingRoot.TrimEnd('/')}/{safeName}{ext}";

        try
        {
            string copiedPath = await dbx.CopyFileAsync(sourceImage, targetPath, autorename: true);
            state.Staged[metadataPath] = new StagedMediaState
            {
                MetadataPath = metadataPath,
                ImageId = imageMeta.Id,
                SourcePath = sourceImage,
                StagingPath = copiedPath,
                StagedAt = DateTimeOffset.UtcNow.ToString("o")
            };
            state.KnownImageIds.Add(imageMeta.Id);
            state.PendingStageInfoDirs.Remove(infoDir);
            Console.WriteLine($"Staged: {copiedPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Staging failed: {metadataPath} -> {targetPath} ({ex.Message})");
            return false;
        }
    }

    private static void TrackTouchedSourceMedia(string pathDisplay, ISet<string> touchedSourceMediaPaths)
    {
        if (IsThumbnailPath(pathDisplay) || !IsImageOrVideo(Path.GetExtension(pathDisplay)))
        {
            return;
        }

        touchedSourceMediaPaths.Add(pathDisplay.Replace('\\', '/'));
    }

    private static bool TryParseEagleInfoDirPath(string rootPath, string pathDisplay, out string infoDirPath)
    {
        infoDirPath = "";

        if (string.IsNullOrWhiteSpace(pathDisplay))
        {
            return false;
        }

        string root = NormalizeRoot(rootPath).TrimEnd('/');
        string path = pathDisplay.Replace('\\', '/').TrimEnd('/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = path[(root.Length + 1)..];
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 ||
            !parts[0].EndsWith(".library", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], "images", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].EndsWith(".info", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        infoDirPath = path;
        return true;
    }

    private static bool TryParseEagleInfoMediaPath(
        string rootPath,
        string pathDisplay,
        out string metadataPath,
        out string infoDirPath)
    {
        metadataPath = "";
        infoDirPath = "";

        if (string.IsNullOrWhiteSpace(pathDisplay))
        {
            return false;
        }

        string root = NormalizeRoot(rootPath).TrimEnd('/');
        string path = pathDisplay.Replace('\\', '/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relative = path[(root.Length + 1)..];
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 ||
            !parts[0].EndsWith(".library", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(parts[1], "images", StringComparison.OrdinalIgnoreCase) ||
            !parts[2].EndsWith(".info", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parts[3], "metadata.json", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string libraryPath = $"{root}/{parts[0]}";
        infoDirPath = $"{libraryPath}/images/{parts[2]}";
        metadataPath = $"{infoDirPath}/metadata.json";
        return true;
    }

    private static void RemoveStagedMedia(DropboxSyncState state, string metadataPath)
    {
        if (state.Staged.Remove(metadataPath))
        {
            Console.WriteLine($"Removed staged record: {metadataPath}");
        }
    }

    private static bool IsImageOrVideo(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        string normalized = ext.Trim().TrimStart('.').ToLowerInvariant();
        return normalized is "jpg" or "jpeg" or "png" or "gif" or "webp" or "bmp" or "tif" or "tiff"
            or "heic" or "heif" or "avif" or "ico" or "raw" or "cr2" or "cr3" or "nef" or "arw" or "dng" or "orf" or "rw2"
            or "mp4" or "mov" or "mkv" or "avi" or "webm" or "m4v" or "wmv" or "flv" or "mpeg" or "mpg" or "3gp" or "mts";
    }

    private static bool IsThumbnailPath(string path)
    {
        return Path.GetFileName(path).Contains("_thumbnail", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeDropboxFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "untitled";
        }

        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\' };
        var sb = new StringBuilder(name.Length);
        foreach (char c in name.Trim())
        {
            sb.Append(invalid.Contains(c) || c < 32 ? '_' : c);
        }

        string result = sb.ToString().Trim().Trim('.');
        return string.IsNullOrWhiteSpace(result) ? "untitled" : result;
    }

    private static async Task EnsureLibraryAsync(DropboxApiClient dbx, DropboxSyncState state, string libraryPath)
    {
        if (!state.Libraries.TryGetValue(libraryPath, out SyncLibraryState? lib))
        {
            string name = Path.GetFileNameWithoutExtension(libraryPath.TrimEnd('/'));
            lib = new SyncLibraryState
            {
                Id = StableLibraryId(libraryPath),
                Name = name,
                Path = libraryPath
            };
            state.Libraries[libraryPath] = lib;
        }

        string metadataPath = $"{libraryPath.TrimEnd('/')}/metadata.json";
        try
        {
            Console.WriteLine($"Downloading library metadata: {metadataPath}");
            lib.MetadataJson = await dbx.DownloadTextAsync(metadataPath);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: cannot download {metadataPath}: {ex.Message}");
        }
    }

    private static async Task ProcessImageMetadataAsync(
        DropboxApiClient dbx,
        DropboxSyncState state,
        string metadataPath,
        string publishTag,
        string cachePath)
    {
        var info = ParseEaglePath(state.RootPath, metadataPath);
        if (info is null || !info.IsImageMetadata)
        {
            return;
        }

        await EnsureLibraryAsync(dbx, state, info.LibraryPath);
        SyncLibraryState lib = state.Libraries[info.LibraryPath];

        string json;
        try
        {
            Console.WriteLine($"Downloading image metadata: {metadataPath}");
            json = await dbx.DownloadTextAsync(metadataPath);
        }
        catch (Exception ex)
        {
            // File gone from Dropbox → drop from published set.
            Console.WriteLine($"Warning: metadata missing, unpublish: {metadataPath} ({ex.Message})");
            RemovePublishedImage(state, metadataPath, cachePath);
            return;
        }

        ImageMetadata? imageMeta;
        try
        {
            imageMeta = JsonSerializer.Deserialize<ImageMetadata>(json, JsonRead);
        }
        catch
        {
            Console.WriteLine($"Warning: invalid metadata JSON: {metadataPath}");
            return;
        }

        if (imageMeta is null || string.IsNullOrWhiteSpace(imageMeta.Id))
        {
            return;
        }

        bool isPublished = !imageMeta.IsDeleted &&
            (imageMeta.Tags ?? []).Any(t =>
                string.Equals(t, publishTag, StringComparison.OrdinalIgnoreCase));

        if (!isPublished)
        {
            RemovePublishedImage(state, metadataPath, cachePath);
            Console.WriteLine($"Unpublished (tag removed / deleted): {metadataPath}");
            return;
        }

        string assetFileName = $"{lib.Id}_{imageMeta.Id}{NormalizeExt(imageMeta.Ext)}";
        string thumbAssetFileName = $"{lib.Id}_{imageMeta.Id}_thumb.png";
        var syncImage = new SyncImageState
        {
            Key = $"{lib.Id}:{imageMeta.Id}",
            LibraryPath = info.LibraryPath,
            LibraryId = lib.Id,
            LibraryName = lib.Name,
            ImageId = imageMeta.Id,
            MetadataPath = metadataPath,
            InfoDirPath = info.InfoDirPath!,
            AssetFileName = assetFileName,
            ThumbnailAssetFileName = thumbAssetFileName,
            Metadata = imageMeta
        };

        var (sourceImage, sourceThumb) = await ResolveSourcePathsAsync(dbx, syncImage);
        syncImage.SourceImagePath = sourceImage;
        syncImage.SourceThumbnailPath = sourceThumb;
        if (!string.IsNullOrWhiteSpace(sourceThumb))
        {
            syncImage.ThumbnailAssetFileName = $"{lib.Id}_{imageMeta.Id}_thumb{Path.GetExtension(sourceThumb)}";
        }

        state.Images[metadataPath] = syncImage;

        await EnsureCachedAssetAsync(dbx, syncImage, cachePath, original: true);
        await EnsureCachedAssetAsync(dbx, syncImage, cachePath, original: false);

        Console.WriteLine($"Published: {syncImage.Key}");
    }

    private static void BackfillThumbnailFields(SyncImageState image)
    {
        if (string.IsNullOrWhiteSpace(image.ThumbnailAssetFileName) &&
            !string.IsNullOrWhiteSpace(image.LibraryId) &&
            !string.IsNullOrWhiteSpace(image.ImageId))
        {
            image.ThumbnailAssetFileName = $"{image.LibraryId}_{image.ImageId}_thumb.png";
        }
    }

    private static async Task EnsureCachedAssetAsync(
        DropboxApiClient dbx,
        SyncImageState image,
        string cachePath,
        bool original)
    {
        if (original)
        {
            if (string.IsNullOrWhiteSpace(image.AssetFileName))
            {
                return;
            }

            string localAsset = Path.Combine(cachePath, "assets", image.AssetFileName);
            if (File.Exists(localAsset))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(image.SourceImagePath))
            {
                var (src, _) = await ResolveSourcePathsAsync(dbx, image);
                image.SourceImagePath = src;
            }

            if (string.IsNullOrWhiteSpace(image.SourceImagePath))
            {
                Console.WriteLine($"Warning: no source image for {image.Key}");
                return;
            }

            Console.WriteLine($"Downloading asset: {image.SourceImagePath}");
            await dbx.DownloadToFileAsync(image.SourceImagePath, localAsset);
            return;
        }

        // Thumbnail path — resolve Dropbox source even for older state entries.
        if (string.IsNullOrWhiteSpace(image.SourceThumbnailPath) ||
            string.IsNullOrWhiteSpace(image.ThumbnailAssetFileName))
        {
            var (_, thumb) = await ResolveSourcePathsAsync(dbx, image);
            image.SourceThumbnailPath = thumb;
            if (!string.IsNullOrWhiteSpace(thumb))
            {
                image.ThumbnailAssetFileName =
                    $"{image.LibraryId}_{image.ImageId}_thumb{Path.GetExtension(thumb)}";
            }
        }

        if (string.IsNullOrWhiteSpace(image.SourceThumbnailPath) ||
            string.IsNullOrWhiteSpace(image.ThumbnailAssetFileName))
        {
            Console.WriteLine($"Warning: no thumbnail for {image.Key}");
            return;
        }

        string localThumb = Path.Combine(cachePath, "assets", image.ThumbnailAssetFileName);
        if (File.Exists(localThumb))
        {
            return;
        }

        Console.WriteLine($"Downloading thumbnail: {image.SourceThumbnailPath}");
        await dbx.DownloadToFileAsync(image.SourceThumbnailPath, localThumb);
    }

    private static async Task<(string? SourceImage, string? SourceThumbnail)> ResolveSourcePathsAsync(
        DropboxApiClient dbx,
        SyncImageState image)
    {
        var (entries, _) = await dbx.ListFolderNonRecursiveAsync(image.InfoDirPath);
        var files = entries.Where(e => e.IsFile).Select(e => e.PathDisplay).ToList();
        string expected = string.IsNullOrWhiteSpace(image.Metadata.Ext)
            ? image.ImageId
            : $"{image.ImageId}.{image.Metadata.Ext.TrimStart('.')}";

        string? preferred = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));

        string? sourceImage = preferred ?? files.FirstOrDefault(f =>
        {
            string name = Path.GetFileName(f);
            if (string.Equals(name, "metadata.json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !name.Contains("_thumbnail", StringComparison.OrdinalIgnoreCase);
        });

        string? sourceThumb = files.FirstOrDefault(f =>
            Path.GetFileName(f).Contains("_thumbnail", StringComparison.OrdinalIgnoreCase));

        return (sourceImage, sourceThumb);
    }

    private static async Task<string?> ResolveSourceImagePathAsync(DropboxApiClient dbx, SyncImageState image)
    {
        var (source, _) = await ResolveSourcePathsAsync(dbx, image);
        return source;
    }

    private static void RemoveByDeletedDropboxPath(DropboxSyncState state, string deletedPath, string cachePath)
    {
        string path = deletedPath.Replace('\\', '/').TrimEnd('/');

        // Direct metadata.json delete
        if (state.Images.ContainsKey(path))
        {
            RemovePublishedImage(state, path, cachePath);
            Console.WriteLine($"Removed published image (deleted metadata): {path}");
            return;
        }

        // Deleted .info folder or any file inside it
        foreach (string metadataPath in state.Images.Keys.ToList())
        {
            SyncImageState image = state.Images[metadataPath];
            string infoDir = image.InfoDirPath.TrimEnd('/');
            if (string.Equals(path, infoDir, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(infoDir + "/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, image.SourceImagePath, StringComparison.OrdinalIgnoreCase))
            {
                RemovePublishedImage(state, metadataPath, cachePath);
                Console.WriteLine($"Removed published image (deleted path {path}): {metadataPath}");
            }
        }
    }

    private static void RemovePublishedImage(DropboxSyncState state, string metadataPath, string cachePath)
    {
        if (!state.Images.TryGetValue(metadataPath, out SyncImageState? image))
        {
            return;
        }

        string localAsset = Path.Combine(cachePath, "assets", image.AssetFileName);
        if (File.Exists(localAsset))
        {
            File.Delete(localAsset);
        }

        if (!string.IsNullOrWhiteSpace(image.ThumbnailAssetFileName))
        {
            string localThumb = Path.Combine(cachePath, "assets", image.ThumbnailAssetFileName);
            if (File.Exists(localThumb))
            {
                File.Delete(localThumb);
            }
        }

        state.Images.Remove(metadataPath);
    }

    public static DropboxPathInfo? ParseEaglePath(string rootPath, string pathDisplay)
    {
        if (string.IsNullOrWhiteSpace(pathDisplay))
        {
            return null;
        }

        string root = NormalizeRoot(rootPath).TrimEnd('/');
        string path = pathDisplay.Replace('\\', '/');
        if (!path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string relative = path.Length == root.Length ? "" : path[(root.Length + 1)..];
        string[] parts = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        string libraryFolder = parts[0];
        if (!libraryFolder.EndsWith(".library", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string libraryPath = $"{root}/{libraryFolder}";
        string libraryName = Path.GetFileNameWithoutExtension(libraryFolder);

        // /Eagle/Foo.library/metadata.json
        if (parts.Length == 2 &&
            string.Equals(parts[1], "metadata.json", StringComparison.OrdinalIgnoreCase))
        {
            return new DropboxPathInfo
            {
                LibraryPath = libraryPath,
                LibraryName = libraryName,
                IsLibraryMetadata = true
            };
        }

        // /Eagle/Foo.library/images/XXX.info/metadata.json
        if (parts.Length == 4 &&
            string.Equals(parts[1], "images", StringComparison.OrdinalIgnoreCase) &&
            parts[2].EndsWith(".info", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(parts[3], "metadata.json", StringComparison.OrdinalIgnoreCase))
        {
            return new DropboxPathInfo
            {
                LibraryPath = libraryPath,
                LibraryName = libraryName,
                IsImageMetadata = true,
                InfoDirPath = $"{libraryPath}/images/{parts[2]}"
            };
        }

        return null;
    }

    private static string StableLibraryId(string libraryPath)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(libraryPath.ToLowerInvariant()));
        return "lib-" + Convert.ToHexString(hash)[..10].ToLowerInvariant();
    }

    private static string NormalizeExt(string? ext)
    {
        if (string.IsNullOrWhiteSpace(ext))
        {
            return "";
        }

        return ext.StartsWith('.') ? ext : "." + ext;
    }

    private static string NormalizeRoot(string path)
    {
        path = path.Replace('\\', '/').Trim();
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "";
        }

        return path.StartsWith('/') ? path.TrimEnd('/') : "/" + path.TrimEnd('/');
    }

    private static DropboxSyncState LoadState(string path)
    {
        if (!File.Exists(path))
        {
            return new DropboxSyncState();
        }

        try
        {
            return JsonSerializer.Deserialize<DropboxSyncState>(File.ReadAllText(path), JsonRead)
                   ?? new DropboxSyncState();
        }
        catch
        {
            Console.WriteLine("Warning: state file invalid, starting fresh.");
            return new DropboxSyncState();
        }
    }

    private static void SaveState(string path, DropboxSyncState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(state, StateJson));
    }

    private static string RequiredEnv(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Missing required environment variable: {name}");
        }

        return value.Trim();
    }

    private static string EnvOrDefault(string name, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
