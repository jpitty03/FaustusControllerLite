using FaustusControllerLite.Orders;

namespace FaustusControllerLite.Input;

public enum CalibrationWizardStep
{
    Inactive,
    WantedPicker,
    OfferedPicker,
    PlaceOrderButton,
    TrackedCollectionSlot,
    TrackedCancelButton,
    CanceledReturnSlot,
    Complete,
}

public sealed record CalibrationWizardState(
    CalibrationWizardStep Step,
    string LatestConfirmation,
    string LatestError)
{
    public static CalibrationWizardState Inactive { get; } =
        new(CalibrationWizardStep.Inactive, string.Empty, string.Empty);
}

public sealed record CalibrationWizardEligibility(bool Eligible, string Guidance);

public static class CalibrationWizard
{
    public const int CalibrationStepCount = 6;
    public static readonly TimeSpan CancelTestTimeout = TimeSpan.FromSeconds(5);

    public static CalibrationWizardState StartOrRestart() => new(
        CalibrationWizardStep.WantedPicker,
        "Calibration wizard started at the wanted picker.",
        string.Empty);

    public static CalibrationWizardState Succeed(
        CalibrationWizardState state,
        string confirmation)
    {
        ArgumentNullException.ThrowIfNull(state);
        var next = state.Step switch
        {
            CalibrationWizardStep.WantedPicker => CalibrationWizardStep.OfferedPicker,
            CalibrationWizardStep.OfferedPicker => CalibrationWizardStep.PlaceOrderButton,
            CalibrationWizardStep.PlaceOrderButton => CalibrationWizardStep.TrackedCollectionSlot,
            CalibrationWizardStep.TrackedCollectionSlot => CalibrationWizardStep.TrackedCancelButton,
            CalibrationWizardStep.TrackedCancelButton => CalibrationWizardStep.CanceledReturnSlot,
            CalibrationWizardStep.CanceledReturnSlot => CalibrationWizardStep.Complete,
            _ => state.Step,
        };
        return next == state.Step
            ? state
            : new CalibrationWizardState(next, confirmation, string.Empty);
    }

