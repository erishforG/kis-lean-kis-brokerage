namespace QuantConnect.KisBrokerage.Client;

/// <summary>Bounds order HTTP calls and the follow-up lookup used after an ambiguous submit.</summary>
internal sealed record KisOrderRequestOptions(
    TimeSpan HttpTimeout,
    TimeSpan ReconciliationTimeout,
    TimeSpan ReconciliationPollDelay)
{
    public static KisOrderRequestOptions Default { get; } = new(
        HttpTimeout: TimeSpan.FromSeconds(15),
        ReconciliationTimeout: TimeSpan.FromSeconds(20),
        ReconciliationPollDelay: TimeSpan.FromSeconds(1));
}

/// <summary>
/// Raised internally when an order request may have reached KIS but its response was not fully
/// observed. A submit in this state must be reconciled and must never be blindly replayed.
/// </summary>
internal sealed class KisAmbiguousOrderSubmissionException : Exception
{
    public KisAmbiguousOrderSubmissionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
