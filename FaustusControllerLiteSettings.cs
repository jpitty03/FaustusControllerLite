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

    [Category("Calibration Wizard")]
    public HotkeyNodeV2 CalibrationWizardStartHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Calibration Wizard")]
    public HotkeyNodeV2 CalibrationWizardNextHotkey { get; set; } = CreateUnboundHotkey();

    [IgnoreMenu]
    public string TargetCurrencyMetadata { get; set; } = "Metadata/Items/Currency/CurrencyRerollMagic";

    [IgnoreMenu]
    public string TargetCurrencyDisplayName { get; set; } = "Orb of Alteration";

    [Category("Bankroll Seeds")]
    public RangeNode<int> StartingChaos { get; set; } = new(1, 1, 5_000);

    [Category("Bankroll Seeds")]
    public RangeNode<int> StartingDivine { get; set; } = new(1, 1, 1_000);

    [Category("Strategy")]
    public RangeNode<int> MinimumProfitChaos { get; set; } = new(5, 1, 5_000);

    [Category("Strategy")]
    public RangeNode<int> CompetingOrderWaitMinutes { get; set; } = new(5, 1, 3_600);

    [Category("Strategy")]
    public ToggleNode EnableDirectDivineCycles { get; set; } = new(false);

    [Category("Strategy")]
    public RangeNode<int> MaximumDirectDivinePrincipal { get; set; } = new(1_000, 1, 1_000);

    [Category("Strategy")]
    public ToggleNode EnableCompetingPriceImprovement { get; set; } = new(false);

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
    /// A holding worth less than this at the best usable rate is skipped rather than sold.
    /// </summary>
    [Category("Strategy")]
    public RangeNode<int> MinimumSaleChaos { get; set; } = new(10, 1, 5_000);

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

    [Category("Hotkeys")]
    public HotkeyNodeV2 MarketSweepHotkey { get; set; } = CreateUnboundHotkey();

    [Category("Hotkeys")]
    public HotkeyNodeV2 MarketSweepBoardSortHotkey { get; set; } = CreateUnboundHotkey();

    // The board is advisory. Nothing here feeds the route planner or latest-rates.json; the sweep only
    // reads books and appends its own observation file, so every setting below is safe to leave on.
    [Category("Market Sweep")]
    public ToggleNode EnableMarketSweepBoard { get; set; } = new(false);

    [Category("Market Sweep")]
    public ToggleNode SweepCurrency { get; set; } = new(true);

    [Category("Market Sweep")]
    public ToggleNode SweepDeliriumOrbs { get; set; } = new(false);

    [Category("Market Sweep")]
    public ToggleNode SweepScarabs { get; set; } = new(false);

    [Category("Market Sweep")]
    public ToggleNode SweepFossils { get; set; } = new(false);

    [Category("Market Sweep")]
    public ToggleNode SweepEssences { get; set; } = new(false);

    [Category("Market Sweep")]
    public RangeNode<int> SweepBoardRowCount { get; set; } = new(15, 5, 40);

    [Category("Market Sweep")]
    public RangeNode<int> VelocityHistoryDays { get; set; } = new(7, 1, 30);

    // Intervals longer than this are discarded, not averaged in, so this must comfortably exceed the rate at
    // which a pair is actually revisited. It is deliberately far above SweepStalePairMinutes below: a full
    // idle cycle takes (pairs x IdleSweepIntervalSeconds), which is well over the stale threshold itself.
    [Category("Market Sweep")]
    public RangeNode<int> ChurnIntervalCapMinutes { get; set; } = new(90, 5, 240);

    // Must stay below ChurnIntervalCapMinutes or every idle-generated interval is thrown away and churn never
    // appears. The board says so out loud when a saved configuration still has these two the wrong way round.
    [Category("Market Sweep")]
    public RangeNode<int> SweepStalePairMinutes { get; set; } = new(30, 5, 1_440);

    [Category("Market Sweep")]
    public RangeNode<int> SweepDepthCap { get; set; } = new(1_000, 1, 100_000);

    // One pair per idle window, never a burst, so the continuous workflow always wins the panel.
    [Category("Market Sweep")]
    public ToggleNode SweepWhileIdle { get; set; } = new(false);

    [Category("Market Sweep")]
    public RangeNode<int> IdleSweepIntervalSeconds { get; set; } = new(15, 5, 600);

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
