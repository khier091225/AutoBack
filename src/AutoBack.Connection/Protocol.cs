using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutoBack.Connection;

public static partial class Protocol
{
    public const int Version = 3;
    public const int DefaultPort = 47831;
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan JobPollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(5);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const int MaxFrameLength = 64 * 1024;

    [GeneratedRegex(@"^[\p{L}\p{N}][\p{L}\p{N} ._-]{0,59}$")]
    private static partial Regex NamePattern();

    public static bool ValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool ValidSecret(string? secret) => secret?.Length == 64 && secret.All(Uri.IsHexDigit);
    public static bool ValidHost(string? host) => !string.IsNullOrWhiteSpace(host) && host.Length <= 253 &&
        (IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) == UriHostNameType.Dns);
    public static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool SecretMatches(string? secret, string hash) => ValidSecret(secret) &&
        CryptographicOperations.FixedTimeEquals(Convert.FromHexString(Hash(secret!)), Convert.FromHexString(hash));

    public static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (bytes.Length > MaxFrameLength) throw new InvalidDataException("Message is too large.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is <= 0 or > MaxFrameLength) throw new InvalidDataException("Invalid message length.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        return JsonSerializer.Deserialize<T>(bytes, Json) ?? throw new InvalidDataException("Empty message.");
    }
}

public sealed record PairingInvitation(int Version, string Host, int Port, string CertificateSha256, string Secret, DateTimeOffset ExpiresUtc)
{
    public void Validate()
    {
        if (Version != Protocol.Version || !Protocol.ValidHost(Host) || Port is < 1 or > 65535 ||
            !Protocol.ValidSecret(CertificateSha256) || !Protocol.ValidSecret(Secret))
            throw new InvalidDataException("Invalid AutoBack pairing invitation.");
    }
}

public sealed record AgentCredentials(string Host, int Port, string CertificateSha256, Guid AgentId, string Secret, string Name)
{
    public void Validate()
    {
        if (!Protocol.ValidHost(Host) || Port is < 1 or > 65535 || !Protocol.ValidSecret(CertificateSha256) ||
            AgentId == Guid.Empty || !Protocol.ValidSecret(Secret) || !Protocol.ValidName(Name))
            throw new InvalidDataException("Invalid agent credentials. Pair this client again.");
    }
}

public sealed record AgentRequest(int Version, string Kind, Guid AgentId, string Secret, string? InvitationSecret = null,
    string? Name = null, string? MachineName = null, string? AgentVersion = null,
    BackupConfiguration? Configuration = null, BackupReport? Report = null,
    CloudConfiguration? CloudConfiguration = null, CloudReport? CloudReport = null);
public sealed record HostResponse(bool Accepted, string Message, DateTimeOffset ServerUtc,
    BackupCommand? Command = null, Guid? AcknowledgedJobId = null, long AcknowledgedSequence = 0,
    Guid? AcknowledgedCloudJobId = null, long AcknowledgedCloudSequence = 0);
public sealed record AgentSnapshot(Guid Id, string Name, string MachineName, string AgentVersion, string Address,
    DateTimeOffset PairedUtc, DateTimeOffset? LastSeenUtc, bool Online,
    BackupConfiguration? Configuration = null, HostBackupJob? LatestJob = null, HostBackupJob? LastSuccessfulJob = null,
    CloudConfiguration? CloudConfiguration = null, HostBackupJob? LatestCloudJob = null);
public sealed record ConnectionActivity(DateTimeOffset TimeUtc, string Title, string Description);

public sealed record BackupConfiguration(string PlanId, string Source, string Destination);
public sealed record BackupCommand(Guid JobId, string PlanId, DateTimeOffset CreatedUtc, bool CancelRequested = false);
public sealed record BackupReport(Guid JobId, long Sequence, string State, int Percent, long BytesCopied, long TotalBytes,
    int FilesCopied, int TotalFiles, string Detail, string? ArchivePath = null);
public sealed record HostBackupJob(Guid AgentId, string AgentName, BackupCommand Command, BackupReport Report,
    DateTimeOffset UpdatedUtc, bool CancelRequested = false, CloudReport? Cloud = null, DateTimeOffset? CloudUpdatedUtc = null);
public sealed record CloudConfiguration(string State, string Account, string Detail);
public sealed record CloudReport(Guid JobId, long Sequence, string State, int Percent, long BytesUploaded, long TotalBytes,
    string Detail, string? FileId = null, string? WebLink = null);

public static class CloudStates
{
    public static bool ValidId(string? id) => id is { Length: > 0 and <= 200 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    public static bool ValidConfiguration(CloudConfiguration? configuration) => configuration is null ||
        (configuration.State is "Not connected" or "Connected" or "Needs sign-in" && configuration.Account is { Length: <= 254 } &&
            !configuration.Account.Any(char.IsControl) && BackupStates.ValidText(configuration.Detail, 1024));
    public static bool ValidReport(CloudReport report) => report.JobId != Guid.Empty && report.Sequence > 0 &&
        report.State is "Queued" or "Uploading" or "Retrying" or "Needs sign-in" or "Failed" or "Completed" &&
        report.Percent is >= 0 and <= 100 && report.BytesUploaded >= 0 && report.TotalBytes >= report.BytesUploaded &&
        BackupStates.ValidText(report.Detail, 1024) && (report.FileId is null || ValidId(report.FileId)) &&
        (report.WebLink is null || report.WebLink == $"https://drive.google.com/file/d/{report.FileId}/view") &&
        (report.State != "Completed" || (report.Percent == 100 && report.BytesUploaded == report.TotalBytes &&
            ValidId(report.FileId) && report.WebLink is not null));
}

public static class BackupStates
{
    public static bool Terminal(string state) => state is "Completed" or "Failed" or "Cancelled";
    public static bool Running(string state) => state is "Preparing" or "Copying" or "Verifying";
    public static bool Valid(string state) => Running(state) || Terminal(state);
    public static bool ValidConfiguration(BackupConfiguration? plan) => plan is null ||
        (Protocol.ValidSecret(plan.PlanId) && ValidText(plan.Source, 1024) && ValidText(plan.Destination, 1024));
    public static bool ValidText(string? text, int max) => !string.IsNullOrWhiteSpace(text) && text.Length <= max && !text.Any(char.IsControl);
    public static bool ValidReport(BackupReport report) => report.JobId != Guid.Empty && report.Sequence > 0 &&
        Valid(report.State) && report.Percent is >= 0 and <= 100 && report.BytesCopied >= 0 && report.TotalBytes >= report.BytesCopied &&
        report.FilesCopied >= 0 && report.TotalFiles >= report.FilesCopied && ValidText(report.Detail, 1024) &&
        (report.ArchivePath is null || ValidText(report.ArchivePath, 1024)) &&
        (report.State != "Completed" || (report.Percent == 100 && report.BytesCopied == report.TotalBytes &&
            report.FilesCopied == report.TotalFiles && report.ArchivePath is not null));
}
