using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace QuantConnect.KisBrokerage.Client;

// NOTE: this and KisKrTradingClient.cs were ported unchanged (only the namespace moved) from
// poc/KisKrOAuthClient.cs during the LEAN-plugin-shape rewrite in feat/kr-oauth-client — see
// src/KisBrokerage/README.md for the overall directory move.

/// <summary>
/// Configuration required to authenticate against the KIS (Korea Investment &amp; Securities)
/// Open API. Values must be supplied via environment variables (or an equivalent external
/// config source) — never hardcode real app key/secret/account values in source.
///
/// The same configuration shape is used for KIS paper and real-account environments.
/// <see cref="AccountNo"/> and <see cref="ProductCode"/> are not needed for OAuth token
/// issuance itself, but are required by the balance/order calls that follow.
/// </summary>
public sealed record KisKrOAuthConfig(
    string BaseUrl,
    string AppKey,
    string AppSecret,
    string AccountNo,
    string ProductCode)
{
    /// <summary>
    /// Builds config from process environment variables. Mirrors the KIS_PAPER_* variable
    /// names used by the reference Node implementation (kis/.env.kis) so an operator can wire
    /// the same values in without this repo ever reading or copying that file's contents.
    ///
    /// <c>KIS_PAPER_BASE_URL</c> defaults to KIS's publicly documented paper-trading host —
    /// this is a fixed API endpoint, not a secret. App key/secret/account values have no
    /// default and must be provided by the caller's environment.
    /// </summary>
    public static KisKrOAuthConfig FromEnvironment()
    {
        static string Get(string name, string fallback = "") =>
            Environment.GetEnvironmentVariable(name) ?? fallback;

        return new KisKrOAuthConfig(
            BaseUrl: Get("KIS_PAPER_BASE_URL", "https://openapivts.koreainvestment.com:29443"),
            AppKey: Get("KIS_PAPER_APP_KEY"),
            AppSecret: Get("KIS_PAPER_APP_SECRET"),
            AccountNo: Get("KIS_PAPER_ACCT_NO"),
            ProductCode: Get("KIS_PAPER_PROD_CODE"));
    }
}

/// <summary>Shape of the KIS <c>/oauth2/tokenP</c> response body.</summary>
public sealed record KisTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("expires_in")] long? ExpiresIn);

/// <summary>
/// KIS KR-market OAuth client — token issuance and cached refresh only (no order placement,
/// no balance/status queries; those are out of scope for this round).
///
/// Ports the behavior of the reference Node implementation
/// (kis/kis_client.mjs::getAccessToken, as delegated to by kis/lib/market_adapter_kr.mjs):
///   - POST {baseUrl}/oauth2/tokenP with body {"grant_type":"client_credentials","appkey":...,"appsecret":...}
///   - response field `access_token` is the bearer token, `expires_in` (seconds) sizes the cache
///   - cached token is reused until 60s before its (expires_in - 300s safety margin) expiry
///   - paper-trading tokens need a short delay after issuance before other endpoints accept them
/// </summary>
public sealed class KisKrOAuthClient : IDisposable
{
    private const string TokenPath = "/oauth2/tokenP";

    // Reuse the cached token as long as it is valid for at least this much longer.
    // Matches kis_client.mjs: `cache.expires_at > Date.now() / 1000 + 60`.
    private const int RefreshLeadTimeSeconds = 60;

    // Trim the server-reported expires_in by this much when computing our own expiry so a
    // slightly stale cache never gets used against a token KIS has already invalidated.
    // Matches kis_client.mjs: `expires_in - 300`.
    private const int ExpiresInSafetyMarginSeconds = 300;

    // KIS 모의투자(paper) tokens are not immediately usable by other endpoints right after
    // issuance. Matches kis_client.mjs: `if (ACCOUNTS[type]?.paper) await sleep(1000)`.
    private static readonly TimeSpan PaperActivationDelay = TimeSpan.FromSeconds(1);

