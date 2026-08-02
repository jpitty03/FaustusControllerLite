using ExileCore;
using FaustusControllerLite.Input;
using FaustusControllerLite.Probing;
using System.Globalization;
using System.Numerics;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum SingleLegPlacementState
{
    Idle,
    MovingToPlaceOrder,
    ReadyToClick,
    WaitingForOrder,
    ReleasingInput,
    Completed,
    Ambiguous,
    Cancelled,
}

public sealed record PlacementInputPermissions(
    bool MouseMovement,
    bool Clicking,
    bool Placement,
    bool FullWorkflow,
    bool WorkflowAuthorized = false)
{
    public bool Ready => MouseMovement && Clicking && Placement && (!FullWorkflow || WorkflowAuthorized);

    public static PlacementInputPermissions From(FaustusControllerLiteSettings settings, bool workflowAuthorized = false) => new(
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowFullWorkflow.Value,
        workflowAuthorized);
}

public sealed record PlacedOrderSnapshot(
    int PlayerOrderId,
    DateTimeOffset CreationDate,
    string OfferedMetadata,
    uint OfferedHash,
    string WantedMetadata,
    uint WantedHash,
    int OriginalOfferedAmount,
    int RemainingOfferedAmount,
    int ReceivedWantedAmount,
    int OfferedRatioPart,
    int WantedRatioPart,
    int GoldCost,
    bool IsCompleted,
    bool IsCanceled);

public enum PlacementObservationKind
{
    Waiting,
    Matched,
    Ambiguous,
}

public sealed record PlacementObservation(
    PlacementObservationKind Kind,
    PlacedOrderSnapshot? Match,
    string Detail);

