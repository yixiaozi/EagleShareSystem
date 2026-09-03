using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

public sealed class DropboxApiClient : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly string _appKey;
    private readonly string _appSecret;
    private readonly string _refreshToken;
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public DropboxApiClient(string appKey, string appSecret, string refreshToken)
    {
        _appKey = appKey;
        _appSecret = appSecret;
        _refreshToken = refreshToken;
    }

    public async Task EnsureAccessTokenAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(_accessToken) && DateTimeOffset.UtcNow < _accessTokenExpiresAt.AddMinutes(-2))
        {
            return;
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.dropboxapi.com/oauth2/token");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = _refreshToken,
            ["client_id"] = _appKey,
            ["client_secret"] = _appSecret
        });

        using var resp = await SendWithRetryAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Dropbox token refresh failed ({(int)resp.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        _accessToken = doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Dropbox token response missing access_token.");
        int expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 14400;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
    }

    public async Task<string> GetLatestCursorAsync(string path, CancellationToken ct = default)
    {
        await EnsureAccessTokenAsync(ct);
        var payload = new { path = NormalizePath(path), recursive = true };
        using var doc = await PostApiAsync("https://api.dropboxapi.com/2/files/list_folder/get_latest_cursor", payload, ct);
        return doc.RootElement.GetProperty("cursor").GetString()
            ?? throw new InvalidOperationException("get_latest_cursor returned empty cursor.");
    }

    public async Task<(List<DropboxEntry> Entries, string Cursor, bool HasMore)> ListFolderAsync(
        string path,
        string? cursor,
        CancellationToken ct = default)
    {
        await EnsureAccessTokenAsync(ct);
        JsonDocument doc;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            var payload = new
            {
                path = NormalizePath(path),
                recursive = true,
                include_deleted = true,
                limit = 2000
            };
            doc = await PostApiAsync("https://api.dropboxapi.com/2/files/list_folder", payload, ct);
        }
        else
        {
            doc = await PostApiAsync("https://api.dropboxapi.com/2/files/list_folder/continue", new { cursor }, ct);
        }

        using (doc)
        {
            var entries = ParseEntries(doc.RootElement.GetProperty("entries"));
            string nextCursor = doc.RootElement.GetProperty("cursor").GetString() ?? cursor ?? "";
            bool hasMore = doc.RootElement.GetProperty("has_more").GetBoolean();
            return (entries, nextCursor, hasMore);
        }
    }

    public async Task<(List<DropboxEntry> Entries, string Cursor)> ListFolderNonRecursiveAsync(
        string path,
        CancellationToken ct = default)
    {
        await EnsureAccessTokenAsync(ct);
        string? cursor = null;
        bool hasMore = true;
        var all = new List<DropboxEntry>();
        string lastCursor = "";

        while (hasMore)
        {
            JsonDocument doc;
            if (cursor is null)
            {
                doc = await PostApiAsync(
                    "https://api.dropboxapi.com/2/files/list_folder",
                    new
                    {
                        path = NormalizePath(path),
                        recursive = false,
                        include_deleted = false,
                        limit = 2000
                    },
                    ct);
            }
            else
            {
                doc = await PostApiAsync(
                    "https://api.dropboxapi.com/2/files/list_folder/continue",
                    new { cursor },
                    ct);
            }

            using (doc)
            {
                all.AddRange(ParseEntries(doc.RootElement.GetProperty("entries")));
                lastCursor = doc.RootElement.GetProperty("cursor").GetString() ?? lastCursor;
                hasMore = doc.RootElement.GetProperty("has_more").GetBoolean();
                cursor = lastCursor;
            }
        }

        return (all, lastCursor);
    }

    public async Task DownloadToFileAsync(string dropboxPath, string localPath, CancellationToken ct = default)
    {
        await EnsureAccessTokenAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://content.dropboxapi.com/2/files/download");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        string arg = JsonSerializer.Serialize(new { path = NormalizePath(dropboxPath) });
        req.Headers.Add("Dropbox-API-Arg", arg);

        using var resp = await SendWithRetryAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            string err = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Download failed for {dropboxPath} ({(int)resp.StatusCode}): {err}");
        }

        await using var fs = File.Create(localPath);
        await resp.Content.CopyToAsync(fs, ct);
    }

    public async Task<string> DownloadTextAsync(string dropboxPath, CancellationToken ct = default)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"eagle-dbx-{Guid.NewGuid():N}.json");
        try
        {
            await DownloadToFileAsync(dropboxPath, temp, ct);
            return await File.ReadAllTextAsync(temp, ct);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private async Task<JsonDocument> PostApiAsync(string url, object payload, CancellationToken ct)
    {
        await EnsureAccessTokenAsync(ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var resp = await SendWithRetryAsync(req, ct);
        string body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Dropbox API failed ({(int)resp.StatusCode}) {url}: {body}");
        }

        return JsonDocument.Parse(body);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct)
    {
        const int maxAttempts = 6;
        HttpResponseMessage? lastResponse = null;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            lastResponse?.Dispose();
            using var req = await CloneRequestAsync(request, ct);
            lastResponse = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)lastResponse.StatusCode;
            if (code != 429 && code < 500)
            {
                return lastResponse;
            }

            int delayMs = Math.Min(30_000, 1000 * (int)Math.Pow(2, attempt));
            Console.WriteLine($"Dropbox transient error {code}, retry in {delayMs}ms...");
            await Task.Delay(delayMs, ct);
        }

        return lastResponse!;
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (request.Content is not null)
        {
            byte[] bytes = await request.Content.ReadAsByteArrayAsync(ct);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in request.Content.Headers)
            {
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return clone;
    }

    private static List<DropboxEntry> ParseEntries(JsonElement entries)
    {
        var list = new List<DropboxEntry>();
        foreach (var el in entries.EnumerateArray())
        {
            string tag = el.TryGetProperty(".tag", out var t) ? (t.GetString() ?? "") : "";
            string path = el.TryGetProperty("path_display", out var pd)
                ? (pd.GetString() ?? "")
                : (el.TryGetProperty("path_lower", out var pl) ? (pl.GetString() ?? "") : "");

            DateTimeOffset? serverModified = null;
            if (el.TryGetProperty("server_modified", out var sm) && sm.ValueKind == JsonValueKind.String)
            {
                if (DateTimeOffset.TryParse(sm.GetString(), out var parsed))
                {
                    serverModified = parsed;
                }
            }

            list.Add(new DropboxEntry
            {
                Tag = tag,
                PathDisplay = path,
                ServerModified = serverModified
            });
        }

        return list;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "";
        }

        return path.StartsWith('/') ? path : "/" + path;
    }

    public void Dispose() => _http.Dispose();
}

public sealed class DropboxEntry
{
    public string Tag { get; set; } = "";
    public string PathDisplay { get; set; } = "";
    public DateTimeOffset? ServerModified { get; set; }

    public bool IsDeleted => string.Equals(Tag, "deleted", StringComparison.OrdinalIgnoreCase);
    public bool IsFile => string.Equals(Tag, "file", StringComparison.OrdinalIgnoreCase);
    public bool IsFolder => string.Equals(Tag, "folder", StringComparison.OrdinalIgnoreCase);
}
