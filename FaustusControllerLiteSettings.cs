using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Attributes;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Windows.Forms;

namespace FaustusControllerLite;

public sealed class FaustusControllerLiteSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);

    [Category("Market")]
    public ListNode TargetCurrency { get; set; } = new() { Value = "Orb of Alteration" };

    [IgnoreMenu]
    public string TargetCurrencyMetadata { get; set; } = "Metadata/Items/Currency/CurrencyRerollMagic";

    [IgnoreMenu]
    public string TargetCurrencyDisplayName { get; set; } = "Orb of Alteration";

    [Category("Bankroll Seeds")]
    public RangeNode<int> StartingChaos { get; set; } = new(0, 0, 1_000_000);

    [Category("Bankroll Seeds")]
    public RangeNode<int> StartingDivine { get; set; } = new(0, 0, 1_000_000);

    [Category("Strategy")]
    public RangeNode<int> MinimumProfitChaos { get; set; } = new(5, 0, 1_000_000);

    [Category("Strategy")]
    public RangeNode<int> CompetingOrderWaitMinutes { get; set; } = new(5, 1, 120);

    [Category("Strategy")]
    public RangeNode<int> ContinuousWorkflowRetrySeconds { get; set; } = new(10, 2, 90);

    [Category("Probing")]
    public RangeNode<int> CursorTweenSpeed { get; set; } = new(1600, 400, 4000);

    [Category("Probing")]
    public RangeNode<int> StableRateSampleCount { get; set; } = new(3, 1, 10);

    [Category("Probing")]
    public RangeNode<int> MaximumQuoteAgeSeconds { get; set; } = new(60, 1, 3600);

    [Category("Permissions")]
    public ToggleNode AllowAutomatedProbing { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowVerifiedMouseMovement { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowVerifiedClicks { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowQueryInput { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowAmountInput { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowOrderPlacement { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowOrderCancellation { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowOrderCollection { get; set; } = new(false);

    [Category("Input Permissions")]
    public ToggleNode AllowStashTransfer { get; set; } = new(false);

    [Category("Permissions")]
    public ToggleNode AllowFullWorkflow { get; set; } = new(false);

    [Category("Hotkeys")]
    public HotkeyNodeV2 ProbeMarketsHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CalibratePickerButtonHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CalibratePlaceOrderHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CalibrateCollectionHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CalibrateCancelHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CalibrateReturnSlotHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CaptureCurrentPairHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 DumpSdkReadsHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 ExecuteSingleLegHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 PlaceStagedLegHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CollectTrackedOrderHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 StashCollectedCurrencyHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 CancelTimedOutOrderHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 AdoptPendingOrderHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 FullWorkflowHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Fresh State Reset")]
    [JsonIgnore]
    public ButtonNode ArmFreshStateReset { get; set; } = new();

    [Category("Fresh State Reset")]
    [JsonIgnore]
    public ButtonNode ApplyArmedFreshStateReset { get; set; } = new();

    // Manual override for when the safe reset refuses: unreadable state, or custody the plugin can
    // no longer resolve. It abandons accounting and quarantines evidence; it never moves an item.
    [Category("Fresh State Reset")]
    [JsonIgnore]
    public ButtonNode ArmForcedFreshStateReset { get; set; } = new();

    [Category("Fresh State Reset")]
    [JsonIgnore]
    public ButtonNode ApplyArmedForcedFreshStateReset { get; set; } = new();

    private static HotkeyNodeV2 CreateUnboundHotkey() => new(Keys.None)
    {
        IgnoreFocusedInput = true
    };
}
