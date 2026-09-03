using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

public static class SiteGenerator
{
    private static readonly JsonSerializerOptions SiteJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static void WriteSite(SiteDataDto siteData, string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        string siteDataJson = JsonSerializer.Serialize(siteData, SiteJsonOptions);
        File.WriteAllText(Path.Combine(outputPath, "data.json"), siteDataJson);
        File.WriteAllText(Path.Combine(outputPath, "data.js"), $"window.__EAGLE_SITE_DATA__ = {siteDataJson};");
        File.WriteAllText(Path.Combine(outputPath, "index.html"), ReadAsset("index.html"));
        File.WriteAllText(Path.Combine(outputPath, "styles.css"), ReadAsset("styles.css"));
        File.WriteAllText(Path.Combine(outputPath, "app.js"), ReadAsset("app.js"));
    }

    public static void WriteFromSyncState(
        DropboxSyncState state,
        string cachePath,
        string outputPath,
        string publishTag)
    {
        Directory.CreateDirectory(outputPath);
        string assetsOutput = Path.Combine(outputPath, "assets");
        Directory.CreateDirectory(assetsOutput);

        var librariesData = new List<LibraryDto>();
        var flatFolders = new List<FolderDto>();
        var imageItems = new List<ImageItemDto>();

        foreach (SyncLibraryState lib in state.Libraries.Values.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase))
        {
            var folderIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(lib.MetadataJson))
            {
                try
                {
                    LibraryMetadata? libraryMetadata =
                        JsonSerializer.Deserialize<LibraryMetadata>(lib.MetadataJson, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });
                    FlattenFolders(libraryMetadata?.Folders, 0, null, lib.Id, lib.Name, flatFolders, folderIdMap);
                }
                catch
                {
                    Console.WriteLine($"Warning: invalid library metadata for {lib.Path}");
                }
            }

            var libImages = state.Images.Values
                .Where(i => string.Equals(i.LibraryPath, lib.Path, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (SyncImageState image in libImages)
            {
                string cacheAsset = Path.Combine(cachePath, "assets", image.AssetFileName);
                string? relativeAssetPath = null;
                if (File.Exists(cacheAsset))
                {
                    string targetPath = Path.Combine(assetsOutput, image.AssetFileName);
                    File.Copy(cacheAsset, targetPath, overwrite: true);
                    relativeAssetPath = $"assets/{image.AssetFileName}";
                }

                ImageMetadata meta = image.Metadata;
                imageItems.Add(new ImageItemDto
                {
                    Id = image.Key,
                    LibraryId = lib.Id,
                    LibraryName = lib.Name,
                    Name = meta.Name ?? meta.Id ?? image.ImageId,
                    Ext = meta.Ext ?? "",
                    Size = meta.Size,
                    Width = meta.Width,
                    Height = meta.Height,
                    Url = meta.Url ?? "",
                    Annotation = meta.Annotation ?? "",
                    Tags = meta.Tags ?? [],
                    FolderIds = (meta.Folders ?? []).Select(fid =>
                        folderIdMap.TryGetValue(fid, out string? mapped) ? mapped : $"{lib.Id}:{fid}").ToList(),
                    FolderPath = string.Empty,
                    ModificationTime = meta.ModificationTime,
                    Btime = meta.Btime,
                    Mtime = meta.Mtime,
                    LastModified = meta.LastModified,
                    SearchTokens = string.Empty,
                    ImagePath = relativeAssetPath
                });
            }

            librariesData.Add(new LibraryDto
            {
                Id = lib.Id,
                Name = lib.Name,
                Path = lib.Path,
                ImageCount = libImages.Count
            });
        }

        // Libraries that only appear via images
        foreach (SyncImageState image in state.Images.Values)
        {
            if (librariesData.Any(l => l.Id == image.LibraryId))
            {
                continue;
            }

            librariesData.Add(new LibraryDto
            {
                Id = image.LibraryId,
                Name = image.LibraryName,
                Path = image.LibraryPath,
                ImageCount = state.Images.Values.Count(i => i.LibraryId == image.LibraryId)
            });
        }

        // Only keep libraries/folders that actually have published images.
        librariesData = librariesData.Where(l => l.ImageCount > 0).ToList();
        var libraryIdsWithImages = librariesData.Select(l => l.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        flatFolders = flatFolders.Where(f => libraryIdsWithImages.Contains(f.LibraryId)).ToList();

        imageItems = imageItems
            .OrderByDescending(i => i.ModificationTime)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var folderLookup = flatFolders.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        foreach (ImageItemDto image in imageItems)
        {
            image.FolderPath = BuildPrimaryFolderPath(image.FolderIds, folderLookup);
            image.SearchTokens = BuildSearchTokens(image);
        }

        flatFolders = FilterFoldersWithPublishedImages(flatFolders, imageItems);

        List<string> allTags = imageItems
            .SelectMany(i => i.Tags)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var siteData = new SiteDataDto
        {
            SiteName = "Eagle Multi Library",
            GeneratedAt = DateTimeOffset.Now,
            PublishTag = publishTag,
            Libraries = librariesData.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            Folders = flatFolders,
            AllTags = allTags,
            Images = imageItems
        };

        WriteSite(siteData, outputPath);
        Console.WriteLine($"Done. Generated static site to: {outputPath}");
        Console.WriteLine($"Publish tag: {publishTag}");
        Console.WriteLine($"Libraries: {siteData.Libraries.Count}");
        Console.WriteLine($"Published images: {siteData.Images.Count}");
        Console.WriteLine($"Images with source file: {siteData.Images.Count(i => !string.IsNullOrWhiteSpace(i.ImagePath))}/{siteData.Images.Count}");
    }

    public static List<FolderDto> FilterFoldersWithPublishedImages(
        List<FolderDto> folders,
        IEnumerable<ImageItemDto> images)
    {
        var byId = folders.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (ImageItemDto image in images)
        {
            foreach (string folderId in image.FolderIds)
            {
                string? currentId = folderId;
                while (!string.IsNullOrWhiteSpace(currentId) && byId.TryGetValue(currentId, out FolderDto? folder))
                {
                    if (!keep.Add(folder.Id))
                    {
                        break;
                    }

                    currentId = folder.ParentId;
                }
            }
        }

        return folders.Where(f => keep.Contains(f.Id)).ToList();
    }

    public static void FlattenFolders(
        IEnumerable<FolderNode>? folders,
        int level,
        string? parentId,
        string libraryId,
        string libraryName,
        List<FolderDto> output,
        Dictionary<string, string> folderIdMap)
    {
        if (folders is null)
        {
            return;
        }

        foreach (FolderNode folder in folders)
        {
            string rawId = folder.Id ?? Guid.NewGuid().ToString("N");
            string compositeId = $"{libraryId}:{rawId}";
            folderIdMap[rawId] = compositeId;
            output.Add(new FolderDto
            {
                Id = compositeId,
                RawId = rawId,
                Name = folder.Name ?? "",
                Level = level,
                ParentId = parentId,
                LibraryId = libraryId,
                LibraryName = libraryName
            });
            FlattenFolders(folder.Children, level + 1, compositeId, libraryId, libraryName, output, folderIdMap);
        }
    }

    public static string BuildPrimaryFolderPath(IEnumerable<string> folderIds, Dictionary<string, FolderDto> folderLookup)
    {
        foreach (string folderId in folderIds)
        {
            if (!folderLookup.TryGetValue(folderId, out FolderDto? folder))
            {
                continue;
            }

            var stack = new Stack<string>();
            FolderDto? current = folder;
            while (current is not null)
            {
                stack.Push(current.Name);
                current = current.ParentId is not null && folderLookup.TryGetValue(current.ParentId, out FolderDto? parent)
                    ? parent
                    : null;
            }

            return string.Join(" / ", stack);
        }

        return string.Empty;
    }

    public static string BuildSearchTokens(ImageItemDto image)
    {
        var parts = new List<string>
        {
            image.Name,
            image.LibraryName,
            image.FolderPath,
            image.Annotation,
            image.Url
        };
        parts.AddRange(image.Tags);
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p))).ToLowerInvariant();
    }

    private static string ReadAsset(string fileName)
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "SiteAssets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "EagleStaticSiteTool", "SiteAssets", fileName),
            Path.Combine(Directory.GetCurrentDirectory(), "SiteAssets", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "SiteAssets", fileName))
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Site asset not found: {fileName}. Looked in: {string.Join(" | ", candidates)}");
    }
}
