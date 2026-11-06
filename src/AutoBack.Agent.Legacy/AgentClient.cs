using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace AutoBack.Connection;

public sealed class AgentRejectedException(string message) : Exception(message);

// Framework's SslStream uses Schannel TLS 1.2; the out-of-band certificate pin is mandatory.
public sealed class AgentClient
{
    public static AgentCredentials CreateCredentials(PairingInvitation invitation, string name)
    {
        invitation.Validate();
        if (!Protocol.ValidName(name)) throw new ArgumentException("Use a valid computer name of 1-60 characters.");
        return new(invitation.Host, invitation.Port, invitation.CertificateSha256, Guid.NewGuid(), Protocol.NewSecret(), name);
    }
    public Task PairAsync(PairingInvitation invitation, AgentCredentials credentials, CancellationToken cancellationToken = default)
    {
        invitation.Validate(); credentials.Validate();
        if (invitation.Host != credentials.Host || invitation.Port != credentials.Port ||
            !invitation.CertificateSha256.Equals(credentials.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending credential belongs to a different host.");
        return SendAsync(credentials, new(Protocol.Version, "pair", credentials.AgentId, credentials.Secret,
            invitation.Secret, credentials.Name, Environment.MachineName, "0.8.2-win7-x86"), cancellationToken);
    }
    public Task HeartbeatAsync(AgentCredentials credentials, CancellationToken cancellationToken = default)
        => PollAsync(credentials, null, null, cancellationToken);
    public Task<HostResponse> PollAsync(AgentCredentials credentials, BackupConfiguration? configuration, BackupReport? report,
        CancellationToken cancellationToken = default, CloudConfiguration? cloudConfiguration = null, CloudReport? cloudReport = null,
        RestoreSummary? latestRestore = null, AgentProtectionSummary? protection = null)
    {
        credentials.Validate();
        return SendAsync(credentials, new(Protocol.Version, "heartbeat", credentials.AgentId, credentials.Secret,
            AgentVersion: "0.8.2-win7-x86", Configuration: configuration, Report: report, CloudConfiguration: cloudConfiguration,
            CloudReport: cloudReport, LatestRestore: latestRestore, Protection: protection), cancellationToken);
    }
    private static async Task<HostResponse> SendAsync(AgentCredentials credentials, AgentRequest request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(Protocol.RequestTimeout);
        using var tcp = new TcpClient();
        using var cancel = timeout.Token.Register(() => tcp.Close());
        try
        {
            await tcp.ConnectAsync(credentials.Host, credentials.Port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                using var candidate = new X509Certificate2(certificate.GetRawCertData());
                var now = DateTime.UtcNow;
                return now >= candidate.NotBefore.ToUniversalTime() && now <= candidate.NotAfter.ToUniversalTime() &&
                    CryptographicOperations.FixedTimeEquals(SHA256.HashData(candidate.RawData), Convert.FromHexString(credentials.CertificateSha256));
            });
            await tls.AuthenticateAsClientAsync(credentials.Host, null, SslProtocols.Tls12, false);
            await Protocol.WriteAsync(tls, request, timeout.Token);
            var response = await Protocol.ReadAsync<HostResponse>(tls, timeout.Token);
            if (!response.Accepted) throw new AgentRejectedException(response.Message);
            return response;
        }
        catch (Exception) when (timeout.IsCancellationRequested) { throw new OperationCanceledException(timeout.Token); }
    }
}

public sealed record AgentProtectionSummary(bool Encrypted, int KeepLocal, int KeepCloud, string State,
    string Detail, DateTimeOffset? LastRunUtc = null);