    public static CalibrationWizardState Fail(
        CalibrationWizardState state,
        string failure)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.Step is CalibrationWizardStep.Inactive or CalibrationWizardStep.Complete
            ? state
            : state with { LatestError = failure };
    }

    public static bool IsVisible(CalibrationWizardState state) =>
        state.Step != CalibrationWizardStep.Inactive;

    public static bool IsBlocking(CalibrationWizardState state) => state.Step is
        CalibrationWizardStep.WantedPicker or
        CalibrationWizardStep.OfferedPicker or
        CalibrationWizardStep.PlaceOrderButton or
        CalibrationWizardStep.TrackedCollectionSlot or
        CalibrationWizardStep.TrackedCancelButton or
        CalibrationWizardStep.CanceledReturnSlot;

    public static bool UsesAcceleratedCancelTimeout(CalibrationWizardState state) =>
        state.Step == CalibrationWizardStep.TrackedCancelButton;

    public static int StepNumber(CalibrationWizardStep step) => step switch
    {
        CalibrationWizardStep.WantedPicker => 1,
        CalibrationWizardStep.OfferedPicker => 2,
        CalibrationWizardStep.PlaceOrderButton => 3,
        CalibrationWizardStep.TrackedCollectionSlot => 4,
        CalibrationWizardStep.TrackedCancelButton => 5,
        CalibrationWizardStep.CanceledReturnSlot or CalibrationWizardStep.Complete => 6,
        _ => 0,
    };

    public static bool PickerSideMatches(CalibrationWizardStep step, bool openedWantedSide) =>
        step switch
        {
            CalibrationWizardStep.WantedPicker => openedWantedSide,
            CalibrationWizardStep.OfferedPicker => !openedWantedSide,
            _ => false,
        };

    public static string Instruction(CalibrationWizardStep step, bool pickerObservationArmed = false) =>
        step switch
        {
            CalibrationWizardStep.Inactive =>
                "Press CalibrationWizardStartHotkey to start the six-step calibration wizard.",
            CalibrationWizardStep.WantedPicker when pickerObservationArmed =>
                "Manually click the wanted-currency picker now without moving the cursor.",
            CalibrationWizardStep.WantedPicker =>
                "Close the picker, hover the wanted-currency picker button, then press CalibrationWizardNextHotkey.",
            CalibrationWizardStep.OfferedPicker when pickerObservationArmed =>
                "Manually click the offered-currency picker now without moving the cursor.",
            CalibrationWizardStep.OfferedPicker =>
                "Close the picker, hover the offered-currency picker button, then press CalibrationWizardNextHotkey.",
            CalibrationWizardStep.PlaceOrderButton =>
                "Show a valid Place Order button, hover it without clicking, then press CalibrationWizardNextHotkey.",
            CalibrationWizardStep.TrackedCollectionSlot =>
                "Hover the tracked completed row's left wanted-proceeds slot, then press CalibrationWizardNextHotkey; do not click the slot.",
            CalibrationWizardStep.TrackedCancelButton =>
                "Hover the exact tracked timed-out row's right-edge cancel X, then press CalibrationWizardNextHotkey; do not click it.",
            CalibrationWizardStep.CanceledReturnSlot =>
                "Hover the terminal row's right offered-return slot, then press CalibrationWizardNextHotkey; do not click the slot.",
            CalibrationWizardStep.Complete =>
                "All six calibration targets persisted. Collect and stash any calibration test order before starting production automation.",
            _ => string.Empty,
        };

    public static CalibrationWizardEligibility CollectionEligibility(
        TrackedOrderStatus? status,
        long remainingWanted)
    {
        var eligible = status == TrackedOrderStatus.CompletedUncollected && remainingWanted > 0;
        return new CalibrationWizardEligibility(
            eligible,
            eligible ? $"Ready: exact CompletedUncollected state with {remainingWanted} wanted proceeds remaining."
                : status is null or TrackedOrderStatus.None or TrackedOrderStatus.Stashed
                    ? "Create or adopt a tiny order and let it fill to CompletedUncollected with wanted proceeds remaining."
                : status is TrackedOrderStatus.Pending or TrackedOrderStatus.TimedOut
                    ? $"Collection calibration requires a filled order; current state is {status}."
                : status == TrackedOrderStatus.CanceledUncollected
                    ? "Resolve the canceled order and its custody, then create a completed order with wanted proceeds remaining."
                : status == TrackedOrderStatus.CompletedUncollected
                    ? "This completed order has no wanted proceeds remaining; resolve it, then create another filled test order."
                : $"Resolve current {status} custody, then create a completed order with wanted proceeds remaining.");
    }

    public static CalibrationWizardEligibility CancelEligibility(TrackedOrderStatus? status)
    {
        var eligible = status == TrackedOrderStatus.TimedOut;
        return new CalibrationWizardEligibility(
            eligible,
                eligible ? "Ready: exact TimedOut state."
                : status is null or TrackedOrderStatus.None or TrackedOrderStatus.Stashed
                    ? "Create or adopt a tiny unattractive order; step 5 uses a five-second timeout after adoption."
                : status == TrackedOrderStatus.Pending
                    ? "Wait up to five seconds after adoption for the exact pending order to reach TimedOut."
                : status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected or
                    TrackedOrderStatus.Collected or TrackedOrderStatus.StashTransferArmed
                    ? $"Resolve {status} custody first, then create the unattractive timeout test order."
                : $"Cancel calibration requires exact TimedOut state; current state is {Describe(status)}.");
    }

    public static CalibrationWizardEligibility ReturnEligibility(
        TrackedOrderStatus? status,
        long remainingReturn)
    {
        var eligible = (status is TrackedOrderStatus.CanceledUncollected or
            TrackedOrderStatus.CompletedUncollected) && remainingReturn > 0;
        return new CalibrationWizardEligibility(
            eligible,
            eligible ? $"Ready: exact {status} state with {remainingReturn} offered return remaining."
                : status == TrackedOrderStatus.TimedOut
                    ? "Use CancelTimedOutOrderHotkey, then wait for the canceled terminal row and offered return."
                : status == TrackedOrderStatus.Pending
                    ? "Wait for TimedOut, calibrate the cancel X if needed, cancel the order, then wait for its offered return."
                : (status is TrackedOrderStatus.CanceledUncollected or TrackedOrderStatus.CompletedUncollected) &&
                    remainingReturn <= 0
                    ? "This terminal order has zero offered return; create a cancellation test that returns offered currency."
                : status is null or TrackedOrderStatus.None or TrackedOrderStatus.Stashed
                    ? "Create or adopt a cancellation test that will return offered currency, then cancel it after TimedOut."
                : $"Resolve current {Describe(status)} custody, then create a cancellation test with offered currency to return.");
    }

    public static string Prerequisite(
        CalibrationWizardStep step,
        TrackedOrderStatus? status,
        long remainingWanted,
        long remainingReturn) => step switch
    {
        CalibrationWizardStep.WantedPicker or CalibrationWizardStep.OfferedPicker =>
            "Foreground exchange, picker closed, cursor stationary over the expected button, and Control/Shift/Alt released.",
        CalibrationWizardStep.PlaceOrderButton =>
            "Foreground exchange, picker closed, valid pair and amounts visible, and Control/Shift/Alt released.",
        CalibrationWizardStep.TrackedCollectionSlot =>
            CollectionEligibility(status, remainingWanted).Guidance,
        CalibrationWizardStep.TrackedCancelButton =>
            CancelEligibility(status).Guidance,
        CalibrationWizardStep.CanceledReturnSlot =>
            ReturnEligibility(status, remainingReturn).Guidance,
        CalibrationWizardStep.Complete =>
            "All six targets persisted; any calibration test order must be collected and stashed before production automation.",
        _ => "No wizard step is active.",
    };

    private static string Describe(TrackedOrderStatus? status) => status?.ToString() ?? "None";
}
