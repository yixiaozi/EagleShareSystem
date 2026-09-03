using System.Text.Json.Serialization;

public sealed class LibraryMetadata
{
    [JsonPropertyName("folders")]
    public List<FolderNode> Folders { get; set; } = [];
}

public sealed class FolderNode
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("children")]
    public List<FolderNode>? Children { get; set; } = [];
}

public sealed class ImageMetadata
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("ext")]
    public string? Ext { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("height")]
    public int Height { get; set; }

    [JsonPropertyName("folders")]
    public List<string>? Folders { get; set; } = [];

    [JsonPropertyName("tags")]
    public List<string>? Tags { get; set; } = [];

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("annotation")]
    public string? Annotation { get; set; }

    [JsonPropertyName("modificationTime")]
    public long ModificationTime { get; set; }

    [JsonPropertyName("btime")]
    public long Btime { get; set; }

    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }

    [JsonPropertyName("lastModified")]
    public long LastModified { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }
}

public sealed class SiteDataDto
{
    public string SiteName { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public string PublishTag { get; set; } = string.Empty;
    public List<LibraryDto> Libraries { get; set; } = [];
    public List<FolderDto> Folders { get; set; } = [];
    public List<string> AllTags { get; set; } = [];
    public List<ImageItemDto> Images { get; set; } = [];
}

public sealed class LibraryDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int ImageCount { get; set; }
}

public sealed class FolderDto
{
    public string Id { get; set; } = string.Empty;
    public string RawId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public string? ParentId { get; set; }
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
}

public sealed class ImageItemDto
{
    public string Id { get; set; } = string.Empty;
    public string LibraryId { get; set; } = string.Empty;
    public string LibraryName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Ext { get; set; } = string.Empty;
    public long Size { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Url { get; set; } = string.Empty;
    public string Annotation { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public List<string> FolderIds { get; set; } = [];
    public string FolderPath { get; set; } = string.Empty;
    public long ModificationTime { get; set; }
    public long Btime { get; set; }
    public long Mtime { get; set; }
    public long LastModified { get; set; }
    public string SearchTokens { get; set; } = string.Empty;
    public string? ImagePath { get; set; }
}
