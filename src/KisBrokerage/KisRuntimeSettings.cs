using QuantConnect.Configuration;
using QuantConnect.KisBrokerage.Client;

namespace QuantConnect.KisBrokerage;

/// <summary>Resolves the launcher configuration for either a KIS paper or real account.</summary>
internal sealed record KisRuntimeSettings(KisKrOAuthConfig Credentials, bool IsPaper, KisMarketScope MarketScope)
{
    private const string PaperBaseUrl = "https://openapivts.koreainvestment.com:29443";
    private const string RealBaseUrl = "https://openapi.koreainvestment.com:9443";

    public static KisRuntimeSettings FromLeanConfig()
    {
        var mode = Config.Get("kis-account-mode", "paper").Trim().ToLowerInvariant();
        var isPaper = mode switch
        {
            "paper" or "mock" or "모의" => true,
            "real" or "live" or "stock" => false,
            _ => throw new InvalidOperationException(
                $"Unsupported kis-account-mode '{mode}'. Use 'paper' or 'real'.")
        };

        var prefix = isPaper ? "kis-paper-" : "kis-real-";
        var baseUrl = GetFirstNonEmpty(
            Config.Get("kis-base-url", ""),
            Config.Get(prefix + "base-url", isPaper ? PaperBaseUrl : RealBaseUrl));
        var appKey = GetFirstNonEmpty(Config.Get("kis-app-key", ""), Config.Get(prefix + "app-key", ""));
        var appSecret = GetFirstNonEmpty(Config.Get("kis-app-secret", ""), Config.Get(prefix + "app-secret", ""));
        var accountNo = GetFirstNonEmpty(Config.Get("kis-acct-no", ""), Config.Get(prefix + "acct-no", ""));
        var productCode = GetFirstNonEmpty(Config.Get("kis-prod-code", ""), Config.Get(prefix + "prod-code", ""));

        return new KisRuntimeSettings(
            new KisKrOAuthConfig(baseUrl, appKey, appSecret, accountNo, productCode),
            isPaper,
            KisMarketScopes.Parse(Config.Get("kis-market-scope", "Both")));
    }

    private static string GetFirstNonEmpty(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
