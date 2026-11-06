using AutoBack.Connection;

namespace AutoBack.Drive;

// Classes deliberately retain the default ToString: never stringify OAuth credentials into logs.
public sealed class DriveAccount
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public required string RefreshToken { get; init; }
    public required string Email { get; init; }
    public required string UserId { get; init; }
    public Guid Generation { get; init; } = Guid.NewGuid();
    public bool NeedsSignIn { get; set; }
    public string Key => Protocol.Hash(ClientId + "|" + UserId);
}

public enum DriveFailure { Retryable, Authorization, Permanent }
public sealed class DriveException(DriveFailure failure, string message) : Exception(message)
{
    public DriveFailure Failure { get; } = failure;
}

public sealed record RemoteFile(string Id, string MimeType, long Size, string? Md5, string? Sha256,
    bool Trashed, string[] Parents, Dictionary<string, string> Properties);
public sealed record UploadCheckpoint(string? FileId = null, string? SessionUri = null, string? Md5 = null,
    string? Sha256 = null, long Length = 0);
public sealed record UploadProgress(long ConfirmedBytes, long TotalBytes, string Detail);
public sealed record UploadResult(string FileId, string WebLink, long Size);

public static class DriveHttp
{
    public const string Scope = "https://www.googleapis.com/auth/drive.file";
#if NETFRAMEWORK
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false, SslProtocols = System.Security.Authentication.SslProtocols.Tls12 }) { Timeout = TimeSpan.FromMinutes(3) };
#else
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
#endif
    public static bool SafeSessionUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "www.googleapis.com" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        uri.AbsolutePath.StartsWith("/upload/drive/v3/files", StringComparison.Ordinal) && string.IsNullOrEmpty(uri.Fragment);
    public static string SafeMessage(string value) => new(value.Where(c => !char.IsControl(c)).Take(950).ToArray());
}
