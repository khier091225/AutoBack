using System.Text.Json;
using AutoBack.Connection;

namespace AutoBack.Drive;

public sealed class GoogleTokenProvider(ProtectedStore store, HttpClient http)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private Guid _generation;
    private DateTimeOffset _expires;

    public DriveAccount? Account => store.Load<DriveAccount>("drive-account.dat");

    public async Task<string> GetAsync(string accountKey, bool forceRefresh, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var account = Account;
            if (account is null || account.NeedsSignIn || account.Key != accountKey)
                throw new DriveException(DriveFailure.Authorization, "Sign in to the original Google account on this client before resuming this upload.");
            if (!forceRefresh && _generation == account.Generation && _accessToken is not null && _expires > DateTimeOffset.UtcNow)
                return _accessToken;
            using var response = await http.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = account.ClientId, ["client_secret"] = account.ClientSecret,
                ["refresh_token"] = account.RefreshToken, ["grant_type"] = "refresh_token"
            }), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 400 or 401)
                {
                    account.NeedsSignIn = true;
                    store.Save("drive-account.dat", account);
                    throw new DriveException(DriveFailure.Authorization, "Google authorization expired or was revoked. Run drive-connect on this client.");
                }
                throw new DriveException(DriveFailure.Retryable, "Google authorization service is temporarily unavailable. The upload will retry.");
            }
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            _accessToken = json.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(_accessToken)) throw new DriveException(DriveFailure.Authorization, "Google returned no access token. Reconnect the client account.");
            _generation = account.Generation;
            _expires = DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, json.RootElement.GetProperty("expires_in").GetInt32() - 60));
            return _accessToken!;
        }
        finally { _gate.Release(); }
    }
}
