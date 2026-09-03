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

        Console.WriteLine($"Dropbox root: {rootPath}");
        Console.WriteLine($"Since: {since:o}");
        Console.WriteLine($"Publish tag: {publishTag}");
        Console.WriteLine($"State: {statePath}");

        using var dbx = new DropboxApiClient(appKey, appSecret, refreshToken);
        await dbx.EnsureAccessTokenAsync();

        var touchedLibraryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touchedImageMetaPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!state.BootstrapCompleted || string.IsNullOrWhiteSpace(state.Cursor))
        {
            Console.WriteLine("Bootstrap: listing Dropbox entries and keeping only changes since start date (no full metadata download)...");
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
                    var info = ParseEaglePath(rootPath, entry.PathDisplay);
                    if (info is null)
                    {
                        continue;
                    }

                    if (entry.IsDeleted)
                    {
                        deletedPaths.Add(entry.PathDisplay);
                        if (info.IsImageMetadata)
                        {
                            touchedImageMetaPaths.Add(entry.PathDisplay);
                        }
                        if (info.IsLibraryMetadata || info.IsImageMetadata)
                        {
                            touchedLibraryPaths.Add(info.LibraryPath);
                        }
                        continue;
                    }

                    if (!entry.IsFile)
                    {
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
                }

                Console.WriteLine($"  delta page {page}, entries={entries.Count}");
            }

            state.Cursor = cursor;
        }

        foreach (string deleted in deletedPaths)
        {
            var info = ParseEaglePath(rootPath, deleted);
            if (info?.IsImageMetadata == true)
            {
                RemovePublishedImage(state, deleted, cachePath);
            }
        }

        foreach (string libraryPath in touchedLibraryPaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
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

        // Ensure assets exist for all published images (cache may be cold on CI).
        foreach (var image in state.Images.Values.ToList())
        {
            string localAsset = Path.Combine(cachePath, "assets", image.AssetFileName);
            if (File.Exists(localAsset))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(image.SourceImagePath))
            {
                image.SourceImagePath = await ResolveSourceImagePathAsync(dbx, image);
            }

            if (string.IsNullOrWhiteSpace(image.SourceImagePath))
            {
                Console.WriteLine($"Warning: no source image for {image.Key}");
                continue;
            }

            Console.WriteLine($"Downloading asset: {image.SourceImagePath}");
            await dbx.DownloadToFileAsync(image.SourceImagePath, localAsset);
        }

        SaveState(statePath, state);
        SiteGenerator.WriteFromSyncState(state, cachePath, outputPath, publishTag);

        Console.WriteLine($"Published images in state: {state.Images.Count}");
        Console.WriteLine($"Libraries in state: {state.Libraries.Count}");
        return 0;
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
            Console.WriteLine($"Warning: skip {metadataPath}: {ex.Message}");
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

        bool isPublished = (imageMeta.Tags ?? []).Any(t =>
            string.Equals(t, publishTag, StringComparison.OrdinalIgnoreCase));

        if (!isPublished)
        {
            RemovePublishedImage(state, metadataPath, cachePath);
            Console.WriteLine($"Unpublished (tag removed): {metadataPath}");
            return;
        }

        string assetFileName = $"{lib.Id}_{imageMeta.Id}{NormalizeExt(imageMeta.Ext)}";
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
            Metadata = imageMeta
        };

        syncImage.SourceImagePath = await ResolveSourceImagePathAsync(dbx, syncImage);
        state.Images[metadataPath] = syncImage;

        if (!string.IsNullOrWhiteSpace(syncImage.SourceImagePath))
        {
            string localAsset = Path.Combine(cachePath, "assets", syncImage.AssetFileName);
            Console.WriteLine($"Downloading asset: {syncImage.SourceImagePath}");
            await dbx.DownloadToFileAsync(syncImage.SourceImagePath, localAsset);
        }

        Console.WriteLine($"Published: {syncImage.Key}");
    }

    private static async Task<string?> ResolveSourceImagePathAsync(DropboxApiClient dbx, SyncImageState image)
    {
        var (entries, _) = await dbx.ListFolderNonRecursiveAsync(image.InfoDirPath);
        var files = entries.Where(e => e.IsFile).Select(e => e.PathDisplay).ToList();
        string expected = string.IsNullOrWhiteSpace(image.Metadata.Ext)
            ? image.ImageId
            : $"{image.ImageId}.{image.Metadata.Ext.TrimStart('.')}";

        string? preferred = files.FirstOrDefault(f =>
            string.Equals(Path.GetFileName(f), expected, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null)
        {
            return preferred;
        }

        // Eagle often stores original filename rather than id.ext
        return files.FirstOrDefault(f =>
        {
            string name = Path.GetFileName(f);
            if (string.Equals(name, "metadata.json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (name.Contains("_thumbnail", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        });
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
