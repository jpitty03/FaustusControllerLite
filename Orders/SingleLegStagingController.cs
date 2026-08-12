using ExileCore;
using FaustusControllerLite.Core;
using ExileCore.PoEMemory;
using FaustusControllerLite.Input;
using FaustusControllerLite.Probing;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum SingleLegStagingState
{
    Idle,
    SelectingPair,
    WaitingForSelectorRelease,
    SamplingInitialQuote,
    MovingToAmount,
    ClickingAmount,
    WaitingForAmountFocus,
    SelectingAmountText,
    ClearingAmountText,
    TypingAmount,
    VerifyingAmount,
    LockingAmounts,
    SamplingFinalQuote,
    ObservingNoOrder,
    CancellingPairSelection,
    ReleasingInput,
    Staged,
    Cancelled,
}

public enum SingleLegQuoteValidationPolicy
{
    ExactCandidate,
    PreserveCompetingLimit,
    AggressiveImmediateLimit,
}

public sealed record StagingInputPermissions(
    bool MouseMovement,
    bool Clicking,
    bool QueryInput,
    bool AmountInput,
    bool Placement,
    bool FullWorkflow,
    bool WorkflowAuthorized = false,
    bool SellSweep = false,
    bool SweepAuthorized = false)
{
    private CoordinatorOwnership Owner => new(FullWorkflow, WorkflowAuthorized, SellSweep, SweepAuthorized);

    public bool Ready => MouseMovement && Clicking && QueryInput && AmountInput && !Placement && Owner.None;
    public bool ReadyForPlacementWorkflow =>
        MouseMovement && Clicking && QueryInput && AmountInput && Placement &&
        (Owner.None || Owner.Authorized);

    public static StagingInputPermissions From(
        FaustusControllerLiteSettings settings,
        bool workflowAuthorized = false,
        bool sweepAuthorized = false) => new(
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowQueryInput.Value,
        settings.AllowAmountInput.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowFullWorkflow.Value,
        workflowAuthorized,
        settings.AllowSellSweep.Value,
        sweepAuthorized);
}

public readonly record struct StagingQuoteSample(
    Rational Rate,
    QuoteExecutionIntent ExecutionIntent,
    long ImmediateInputDepth,
    long CompetingQueueAhead,
    string RelevantBookFingerprint);

public sealed class SingleLegStagingController
{
    public const long MaximumAmount = 999_999_999;
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(35);
    private static readonly TimeSpan KeyInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan NoOrderObservation = TimeSpan.FromSeconds(3);

    private readonly AutomatedProbeController _pairSelector = new();
    private readonly HashSet<int> _baselineOrderIds = [];
    private readonly HashSet<Keys> _ownedKeys = [];
    private readonly StableSampleCounter<StagingQuoteSample> _stableSamples = new();
    private RouteLegResult? _leg;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private Guid _sessionId;
    private bool _wantedInput;
    private bool _pairSelectionConfirmed;
    private string _amountDigits = string.Empty;
    private int _digitIndex;
    private Vector2 _moveStart;
    private Vector2 _moveTarget;
    private Vector2 _lastCommandedCursor;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _stepDeadline;
    private DateTimeOffset _overallDeadline;
    private DateTimeOffset _nextActionAt;
    private bool _mouseDown;
    private SingleLegStagingState _releaseTarget;
    private string _releaseStatus = string.Empty;
    private bool _placementWorkflowArmed;
    private SingleLegQuoteValidationPolicy _quoteValidationPolicy;
    private string _lastQuoteSampleFailure = string.Empty;

    public SingleLegStagingState State { get; private set; } = SingleLegStagingState.Idle;
    public string Status { get; private set; } = "Idle; no leg is staged.";
    public string Failure { get; private set; } = string.Empty;
    public bool FreshProbeRetryRecommended { get; private set; }
    public bool IsRunning => State is not SingleLegStagingState.Idle and
        not SingleLegStagingState.Staged and
        not SingleLegStagingState.Cancelled;
    public RouteLegResult? StagedLeg => State == SingleLegStagingState.Staged ? _leg : null;

