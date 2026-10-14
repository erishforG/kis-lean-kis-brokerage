using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.KisBrokerage.Client;
using QuantConnect.Packets;
using QuantConnect.Securities;

namespace QuantConnect.KisBrokerage;

/// <summary>
/// LEAN <see cref="BrokerageFactory"/> for <see cref="KisBrokerage"/>. This is what the LEAN
/// Launcher actually instantiates by reflection when <c>config.json</c>'s
/// <c>"live-mode-brokerage"</c>/<c>"brokerage-factory"</c> points at this assembly's
/// <c>QuantConnect.KisBrokerage.KisBrokerageFactory</c> type name (see config.json alongside
/// this file, and its accompanying README.md note on what a full Launcher checkout would
/// additionally need to make that resolution work end-to-end).
/// </summary>
public sealed class KisBrokerageFactory : BrokerageFactory
{
    // Same env-var-first pattern as the rest of this port: KisKrOAuthConfig.FromEnvironment()
    // remains the primary path (env vars only, never hardcoded secrets); these Config.Get(...)
    // keys are LEAN's own config.json-driven pattern for when a brokerage is launched by the
    // Launcher rather than instantiated directly. All defaults below are empty-string
    // placeholders — no real secret values live in this repo.
    //
    // No separate "kis-us-*" keys: KIS scopes app key/secret/account/token to an *account*
    // (paper/stock/pension), not a market — the same paper account credentials below are reused
    // unchanged for US (overseas) order/balance calls by KisUsTradingClient. See
    // KisUsTradingClient.cs's file-level NOTE for the reference-code evidence.
    public KisBrokerageFactory()
        : base(typeof(KisBrokerage))
    {
    }

    /// <summary>
    /// BrokerageData is LEAN's mechanism for surfacing a brokerage's required config keys (e.g.
    /// to the cloud UI's "connect brokerage" form). Values here are placeholders; the actual
    /// values used at runtime come from <see cref="CreateBrokerage"/>'s <c>Config.Get(...)</c>
    /// calls (or the job packet's own BrokerageData, if the Launcher populates it that way).
    /// </summary>
    public override Dictionary<string, string> BrokerageData => new()
    {
        { "kis-paper-app-key", Config.Get("kis-paper-app-key", "") },
        { "kis-paper-app-secret", Config.Get("kis-paper-app-secret", "") },
        { "kis-paper-acct-no", Config.Get("kis-paper-acct-no", "") },
        { "kis-paper-prod-code", Config.Get("kis-paper-prod-code", "") },
        { "kis-paper-base-url", Config.Get("kis-paper-base-url", "https://openapivts.koreainvestment.com:29443") },
        { "kis-market-scope", Config.Get("kis-market-scope", "Both") },
    };

    /// <summary>
    /// Uses a cash brokerage model with a zero-fee smoke-test model. LEAN's generic default
    /// selects InteractiveBrokersFeeModel for equities, which throws for Market.KRX before the
    /// algorithm can receive data. Real KIS commissions and Korean transaction taxes remain a
    /// production follow-up; zero fees are intentionally limited to this paper-test adapter.
    /// </summary>
    public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) =>
        new KisBrokerageModel();

    public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
    {
        var config = new KisKrOAuthConfig(
            BaseUrl: Config.Get("kis-paper-base-url", "https://openapivts.koreainvestment.com:29443"),
            AppKey: Config.Get("kis-paper-app-key", ""),
            AppSecret: Config.Get("kis-paper-app-secret", ""),
            AccountNo: Config.Get("kis-paper-acct-no", ""),
            ProductCode: Config.Get("kis-paper-prod-code", ""));

        // isPaper: true is hardcoded — this repo/PoC is paper-account only by design (see
        // KisKrOAuthConfig's doc comment and the repo-wide "never a live/pension account" rule).
        var brokerage = new KisBrokerage(
            config,
            isPaper: true,
            marketScope: KisMarketScopes.Parse(Config.Get("kis-market-scope", "Both")));
        var fillPollSeconds = Math.Clamp(Config.GetInt("kis-fill-poll-interval-seconds", 30), 15, 120);
        brokerage.EnableFillPolling(TimeSpan.FromSeconds(fillPollSeconds));
        KisBrokerageRuntimeRegistry.RegisterTradingBrokerage(brokerage);
        return brokerage;
    }

    public override void Dispose()
    {
        // Nothing to release at the factory level; KisBrokerage.Dispose() owns its own
        // HttpClient/OAuth client lifetimes.
    }
}