public static class PlacementOrderMatcher
{
    public static PlacementObservation Evaluate(
        IReadOnlyCollection<int> baselineIds,
        IReadOnlyCollection<PlacedOrderSnapshot> current,
        RouteLegResult leg,
        DateTimeOffset clickedAtUtc,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(baselineIds);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(leg);
        if (current.Any(order => order.PlayerOrderId <= 0) ||
            current.Select(order => order.PlayerOrderId).Distinct().Count() != current.Count)
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, null,
                "Order list contained a nonpositive or duplicate ID.");
        }

        var baseline = baselineIds.ToHashSet();
        if (!baseline.IsSubsetOf(current.Select(order => order.PlayerOrderId)))
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, null,
                "A baseline order disappeared during placement verification.");
        }

        var newOrders = current.Where(order => !baseline.Contains(order.PlayerOrderId)).ToArray();
        if (newOrders.Length == 0)
        {
            return new PlacementObservation(PlacementObservationKind.Waiting, null, "No new order observed yet.");
        }

        if (newOrders.Length != 1)
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, null,
                $"Observed {newOrders.Length} new orders after one click.");
        }

        var order = newOrders[0];
        if (leg.Edge.From.Hash == 0 || leg.Edge.To.Hash == 0 ||
            order.OfferedHash != leg.Edge.From.Hash || order.WantedHash != leg.Edge.To.Hash)
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, order,
                "The single new order did not have exact nonzero currency hashes.");
        }

        var creationPlausible = order.CreationDate >= clickedAtUtc.AddSeconds(-1) &&
            order.CreationDate <= observedAtUtc.AddSeconds(1);
        var economicsMatch = string.Equals(order.OfferedMetadata, leg.Edge.From.Metadata, StringComparison.Ordinal) &&
            string.Equals(order.WantedMetadata, leg.Edge.To.Metadata, StringComparison.Ordinal) &&
            order.OriginalOfferedAmount == leg.InputSpent &&
            RatiosEquivalent(order.OfferedRatioPart, order.WantedRatioPart, leg.InputSpent, leg.Output) &&
            order.OfferedRatioPart > 0 && order.OriginalOfferedAmount % order.OfferedRatioPart == 0 &&
            (BigInteger)(order.OriginalOfferedAmount / order.OfferedRatioPart) * order.WantedRatioPart == leg.Output &&
            order.RemainingOfferedAmount is >= 0 && order.RemainingOfferedAmount <= order.OriginalOfferedAmount &&
            order.ReceivedWantedAmount >= 0;
        if (order.IsCompleted && !CompletedFillProvable(order, leg.Output))
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, order,
                "Completed order reported terminal amounts its placed ratio cannot prove.");
        }
        if (!creationPlausible || !economicsMatch || order.IsCanceled)
        {
            return new PlacementObservation(PlacementObservationKind.Ambiguous, order,
                "The single new order did not match timestamp, pair, amount, ratio, or non-canceled status.");
        }

        if (!order.IsCompleted)
        {
            return new PlacementObservation(PlacementObservationKind.Matched, order, "Matching order is pending.");
        }

        return new PlacementObservation(PlacementObservationKind.Matched, order, order.RemainingOfferedAmount == 0
            ? "Matching order completed immediately with the full planned fill."
            : "Matching order completed immediately without filling everything; the offered remainder is collectible.");
    }

    /// <summary>
    /// True when a completed order's terminal amounts are provable from the row itself. An order that
    /// executes immediately is terminal even when the book could not fill all of it: the unfilled
    /// offered amount is a collectible return. A fill may improve on the placed limit ratio, so the
    /// received amount is bounded below by what that ratio entitles for the filled portion and above
    /// by the whole order's wanted amount. Anything outside those bounds is genuine ambiguity.
    /// </summary>
    public static bool TerminalAmountsProvable(PlacedOrderSnapshot order, long orderedWantedAmount)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.OfferedRatioPart <= 0 || order.WantedRatioPart <= 0 || orderedWantedAmount <= 0 ||
            order.RemainingOfferedAmount < 0 || order.RemainingOfferedAmount > order.OriginalOfferedAmount ||
            order.ReceivedWantedAmount < 0 || order.ReceivedWantedAmount > orderedWantedAmount)
        {
            return false;
        }

        var filled = (BigInteger)order.OriginalOfferedAmount - order.RemainingOfferedAmount;
        if (filled == 0) return order.ReceivedWantedAmount == 0;
        return filled * order.WantedRatioPart / order.OfferedRatioPart <= order.ReceivedWantedAmount;
    }

    /// <summary>
    /// True when a <em>completed</em> order's amounts are believable. Completion means the book
    /// executed the order, so at least one unit must have filled; a completed row that filled nothing
    /// is a misread, not a terminal outcome. Cancellation has no such requirement.
    /// </summary>
    public static bool CompletedFillProvable(PlacedOrderSnapshot order, long orderedWantedAmount)
    {
        ArgumentNullException.ThrowIfNull(order);
        return order.RemainingOfferedAmount < order.OriginalOfferedAmount &&
            TerminalAmountsProvable(order, orderedWantedAmount);
    }

    public static bool RatiosEquivalent(long offeredLeft, long wantedLeft, long offeredRight, long wantedRight)
    {
        if (offeredLeft <= 0 || wantedLeft <= 0 || offeredRight <= 0 || wantedRight <= 0)
        {
            return false;
        }

        return (BigInteger)offeredLeft * wantedRight == (BigInteger)offeredRight * wantedLeft;
    }
}

