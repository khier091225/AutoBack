using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutoBack.Drive.Tests;

// Deterministic Google HTTP responses; this handler never contacts Google or another network service.
internal sealed class FakeGoogle : HttpMessageHandler
{
    private sealed class Session(string id, JsonElement metadata)
    {
        public string FileId { get; } = id;
        public JsonElement Metadata { get; } = metadata;
        public MemoryStream Data { get; } = new();
    }
    private readonly Dictionary<string, object> _files = [];
    private readonly Dictionary<string, Session> _sessions = [];
    private int _id;
    private int _session;
    public bool LoseFirstChunkResponse { get; set; }
    public bool LoseFinalResponse { get; set; }
    public bool ExpireNextStatus { get; set; }
    public bool BadChecksum { get; set; }
    public bool RejectRefresh { get; set; }
    public bool RejectFirstApiToken { get; set; }
    public bool RateLimitOnce { get; set; }
    public bool FullStorage { get; set; }
    public bool EvilLocation { get; set; }
    public int Refreshes { get; private set; }
    public int SessionsStarted => _session;
    public int BinaryFiles { get; private set; }
    public int DataRequests { get; private set; }
    public List<long> ChunkOffsets { get; } = [];
    public List<string> UploadIds { get; } = [];
    public string? ExpectedChallenge { get; set; }
    public bool CodeVerifierChecked { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "oauth2.googleapis.com")
        {
            var form = Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (form["grant_type"] == "authorization_code")
            {
                CodeVerifierChecked = form["code"] == "test-code" && GoogleAuthorization.Challenge(form["code_verifier"]) == ExpectedChallenge;
                return Json(new { access_token = "ACCESS_SENTINEL", refresh_token = "REFRESH_SENTINEL", expires_in = 3600, scope = DriveHttp.Scope });
            }
            Refreshes++;
            return RejectRefresh ? Json(new { error = "invalid_grant" }, 400) : Json(new { access_token = "ACCESS_SENTINEL", expires_in = 3600 });
        }
        if (uri.Host != "www.googleapis.com") throw new InvalidOperationException("An access token was sent to an unexpected host.");
        if (request.Headers.Authorization?.Parameter != "ACCESS_SENTINEL") return Json(new { }, 401);
        if (RejectFirstApiToken) { RejectFirstApiToken = false; return Json(new { }, 401); }
        if (uri.AbsolutePath.EndsWith("/about", StringComparison.Ordinal)) return Json(new { user = new { emailAddress = "backup@example.invalid", permissionId = "user-one" } });
        if (uri.AbsolutePath.EndsWith("/generateIds", StringComparison.Ordinal)) return Json(new { ids = new[] { "generated-" + ++_id } });
        if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Get)
        {
            var roots = _files.Where(p => JsonSerializer.Serialize(p.Value).Contains("\"root\"", StringComparison.Ordinal)).Select(p => new { id = p.Key }).ToArray();
            return Json(new { files = roots });
        }
        if (uri.AbsolutePath == "/drive/v3/files" && request.Method == HttpMethod.Post)
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var metadata = doc.RootElement.Clone();
            var id = metadata.GetProperty("id").GetString()!;
            if (_files.ContainsKey(id)) return Json(new { }, 409);
            _files[id] = new
            {
                id, mimeType = metadata.GetProperty("mimeType").GetString(), trashed = false,
                parents = metadata.TryGetProperty("parents", out var parents) ? parents.Deserialize<string[]>() : [],
                appProperties = metadata.GetProperty("appProperties").Deserialize<Dictionary<string, string>>()
            };
            return Json(new { id });
        }
        if (uri.AbsolutePath.StartsWith("/drive/v3/files/", StringComparison.Ordinal))
            return _files.TryGetValue(uri.Segments.Last(), out var file) ? Json(file) : Json(new { }, 404);
        if (uri.AbsolutePath == "/upload/drive/v3/files" && request.Method == HttpMethod.Post)
        {
            if (FullStorage) return Json(new { error = new { errors = new[] { new { reason = "storageQuotaExceeded" } } } }, 403);
            if (RateLimitOnce) { RateLimitOnce = false; return Json(new { }, 429); }
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var metadata = doc.RootElement.Clone();
            var id = metadata.GetProperty("id").GetString()!;
            UploadIds.Add(id);
            var session = "secret-session-" + ++_session;
            _sessions[session] = new(id, metadata);
            var result = Json(new { });
            result.Headers.Location = new(EvilLocation ? "https://example.invalid/steal" : "https://www.googleapis.com/upload/drive/v3/files?upload_id=" + session);
            return result;
        }
        if (uri.AbsolutePath == "/upload/drive/v3/files" && request.Method == HttpMethod.Put)
        {
            var key = Parse(uri.Query.TrimStart('?'))["upload_id"];
            if (!_sessions.TryGetValue(key, out var session)) return Json(new { }, 404);
            var range = request.Content!.Headers.ContentRange!;
            if (!range.HasRange)
            {
                if (ExpireNextStatus && session.Data.Length > 0) { ExpireNextStatus = false; _sessions.Remove(key); return Json(new { }, 404); }
                return Incomplete(session.Data.Length);
            }
            DataRequests++;
            var offset = range.From!.Value;
            ChunkOffsets.Add(offset);
            if (offset != session.Data.Length) throw new InvalidOperationException("Upload resumed at an incorrect server offset.");
            var content = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            session.Data.Write(content);
            if (LoseFirstChunkResponse) { LoseFirstChunkResponse = false; throw new HttpRequestException("Simulated connection loss after the server stored a chunk."); }
            if (session.Data.Length < range.Length) return Incomplete(session.Data.Length);
            var bytes = session.Data.ToArray();
            _files[session.FileId] = new
            {
                id = session.FileId, mimeType = "application/zip", size = bytes.Length.ToString(), trashed = false,
                md5Checksum = BadChecksum ? new string('0', 32) : Convert.ToHexString(MD5.HashData(bytes)),
                sha256Checksum = Convert.ToHexString(SHA256.HashData(bytes)),
                parents = session.Metadata.GetProperty("parents").Deserialize<string[]>(),
                appProperties = session.Metadata.GetProperty("appProperties").Deserialize<Dictionary<string, string>>()
            };
            BinaryFiles++;
            if (LoseFinalResponse) { LoseFinalResponse = false; throw new HttpRequestException("Simulated lost final upload response."); }
            return Json(new { id = session.FileId });
        }
        throw new InvalidOperationException("Unexpected Google API request shape: " + uri.AbsolutePath);
    }

    private static HttpResponseMessage Incomplete(long length)
    {
        var response = Json(new { }, 308);
        if (length > 0) response.Headers.TryAddWithoutValidation("Range", $"bytes=0-{length - 1}");
        return response;
    }
    private static HttpResponseMessage Json(object value, int code = 200) => new((HttpStatusCode)code)
    { Content = new StringContent(JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json") };
    public static Dictionary<string, string> Parse(string text) => text.Split('&').Select(p => p.Split('=', 2))
        .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
}
