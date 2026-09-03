using System.Text.Json;

if (args.Length >= 1 && string.Equals(args[0], "dropbox", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        return await DropboxPublishCommand.RunAsync(args.Skip(1).ToArray());
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.ToString());
        return 1;
    }
}

if (args.Length < 2)
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  EagleStaticSiteTool <LibraryPathOrRootPath> <OutputDirectory> [PublishTag]");
    Console.WriteLine("  EagleStaticSiteTool dropbox [OutputDirectory] [PublishTag]");
    Console.WriteLine("Example local: EagleStaticSiteTool . ./EagleSiteOutput 发布");
    Console.WriteLine("Example dropbox: EagleStaticSiteTool dropbox ./EagleSiteOutput 发布");
    return 1;
}

string inputPath = Path.GetFullPath(args[0]);
string outputPath = Path.GetFullPath(args[1]);
string publishTag = args.Length >= 3 ? args[2] : "发布";

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

List<string> libraries = ResolveLibraryPaths(inputPath);
if (libraries.Count == 0)
{
    Console.WriteLine($"No valid Eagle libraries found from: {inputPath}");
    return 1;
}

Directory.CreateDirectory(outputPath);
string assetsOutput = Path.Combine(outputPath, "assets");
Directory.CreateDirectory(assetsOutput);

var librariesData = new List<LibraryDto>();
var flatFolders = new List<FolderDto>();
var imageItems = new List<ImageItemDto>();

for (int i = 0; i < libraries.Count; i++)
{
    string libraryPath = libraries[i];
    string libraryName = Path.GetFileNameWithoutExtension(libraryPath);
    string libraryId = $"lib-{i + 1}";

    string libraryMetadataPath = Path.Combine(libraryPath, "metadata.json");
    if (!File.Exists(libraryMetadataPath))
    {
        continue;
    }

    LibraryMetadata libraryMetadata = JsonSerializer.Deserialize<LibraryMetadata>(
        File.ReadAllText(libraryMetadataPath), jsonOptions) ?? new LibraryMetadata();

    var folderIdMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    SiteGenerator.FlattenFolders(libraryMetadata.Folders, 0, null, libraryId, libraryName, flatFolders, folderIdMap);

    string imagesRoot = Path.Combine(libraryPath, "images");
    if (!Directory.Exists(imagesRoot))
    {
        continue;
    }

    int publishedCountForLibrary = 0;
    var infoDirectories = Directory.GetDirectories(imagesRoot, "*.info", SearchOption.TopDirectoryOnly);
    foreach (string infoDir in infoDirectories)
    {
        string metadataPath = Path.Combine(infoDir, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            continue;
        }

        ImageMetadata? imageMeta;
        try
        {
            imageMeta = JsonSerializer.Deserialize<ImageMetadata>(File.ReadAllText(metadataPath), jsonOptions);
        }
        catch
        {
            continue;
        }

        if (imageMeta is null || string.IsNullOrWhiteSpace(imageMeta.Id))
        {
            continue;
        }

        bool isPublished = (imageMeta.Tags ?? []).Any(t => string.Equals(t, publishTag, StringComparison.OrdinalIgnoreCase));
        if (!isPublished)
        {
            continue;
        }

        string? sourceImagePath = FindSourceImage(infoDir, imageMeta);
        string? relativeAssetPath = null;

        if (sourceImagePath is not null)
        {
            string ext = Path.GetExtension(sourceImagePath);
            string targetName = $"{libraryId}_{imageMeta.Id}{ext}";
            string targetPath = Path.Combine(assetsOutput, targetName);
            File.Copy(sourceImagePath, targetPath, overwrite: true);
            relativeAssetPath = $"assets/{targetName}";
        }

        imageItems.Add(new ImageItemDto
        {
            Id = $"{libraryId}:{imageMeta.Id}",
            LibraryId = libraryId,
            LibraryName = libraryName,
            Name = imageMeta.Name ?? imageMeta.Id,
            Ext = imageMeta.Ext ?? "",
            Size = imageMeta.Size,
            Width = imageMeta.Width,
            Height = imageMeta.Height,
            Url = imageMeta.Url ?? "",
            Annotation = imageMeta.Annotation ?? "",
            Tags = imageMeta.Tags ?? [],
            FolderIds = (imageMeta.Folders ?? []).Select(fid =>
                folderIdMap.TryGetValue(fid, out string? mapped) ? mapped : $"{libraryId}:{fid}").ToList(),
            FolderPath = string.Empty,
            ModificationTime = imageMeta.ModificationTime,
            Btime = imageMeta.Btime,
            Mtime = imageMeta.Mtime,
            LastModified = imageMeta.LastModified,
            SearchTokens = string.Empty,
            ImagePath = relativeAssetPath
        });
        publishedCountForLibrary++;
    }

    librariesData.Add(new LibraryDto
    {
        Id = libraryId,
        Name = libraryName,
        Path = libraryPath,
        ImageCount = publishedCountForLibrary
    });
}

imageItems = imageItems
    .OrderByDescending(i => i.ModificationTime)
    .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
    .ToList();

var folderLookup = flatFolders.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
foreach (ImageItemDto image in imageItems)
{
    image.FolderPath = SiteGenerator.BuildPrimaryFolderPath(image.FolderIds, folderLookup);
    image.SearchTokens = SiteGenerator.BuildSearchTokens(image);
}

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
    Libraries = librariesData,
    Folders = flatFolders,
    AllTags = allTags,
    Images = imageItems
};

SiteGenerator.WriteSite(siteData, outputPath);
Console.WriteLine($"Done. Generated static site to: {outputPath}");
Console.WriteLine($"Publish tag: {publishTag}");
Console.WriteLine($"Libraries merged: {librariesData.Count}");
Console.WriteLine($"Published images: {imageItems.Count}");
Console.WriteLine($"Images with source file: {imageItems.Count(i => !string.IsNullOrWhiteSpace(i.ImagePath))}/{imageItems.Count}");
return 0;

static List<string> ResolveLibraryPaths(string pathArg)
{
    var inputs = pathArg.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var result = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (string raw in inputs)
    {
        string candidate = Path.GetFullPath(raw);
        if (!Directory.Exists(candidate))
        {
            continue;
        }

        bool isLibrary = candidate.EndsWith(".library", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(candidate, "metadata.json"));
        if (isLibrary)
        {
            if (seen.Add(candidate))
            {
                result.Add(candidate);
            }
            continue;
        }

        foreach (string dir in Directory.GetDirectories(candidate, "*.library", SearchOption.TopDirectoryOnly))
        {
            if (File.Exists(Path.Combine(dir, "metadata.json")) && seen.Add(dir))
            {
                result.Add(dir);
            }
        }
    }

    return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
}

static string? FindSourceImage(string infoDir, ImageMetadata imageMeta)
{
    var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metadata.json" };
    var files = Directory.GetFiles(infoDir);
    string expectedBase = imageMeta.Id ?? "";
    string expectedExt = imageMeta.Ext ?? "";
    string expectedFile = string.IsNullOrWhiteSpace(expectedExt) ? expectedBase : $"{expectedBase}.{expectedExt}";

    string? preferred = files.FirstOrDefault(f =>
        string.Equals(Path.GetFileName(f), expectedFile, StringComparison.OrdinalIgnoreCase));
    if (preferred is not null)
    {
        return preferred;
    }

    return files.FirstOrDefault(f =>
    {
        string name = Path.GetFileName(f);
        if (ignored.Contains(name))
        {
            return false;
        }

        return !name.Contains("_thumbnail", StringComparison.OrdinalIgnoreCase);
    });
}
