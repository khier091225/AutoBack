using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class DriveApi(HttpClient http, GoogleTokenProvider tokens, string accountKey)
{
    public const string Fields = "id,name,mimeType,size,md5Checksum,sha256Checksum,trashed,parents,appProperties";

    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> create, CancellationToken cancellationToken,
        params int[] allowed)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = create();
            request.Headers.Authorization = new("Bearer", await tokens.GetAsync(accountKey, attempt > 0, cancellationToken));
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0) { response.Dispose(); continue; }
            if (response.IsSuccessStatusCode || allowed.Contains((int)response.StatusCode)) return response;
            using (response)
            {
                var code = (int)response.StatusCode;
                if (code == 401) throw new DriveException(DriveFailure.Authorization, "Google rejected authorization. Run drive-connect on this client.");
                var reason = "";
                try
                {
                    using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (error.RootElement.TryGetProperty("error", out var details) && details.TryGetProperty("errors", out var errors) && errors.GetArrayLength() > 0)
                        reason = errors[0].GetProperty("reason").GetString();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException) { }
                if (code is 408 or 429 || code >= 500 || reason is "rateLimitExceeded" or "userRateLimitExceeded" or "backendError")
                    throw new DriveException(DriveFailure.Retryable, "Google Drive is temporarily unavailable or rate limited. The saved upload will retry.");
                if (reason == "storageQuotaExceeded") throw new DriveException(DriveFailure.Permanent, "Google Drive storage is full. Free space, then use drive-retry on this client.");
                if (code == 403) throw new DriveException(DriveFailure.Permanent, "Google Drive denied this operation. Check account policy, folder access, and available storage.");
                throw new DriveException(DriveFailure.Permanent, $"Google Drive returned HTTP {code}. Check the Drive setup, then retry.");
            }
        }
    }

    public async Task<string> GenerateIdAsync(CancellationToken token)
    {
        using var response = await SendAsync(() => new(HttpMethod.Get, "https://www.googleapis.com/drive/v3/files/generateIds?count=1&space=drive&type=files"), token);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        var id = json.RootElement.GetProperty("ids")[0].GetString();
        if (!CloudStates.ValidId(id)) throw new DriveException(DriveFailure.Permanent, "Google returned an invalid file ID.");
        return id!;
    }

    public async Task<RemoteFile?> GetFileAsync(string id, CancellationToken token)
    {
        if (!CloudStates.ValidId(id)) throw new InvalidDataException("Invalid saved Google Drive file ID.");
        using var response = await SendAsync(() => new(HttpMethod.Get, $"https://www.googleapis.com/drive/v3/files/{id}?fields={Fields}"), token, 404);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return ParseFile(json.RootElement);
    }

    private static RemoteFile ParseFile(JsonElement root)
    {
        return new(root.GetProperty("id").GetString()!, root.GetProperty("mimeType").GetString()!,
            root.TryGetProperty("size", out var size) ? long.Parse(size.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : 0,
            root.TryGetProperty("md5Checksum", out var md5) ? md5.GetString() : null,
            root.TryGetProperty("sha256Checksum", out var sha) ? sha.GetString() : null,
            root.TryGetProperty("trashed", out var trashed) && trashed.GetBoolean(),
            root.TryGetProperty("parents", out var parents) ? parents.EnumerateArray().Select(p => p.GetString()!).ToArray() : [],
            root.TryGetProperty("appProperties", out var props) ? props.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!) : [],
            root.TryGetProperty("name", out var name) ? name.GetString() : null);
    }

    public async Task<List<RemoteFile>> ListAsync(string query, CancellationToken token)
    {
        var result = new List<RemoteFile>();
        string? page = null;
        var pages = new HashSet<string>(StringComparer.Ordinal);
        do
        {
            var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&spaces=drive&pageSize=100&fields=nextPageToken,files({Fields})";
            if (page is not null) url += "&pageToken=" + Uri.EscapeDataString(page);
            using var response = await SendAsync(() => new(HttpMethod.Get, url), token);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            result.AddRange(json.RootElement.GetProperty("files").EnumerateArray().Select(ParseFile));
            page = json.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (page is not null && !pages.Add(page)) throw new InvalidDataException("Drive returned a repeated listing page.");
        } while (!string.IsNullOrEmpty(page));
        return result;
    }

    public async Task PatchAsync(string id, object metadata, CancellationToken token)
    {
        if (!CloudStates.ValidId(id)) throw new InvalidDataException("Invalid Drive ID.");
        using var response = await SendAsync(() => new(new HttpMethod("PATCH"), $"https://www.googleapis.com/drive/v3/files/{id}")
        { Content = JsonContent.Create(metadata) }, token);
    }

    public async Task<string?> FindRootAsync(CancellationToken token)
    {
        var files = await ListAsync("trashed = false and mimeType = 'application/vnd.google-apps.folder' and appProperties has { key='autobackRole' and value='root' }", token);
        return files.OrderBy(f => f.Id, StringComparer.Ordinal).FirstOrDefault()?.Id;
    }

    public async Task EnsureFolderAsync(string id, string name, string? parent, string role, Guid agentId, CancellationToken token, string? device = null, string? pathKey = null)
    {
        var existing = await GetFileAsync(id, token);
        if (existing is null)
        {
            var properties = new Dictionary<string, string> { ["autobackRole"] = role, ["autobackAgent"] = agentId.ToString("N") };
            if (device is not null) properties["autobackDevice"] = device;
            if (pathKey is not null) properties["autobackPath"] = pathKey;
            var metadata = new Dictionary<string, object>
            {
                ["id"] = id, ["name"] = name, ["mimeType"] = "application/vnd.google-apps.folder",
                ["appProperties"] = properties
            };
            if (parent is not null) metadata["parents"] = new[] { parent };
            using var created = await SendAsync(() => new(HttpMethod.Post, "https://www.googleapis.com/drive/v3/files") { Content = JsonContent.Create(metadata) }, token, 409);
            existing = await GetFileAsync(id, token);
        }
        if (existing is null || existing.Trashed || existing.MimeType != "application/vnd.google-apps.folder" ||
            existing.Properties.GetValueOrDefault("autobackRole") != role || (parent is not null && !existing.Parents.Contains(parent)) ||
            (role != "root" && !(device is not null && existing.Properties.GetValueOrDefault("autobackDevice") == device) && existing.Properties.GetValueOrDefault("autobackAgent") != agentId.ToString("N")) ||
            (pathKey is not null && existing.Properties.GetValueOrDefault("autobackPath") != pathKey))
            throw new DriveException(DriveFailure.Permanent, "The Drive folder has unexpected ownership or metadata. Backup stopped to protect other files.");
        if (existing.Name != name || (device is not null && existing.Properties.GetValueOrDefault("autobackDevice") != device))
        {
            var properties = new Dictionary<string, string>(existing.Properties);
            if (device is not null) properties["autobackDevice"] = device;
            await PatchAsync(id, new { name, appProperties = properties }, token);
        }
    }
}
