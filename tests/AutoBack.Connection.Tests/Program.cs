using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using AutoBack.Connection;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-connection-tests", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var client = new AgentClient();
try
{
    await Run("pairing, heartbeat, invitation replay, and credential rejection", async () =>
    {
        await using var host = NewHost("pair");
        var invitation = host.CreateInvitation("127.0.0.1");
        var identity = AgentClient.CreateCredentials(invitation, "PC1-Test");
        await client.PairAsync(invitation, identity);
        Check(host.GetSnapshots().Count == 1 && !host.GetSnapshots()[0].Online, "pairing alone does not count as a heartbeat");
        await client.PairAsync(invitation, identity); // Idempotent retry after a lost response.
        Check(host.GetSnapshots().Count == 1, "pairing retry does not duplicate a computer");
        await client.HeartbeatAsync(identity);
        Check(host.GetSnapshots()[0].Online, "authenticated heartbeat sets online");
        await Reject(() => client.PairAsync(invitation, AgentClient.CreateCredentials(invitation, "PC2-Replay")));
        var lastSeen = host.GetSnapshots()[0].LastSeenUtc;
        await Reject(() => client.HeartbeatAsync(identity with { Secret = Protocol.NewSecret() }));
        Check(host.GetSnapshots()[0].LastSeenUtc == lastSeen, "unauthorized request cannot refresh last seen");
        await Reject(() => client.HeartbeatAsync(identity with { AgentId = Guid.NewGuid() }));
        await Reject(() => client.HeartbeatAsync(identity with { CertificateSha256 = Protocol.NewSecret() }));
        var unsupported = await Raw(host, new(99, "heartbeat", identity.AgentId, identity.Secret));
        Check(!unsupported.Accepted, "incompatible protocol is rejected");
        var unknown = await Raw(host, new(Protocol.Version, "execute-shell", identity.AgentId, identity.Secret));
        Check(!unknown.Accepted, "unsupported commands are rejected");
        Check(host.GetSnapshots().Count == 1, "rejected clients never enter inventory");
    });

    await Run("invitation expiry, cancellation, replacement, and concurrent use", async () =>
    {
        var clock = new TestClock();
        await using var host = NewHost("expiry", clock: clock);
        var expired = host.CreateInvitation("127.0.0.1");
        clock.Advance(Protocol.InvitationLifetime);
        await Reject(() => client.PairAsync(expired, AgentClient.CreateCredentials(expired, "Expired")));
        var cancelled = host.CreateInvitation("127.0.0.1");
        host.CancelInvitation();
        await Reject(() => client.PairAsync(cancelled, AgentClient.CreateCredentials(cancelled, "Cancelled")));
        var replaced = host.CreateInvitation("127.0.0.1");
        var current = host.CreateInvitation("127.0.0.1");
        await Reject(() => client.PairAsync(replaced, AgentClient.CreateCredentials(replaced, "Replaced")));
        var results = await Task.WhenAll(Enumerable.Range(1, 2).Select(async i =>
        {
            try { await client.PairAsync(current, AgentClient.CreateCredentials(current, "Concurrent" + i)); return true; }
            catch (AgentRejectedException) { return false; }
        }));
        Check(results.Count(accepted => accepted) == 1 && host.GetSnapshots().Count == 1, "one invitation admits exactly one concurrent client");
    });

    await Run("offline threshold, host restart, and persistent revocation", async () =>
    {
        var clock = new TestClock();
        var folder = Path.Combine(root, "restart");
        await using var host = NewHost("restart", clock: clock);
        var invitation = host.CreateInvitation("127.0.0.1");
        var identity = AgentClient.CreateCredentials(invitation, "Persistent-PC");
        await client.PairAsync(invitation, identity);
        await client.HeartbeatAsync(identity);
        clock.Advance(Protocol.OfflineAfter - TimeSpan.FromMilliseconds(1));
        Check(host.GetSnapshots()[0].Online, "online before heartbeat timeout");
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Check(!host.GetSnapshots()[0].Online, "offline exactly at 20 seconds without a heartbeat");
        await client.HeartbeatAsync(identity);
        Check(host.GetSnapshots()[0].Online, "new heartbeat reconnects an offline client");
        var fingerprint = host.CertificateSha256;
        await Reject(() => Task.Run(() => new HostServer(folder, 0, IPAddress.Loopback)));
        var port = host.Port;
        await host.DisposeAsync();
        await using var restarted = new HostServer(folder, port, IPAddress.Loopback, clock);
        restarted.Start();
        Check(restarted.CertificateSha256 == fingerprint && restarted.GetSnapshots().Count == 1, "host identity and registry survive restart");
        Check(!restarted.GetSnapshots()[0].Online, "restart waits for fresh heartbeats");
        await client.HeartbeatAsync(identity);
        restarted.Revoke(identity.AgentId);
        Check(restarted.GetSnapshots().Count == 0, "revoked client leaves inventory");
        await Reject(() => client.HeartbeatAsync(identity));
        await Reject(() => client.PairAsync(invitation, identity));
        await restarted.DisposeAsync();
        await using var third = new HostServer(folder, port, IPAddress.Loopback, clock);
        third.Start();
        await Reject(() => client.HeartbeatAsync(identity));
        Check(third.GetSnapshots().Count == 0, "revocation persists after restart");
        var fresh = third.CreateInvitation("127.0.0.1");
        await client.PairAsync(fresh, AgentClient.CreateCredentials(fresh, "Persistent-PC"));
        Check(third.GetSnapshots().Count == 1, "revoked computer can pair with a new identity");
    });

    await Run("encrypted storage, restricted ACLs, and bounded frames", async () =>
    {
        var folder = Path.Combine(root, "store");
        var store = new ProtectedStore(folder);
        var secret = Protocol.NewSecret();
        store.Save("sample.dat", new[] { secret });
        Check(store.Load<string[]>("sample.dat")![0] == secret, "DPAPI data roundtrips");
        Check(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(folder, "sample.dat"))).Contains(secret), "credential is not stored as plaintext");
        Check(new DirectoryInfo(folder).GetAccessControl().AreAccessRulesProtected, "credential directory disables inherited access");
        var unrelated = Path.Combine(root, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "User data");
        await Reject(() => Task.Run(() => new ProtectedStore(unrelated)));
        Check(File.ReadAllText(Path.Combine(unrelated, "keep.txt")) == "User data", "unrelated directory is preserved");
        store.Save("sample.dat", new[] { "updated" });
        Check(store.Load<string[]>("sample.dat")![0] == "updated", "atomic overwrite reads new value");
        using var currentUser = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(currentUser).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            var machineStore = new ProtectedStore(Path.Combine(root, "machine-store"), machineScope: true);
            machineStore.Save("sample.dat", new[] { secret });
            Check(machineStore.Load<string[]>("sample.dat")![0] == secret, "machine-scope store roundtrips with explicit ACL");
        }
        else
        {
            try { new ProtectedStore(Path.Combine(root, "machine-store"), machineScope: true); throw new InvalidOperationException("Machine store accepted a non-elevated account."); }
            catch (UnauthorizedAccessException) { }
            Check(!Directory.Exists(Path.Combine(root, "machine-store")), "non-admin machine mode fails before changing filesystem permissions");
            Console.WriteLine("  SKIP machine-scope roundtrip requires an elevated terminal; console mode is tested.");
        }
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, Protocol.MaxFrameLength + 1);
        await Reject(() => Protocol.ReadAsync<AgentRequest>(new MemoryStream(header), CancellationToken.None));
        await using var host = NewHost("frames");
        using (var tcp = new TcpClient())
        {
            await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
            using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == host.CertificateSha256);
            await tls.AuthenticateAsClientAsync("127.0.0.1");
            await tls.WriteAsync(header);
        }
        var invitation = host.CreateInvitation("127.0.0.1");
        var identity = AgentClient.CreateCredentials(invitation, "AfterBadFrame");
        await client.PairAsync(invitation, identity);
        await client.HeartbeatAsync(identity);
        Check(host.GetSnapshots()[0].Online, "listener remains usable after malformed input");
    });

    await Run("real agent process pairing, heartbeat, shutdown, and reconnect", async () =>
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AutoBack.slnx"))) repository = repository.Parent;
        Check(repository is not null, "repository found");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var executable = Path.Combine(repository!.FullName, "src", "AutoBack.Agent", "bin", configuration, "net10.0-windows", "AutoBack.Agent.exe");
        Check(File.Exists(executable), "real agent executable built");
        await using var initialHost = NewHost("process-host", offlineAfter: TimeSpan.FromSeconds(1));
        var host = initialHost;
        var port = host.Port;
        var invitation = host.CreateInvitation("127.0.0.1");
        var invitationPath = Path.Combine(root, "process.autoback-invite");
        await File.WriteAllTextAsync(invitationPath, JsonSerializer.Serialize(invitation, Protocol.Json));
        var dataDirectory = Path.Combine(root, "process-agent");
        var pair = await Command(executable, ["pair", "--invite", invitationPath, "--name", "Real-Agent-Test", "--data-dir", dataDirectory]);
        Check(pair.Code == 0 && pair.Output.Contains("Paired"), "agent CLI completes real TLS pairing");
        var saved = new ProtectedStore(dataDirectory).Load<AgentCredentials>("agent.dat")!;
        Check(saved.Name == "Real-Agent-Test", "agent pairing persisted");
        var repeated = await Command(executable, ["pair", "--invite", invitationPath, "--data-dir", dataDirectory]);
        Check(repeated.Code != 0, "accidental replacement is rejected");
        using var agent = Start(executable, ["run", "--data-dir", dataDirectory]);
        var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
        agent.OutputDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
        agent.ErrorDataReceived += (_, e) => { if (e.Data is not null) logs.Enqueue(e.Data); };
        agent.BeginOutputReadLine();
        agent.BeginErrorReadLine();
        try
        {
            await Until(() => host.GetSnapshots().Any(a => a.Online), TimeSpan.FromSeconds(15));
            await Until(() => logs.Any(line => line.Contains("Connected to host")), TimeSpan.FromSeconds(5));
            await host.DisposeAsync();
            await Until(() => logs.Any(line => line.Contains("Connection lost")), TimeSpan.FromSeconds(25));
            host = new HostServer(Path.Combine(root, "process-host"), port, IPAddress.Loopback, offlineAfter: TimeSpan.FromSeconds(1));
            host.Start();
            await Until(() => host.GetSnapshots().Any(a => a.Online), TimeSpan.FromSeconds(20));
            await Until(() => logs.Count(line => line.Contains("Connected to host")) >= 2, TimeSpan.FromSeconds(5));
            Console.WriteLine("  PASS agent automatically reconnects after a host outage");
            agent.Kill(entireProcessTree: true);
            await agent.WaitForExitAsync();
            await Task.Delay(TimeSpan.FromMilliseconds(1300));
            Check(!host.GetSnapshots()[0].Online, "stopped agent becomes offline while retaining last seen");
            Check(host.GetSnapshots()[0].LastSeenUtc.HasValue, "last reported heartbeat retained");
            agent.WaitForExit(); // Drain asynchronous output callbacks after the process exits.
            var output = string.Join('\n', logs);
            Check(output.Contains("Connection lost") && output.Contains("Connected to host"), "agent reports outage and reconnection");
            Check(!output.Contains(saved.Secret) && !output.Contains(invitation.Secret), "logs contain no credentials or invitation secrets");
            var status = await Command(executable, ["status", "--data-dir", dataDirectory]);
            Check(status.Code == 0 && status.Output.Contains("Real-Agent-Test"), "saved pairing can be inspected");
        }
        finally
        {
            if (!agent.HasExited) { agent.Kill(entireProcessTree: true); await agent.WaitForExitAsync(); }
            await host.DisposeAsync();
        }
    });
    Console.WriteLine("ALL CONNECTION CHECKS PASSED");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    var expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutoBack-connection-tests"));
    // Delete only this run's explicitly created GUID directory after verifying its resolved parent.
    if (Directory.Exists(root) && Path.GetDirectoryName(root) == expectedParent && Guid.TryParseExact(Path.GetFileName(root), "N", out _))
        Directory.Delete(root, recursive: true);
}