    public bool Start(
        GameController gameController,
        RouteLegResult leg,
        PickerCalibration calibration,
        StagingInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        bool placementWorkflowArmed,
        SingleLegQuoteValidationPolicy quoteValidationPolicy,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(leg);
        if (IsRunning)
        {
            failure = "A single-leg staging operation is already running.";
            return false;
        }

        State = SingleLegStagingState.Idle;
        Status = "Idle; no leg is staged.";
        Failure = string.Empty;
        FreshProbeRetryRecommended = false;
        _leg = null;

        if (!TryValidatePolicy(leg, quoteValidationPolicy, out failure))
        {
            return false;
        }

        if (placementWorkflowArmed ? !permissions.ReadyForPlacementWorkflow : !permissions.Ready)
        {
            failure = placementWorkflowArmed
                ? "Armed staging requires movement, click, query, amount, and placement permission while full workflow remains disabled."
                : "Staging requires movement, click, query, and amount input while placement and full workflow remain disabled.";
            return false;
        }

        if (!IsValidAmount(leg.InputSpent) || !IsValidAmount(leg.Output))
        {
            failure = $"Staged amounts must be between 1 and {MaximumAmount}.";
            return false;
        }

        if (!TryReadOrderIds(gameController, out var orderIds, out failure))
        {
            return false;
        }

        var server = gameController.Game.IngameState.ServerData;
        _leg = leg;
        _placementWorkflowArmed = placementWorkflowArmed;
        _quoteValidationPolicy = quoteValidationPolicy;
        _league = server.League;
        _areaInstanceId = server.InstanceId;
        _sessionId = Guid.NewGuid();
        _baselineOrderIds.Clear();
        _baselineOrderIds.UnionWith(orderIds);
        _pairSelectionConfirmed = false;
        _lastCommandedCursor = ExileInput.MousePositionNum;
        _overallDeadline = DateTimeOffset.UtcNow + OverallTimeout;
        Failure = string.Empty;

        var selectorPermissions = new ProbeInputPermissions(
            true,
            permissions.MouseMovement,
            permissions.Clicking,
            permissions.QueryInput);
        if (!_pairSelector.StartPairSelection(
                gameController,
                leg.Edge.From,
                leg.Edge.To,
                calibration,
                selectorPermissions,
                conflictingControllerEnabled,
                cursorSpeed,
                out failure))
        {
            _leg = null;
            State = SingleLegStagingState.Cancelled;
            Status = failure;
            Failure = failure;
            return false;
        }

        State = SingleLegStagingState.SelectingPair;
        Status = $"Selecting pair for dry-run staging: {leg.InputSpent} {leg.Edge.From.Name} -> {leg.Output} {leg.Edge.To.Name}.";
        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerCalibration calibration,
        StagingInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        int stableSampleCount)
    {
        if (!IsRunning)
        {
            return;
        }

        var selectorPermissions = new ProbeInputPermissions(
            true,
            permissions.MouseMovement,
            permissions.Clicking,
            permissions.QueryInput);
        if (State == SingleLegStagingState.CancellingPairSelection)
        {
            _pairSelector.Tick(gameController, calibration, selectorPermissions,
                conflictingControllerEnabled, cursorSpeed, stableSampleCount: 1);
            if (!_pairSelector.IsRunning)
            {
                BeginRelease(SingleLegStagingState.Cancelled, $"Cancelled: {Failure}");
            }

            return;
        }

        if (State == SingleLegStagingState.ReleasingInput)
        {
            if (TryReleaseOwnedInput())
            {
                State = _releaseTarget;
                Status = _releaseStatus;
            }

            return;
        }

        var now = DateTimeOffset.UtcNow;
        try
        {
            if (!ValidateGlobalState(gameController, permissions, conflictingControllerEnabled, now, out var failure))
            {
                Cancel(failure);
                return;
            }

            switch (State)
            {
                case SingleLegStagingState.SelectingPair:
                case SingleLegStagingState.WaitingForSelectorRelease:
                    TickSelectingPair(gameController, calibration, selectorPermissions,
                        conflictingControllerEnabled, cursorSpeed, now);
                    break;
                case SingleLegStagingState.SamplingInitialQuote:
                    SampleFreshQuote(gameController, now, cursorSpeed, stableSampleCount, finalSample: false);
                    break;
                case SingleLegStagingState.MovingToAmount:
                    TickMovement(now, SingleLegStagingState.ClickingAmount);
                    break;
                case SingleLegStagingState.ClickingAmount:
                    ClickAmount(gameController, now);
                    break;
                case SingleLegStagingState.WaitingForAmountFocus:
                    WaitForAmountFocus(gameController, now);
                    break;
                case SingleLegStagingState.SelectingAmountText:
                    SelectAmountText(gameController, now);
                    break;
                case SingleLegStagingState.ClearingAmountText:
                    ClearAmountText(gameController, now);
                    break;
                case SingleLegStagingState.TypingAmount:
                    TypeAmount(gameController, now);
                    break;
                case SingleLegStagingState.VerifyingAmount:
                    VerifyAmount(gameController, now, cursorSpeed);
                    break;
                case SingleLegStagingState.LockingAmounts:
                    LockAmounts(gameController, now);
                    break;
                case SingleLegStagingState.SamplingFinalQuote:
                    SampleFreshQuote(gameController, now, cursorSpeed, stableSampleCount, finalSample: true);
                    break;
                case SingleLegStagingState.ObservingNoOrder:
                    ObserveNoOrder(gameController, now);
                    break;
            }
        }
        catch (Exception exception)
        {
            Cancel($"Single-leg staging failed closed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning)
        {
            return;
        }

        Failure = reason;
        if (_pairSelector.IsRunning)
        {
            _pairSelector.Cancel(reason);
            State = SingleLegStagingState.CancellingPairSelection;
            Status = $"Cancelling pair selection: {reason}";
            return;
        }

        BeginRelease(SingleLegStagingState.Cancelled, $"Cancelled: {reason}");
    }

    public void Invalidate(string reason)
    {
        if (IsRunning)
        {
            Cancel(reason);
            return;
        }

        _leg = null;
        Failure = reason;
        State = SingleLegStagingState.Cancelled;
        Status = $"Invalidated: {reason}";
    }

    private bool ValidateGlobalState(
        GameController gameController,
        StagingInputPermissions permissions,
        bool conflictingControllerEnabled,
        DateTimeOffset now,
        out string failure)
    {
        if (_placementWorkflowArmed ? !permissions.ReadyForPlacementWorkflow : !permissions.Ready)
        {
            failure = "A staging permission changed or placement/full workflow became enabled.";
            return false;
        }

        if (conflictingControllerEnabled || now > _overallDeadline ||
            !gameController.Window.IsForeground() || ModifiersHeld())
        {
            failure = "Controller exclusion, timeout, foreground, or modifier preflight failed.";
            return false;
        }

        var ui = gameController.Game.IngameState.IngameUi;
        var panel = ui.CurrencyExchangePanel;
        var server = gameController.Game.IngameState.ServerData;
        if (!panel.IsVisible || ui.PopUpWindow.IsVisible ||
            !string.Equals(server.League, _league, StringComparison.Ordinal) ||
            server.InstanceId != _areaInstanceId)
        {
            failure = "Exchange visibility, league, or area changed during staging.";
            return false;
        }

        if (State != SingleLegStagingState.SelectingPair)
        {
            if (panel.CurrencyPicker.IsVisible ||
                !string.Equals(panel.OfferedItemType?.Metadata, _leg!.Edge.From.Metadata, StringComparison.Ordinal) ||
                !string.Equals(panel.WantedItemType?.Metadata, _leg.Edge.To.Metadata, StringComparison.Ordinal))
            {
                failure = "The staged pair or picker state changed.";
                return false;
            }

            if (Vector2.Distance(ExileInput.MousePositionNum, _lastCommandedCursor) > CursorTolerance)
            {
                failure = "Manual cursor movement interrupted staging.";
                return false;
            }
        }

        if (!TryReadOrderIds(gameController, out var currentOrderIds, out failure) ||
            !OrderSetMatchesBaseline(_baselineOrderIds, currentOrderIds))
        {
            failure = string.IsNullOrEmpty(failure)
                ? "The exact order-ID baseline changed during dry-run staging."
                : failure;
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void TickSelectingPair(
        GameController gameController,
        PickerCalibration calibration,
        ProbeInputPermissions selectorPermissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        DateTimeOffset now)
    {
        if (_pairSelectionConfirmed)
        {
            _pairSelector.Tick(gameController, calibration, selectorPermissions,
                conflictingControllerEnabled, cursorSpeed, stableSampleCount: 1);
            if (_pairSelector.IsRunning)
            {
                Status = "Verified pair selected; waiting for selector input release.";
                return;
            }

            _lastCommandedCursor = ExileInput.MousePositionNum;
            BeginQuoteSampling(now, finalSample: false);
            return;
        }

        _pairSelector.Tick(gameController, calibration, selectorPermissions,
            conflictingControllerEnabled, cursorSpeed, stableSampleCount: 1);
        if (_pairSelector.State == AutomatedProbeState.Completed)
        {
            _pairSelectionConfirmed = true;
            _lastCommandedCursor = ExileInput.MousePositionNum;
            State = SingleLegStagingState.WaitingForSelectorRelease;
            _pairSelector.AcknowledgeCompletion();
            return;
        }

        if (_pairSelector.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed)
        {
            Cancel($"Verified pair selection failed: {_pairSelector.Failure}");
            return;
        }

        Status = $"Selecting pair: {_pairSelector.Status}";
    }

    private void BeginAmountInput(GameController gameController, bool wantedInput, DateTimeOffset now, int cursorSpeed)
    {
        _wantedInput = wantedInput;
        _amountDigits = (wantedInput ? _leg!.Output : _leg!.InputSpent).ToString(CultureInfo.InvariantCulture);
        var input = ResolveAmountInput(gameController);
        var rect = input.GetClientRectCache;
        if (!input.IsVisible || rect.Width <= 0 || rect.Height <= 0)
        {
            Cancel($"The {SideName} amount input is not visible with valid geometry.");
            return;
        }

        BeginMovement(new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f), now, cursorSpeed);
        Status = $"Moving to {SideName} amount input for exact value {_amountDigits}.";
    }

    private string SideName => _wantedInput ? "wanted" : "offered";

    private Element ResolveAmountInput(GameController gameController)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        return _wantedInput ? panel.WantedItemCountInput : panel.OfferedItemCountInput;
    }

    private void BeginMovement(Vector2 target, DateTimeOffset now, int cursorSpeed)
    {
        if (cursorSpeed <= 0)
        {
            Cancel("Cursor speed must be positive.");
            return;
        }

        _moveStart = ExileInput.MousePositionNum;
        _moveTarget = target;
        _lastCommandedCursor = _moveStart;
        _moveStartedAt = now;
        _moveDuration = TimeSpan.FromSeconds(Math.Max(Vector2.Distance(_moveStart, target) / cursorSpeed, 0.01));
        _stepDeadline = now + _moveDuration + StepTimeout;
        State = SingleLegStagingState.MovingToAmount;
    }

    private void TickMovement(DateTimeOffset now, SingleLegStagingState completedState)
    {
        if (now > _stepDeadline)
        {
            Cancel("Amount-input cursor movement timed out.");
            return;
        }

        var progress = Math.Clamp((now - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _moveTarget, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommandedCursor = next;
        if (progress >= 1)
        {
            State = completedState;
        }
    }

    private void ClickAmount(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Amount-input click deadline expired.");
            return;
        }

        var input = ResolveAmountInput(gameController);
        var rect = input.GetClientRectCache;
        var freshCenter = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
        if (!input.IsVisible || rect.Width <= 0 || rect.Height <= 0 ||
            Vector2.Distance(freshCenter, _moveTarget) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshCenter) > CursorTolerance)
        {
            Cancel("Amount-input geometry changed before its verified click.");
            return;
        }

        ClickLeft();
        _stepDeadline = now + StepTimeout;
        State = SingleLegStagingState.WaitingForAmountFocus;
    }

    private void WaitForAmountFocus(GameController gameController, DateTimeOffset now)
    {
        if (HasExactAmountFocus(gameController))
        {
            State = SingleLegStagingState.SelectingAmountText;
            return;
        }

        if (now > _stepDeadline)
        {
            Cancel($"The {SideName} amount input did not receive verified focus.");
        }
    }

    private void SelectAmountText(GameController gameController, DateTimeOffset now)
    {
        if (!ValidateAmountFocus(gameController, now, out var failure))
        {
            Cancel(failure);
            return;
        }

        SendControlChord(Keys.A);
        _nextActionAt = now + TimeSpan.FromMilliseconds(50);
        State = SingleLegStagingState.ClearingAmountText;
    }

    private void ClearAmountText(GameController gameController, DateTimeOffset now)
    {
        if (now < _nextActionAt)
        {
            return;
        }

        if (!ValidateAmountFocus(gameController, now, out var failure))
        {
            Cancel(failure);
            return;
        }

        TapKey(Keys.Back);
        _digitIndex = 0;
        _nextActionAt = now + KeyInterval;
        _stepDeadline = now + StepTimeout;
        State = SingleLegStagingState.TypingAmount;
    }

    private void TypeAmount(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel($"Typing the {SideName} amount timed out.");
            return;
        }

        if (now < _nextActionAt)
        {
            return;
        }

        if (_digitIndex < _amountDigits.Length)
        {
            if (!ValidateAmountFocus(gameController, now, out var failure))
            {
                Cancel(failure);
                return;
            }

            TapKey((Keys)((int)Keys.D0 + (_amountDigits[_digitIndex++] - '0')));
            _nextActionAt = now + KeyInterval;
            return;
        }

        _nextActionAt = now + TimeSpan.FromMilliseconds(75);
        State = SingleLegStagingState.VerifyingAmount;
    }

    private void VerifyAmount(GameController gameController, DateTimeOffset now, int cursorSpeed)
    {
        if (now < _nextActionAt)
        {
            return;
        }

        if (!TryReadExactDigits(ResolveAmountInput(gameController), out var digits, out var readFailure) ||
            !string.Equals(digits, _amountDigits, StringComparison.Ordinal))
        {
            Cancel($"The {SideName} amount verification read '{digits}', expected '{_amountDigits}': {readFailure}");
            return;
        }

        if (!_wantedInput)
        {
            BeginAmountInput(gameController, wantedInput: true, now, cursorSpeed);
            return;
        }

        _stepDeadline = now + StepTimeout;
        State = SingleLegStagingState.LockingAmounts;
        Status = "Both exact amounts verified; preparing one focused Enter to lock the ratio. Place Order remains untouched.";
    }

    private void LockAmounts(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline || !HasExactAmountFocus(gameController))
        {
            Cancel(now > _stepDeadline
                ? "Amount lock-in deadline expired."
                : "Wanted amount input lost verified focus before Enter lock-in.");
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var offeredReadable = TryReadExactDigits(
            panel.OfferedItemCountInput, out var offered, out var offeredFailure);
        var wantedReadable = TryReadExactDigits(
            panel.WantedItemCountInput, out var wanted, out var wantedFailure);
        if (!offeredReadable || !wantedReadable ||
            offered != _leg!.InputSpent.ToString(CultureInfo.InvariantCulture) ||
            wanted != _leg.Output.ToString(CultureInfo.InvariantCulture))
        {
            Cancel($"Pre-Enter amount verification failed: offered '{offered}' ({offeredFailure}), " +
                $"wanted '{wanted}' ({wantedFailure}).");
            return;
        }

        TapKey(Keys.Enter);
        BeginQuoteSampling(now, finalSample: true);
        Status = "Pressed Enter once with verified wanted-field focus; validating locked amounts, quote, and unchanged orders.";
    }

    private void BeginQuoteSampling(DateTimeOffset now, bool finalSample)
    {
        _stableSamples.Reset();
        _lastQuoteSampleFailure = string.Empty;
        _nextActionAt = now;
        _stepDeadline = now + TimeSpan.FromSeconds(10);
        State = finalSample ? SingleLegStagingState.SamplingFinalQuote : SingleLegStagingState.SamplingInitialQuote;
    }

    private void SampleFreshQuote(
        GameController gameController,
        DateTimeOffset now,
        int cursorSpeed,
        int requiredSamples,
        bool finalSample)
    {
        if (now > _stepDeadline)
        {
            CancelForFreshProbe(string.IsNullOrWhiteSpace(_lastQuoteSampleFailure)
                ? "Stable staging quote sampling timed out."
                : $"Stable staging quote sampling timed out: {_lastQuoteSampleFailure}");
            return;
        }

        if (now < _nextActionAt)
        {
            return;
        }

        if (!CurrentMarketReader.TryCapture(
                gameController, _sessionId, out var capture, out var failure, requireSelectedMarketHead: false) || capture is null)
        {
            CancelForFreshProbe(failure);
            return;
        }

        if (!TryCreateStagingSample(
                _leg!, capture, out var sample, out var sampleFailure, _quoteValidationPolicy))
        {
            if (ShouldRetryMissingCompetingBook(_leg!, capture, _quoteValidationPolicy))
            {
                _lastQuoteSampleFailure = sampleFailure;
                Status = $"Waiting for the selected competing book to become readable: {sampleFailure}";
                _nextActionAt = now + TimeSpan.FromMilliseconds(100);
                return;
            }
            CancelForFreshProbe(sampleFailure);
            return;
        }

        _lastQuoteSampleFailure = string.Empty;
        if (!_stableSamples.Observe(sample, requiredSamples))
        {
            Status = $"Stable {(finalSample ? "final" : "initial")} quote sample {_stableSamples.Count}/{requiredSamples}.";
            _nextActionAt = now + TimeSpan.FromMilliseconds(100);
            return;
        }

        if (!finalSample)
        {
            BeginAmountInput(gameController, wantedInput: false, now, cursorSpeed);
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var offeredReadable = TryReadExactDigits(
            panel.OfferedItemCountInput, out var offered, out var offeredFailure);
        var wantedReadable = TryReadExactDigits(
            panel.WantedItemCountInput, out var wanted, out var wantedFailure);
        if (!offeredReadable || !wantedReadable ||
            !string.Equals(offered, _leg!.InputSpent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(wanted, _leg.Output.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            Cancel($"Final amount verification failed: offered '{offered}' ({offeredFailure}), wanted '{wanted}' ({wantedFailure}).");
            return;
        }

        _stepDeadline = now + NoOrderObservation;
        State = SingleLegStagingState.ObservingNoOrder;
        Status = "Amounts and stable live quote verified; observing three seconds for proof that no order appeared.";
    }

    private void ObserveNoOrder(GameController gameController, DateTimeOffset now)
    {
        if (now < _stepDeadline)
        {
            return;
        }

        State = SingleLegStagingState.Staged;
        Status = $"DRY RUN staged: {_leg!.InputSpent} {_leg.Edge.From.Name} -> {_leg.Output} {_leg.Edge.To.Name}; " +
            "exact pair/amounts/quote verified, no new order observed, Place Order was not clicked.";
    }

    private bool VerifyFreshQuote(GameController gameController, out string failure)
    {
        var leg = _leg ?? throw new InvalidOperationException("No staging leg is active.");
        if (!CurrentMarketReader.TryCapture(
                gameController, _sessionId, out var capture, out failure, requireSelectedMarketHead: false) || capture is null)
        {
            return false;
        }

        return TryValidateLiveEdge(
            leg, MarketCaptureNormalizer.CreateEdges(capture), out failure, _quoteValidationPolicy);
    }

    /// <summary>
    /// True when the planner deliberately priced this leg one minimum unit better than the quoted
    /// competing head, which is the only case where the staged rate is expected to match no live row.
    /// </summary>
    public static bool IsImprovedCompetingLeg(RouteLegResult leg)
    {
        ArgumentNullException.ThrowIfNull(leg);
        return leg.Edge.ExecutionIntent == QuoteExecutionIntent.Competing &&
            leg.Edge.SourceBook == QuoteBookSource.ImprovedCompeting;
    }

    /// <summary>
    /// Validates an improved leg against the live books by bracket rather than by exact rate: it must
    /// still sit strictly better than the competing head it is jumping and strictly short of crossing
    /// the immediate price. Either bound closing is a market move, so callers re-probe.
    /// </summary>
    public static bool TryValidateImprovedCompetingBounds(
        RouteLegResult leg,
        IEnumerable<DirectedExchangeEdge> liveEdges,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(liveEdges);
        var directed = liveEdges
            .Where(edge => edge.From.Equals(leg.Edge.From) && edge.To.Equals(leg.Edge.To))
            .ToArray();
        var immediate = directed
            .Where(edge => edge.ExecutionIntent == QuoteExecutionIntent.Immediate)
            .OrderByDescending(edge => edge.Rate)
            .FirstOrDefault();
        var competing = directed
            .Where(edge => edge.ExecutionIntent == QuoteExecutionIntent.Competing)
            .OrderBy(edge => edge.Rate)
            .FirstOrDefault();
        if (immediate is null || competing is null)
        {
            failure = "The live books no longer publish both sides needed to bracket the improved rate.";
            return false;
        }
        if (leg.Edge.Rate <= immediate.Rate)
        {
            failure = "The improved competing rate would now meet or cross the live immediate price.";
            return false;
        }
        if (leg.Edge.Rate >= competing.Rate)
        {
            failure = "The live competing head now matches or beats the improved rate.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryValidateLiveEdge(
        RouteLegResult leg,
        IEnumerable<DirectedExchangeEdge> liveEdges,
        out string failure,
        SingleLegQuoteValidationPolicy policy = SingleLegQuoteValidationPolicy.ExactCandidate)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(liveEdges);
        if (!TryValidatePolicy(leg, policy, out failure))
        {
            return false;
        }
        if (IsImprovedCompetingLeg(leg))
        {
            return TryValidateImprovedCompetingBounds(leg, liveEdges, out failure);
        }
        var matching = liveEdges.FirstOrDefault(edge =>
            edge.From.Equals(leg.Edge.From) && edge.To.Equals(leg.Edge.To) &&
            edge.ExecutionIntent == leg.Edge.ExecutionIntent && edge.Rate == leg.Edge.Rate &&
            (policy != SingleLegQuoteValidationPolicy.AggressiveImmediateLimit ||
             IdentityMatches(edge.From, leg.Edge.From) && IdentityMatches(edge.To, leg.Edge.To)));
        if (matching is null)
        {
            failure = "The live quote no longer exactly matches the candidate leg.";
            return false;
        }

        if (matching.ExecutionIntent == QuoteExecutionIntent.Immediate &&
            (policy == SingleLegQuoteValidationPolicy.AggressiveImmediateLimit
                ? matching.ImmediateInputDepth <= 0
                : matching.ImmediateInputDepth < leg.InputSpent))
        {
            failure = policy == SingleLegQuoteValidationPolicy.AggressiveImmediateLimit
                ? "The live immediate head no longer has positive readable depth."
                : "The live immediate depth no longer covers the staged input.";
            return false;
        }

        if (matching.ExecutionIntent == QuoteExecutionIntent.Competing &&
            matching.CompetingQueueAhead > leg.Edge.CompetingQueueAhead)
        {
            failure = "The live competing queue worsened after candidate selection.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryCreateStagingSample(
        RouteLegResult leg,
        MarketCapture capture,
        out StagingQuoteSample sample,
        out string failure,
        SingleLegQuoteValidationPolicy policy = SingleLegQuoteValidationPolicy.ExactCandidate)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(capture);
        if (!TryValidatePolicy(leg, policy, out failure))
        {
            sample = default;
            return false;
        }
        var edges = MarketCaptureNormalizer.CreateEdges(capture);
        DirectedExchangeEdge? matching;
        // An improved leg holds a rate no live row carries, so it samples the competing head it is
        // jumping and is validated by the bracket check instead of by an exact rate match.
        if (policy == SingleLegQuoteValidationPolicy.PreserveCompetingLimit || IsImprovedCompetingLeg(leg))
        {
            matching = edges.FirstOrDefault(edge =>
                edge.From.Equals(leg.Edge.From) && edge.To.Equals(leg.Edge.To) &&
                edge.ExecutionIntent == QuoteExecutionIntent.Competing);
            if (matching is null)
            {
                sample = default;
                failure = "The competing limit pair or readable book head disappeared during staging.";
                return false;
            }
            if (IsImprovedCompetingLeg(leg) && !TryValidateImprovedCompetingBounds(leg, edges, out failure))
            {
                sample = default;
                return false;
            }
        }
        else if (!TryValidateLiveEdge(leg, edges, out failure, policy))
        {
            sample = default;
            return false;
        }
        else
        {
            matching = edges.First(edge =>
                edge.From.Equals(leg.Edge.From) && edge.To.Equals(leg.Edge.To) &&
                edge.ExecutionIntent == leg.Edge.ExecutionIntent && edge.Rate == leg.Edge.Rate &&
                (policy != SingleLegQuoteValidationPolicy.AggressiveImmediateLimit ||
                 IdentityMatches(edge.From, leg.Edge.From) && IdentityMatches(edge.To, leg.Edge.To)));
        }
        var relevantRows = matching.SourceBook == QuoteBookSource.WantedItemStock
            ? capture.WantedItemStock
            : capture.OfferedItemStock;
        var fingerprint = string.Join("|", relevantRows.Select(level =>
            $"{level.Get}/{level.Give}:{level.ListedCount}"));
        sample = new StagingQuoteSample(
            matching.Rate,
            matching.ExecutionIntent,
            matching.ImmediateInputDepth,
            matching.CompetingQueueAhead,
            $"{matching.SourceBook}:{fingerprint}");
        failure = string.Empty;
        return true;
    }

    public static bool ShouldRetryMissingCompetingBook(
        RouteLegResult leg,
        MarketCapture capture,
        SingleLegQuoteValidationPolicy policy) =>
        (policy == SingleLegQuoteValidationPolicy.PreserveCompetingLimit || IsImprovedCompetingLeg(leg)) &&
        leg.Edge.ExecutionIntent == QuoteExecutionIntent.Competing &&
        capture.Pair.Equals(leg.Edge.Pair);

    public static bool TryValidatePolicy(
        RouteLegResult leg,
        SingleLegQuoteValidationPolicy policy,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(leg);
        var valid = policy switch
        {
            SingleLegQuoteValidationPolicy.ExactCandidate => true,
            SingleLegQuoteValidationPolicy.PreserveCompetingLimit =>
                leg.Edge.ExecutionIntent == QuoteExecutionIntent.Competing,
            SingleLegQuoteValidationPolicy.AggressiveImmediateLimit =>
                leg.Edge.ExecutionIntent == QuoteExecutionIntent.Immediate,
            _ => false,
        };
        failure = valid
            ? string.Empty
            : $"Quote-validation policy {policy} is incompatible with {leg.Edge.ExecutionIntent} intent.";
        return valid;
    }

    private static bool IdentityMatches(CurrencyIdentity left, CurrencyIdentity right) =>
        string.Equals(left.Metadata, right.Metadata, StringComparison.Ordinal) && left.Hash == right.Hash;

    public static bool IsValidAmount(long amount) => amount is > 0 and <= MaximumAmount;

    public static bool HasNewOrder(IEnumerable<int> baselineOrderIds, IEnumerable<int> currentOrderIds)
    {
        ArgumentNullException.ThrowIfNull(baselineOrderIds);
        ArgumentNullException.ThrowIfNull(currentOrderIds);
        var baseline = baselineOrderIds.ToHashSet();
        return currentOrderIds.Any(id => !baseline.Contains(id));
    }

    public static bool OrderSetMatchesBaseline(IEnumerable<int> baselineOrderIds, IEnumerable<int> currentOrderIds)
    {
        ArgumentNullException.ThrowIfNull(baselineOrderIds);
        ArgumentNullException.ThrowIfNull(currentOrderIds);
        return baselineOrderIds.ToHashSet().SetEquals(currentOrderIds);
    }

    private bool ValidateAmountFocus(GameController gameController, DateTimeOffset now, out string failure)
    {
        if (now > _stepDeadline || !HasExactAmountFocus(gameController))
        {
            failure = now > _stepDeadline
                ? "Amount input deadline expired."
                : $"The {SideName} amount input lost verified focus.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private bool HasExactAmountFocus(GameController gameController)
    {
        var input = ResolveAmountInput(gameController);
        return input.IsActive && HasFocusWithin(gameController, input);
    }

    private static bool HasFocusWithin(GameController gameController, Element input)
    {
        var focused = gameController.Game.IngameState.FocusedInputElement;
        var depth = 0;
        for (var element = focused; element is not null && depth++ < 32; element = element.Parent)
        {
            if (element.Address == input.Address)
            {
                return true;
            }
        }

        return false;
    }

    public static string ReadDigits(Element? input)
    {
        return TryReadExactDigits(input, out var digits, out _) ? digits : string.Empty;
    }

    public static bool TryReadExactDigits(Element? input, out string digits, out string failure)
    {
        if (input is null)
        {
            digits = string.Empty;
            failure = "Amount input was null.";
            return false;
        }

        return TryResolveExactDigitTexts(EnumerateVisibleText(input, depth: 0), out digits, out failure);
    }

    public static bool TryResolveExactDigitTexts(
        IEnumerable<string> visibleTexts,
        out string digits,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(visibleTexts);
        var candidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in visibleTexts)
        {
            var text = value.Trim();
            if (!text.Any(character => character is >= '0' and <= '9'))
            {
                continue;
            }

            if (!text.All(character => character is >= '0' and <= '9'))
            {
                digits = string.Empty;
                failure = $"Amount text '{text}' was not an exact ASCII integer.";
                return false;
            }

            candidates.Add(text);
        }

        if (candidates.Count != 1)
        {
            digits = string.Empty;
            failure = $"Expected one unambiguous amount value, found {candidates.Count}.";
            return false;
        }

        digits = candidates.Single();
        failure = string.Empty;
        return true;
    }

    private static IEnumerable<string> EnumerateVisibleText(Element element, int depth)
    {
        if (depth > 3 || (depth > 0 && !element.IsVisible))
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(element.Text))
        {
            yield return element.Text;
        }

        foreach (var child in element.Children)
        {
            foreach (var text in EnumerateVisibleText(child, depth + 1))
            {
                yield return text;
            }
        }
    }

    private void CancelForFreshProbe(string reason)
    {
        FreshProbeRetryRecommended = true;
        Cancel(reason);
    }

    private static bool TryReadOrderIds(GameController gameController, out HashSet<int> ids, out string failure)
    {
        try
        {
            var values = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.Orders
                .Select(order => order.PlayerOrderId)
                .ToArray();
            if (values.Any(id => id <= 0) || values.Distinct().Count() != values.Length)
            {
                ids = [];
                failure = "Order baseline contained a nonpositive or duplicate player order ID.";
                return false;
            }

            ids = values.ToHashSet();
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            ids = [];
            failure = $"Order baseline read failed: {exception.Message}";
            return false;
        }
    }

    private void ClickLeft()
    {
        _mouseDown = true;
        ExileInput.LeftDown();
        try
        {
        }
        finally
        {
            ExileInput.LeftUp();
            _mouseDown = false;
        }
    }

    private void SendControlChord(Keys key)
    {
        PressKey(Keys.ControlKey);
        try
        {
            TapKey(key);
        }
        finally
        {
            ReleaseKey(Keys.ControlKey);
        }
    }

    private void TapKey(Keys key)
    {
        PressKey(key);
        ReleaseKey(key);
    }

    private void PressKey(Keys key)
    {
        _ownedKeys.Add(key);
        ExileInput.KeyDown(key);
    }

    private void ReleaseKey(Keys key)
    {
        if (_ownedKeys.Remove(key))
        {
            try
            {
                ExileInput.KeyUp(key);
            }
            catch
            {
                _ownedKeys.Add(key);
                throw;
            }
        }
    }

    private void BeginRelease(SingleLegStagingState target, string status)
    {
        _releaseTarget = target;
        _releaseStatus = status;
        if (TryReleaseOwnedInput())
        {
            State = target;
            Status = status;
        }
        else
        {
            State = SingleLegStagingState.ReleasingInput;
            Status = "Input release pending; retrying every tick.";
        }
    }

    private bool TryReleaseOwnedInput()
    {
        foreach (var key in _ownedKeys.ToArray())
        {
            try
            {
                ExileInput.KeyUp(key);
                _ownedKeys.Remove(key);
            }
            catch
            {
            }
        }

        if (_mouseDown)
        {
            try
            {
                ExileInput.LeftUp();
                _mouseDown = false;
            }
            catch
            {
            }
        }

        return _ownedKeys.Count == 0 && !_mouseDown;
    }

    private static bool ModifiersHeld() =>
        ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu);
}
