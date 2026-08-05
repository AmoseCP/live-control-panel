using System.Runtime.Versioning;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.YouTube.v3;
using LiveControlPanel.Config;
using LiveControlPanel.Core;

namespace LiveControlPanel.Youtube;

/// <summary>
/// Authorization-code flow hosted by the panel itself (FR 5.1). YouTube Data API has no service
/// account path, so a user OAuth grant is the only option; the refresh token is kept in
/// <see cref="DpapiDataStore"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class YouTubeAuth
{
    private const string UserId = "panel";

    private static readonly string[] Scopes =
    {
        YouTubeService.Scope.Youtube,
        YouTubeService.Scope.YoutubeForceSsl,
    };

    private readonly ConfigStore _config;
    private readonly DpapiDataStore _store;
    private readonly ILogger<YouTubeAuth> _log;
    private readonly object _gate = new();

    private GoogleAuthorizationCodeFlow? _flow;
    private string? _flowClientId;
    private UserCredential? _credential;

    public YouTubeAuth(ConfigStore config, ILogger<YouTubeAuth> log)
    {
        _config = config;
        _log = log;
        _store = new DpapiDataStore(config.Paths.TokenFile);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Settings.YouTube.ClientId) &&
        !string.IsNullOrWhiteSpace(_config.Settings.YouTube.ClientSecret);

    public DateTime? AuthorizedAtUtc => _store.AuthorizedAtUtc;

    public string RedirectUri(int port) => $"http://localhost:{port}/auth/callback";

    /// <summary>Google consent URL. offline + consent so a refresh token is always issued.</summary>
    public string BuildAuthorizationUrl(int port)
    {
        var request = (AuthorizationCodeRequestUrl)Flow().CreateAuthorizationCodeRequest(RedirectUri(port));
        var url = request.Build().AbsoluteUri;
        return $"{url}&access_type=offline&prompt=consent";
    }

    public async Task ExchangeCodeAsync(string code, int port, CancellationToken ct)
    {
        var token = await Flow().ExchangeCodeForTokenAsync(UserId, code, RedirectUri(port), ct)
            .ConfigureAwait(false);

        if (string.IsNullOrEmpty(token.RefreshToken))
        {
            // Without a refresh token the grant is useless past one hour; make it loud rather than
            // discovering it at 04:40.
            throw new InvalidOperationException(
                "Google did not return a refresh token. Revoke the app's access and authorize again.");
        }

        lock (_gate) _credential = null;
        _log.LogInformation("YouTube authorization stored");
    }

    public async Task RevokeAsync(CancellationToken ct)
    {
        try
        {
            var credential = await TryGetCredentialAsync(ct).ConfigureAwait(false);
            if (credential is not null) await credential.RevokeTokenAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Revoking the Google token failed; clearing it locally anyway");
        }

        await _store.ClearAsync().ConfigureAwait(false);
        lock (_gate) _credential = null;
    }

    /// <summary>
    /// The stored credential, or null when there is nothing usable on disk. Cached: a
    /// <see cref="UserCredential"/> holds the live access token, so handing out a fresh instance per
    /// call would re-refresh on every API request.
    /// </summary>
    public async Task<UserCredential?> TryGetCredentialAsync(CancellationToken ct)
    {
        if (!IsConfigured) return null;

        lock (_gate)
        {
            if (_credential is not null) return _credential;
        }

        var token = await _store.GetAsync<TokenResponse>(UserId).ConfigureAwait(false);
        if (token is null || string.IsNullOrEmpty(token.RefreshToken)) return null;

        var flow = Flow();
        lock (_gate)
        {
            return _credential ??= new UserCredential(flow, UserId, token);
        }
    }

    /// <summary>
    /// Reports authorization health for the pre-flight and the always-visible countdown (FR 8).
    /// Validity is proven by actually refreshing the access token — a token that merely exists on
    /// disk tells the operator nothing.
    /// </summary>
    public async Task<AuthInfo> GetAuthInfoAsync(CancellationToken ct)
    {
        if (!IsConfigured)
            return new AuthInfo(false, null, null, new Msg(
                "YouTube 客户端未配置：请在设置页填写 Client ID 与 Secret。",
                "The YouTube client is not configured: enter the Client ID and Secret on the settings page."));

        var credential = await TryGetCredentialAsync(ct).ConfigureAwait(false);
        if (credential is null)
            return new AuthInfo(false, null, null, new Msg(
                "尚未授权 YouTube 账号。", "The YouTube account has not been authorized yet."));

        var authorizedAt = _store.AuthorizedAtUtc;
        var remaining = RemainingDays(authorizedAt);

        try
        {
            var accessToken = await credential.GetAccessTokenForRequestAsync(cancellationToken: ct)
                .ConfigureAwait(false);
            if (string.IsNullOrEmpty(accessToken))
                return new AuthInfo(false, remaining, authorizedAt?.ToLocalTime(), new Msg(
                    "授权已失效，需要重新授权。", "Authorization has expired and needs renewing."));

            return new AuthInfo(true, remaining, authorizedAt?.ToLocalTime(), null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Refreshing the YouTube access token failed");
            return new AuthInfo(false, remaining, authorizedAt?.ToLocalTime(), new Msg(
                "授权已失效，需要重新授权。", "Authorization has expired and needs renewing."));
        }
    }

    private int? RemainingDays(DateTime? authorizedAtUtc)
    {
        if (authorizedAtUtc is null) return null;
        var total = _config.Settings.YouTube.AssumedValidityDays;
        var elapsed = (DateTime.UtcNow - authorizedAtUtc.Value).TotalDays;
        return Math.Max(0, (int)Math.Floor(total - elapsed));
    }

    /// <summary>Rebuilt when the client id changes, so editing it in settings takes effect at once.</summary>
    private GoogleAuthorizationCodeFlow Flow()
    {
        var clientId = _config.Settings.YouTube.ClientId;
        var clientSecret = _config.Settings.YouTube.ClientSecret;

        lock (_gate)
        {
            if (_flow is not null && _flowClientId == clientId) return _flow;

            // Client id changed in settings: the cached credential belongs to the old client.
            _credential = null;
            _flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
                Scopes = Scopes,
                DataStore = _store,
            });
            _flowClientId = clientId;
            return _flow;
        }
    }
}