public sealed class SingleLegPlacementController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan PlacementTimeout = TimeSpan.FromSeconds(3);
    private readonly HashSet<Keys> _ownedKeys = [];
    private readonly HashSet<int> _baselineOrderIds = [];
    private RouteLegResult? _leg;
    private Func<TrackedOrderState, string, bool>? _persist;
    private Func<RouteLegResult, (bool IsValid, string Failure)>? _finalValidation;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private Vector2 _moveStart;
    private Vector2 _target;
    private Vector2 _lastCommanded;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _deadline;
    private DateTimeOffset _clickedAtUtc;
    private bool _mouseDown;
    private bool _clickAttempted;
    private Guid _attemptId;
    private Guid _probeSessionId;
    private string _candidateSignature = string.Empty;
    private int _competingOrderWaitMinutes;
    private int _offeredMaxStackSize;
    private int _wantedMaxStackSize;
    private SingleLegPlacementState _releaseTarget;
    private string _releaseStatus = string.Empty;

    public SingleLegPlacementState State { get; private set; } = SingleLegPlacementState.Idle;
    public string Status { get; private set; } = "Idle; placement is disabled.";
    public string Failure { get; private set; } = string.Empty;
    public TrackedOrderState? Outcome { get; private set; }
    public bool IsRunning => State is SingleLegPlacementState.MovingToPlaceOrder or
        SingleLegPlacementState.ReadyToClick or SingleLegPlacementState.WaitingForOrder or
        SingleLegPlacementState.ReleasingInput;

    public bool Start(
        GameController gameController,
        RouteLegResult stagedLeg,
        PickerCalibration calibration,
        PlacementInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        int competingOrderWaitMinutes,
        Guid probeSessionId,
        string candidateSignature,
        int offeredMaxStackSize,
        int wantedMaxStackSize,
        Func<RouteLegResult, (bool IsValid, string Failure)> finalValidation,
        Func<TrackedOrderState, string, bool> persist,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(stagedLeg);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(finalValidation);
        if (IsRunning || !permissions.Ready || conflictingControllerEnabled || competingOrderWaitMinutes < 1)
        {
            failure = "Placement is running, permissions are incomplete, full workflow is enabled, or controller exclusion failed.";
            return false;
        }

        if (!TryValidateContext(gameController, stagedLeg, calibration, out var target, out failure) ||
            !TryReadOrders(gameController, out var orders, out failure))
        {
            return false;
        }

        _leg = stagedLeg;
        _persist = persist;
        _finalValidation = finalValidation;
        _attemptId = Guid.NewGuid();
        _probeSessionId = probeSessionId;
        _candidateSignature = candidateSignature;
        _offeredMaxStackSize = offeredMaxStackSize;
        _wantedMaxStackSize = wantedMaxStackSize;
        _competingOrderWaitMinutes = competingOrderWaitMinutes;
        _clickAttempted = false;
        _league = gameController.Game.IngameState.ServerData.League;
        _areaInstanceId = gameController.Game.IngameState.ServerData.InstanceId;
        _baselineOrderIds.Clear();
        _baselineOrderIds.UnionWith(orders.Select(order => order.PlayerOrderId));
        Outcome = null;
        Failure = string.Empty;
        BeginMovement(target, cursorSpeed);
        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerCalibration calibration,
        PlacementInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed)
    {
        if (!IsRunning)
        {
            return;
        }

        if (State == SingleLegPlacementState.ReleasingInput)
        {
            if (TryReleaseOwnedInput())
            {
                State = _releaseTarget;
                Status = _releaseStatus;
            }
            return;
        }

        try
        {
            if (!ValidateGlobal(gameController, permissions, conflictingControllerEnabled, out var failure))
            {
                FinishAmbiguousOrCancel(failure, clicked: _clickAttempted);
                return;
            }

            switch (State)
            {
                case SingleLegPlacementState.MovingToPlaceOrder:
                    TickMovement(gameController, calibration);
                    break;
                case SingleLegPlacementState.ReadyToClick:
                    ClickOnce(gameController, calibration);
                    break;
                case SingleLegPlacementState.WaitingForOrder:
                    ObserveOrder(gameController);
                    break;
            }
        }
        catch (Exception exception)
        {
            FinishAmbiguousOrCancel($"Placement failed closed: {exception.GetType().Name}: {exception.Message}",
                clicked: _clickAttempted);
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning || State == SingleLegPlacementState.ReleasingInput)
        {
            return;
        }
        FinishAmbiguousOrCancel(reason, clicked: _clickAttempted);
    }

    public void EmergencyStop(string reason)
    {
        if (IsRunning && State != SingleLegPlacementState.ReleasingInput)
            FinishAmbiguousOrCancel(reason, clicked: _clickAttempted);
        TryReleaseOwnedInput();
    }

    private bool ValidateGlobal(
        GameController gameController,
        PlacementInputPermissions permissions,
        bool conflictingControllerEnabled,
        out string failure)
    {
        if (!permissions.Ready || conflictingControllerEnabled || !gameController.Window.IsForeground() ||
            ModifiersHeld())
        {
            failure = "Placement permission, controller exclusion, foreground, or modifier gate failed.";
            return false;
        }

        var server = gameController.Game.IngameState.ServerData;
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!string.Equals(server.League, _league, StringComparison.Ordinal) || server.InstanceId != _areaInstanceId)
        {
            failure = "League or area changed during placement.";
            return false;
        }

        if (!panel.IsVisible ||
            !gameController.Game.IngameState.IngameUi.StashElement.IsVisible ||
            !gameController.Game.IngameState.IngameUi.InventoryPanel.IsVisible)
        {
            failure = "Exchange, stash, or inventory visibility changed during placement.";
            return false;
        }
        if (State != SingleLegPlacementState.WaitingForOrder &&
            (panel.CurrencyPicker.IsVisible ||
            !string.Equals(panel.OfferedItemType?.Metadata, _leg!.Edge.From.Metadata, StringComparison.Ordinal) ||
            !string.Equals(panel.WantedItemType?.Metadata, _leg.Edge.To.Metadata, StringComparison.Ordinal) ||
            !SingleLegStagingController.TryReadExactDigits(panel.OfferedItemCountInput, out var offered, out _) ||
            !SingleLegStagingController.TryReadExactDigits(panel.WantedItemCountInput, out var wanted, out _) ||
            offered != _leg.InputSpent.ToString(CultureInfo.InvariantCulture) ||
            wanted != _leg.Output.ToString(CultureInfo.InvariantCulture)))
        {
            failure = "Exchange, stash, inventory, pair, picker, or staged amounts changed during placement.";
            return false;
        }

        if (State != SingleLegPlacementState.WaitingForOrder &&
            Vector2.Distance(ExileInput.MousePositionNum, _lastCommanded) > CursorTolerance)
        {
            failure = "Manual cursor movement interrupted placement.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool TryValidateContext(
        GameController gameController,
        RouteLegResult leg,
        PickerCalibration calibration,
        out Vector2 target,
        out string failure)
    {
        target = default;
        var ui = gameController.Game.IngameState.IngameUi;
        var panel = ui.CurrencyExchangePanel;
        if (!gameController.Window.IsForeground() || !panel.IsVisible || panel.CurrencyPicker.IsVisible ||
            ui.PopUpWindow.IsVisible || !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible ||
            !string.Equals(panel.OfferedItemType?.Metadata, leg.Edge.From.Metadata, StringComparison.Ordinal) ||
            !string.Equals(panel.WantedItemType?.Metadata, leg.Edge.To.Metadata, StringComparison.Ordinal) ||
            !SingleLegStagingController.TryReadExactDigits(panel.OfferedItemCountInput, out var offered, out _) ||
            !SingleLegStagingController.TryReadExactDigits(panel.WantedItemCountInput, out var wanted, out _) ||
            !string.Equals(offered, leg.InputSpent.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ||
            !string.Equals(wanted, leg.Output.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            failure = "Panel, pair, picker, or exact staged amount verification failed.";
            return false;
        }

        var rect = panel.GetClientRectCache;
        return calibration.TryResolvePlaceOrder(rect.X, rect.Y, rect.Width, rect.Height, out target, out failure);
    }

    private void BeginMovement(Vector2 target, int cursorSpeed)
    {
        _target = target;
        _moveStart = ExileInput.MousePositionNum;
        _lastCommanded = _moveStart;
        _moveStartedAt = DateTimeOffset.UtcNow;
        _moveDuration = TimeSpan.FromSeconds(Math.Clamp(
            Vector2.Distance(_moveStart, target) / Math.Max(cursorSpeed, 1), 0.12, 0.65));
        State = SingleLegPlacementState.MovingToPlaceOrder;
        Status = "Moving to calibrated Place Order target; no click yet.";
    }

    private void TickMovement(GameController gameController, PickerCalibration calibration)
    {
        if (!TryValidateContext(gameController, _leg!, calibration, out var freshTarget, out var failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance)
        {
            FinishAmbiguousOrCancel(string.IsNullOrEmpty(failure) ? "Place Order target moved." : failure, clicked: false);
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _target, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommanded = next;
        if (progress >= 1)
        {
            State = SingleLegPlacementState.ReadyToClick;
            Status = "At Place Order target; performing final revalidation before the sole click.";
        }
    }

    private void ClickOnce(GameController gameController, PickerCalibration calibration)
    {
        var finalGate = _finalValidation!(_leg!);
        if (!finalGate.IsValid)
        {
            FinishAmbiguousOrCancel($"Final full-route validation failed: {finalGate.Failure}", clicked: false);
            return;
        }

        if (!TryValidateContext(gameController, _leg!, calibration, out var freshTarget, out var failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshTarget) > CursorTolerance ||
            !TryReadOrders(gameController, out var orders, out failure) ||
            !_baselineOrderIds.SetEquals(orders.Select(order => order.PlayerOrderId)))
        {
            FinishAmbiguousOrCancel(string.IsNullOrEmpty(failure) ? "Final placement context changed." : failure, clicked: false);
            return;
        }

        if (!CurrentMarketReader.TryCapture(
                gameController, Guid.NewGuid(), out var capture, out failure, requireSelectedMarketHead: false) || capture is null ||
            !LiveQuoteAllowsPlacement(_leg!, capture, out failure))
        {
            FinishAmbiguousOrCancel($"Final live quote rejected placement: {failure}", clicked: false);
            return;
        }

        _clickedAtUtc = DateTimeOffset.UtcNow;
        var armed = CreateTrackedState(TrackedOrderStatus.Armed, null, "Placement intent persisted before click.");
        if (!_persist!(armed, "OrderPlacementArmed"))
        {
            FinishAmbiguousOrCancel("Could not persist placement intent before click.", clicked: false);
            return;
        }

        _mouseDown = true;
        _clickAttempted = true;
        Exception? clickFailure = null;
        try
        {
            ExileInput.LeftDown();
        }
        catch (Exception exception)
        {
            clickFailure = exception;
        }
        finally
        {
            try
            {
                ExileInput.LeftUp();
                _mouseDown = false;
            }
            catch (Exception exception)
            {
                clickFailure ??= exception;
            }
        }

        if (clickFailure is not null)
        {
            FinishAmbiguousOrCancel($"Place Order click/release was ambiguous: {clickFailure.Message}", clicked: true);
            return;
        }

        _deadline = DateTimeOffset.UtcNow + PlacementTimeout;
        State = SingleLegPlacementState.WaitingForOrder;
        Status = "Clicked Place Order exactly once; matching the resulting order.";
    }

    public static bool LiveQuoteAllowsPlacement(
        RouteLegResult leg,
        MarketCapture capture,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentNullException.ThrowIfNull(capture);
        failure = string.Empty;
        if (leg.Edge.ExecutionIntent == QuoteExecutionIntent.Immediate)
            return SingleLegStagingController.TryCreateStagingSample(leg, capture, out _, out failure);

        if (capture.OfferedCurrency.Metadata != leg.Edge.From.Metadata ||
            capture.WantedCurrency.Metadata != leg.Edge.To.Metadata ||
            capture.OfferedCurrency.Hash != leg.Edge.From.Hash ||
            capture.WantedCurrency.Hash != leg.Edge.To.Hash ||
            !AutomatedProbeController.TryCreateStableHead(capture, out _, out failure))
        {
            if (string.IsNullOrEmpty(failure))
                failure = "Competing placement market identity changed or its live book became unreadable.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void ObserveOrder(GameController gameController)
    {
        if (!TryReadOrders(gameController, out var orders, out var failure))
        {
            FinishAmbiguousOrCancel(failure, clicked: true);
            return;
        }

        var observation = PlacementOrderMatcher.Evaluate(
            _baselineOrderIds, orders, _leg!, _clickedAtUtc, DateTimeOffset.UtcNow);
        if (observation.Kind == PlacementObservationKind.Matched && observation.Match is { } match)
        {
            var status = match.IsCompleted ? TrackedOrderStatus.CompletedUncollected : TrackedOrderStatus.Pending;
            var tracked = CreateTrackedState(status, match.PlayerOrderId, observation.Detail);
            tracked.GoldCost = match.GoldCost;
            tracked.OrderCreationDateUtc = match.CreationDate;
            tracked.PlacedOfferedRatioPart = match.OfferedRatioPart;
            tracked.PlacedWantedRatioPart = match.WantedRatioPart;
            tracked.WaitStartedAtUtc = _clickedAtUtc;
            tracked.WaitUntilUtc = _clickedAtUtc.AddMinutes(_competingOrderWaitMinutes);
            tracked.LastObservedAtUtc = DateTimeOffset.UtcNow;
            tracked.LastRemainingOfferedAmount = match.RemainingOfferedAmount;
            tracked.LastReceivedWantedAmount = match.ReceivedWantedAmount;
            if (match.IsCompleted)
            {
                tracked.TerminalObservedAtUtc = tracked.LastObservedAtUtc;
                tracked.TerminalRemainingOfferedAmount = match.RemainingOfferedAmount;
                tracked.TerminalReceivedWantedAmount = match.ReceivedWantedAmount;
            }
            Outcome = tracked;
            if (!_persist!(tracked, "OrderPlacementMatched"))
            {
                var recovery = CreateTrackedState(TrackedOrderStatus.Ambiguous, match.PlayerOrderId,
                    "Order matched but durable matched-state persistence failed; retain known ID for reconciliation.");
                Outcome = recovery;
                Failure = recovery.Detail;
                _persist?.Invoke(recovery, "OrderPlacementPersistenceAmbiguous");
                BeginRelease(SingleLegPlacementState.Ambiguous, recovery.Detail);
                return;
            }

            BeginRelease(SingleLegPlacementState.Completed,
                $"Matched order {match.PlayerOrderId}: {observation.Detail}");
            return;
        }

        if (observation.Kind == PlacementObservationKind.Ambiguous)
        {
            if (observation.Match is { } ambiguousMatch)
            {
                var ambiguous = CreateTrackedState(
                    TrackedOrderStatus.Ambiguous, ambiguousMatch.PlayerOrderId, observation.Detail);
                Outcome = ambiguous;
                // Without this the caller reads an empty failure and reports nothing at all.
                Failure = observation.Detail;
                _persist?.Invoke(ambiguous, "OrderPlacementAmbiguousMatchedId");
                BeginRelease(SingleLegPlacementState.Ambiguous, $"AMBIGUOUS: {observation.Detail}");
            }
            else
            {
                FinishAmbiguousOrCancel(observation.Detail, clicked: true);
            }
            return;
        }

        if (DateTimeOffset.UtcNow >= _deadline)
        {
            FinishAmbiguousOrCancel("No matching new order appeared within three seconds.", clicked: true);
        }
    }

    private TrackedOrderState CreateTrackedState(TrackedOrderStatus status, int? playerOrderId, string detail) => new()
    {
        League = _league,
        Status = status,
        PlayerOrderId = playerOrderId,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        ClickedAtUtc = _clickedAtUtc,
        OfferedMetadata = _leg!.Edge.From.Metadata,
        WantedMetadata = _leg.Edge.To.Metadata,
        OfferedAmount = _leg.InputSpent,
        WantedAmount = _leg.Output,
        Detail = detail,
        AttemptId = _attemptId,
        ProbeSessionId = _probeSessionId,
        CandidateSignature = _candidateSignature,
        OfferedHash = _leg.Edge.From.Hash,
        WantedHash = _leg.Edge.To.Hash,
        OfferedMaxStackSize = _offeredMaxStackSize,
        WantedMaxStackSize = _wantedMaxStackSize,
        BaselineOrderIds = _baselineOrderIds.Order().ToList()
    };

    private void FinishAmbiguousOrCancel(string reason, bool clicked)
    {
        Failure = reason;
        if (clicked)
        {
            var ambiguous = CreateTrackedState(TrackedOrderStatus.Ambiguous, null, reason);
            Outcome = ambiguous;
            _persist?.Invoke(ambiguous, "OrderPlacementAmbiguous");
            BeginRelease(SingleLegPlacementState.Ambiguous, $"AMBIGUOUS: {reason}");
        }
        else
        {
            BeginRelease(SingleLegPlacementState.Cancelled, $"Cancelled before click: {reason}");
        }
    }

    public static bool TryReadOrders(
        GameController gameController,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out string failure)
    {
        try
        {
            var snapshots = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.Orders
                .Select(order => new PlacedOrderSnapshot(
                    order.PlayerOrderId,
                    order.CreationDate,
                    order.OfferedItemType?.Metadata ?? string.Empty,
                    order.OfferedItemType?.Hash ?? 0,
                    order.WantedItemType?.Metadata ?? string.Empty,
                    order.WantedItemType?.Hash ?? 0,
                    order.OriginalOfferedItemStackSize,
                    order.OfferedItemStackSize,
                    order.WantedItemStackSize,
                    order.OfferedItemRatioPart,
                    order.WantedItemRatioPart,
                    order.GoldCost,
                    order.IsCompleted,
                    order.IsCanceled))
                .ToArray();
            if (snapshots.Any(order => order.PlayerOrderId <= 0 ||
                    string.IsNullOrWhiteSpace(order.OfferedMetadata) ||
                    string.IsNullOrWhiteSpace(order.WantedMetadata)) ||
                snapshots.Select(order => order.PlayerOrderId).Distinct().Count() != snapshots.Length)
            {
                orders = Array.Empty<PlacedOrderSnapshot>();
                failure = "Order snapshot contained invalid identity or duplicate IDs.";
                return false;
            }

            orders = snapshots;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            orders = Array.Empty<PlacedOrderSnapshot>();
            failure = $"Order snapshot read failed: {exception.Message}";
            return false;
        }
    }

    private void BeginRelease(SingleLegPlacementState target, string status)
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
            State = SingleLegPlacementState.ReleasingInput;
            Status = "Placement input release pending; retrying every tick.";
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