    private readonly KisKrOAuthConfig _config;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly bool _isPaper;
    private readonly Func<DateTimeOffset> _now;

    // LEAN constructs one brokerage for trading and another for IDataQueueHandler. KIS paper
    // OAuth is limited to one issuance per minute, so instance-local caches make startup fail
    // deterministically. Share tokens by endpoint/app key and serialize refreshes process-wide.
    private static readonly ConcurrentDictionary<string, SharedTokenState> SharedTokens = new();
    private readonly SharedTokenState _sharedToken;

    public KisKrOAuthClient(
        KisKrOAuthConfig config,
        HttpClient? httpClient = null,
        bool isPaper = true,
        Func<DateTimeOffset>? clock = null)
    {
        _config = config;
        _isPaper = isPaper;
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _ownsHttpClient = httpClient is null;
        _http = httpClient ?? new HttpClient { BaseAddress = new Uri(config.BaseUrl) };
        _sharedToken = SharedTokens.GetOrAdd(
            $"{config.BaseUrl.TrimEnd('/')}\n{config.AppKey}",
            _ => new SharedTokenState());
    }

    /// <summary>
    /// Returns a cached access token if it still has more than <see cref="RefreshLeadTimeSeconds"/>
    /// of validity left, otherwise issues a fresh one and caches it.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var nowSeconds = _now().ToUnixTimeSeconds();
        if (_sharedToken.Cache is { } cached && cached.ExpiresAtUnixSeconds > nowSeconds + RefreshLeadTimeSeconds)
        {
            return cached.AccessToken;
        }

        await _sharedToken.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            nowSeconds = _now().ToUnixTimeSeconds();
            if (_sharedToken.Cache is { } refreshed &&
                refreshed.ExpiresAtUnixSeconds > nowSeconds + RefreshLeadTimeSeconds)
            {
                return refreshed.AccessToken;
            }

            return await IssueTokenCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sharedToken.Gate.Release();
        }
    }

    /// <summary>Forces a fresh token request, bypassing the cache. Updates the cache on success.</summary>
    public async Task<string> IssueTokenAsync(CancellationToken ct = default)
    {
        await _sharedToken.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await IssueTokenCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sharedToken.Gate.Release();
        }
    }

    private async Task<string> IssueTokenCoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_config.AppKey) || string.IsNullOrWhiteSpace(_config.AppSecret))
        {
            throw new InvalidOperationException(
                "KIS app key/secret missing — set KIS_PAPER_APP_KEY / KIS_PAPER_APP_SECRET " +
                "(paper account only; never a live/pension account here).");
        }

        var requestBody = new
        {
            grant_type = "client_credentials",
            appkey = _config.AppKey,
            appsecret = _config.AppSecret,
        };

        using var response = await _http.PostAsJsonAsync(TokenPath, requestBody, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"KIS OAuth token request failed ({(int)response.StatusCode} {response.StatusCode}): {raw}");
        }

        var payload = System.Text.Json.JsonSerializer.Deserialize<KisTokenResponse>(raw);
        if (string.IsNullOrWhiteSpace(payload?.AccessToken))
        {
            throw new InvalidOperationException("KIS OAuth response missing access_token: " + raw);
        }

        var expiresIn = payload.ExpiresIn ?? 86_400;
        var expiresAt = _now().ToUnixTimeSeconds() + expiresIn - ExpiresInSafetyMarginSeconds;
        _sharedToken.Cache = new CachedToken(payload.AccessToken, expiresAt);

        if (_isPaper)
        {
            await Task.Delay(PaperActivationDelay, ct).ConfigureAwait(false);
        }

        return _sharedToken.Cache.AccessToken;
    }

    /// <summary>Test/inspection hook — not part of the KIS protocol.</summary>
    internal CachedToken? CachedTokenForTesting => _sharedToken.Cache;

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    internal sealed record CachedToken(string AccessToken, long ExpiresAtUnixSeconds);

    private sealed class SharedTokenState
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CachedToken? Cache { get; set; }
    }
}
