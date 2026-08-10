using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileCore.Shared.Attributes;
using FaustusControllerLite.Domain;
using Newtonsoft.Json;
using System.ComponentModel;
using System.Linq;
using System.Windows.Forms;

namespace FaustusControllerLite;

public sealed class FaustusControllerLiteSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);

    /// <summary>
    /// Selects which of the two mutually exclusive automation features owns the hotkeys.
    /// Exactly one is ever active at a time; the other's actions refuse and send no input.
    /// </summary>
    [Category("Feature")]
    public ListNode ActiveFeature { get; set; } = new()
    {
        Value = FeatureModeGate.ArbitrageLabel,
        Values = FeatureModeGate.Labels.ToList(),
    };

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

    /// <summary>
    /// Which stash family the sell sweep liquidates. This also fixes the home tab the leftovers are
    /// returned to, so it is read once per sweep and never inferred from whatever tab is visible.
    /// </summary>
    [Category("Strategy")]
    public ListNode SellSweepKind { get; set; } = new()
    {
        Value = SellSweepKinds.ScarabLabel,
        Values = SellSweepKinds.Labels.ToList(),
    };

    /// <summary>
    /// Selects resting competing orders for value or aggressive immediate-head limits for speed.
    /// The value is captured when a sweep is planned and cannot change that active sweep.
    /// </summary>
    [Category("Strategy")]
    public ListNode SellSweepExecutionStrategy { get; set; } = new()
    {
        Value = SellSweepExecutionModes.MostCurrencyLabel,
        Values = SellSweepExecutionModes.Labels.ToList(),
    };

    /// <summary>
    /// A holding worth less than this at the best usable rate is skipped rather than sold. Zero is
    /// allowed and means "sell anything that has a usable quote".
    /// </summary>
    [Category("Strategy")]
    public RangeNode<int> MinimumSaleChaos { get; set; } = new(10, 0, 1_000_000);

    /// <summary>
    /// Reverses the sweep queue so the smallest stack is swept first. The default (largest first)
    /// is the operating order - an interruption then costs the least remaining value - but a first
    /// live test wants the cheapest possible mistake, so the order is an operator choice rather
    /// than a constant. Ordering only; it never changes which holdings are eligible.
    /// </summary>
    [Category("Strategy")]
    public ToggleNode SellSweepSmallestStackFirst { get; set; } = new(false);

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

    /// <summary>
    /// Authorizes the sell sweep loop. This is on top of - never instead of - the individual
    /// placement, collection, cancellation and stash-transfer permissions each step still checks.
    /// </summary>
    [Category("Permissions")]
    public ToggleNode AllowSellSweep { get; set; } = new(false);

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

    [Category("Hotkeys")]
    public HotkeyNodeV2 SellSweepHotkey { get; set; } = CreateUnboundHotkey();

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
