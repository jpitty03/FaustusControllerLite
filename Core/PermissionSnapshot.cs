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
    bool FullWorkflow)
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
        settings.AllowFullWorkflow.Value);

    public bool AllDisabled => !Probing && !MouseMovement && !Clicking && !QueryInput && !AmountInput &&
        !Placement && !Cancellation && !Collection && !FullWorkflow;
}
