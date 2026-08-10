namespace FaustusControllerLite.Core;

public sealed record PermissionSnapshot(
    bool Probing,
    bool MouseMovement,
    bool Clicking,
    bool QueryInput,
    bool AmountInput,
    bool Placement,
    bool Cancellation,
    bool Collection,
    bool StashTransfer,
    bool FullWorkflow,
    bool SellSweep)
{
    public static PermissionSnapshot From(FaustusControllerLiteSettings settings) => new(
        settings.AllowAutomatedProbing.Value,
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowQueryInput.Value,
        settings.AllowAmountInput.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowOrderCancellation.Value,
        settings.AllowOrderCollection.Value,
        settings.AllowStashTransfer.Value,
        settings.AllowFullWorkflow.Value,
        settings.AllowSellSweep.Value);

    public bool AllDisabled => !Probing && !MouseMovement && !Clicking && !QueryInput && !AmountInput &&
        !Placement && !Cancellation && !Collection && !StashTransfer && !FullWorkflow && !SellSweep;

    public bool ReadyForFullWorkflow => Probing && MouseMovement && Clicking && QueryInput && AmountInput &&
        Placement && Cancellation && Collection && StashTransfer && FullWorkflow && !SellSweep;

    /// <summary>
    /// The sweep drives placement, collection, cancellation and stash transfer, so it needs every
    /// permission the full workflow needs except <c>FullWorkflow</c> itself - that one authorizes
    /// the arbitrage loop, which is a different feature and stays independently gated.
    /// </summary>
    public bool ReadyForSellSweep => Probing && MouseMovement && Clicking && QueryInput && AmountInput &&
        Placement && Cancellation && Collection && StashTransfer && SellSweep && !FullWorkflow;
}
