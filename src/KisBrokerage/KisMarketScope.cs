namespace QuantConnect.KisBrokerage;

public enum KisMarketScope
{
    Both,
    Kr,
    Us
}

public static class KisMarketScopes
{
    public static KisMarketScope Parse(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        null or "" or "BOTH" => KisMarketScope.Both,
        "KR" => KisMarketScope.Kr,
        "US" => KisMarketScope.Us,
        _ => throw new ArgumentException("kis-market-scope must be KR, US, or Both.", nameof(value))
    };

    public static bool Allows(this KisMarketScope scope, string leanMarket) => scope switch
    {
        KisMarketScope.Kr => leanMarket == QuantConnect.Market.KRX,
        KisMarketScope.Us => leanMarket == QuantConnect.Market.USA,
        _ => leanMarket is QuantConnect.Market.KRX or QuantConnect.Market.USA
    };
}
