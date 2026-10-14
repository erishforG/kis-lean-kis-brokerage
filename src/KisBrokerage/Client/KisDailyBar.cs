namespace QuantConnect.KisBrokerage.Client;

/// <summary>A completed-candidate daily OHLCV row returned by a KIS public quotation endpoint.</summary>
public sealed record KisDailyBar(
    DateOnly Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);