HostServer NewHost(string name, TestClock? clock = null, TimeSpan? offlineAfter = null)
{
    var host = new HostServer(Path.Combine(root, name), 0, IPAddress.Loopback, clock, offlineAfter);
    host.Start();
    return host;
}

static async Task Run(string name, Func<Task> action)
{
    Console.WriteLine("CHECK " + name);
    await action();
    Console.WriteLine("PASS " + name);
}

static void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    Console.WriteLine("  PASS " + description);
}

static async Task Reject(Func<Task> action)
{
    try { await action(); }
    catch (Exception ex) when (ex is AgentRejectedException or AuthenticationException or IOException or InvalidDataException or InvalidOperationException) { return; }
    throw new InvalidOperationException("An invalid request was unexpectedly accepted.");
}

static async Task<HostResponse> Raw(HostServer host, AgentRequest request)
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    using var tcp = new TcpClient();
    await tcp.ConnectAsync(IPAddress.Loopback, host.Port, cancellation.Token);
    using var tls = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate?.GetCertHashString(HashAlgorithmName.SHA256) == host.CertificateSha256);
    await tls.AuthenticateAsClientAsync("127.0.0.1");
    await Protocol.WriteAsync(tls, request, cancellation.Token);
    return await Protocol.ReadAsync<HostResponse>(tls, cancellation.Token);
}

static Process Start(string file, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    return Process.Start(start) ?? throw new InvalidOperationException("Could not start agent.");
}

static async Task<(int Code, string Output)> Command(string file, IEnumerable<string> arguments)
{
    using var process = Start(file, arguments);
    var output = process.StandardOutput.ReadToEndAsync();
    var error = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
    return (process.ExitCode, await output + await error);
}

static async Task Until(Func<bool> condition, TimeSpan timeout)
{
    var timer = Stopwatch.StartNew();
    while (!condition())
    {
        if (timer.Elapsed > timeout) throw new TimeoutException("Agent did not reach the expected connection state.");
        await Task.Delay(100);
    }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan time) => _now += time;
}
