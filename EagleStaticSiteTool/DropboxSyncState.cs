public sealed class DropboxSyncState
{
    public string? Cursor { get; set; }
    public string RootPath { get; set; } = "/Eagle";
    public string Since { get; set; } = "2026-09-01T00:00:00+08:00";
    public bool BootstrapCompleted { get; set; }
    public Dictionary<string, SyncLibraryState> Libraries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, SyncImageState> Images { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SyncLibraryState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string? MetadataJson { get; set; }
}

public sealed class SyncImageState
{
    public string Key { get; set; } = "";
    public string LibraryPath { get; set; } = "";
    public string LibraryId { get; set; } = "";
    public string LibraryName { get; set; } = "";
    public string ImageId { get; set; } = "";
    public string MetadataPath { get; set; } = "";
    public string InfoDirPath { get; set; } = "";
    public string? SourceImagePath { get; set; }
    public string AssetFileName { get; set; } = "";
    public ImageMetadata Metadata { get; set; } = new();
}

public sealed class DropboxPathInfo
{
    public string LibraryPath { get; init; } = "";
    public string LibraryName { get; init; } = "";
    public bool IsLibraryMetadata { get; init; }
    public bool IsImageMetadata { get; init; }
    public string? InfoDirPath { get; init; }
}
