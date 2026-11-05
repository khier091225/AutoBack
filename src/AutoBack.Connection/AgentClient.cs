using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AutoBack.Connection;

public sealed class AgentRejectedException(string message) : Exception(message);

public sealed class AgentClient
{
    public static AgentCredentials CreateCredentials(PairingInvitation invitation, string name)
    {
        invitation.Validate();
        if (!Protocol.ValidName(name)) throw new ArgumentException("Use a computer name of 1-60 letters, numbers, spaces, dots, underscores, or hyphens.");
        return new(invitation.Host, invitation.Port, invitation.CertificateSha256, Guid.NewGuid(), Protocol.NewSecret(), name);
    }

    public Task PairAsync(PairingInvitation invitation, AgentCredentials credentials, CancellationToken cancellationToken = default)
    {
        invitation.Validate();
        credentials.Validate();
        if (invitation.Host != credentials.Host || invitation.Port != credentials.Port ||
            !invitation.CertificateSha256.Equals(credentials.CertificateSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pending credential belongs to a different host.");
        return SendAsync(credentials, new(Protocol.Version, "pair", credentials.AgentId, credentials.Secret,
            invitation.Secret, credentials.Name, Environment.MachineName, "0.7.0"), cancellationToken);
    }

    public Task HeartbeatAsync(AgentCredentials credentials, CancellationToken cancellationToken = default)
    {
        credentials.Validate();
        return SendAsync(credentials, new(Protocol.Version, "heartbeat", credentials.AgentId, credentials.Secret), cancellationToken);
    }

    public Task<HostResponse> PollAsync(AgentCredentials credentials, BackupConfiguration? configuration, BackupReport? report,
        CancellationToken cancellationToken = default, CloudConfiguration? cloudConfiguration = null, CloudReport? cloudReport = null,
        RestoreSummary? latestRestore = null, AgentProtectionSummary? protection = null)
    {
        credentials.Validate();
        return SendAsync(credentials, new(Protocol.Version, "heartbeat", credentials.AgentId, credentials.Secret,
            AgentVersion: "0.7.0", Configuration: configuration, Report: report,
            CloudConfiguration: cloudConfiguration, CloudReport: cloudReport, LatestRestore: latestRestore, Protection: protection), cancellationToken);
    }

    private static async Task<HostResponse> SendAsync(AgentCredentials credentials, AgentRequest request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Protocol.RequestTimeout);
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(credentials.Host, credentials.Port, timeout.Token);
        using var tls = new SslStream(tcp.GetStream(), false);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = credentials.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            AllowRenegotiation = false,
            AllowTlsResume = false,
            RemoteCertificateValidationCallback = (_, certificate, _, _) =>
            {
                // Trust only the exact certificate delivered through the out-of-band invitation, not any self-signed certificate.
                if (certificate is null) return false;
                using var candidate = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                var now = DateTime.UtcNow;
                return now >= candidate.NotBefore.ToUniversalTime() && now <= candidate.NotAfter.ToUniversalTime() &&
                    CryptographicOperations.FixedTimeEquals(candidate.GetCertHash(HashAlgorithmName.SHA256), Convert.FromHexString(credentials.CertificateSha256));
            }
        }, timeout.Token);
        await Protocol.WriteAsync(tls, request, timeout.Token);
        var response = await Protocol.ReadAsync<HostResponse>(tls, timeout.Token);
        if (!response.Accepted) throw new AgentRejectedException(response.Message);
        return response;
    }
}
