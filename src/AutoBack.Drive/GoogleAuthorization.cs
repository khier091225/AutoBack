using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class GoogleAuthorization(ProtectedStore store, HttpClient http)
{
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static string Challenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public CloudConfiguration Status()
    {
        var account = store.Load<DriveAccount>("drive-account.dat");
        return account is null ? new("Not connected", "", "Connect Google Drive on this client using drive-connect.")
            : account.NeedsSignIn ? new("Needs sign-in", account.Email, "Google authorization expired or was revoked. Run drive-connect on the client.")
            : new("Connected", account.Email, "Direct uploads are enabled; the worker validates access when uploading.");
    }

    public void Disconnect() => store.Delete("drive-account.dat");
    public bool CanReconnect => store.Load<DriveAccount>("drive-account.dat") is not null;

    public async Task ConnectAsync(string? clientSecretsPath = null, CancellationToken cancellationToken = default,
        Func<Uri, Task>? openBrowser = null, Action<Uri>? signInUrlReady = null)
    {
        if (openBrowser is null && !Environment.UserInteractive)
            throw new InvalidOperationException("Run drive-connect from an interactive terminal on the client, not inside the Windows Service.");
        string? clientId, secret;
        if (clientSecretsPath is null)
        {
            var saved = store.Load<DriveAccount>("drive-account.dat") ?? throw new InvalidOperationException("Choose the Google OAuth Desktop client JSON for the first connection.");
            clientId = saved.ClientId; secret = saved.ClientSecret;
        }
        else
        {
            if (new FileInfo(clientSecretsPath).Length > 64 * 1024) throw new InvalidDataException("OAuth client JSON is too large.");
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(clientSecretsPath, cancellationToken));
            if (!document.RootElement.TryGetProperty("installed", out var installed))
                throw new InvalidDataException("Select the OAuth JSON for a Desktop app, not a Web application or service account.");
            clientId = installed.GetProperty("client_id").GetString(); secret = installed.GetProperty("client_secret").GetString();
        }
        if (clientId is null || !clientId.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || clientId.Length > 400 ||
            !BackupStates.ValidText(secret, 1024)) throw new InvalidDataException("Invalid Desktop app client configuration.");
        // OAuth endpoints are fixed; endpoint URLs embedded in the supplied JSON are never trusted.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(4);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var redirect = $"http://127.0.0.1:{port}/callback/";
        var state = Base64Url(RandomNumberGenerator.GetBytes(32));
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["redirect_uri"] = redirect, ["response_type"] = "code", ["scope"] = DriveHttp.Scope,
            ["access_type"] = "offline", ["prompt"] = "consent", ["state"] = state,
            ["code_challenge"] = Challenge(verifier), ["code_challenge_method"] = "S256"
        };
        var url = new Uri("https://accounts.google.com/o/oauth2/v2/auth?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value))));
        var callback = ReceiveCodeAsync(listener, state, deadline.Token);
        signInUrlReady?.Invoke(url);
        if (openBrowser is not null) await openBrowser(url);
        else
        {
            try { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); }
            // The caller has displayed a usable sign-in URL if shell launching is unavailable.
            catch (System.ComponentModel.Win32Exception) when (signInUrlReady is not null) { }
            catch (System.ComponentModel.Win32Exception)
            {
                await deadline.CancelAsync();
                try { await callback; } catch (OperationCanceledException) { }
                throw new DriveException(DriveFailure.Authorization, "The system browser could not be opened. Configure a default browser and retry drive-connect.");
            }
        }
        var code = await callback;
        using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId, ["client_secret"] = secret!, ["code"] = code, ["code_verifier"] = verifier,
            ["redirect_uri"] = redirect, ["grant_type"] = "authorization_code"
        }), deadline.Token);
        if (!response.IsSuccessStatusCode) throw new DriveException(DriveFailure.Authorization, "Google sign-in could not be completed. Check the OAuth Desktop client, test-user access, and consent settings.");
        using var tokens = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
        var access = tokens.RootElement.GetProperty("access_token").GetString();
        if (!tokens.RootElement.TryGetProperty("refresh_token", out var refreshElement) || string.IsNullOrWhiteSpace(refreshElement.GetString()) || string.IsNullOrWhiteSpace(access))
            throw new DriveException(DriveFailure.Authorization, "Google did not grant background access. Reconnect and allow the requested permission.");
        if (tokens.RootElement.TryGetProperty("scope", out var scope) && !scope.GetString()!.Split(' ').Contains(DriveHttp.Scope))
            throw new DriveException(DriveFailure.Authorization, "Google Drive file access was not granted.");
        using var aboutRequest = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user(emailAddress,permissionId)");
        aboutRequest.Headers.Authorization = new("Bearer", access);
        using var aboutResponse = await http.SendAsync(aboutRequest, deadline.Token);
        if (!aboutResponse.IsSuccessStatusCode) throw new DriveException(DriveFailure.Authorization, "Google Drive access could not be confirmed. Enable the Drive API and reconnect.");
        using var about = JsonDocument.Parse(await aboutResponse.Content.ReadAsStringAsync(deadline.Token));
        var user = about.RootElement.GetProperty("user");
        var email = user.GetProperty("emailAddress").GetString();
        var userId = user.GetProperty("permissionId").GetString();
        if (!BackupStates.ValidText(email, 254) || !BackupStates.ValidText(userId, 200)) throw new InvalidDataException("Google returned incomplete account information.");
        store.Save("drive-account.dat", new DriveAccount
        {
            ClientId = clientId, ClientSecret = secret!, RefreshToken = refreshElement.GetString()!, Email = email!, UserId = userId!
        });
    }

    private static async Task<string> ReceiveCodeAsync(TcpListener listener, string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                using var stream = client.GetStream();
                using var bytes = new MemoryStream();
                var buffer = new byte[1024];
                string header;
                do
                {
                    var count = await stream.ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    bytes.Write(buffer, 0, count);
                    if (bytes.Length > 8192) throw new InvalidDataException("OAuth callback request is too large.");
                    header = Encoding.ASCII.GetString(bytes.ToArray());
                } while (!header.Contains("\r\n\r\n", StringComparison.Ordinal));
                var firstLine = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n")[0].Split(' ');
                var valid = firstLine.Length == 3 && firstLine[0] == "GET" && firstLine[1].StartsWith("/callback/?", StringComparison.Ordinal);
                var query = valid ? ParseQuery(firstLine[1][(firstLine[1].IndexOf('?') + 1)..]) : [];
                valid = valid && query.TryGetValue("state", out var state) && state.Length == expectedState.Length &&
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(state), Encoding.UTF8.GetBytes(expectedState));
                var body = valid ? "AutoBack received the Google authorization response. You can close this tab." : "This is not a valid AutoBack authorization callback.";
                var payload = Encoding.UTF8.GetBytes(body);
                var status = valid ? "200 OK" : "400 Bad Request";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {payload.Length}\r\n\r\n"), timeout.Token);
                await stream.WriteAsync(payload, timeout.Token);
                if (!valid) continue;
                if (query.ContainsKey("error")) throw new DriveException(DriveFailure.Authorization, "Google sign-in was cancelled or declined. Existing account settings are unchanged.");
                if (query.TryGetValue("code", out var code) && !string.IsNullOrWhiteSpace(code)) return code;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException) { }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        }
    }

    private static Dictionary<string, string> ParseQuery(string value)
    {
        var result = new Dictionary<string, string>();
        foreach (var item in value.Split('&'))
        {
            var pair = item.Split('=', 2);
            if (pair.Length == 2 && !result.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1].Replace('+', ' '))))
                throw new InvalidDataException("Duplicate OAuth response parameter.");
        }
        return result;
    }
}
