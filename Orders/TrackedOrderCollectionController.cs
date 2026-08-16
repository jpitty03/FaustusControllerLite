using ExileCore;
using FaustusControllerLite.Core;
using ExileCore.PoEMemory;
using FaustusControllerLite.Input;
using System.Numerics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum TrackedCollectionState
{
    Idle,
    MovingToSlot,
    ReadyToClick,
    WaitingForDisappearance,
    ReleasingInput,
    CollectedEvidence,
    Ambiguous,
    Cancelled,
}

public sealed record CollectionInputPermissions(
    bool MouseMovement,
    bool Clicking,
    bool Collection,
    bool Cancellation,
    bool StashTransfer,
    bool Placement,
    bool FullWorkflow,
    bool WorkflowAuthorized = false,
    bool SellSweep = false,
    bool SweepAuthorized = false)
{
    private CoordinatorOwnership Owner => new(FullWorkflow, WorkflowAuthorized, SellSweep, SweepAuthorized);

    public bool Ready => MouseMovement && Clicking && Collection &&
        (Owner.None && !Cancellation && !StashTransfer && !Placement ||
         Owner.Authorized && Cancellation && StashTransfer && Placement);

    public static CollectionInputPermissions From(
        FaustusControllerLiteSettings settings,
        bool workflowAuthorized = false,
        bool sweepAuthorized = false) => new(
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowOrderCollection.Value,
        settings.AllowOrderCancellation.Value,
        settings.AllowStashTransfer.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowFullWorkflow.Value,
        workflowAuthorized,
        settings.AllowSellSweep.Value,
        sweepAuthorized);
}

public static class CollectionOrderMatcher
{
    public static bool TryMatch(
        TrackedOrderState tracked,
        IReadOnlyCollection<PlacedOrderSnapshot> orders,
        out PlacedOrderSnapshot? match,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(orders);
        match = null;
        if (tracked.Status is not TrackedOrderStatus.CompletedUncollected and not TrackedOrderStatus.CollectionArmed ||
            tracked.PlayerOrderId is not > 0 || tracked.OfferedHash == 0 || tracked.WantedHash == 0)
        {
            failure = "Tracked state is not an exact completed-uncollected collection candidate.";
            return false;
        }

        var matches = orders.Where(order => ExactEconomicsMatch(tracked, order)).ToArray();
        if (matches.Length != 1)
        {
            failure = $"Expected one live order with exact canonical economics, found {matches.Length}.";
            return false;
        }

        var order = matches[0];
        match = order;
        failure = string.Empty;
        return true;
    }

    private static bool ExactEconomicsMatch(TrackedOrderState tracked, PlacedOrderSnapshot order) =>
        order.IsCompleted && !order.IsCanceled &&
        order.OfferedMetadata == tracked.OfferedMetadata && order.WantedMetadata == tracked.WantedMetadata &&
        order.OfferedHash == tracked.OfferedHash && order.WantedHash == tracked.WantedHash &&
        order.OriginalOfferedAmount == tracked.OfferedAmount && order.RemainingOfferedAmount == 0 &&
        order.ReceivedWantedAmount ==
            (tracked.TerminalReceivedWantedAmount ?? tracked.WantedAmount) -
            tracked.SettledWantedAmount - tracked.PendingWantedBatchAmount &&
        PlacementOrderMatcher.RatiosEquivalent(
            order.OfferedRatioPart, order.WantedRatioPart, tracked.OfferedAmount, tracked.WantedAmount);
}

public sealed class TrackedOrderCollectionController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan DisappearanceTimeout = TimeSpan.FromSeconds(3);
    private readonly HashSet<Keys> _ownedKeys = [];
    private readonly HashSet<int> _baselineOrderIds = [];
    private readonly Dictionary<int, PlacedOrderSnapshot> _baselineOrders = [];
    private TrackedOrderState? _tracked;
    private Func<TrackedOrderState, string, bool>? _persist;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private Vector2 _moveStart;
    private Vector2 _target;
    private Vector2 _lastCommanded;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _deadline;
    private bool _mouseDown;
    private bool _clickAttempted;
    private long _batchAmount;
    private long _remainingBefore;
    private long _aggregateOwnedBefore;
    private InventoryTransferSnapshot? _inventoryBefore;
    private string _unrelatedFingerprint = string.Empty;
    private string _unrelatedIdentityFingerprint = string.Empty;
    private readonly List<int> _siblingOrderIds = [];
    private TrackedCollectionState _releaseTarget;
    private string _releaseStatus = string.Empty;

    public TrackedCollectionState State { get; private set; } = TrackedCollectionState.Idle;
    public string Status { get; private set; } = "Idle; collection is disabled.";
    public string Failure { get; private set; } = string.Empty;
    public bool IsRunning => State is TrackedCollectionState.MovingToSlot or TrackedCollectionState.ReadyToClick or
        TrackedCollectionState.WaitingForDisappearance or TrackedCollectionState.ReleasingInput;

    public bool Start(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        CollectionInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        long batchAmount,
        long aggregateOwnedBefore,
        Func<TrackedOrderState, string, bool> persist,
        IReadOnlyCollection<int> siblingOrderIds,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(persist);
        ArgumentNullException.ThrowIfNull(siblingOrderIds);
        if (IsRunning || !permissions.Ready || conflictingControllerEnabled)
        {
            failure = "Collection is running, permissions are incomplete, placement/full workflow is enabled, or controller exclusion failed.";
            return false;
        }
        var remainingToCollect = tracked.TerminalReceivedWantedAmount is null
            ? tracked.WantedAmount
            : TrackedOrderLifecycle.RemainingWantedToCollect(tracked);
        if (batchAmount <= 0 || batchAmount > remainingToCollect ||
            tracked.PendingWantedBatchAmount != 0 || tracked.PendingReturnBatchAmount != 0)
        {
            failure = $"Collection batch {batchAmount} was not a positive amount within the remaining " +
                $"{remainingToCollect} uncollected proceeds with no batch already pending stash.";
            return false;
        }

        if (!TryResolveTarget(gameController, tracked, calibration, out var target, out var orders, out failure) ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, tracked.WantedMetadata, tracked.WantedMaxStackSize,
                out var inventory, out failure))
        {
            return false;
        }
        if (inventory.TargetInventoryAmount != 0 || aggregateOwnedBefore < 0)
        {
            failure = "Collection batch requires zero target inventory and a nonnegative aggregate ownership baseline.";
            return false;
        }

        CollectionOrderMatcher.TryMatch(tracked, orders, out var liveMatch, out _);
        _tracked = CloneTracked(tracked, tracked.Status,
            liveMatch!.PlayerOrderId == tracked.PlayerOrderId
                ? tracked.Detail
                : $"Live order ID changed from {tracked.PlayerOrderId} to {liveMatch.PlayerOrderId}; exact economics remained unique.");
        _tracked.PlayerOrderId = liveMatch.PlayerOrderId;
        _tracked.BaselineOrderIds = orders.Where(order => order.PlayerOrderId != liveMatch.PlayerOrderId)
            .Select(order => order.PlayerOrderId).Order().ToList();
        _persist = persist;
        _league = gameController.Game.IngameState.ServerData.League;
        _areaInstanceId = gameController.Game.IngameState.ServerData.InstanceId;
        _baselineOrderIds.Clear();
        _baselineOrderIds.UnionWith(orders.Select(order => order.PlayerOrderId));
        _baselineOrders.Clear();
        foreach (var order in orders) _baselineOrders.Add(order.PlayerOrderId, order);
        _batchAmount = batchAmount;
        _remainingBefore = liveMatch.ReceivedWantedAmount;
        _aggregateOwnedBefore = aggregateOwnedBefore;
        _inventoryBefore = inventory;
        _siblingOrderIds.Clear();
        _siblingOrderIds.AddRange(siblingOrderIds.Where(id => id != liveMatch.PlayerOrderId));
        (_unrelatedFingerprint, _unrelatedIdentityFingerprint) = TrackedOrderLifecycle.CaptureUnrelatedOrders(
            orders.Where(order => order.PlayerOrderId != liveMatch.PlayerOrderId), _siblingOrderIds);
        _clickAttempted = false;
        Failure = string.Empty;
        BeginMovement(target, cursorSpeed);
        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerCalibration calibration,
        CollectionInputPermissions permissions,
        bool conflictingControllerEnabled)
    {
        if (!IsRunning) return;
        if (State == TrackedCollectionState.ReleasingInput)
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
                Finish(failure, _clickAttempted);
                return;
            }

            switch (State)
            {
                case TrackedCollectionState.MovingToSlot:
                    TickMovement(gameController, calibration);
                    break;
                case TrackedCollectionState.ReadyToClick:
                    ClickOnce(gameController, calibration);
                    break;
                case TrackedCollectionState.WaitingForDisappearance:
                    VerifyDisappearance(gameController);
                    break;
            }
        }
        catch (Exception exception)
        {
            Finish($"Collection failed closed: {exception.GetType().Name}: {exception.Message}", _clickAttempted);
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning || State == TrackedCollectionState.ReleasingInput) return;
        Finish(reason, _clickAttempted);
    }

    public void EmergencyStop(string reason)
    {
        if (IsRunning) Finish(reason, _clickAttempted);
        TryReleaseOwnedInput();
    }

    public static bool TryResolveTrackedRow(
        GameController gameController,
        TrackedOrderState tracked,
        out Element? row,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out string failure)
    {
        row = null;
        if (!SingleLegPlacementController.TryReadOrders(gameController, out orders, out failure) ||
            !CollectionOrderMatcher.TryMatch(tracked, orders, out var match, out failure) || match is null)
        {
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var elements = panel.OrderElements;
        if (elements.Count != orders.Count)
        {
            failure = "Order model and element counts were not parallel.";
            return false;
        }

        var modelIndex = orders.Select((order, index) => (order, index))
            .Single(pair => pair.order.PlayerOrderId == match.PlayerOrderId).index;
        row = elements[modelIndex];
        if (!row.IsVisible || !RowTextsMatchTrackedOrder(EnumerateVisibleText(row, 0), tracked))
        {
            row = null;
            failure = "Parallel SDK order row lacked exact completed-status and ratio evidence.";
            return false;
        }
        var rect = row.GetClientRectCache;
        if (!row.IsVisible || rect.Width <= 0 || rect.Height <= 0)
        {
            row = null;
            failure = "Tracked completed order row was not visibly targetable.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool RowTextsMatchTrackedOrder(IEnumerable<string> rowTexts, TrackedOrderState tracked)
    {
        var texts = rowTexts.Select(text => text.Trim()).Where(text => text.Length > 0).ToArray();
        if (!OrderRowStatusText.IsTerminal(texts)) return false;

        foreach (var text in texts)
        {
            var match = Regex.Match(text, @"(?<left>[\d,]+(?:\.\d+)?)\s*:\s*(?<right>[\d,]+(?:\.\d+)?)");
            if (!match.Success) continue;
            if (DisplayedRatioMatches(
                    match.Groups["left"].Value,
                    match.Groups["right"].Value,
                    tracked.OfferedAmount,
                    tracked.WantedAmount) ||
                DisplayedRatioMatches(
                    match.Groups["left"].Value,
                    match.Groups["right"].Value,
                    tracked.WantedAmount,
                    tracked.OfferedAmount))
            {
                return true;
            }
        }
        return false;
    }

    public static bool DisplayedRatioMatches(
        string leftText,
        string rightText,
        long expectedLeft,
        long expectedRight)
    {
        if (expectedLeft <= 0 || expectedRight <= 0 ||
            !TryParseDisplayedDecimal(leftText, out var left, out var leftScale) ||
            !TryParseDisplayedDecimal(rightText, out var right, out var rightScale))
            return false;

        var displayedNumerator = left.Numerator * right.Denominator;
        var displayedDenominator = left.Denominator * right.Numerator;
        if (displayedNumerator * expectedRight == (BigInteger)expectedLeft * displayedDenominator)
            return true;

        if (right.Numerator == right.Denominator && leftScale > 0)
            return RoundsToDisplayed(left, leftScale, expectedLeft, expectedRight);
        if (left.Numerator == left.Denominator && rightScale > 0)
            return RoundsToDisplayed(right, rightScale, expectedRight, expectedLeft);
        return false;
    }

    private static bool RoundsToDisplayed(
        (BigInteger Numerator, BigInteger Denominator) displayed,
        int scale,
        long expectedNumerator,
        long expectedDenominator)
    {
        var factor = BigInteger.Pow(10, scale);
        var unscaled = displayed.Numerator * factor / displayed.Denominator;
        var difference = BigInteger.Abs((BigInteger)expectedNumerator * factor - unscaled * expectedDenominator);
        return difference * 2 <= expectedDenominator;
    }

    private static bool TryParseDisplayedDecimal(
        string text,
        out (BigInteger Numerator, BigInteger Denominator) value,
        out int scale)
    {
        value = default;
        scale = 0;
        var normalized = text.Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        var parts = normalized.Split('.');
        if (parts.Length is < 1 or > 2 || parts.Any(part => part.Length == 0 || !part.All(char.IsDigit)))
            return false;
        scale = parts.Length == 2 ? parts[1].Length : 0;
        if (scale > 9 || !BigInteger.TryParse(string.Concat(parts), out var numerator) || numerator <= 0)
            return false;
        value = (numerator, BigInteger.Pow(10, scale));
        return true;
    }

    private static IEnumerable<string> EnumerateVisibleText(Element element, int depth)
    {
        if (depth > 4 || (depth > 0 && !element.IsVisible)) yield break;
        if (!string.IsNullOrWhiteSpace(element.TextNoTags)) yield return element.TextNoTags;
        foreach (var child in element.Children)
        {
            foreach (var text in EnumerateVisibleText(child, depth + 1)) yield return text;
        }
    }

    private static bool TryResolveTarget(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        out Vector2 target,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out string failure)
    {
        target = default;
        orders = Array.Empty<PlacedOrderSnapshot>();
        if (!ValidateWindows(gameController, out failure))
        {
            return false;
        }
        if (!TryResolveTrackedRow(gameController, tracked, out var row, out orders, out failure) || row is null)
        {
            return false;
        }

        return CanceledReturnCollectionController.TryResolveVisibleTerminalSlot(
            row, calibration, wantedSlot: true, iconPresent: true, out target, out failure);
    }

    private bool ValidateGlobal(
        GameController gameController,
        CollectionInputPermissions permissions,
        bool conflictingControllerEnabled,
        out string failure)
    {
        var server = gameController.Game.IngameState.ServerData;
        failure = string.Empty;
        if (!permissions.Ready || conflictingControllerEnabled || ModifiersHeld() ||
            server.League != _league || server.InstanceId != _areaInstanceId)
        {
            failure = "Collection permission, modifier, league, or area gate failed.";
            return false;
        }
        if (!ValidateWindows(gameController, out failure))
        {
            return false;
        }

        if (State != TrackedCollectionState.WaitingForDisappearance &&
            Vector2.Distance(ExileInput.MousePositionNum, _lastCommanded) > CursorTolerance)
        {
            failure = "Manual cursor movement interrupted collection.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private static bool ValidateWindows(GameController gameController, out string failure)
    {
        var ui = gameController.Game.IngameState.IngameUi;
        if (!gameController.Window.IsForeground() || !ui.CurrencyExchangePanel.IsVisible ||
            ui.CurrencyExchangePanel.CurrencyPicker.IsVisible || ui.PopUpWindow.IsVisible ||
            !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible)
        {
            failure = "Collection requires foreground exchange, stash, and inventory with picker closed.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void BeginMovement(Vector2 target, int cursorSpeed)
    {
        _target = target;
        _moveStart = ExileInput.MousePositionNum;
        _lastCommanded = _moveStart;
        _moveStartedAt = DateTimeOffset.UtcNow;
        _moveDuration = TimeSpan.FromSeconds(Math.Clamp(
            Vector2.Distance(_moveStart, target) / Math.Max(cursorSpeed, 1), 0.12, 0.65));
        State = TrackedCollectionState.MovingToSlot;
        Status = "Moving to calibrated bought-currency slot; no click yet.";
    }

    private void TickMovement(GameController gameController, PickerCalibration calibration)
    {
        if (!TryResolveTarget(gameController, _tracked!, calibration, out var fresh, out _, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance)
        {
            Finish(string.IsNullOrEmpty(failure) ? "Collection target moved." : failure, clicked: false);
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _target, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommanded = next;
        if (progress >= 1)
        {
            State = TrackedCollectionState.ReadyToClick;
            Status = "At collection slot; performing final exact order/economics validation.";
        }
    }

    private void ClickOnce(GameController gameController, PickerCalibration calibration)
    {
        var tracked = _tracked!;
        if (!TryResolveTarget(gameController, tracked, calibration, out var fresh, out var orders, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance ||
            !SnapshotsEqual(_baselineOrders.Values, orders, _siblingOrderIds) ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, tracked.WantedMetadata, tracked.WantedMaxStackSize,
                out var inventory, out failure) ||
            !_inventoryBefore!.Items.SequenceEqual(inventory.Items) ||
            _inventoryBefore.TargetVisibleStashAmount != inventory.TargetVisibleStashAmount)
        {
            Finish(string.IsNullOrEmpty(failure) ? "Final collection context changed." : failure, clicked: false);
            return;
        }

        var armed = CloneTracked(_tracked!, TrackedOrderStatus.CollectionArmed,
            $"Collection intent persisted before Ctrl-right-click. {_tracked!.Detail}");
        armed.CollectionAssetIntent = new CollectionAssetIntentState
        {
            IntentId = Guid.NewGuid(),
            Metadata = armed.WantedMetadata,
            Amount = _batchAmount,
            WantedSlot = true,
            TerminalStatus = TrackedOrderStatus.CompletedUncollected,
            InventoryAmountBefore = _inventoryBefore!.TargetInventoryAmount,
            VisibleStashAmountBefore = _inventoryBefore.TargetVisibleStashAmount,
            AggregateOwnedBefore = _aggregateOwnedBefore,
            NonTargetInventoryFingerprint = InventoryTransferEvidence.NonTargetFingerprint(
                _inventoryBefore, armed.WantedMetadata),
            UnrelatedOrdersFingerprint = _unrelatedFingerprint,
            UnrelatedIdentityFingerprint = _unrelatedIdentityFingerprint,
            SiblingOrderIds = [.. _siblingOrderIds],
            AreaInstanceId = _areaInstanceId,
            ArmedAtUtc = DateTimeOffset.UtcNow,
        };
        if (!_persist!(armed, "OrderCollectionArmed"))
        {
            Finish("Could not persist collection intent before click.", clicked: false);
            return;
        }
        _tracked = armed;

        if (!TryResolveTarget(gameController, _tracked, calibration, out fresh, out orders, out failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance ||
            !SnapshotsEqual(_baselineOrders.Values, orders, _siblingOrderIds) ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, _tracked.WantedMetadata, _tracked.WantedMaxStackSize,
                out inventory, out failure) ||
            !_inventoryBefore.Items.SequenceEqual(inventory.Items) ||
            _inventoryBefore.TargetVisibleStashAmount != inventory.TargetVisibleStashAmount)
        {
            var reason = string.IsNullOrEmpty(failure)
                ? "Collection context changed after durable arming and before input."
                : failure;
            var disarmed = CloneTracked(_tracked, TrackedOrderStatus.CompletedUncollected,
                $"Disarmed without input after final revalidation failed: {reason}");
            if (_persist(disarmed, "OrderCollectionDisarmedBeforeClick")) _tracked = disarmed;
            Finish(reason, clicked: false);
            return;
        }

        _clickAttempted = true;
        PressKey(Keys.ControlKey);
        _mouseDown = true;
        Exception? clickFailure = null;
        try
        {
            ExileInput.RightDown();
        }
        catch (Exception exception)
        {
            clickFailure = exception;
        }
        finally
        {
            try
            {
                ExileInput.RightUp();
                _mouseDown = false;
            }
            catch (Exception exception)
            {
                clickFailure ??= exception;
            }
            try
            {
                ReleaseKey(Keys.ControlKey);
            }
            catch (Exception exception)
            {
                clickFailure ??= exception;
            }
        }

        if (clickFailure is not null)
        {
            Finish($"Collection click/release was ambiguous: {clickFailure.Message}", clicked: true);
            return;
        }

        _deadline = DateTimeOffset.UtcNow + DisappearanceTimeout;
        State = TrackedCollectionState.WaitingForDisappearance;
        Status = "Ctrl-right-clicked tracked collection slot once; verifying exact order disappearance.";
    }

    private void VerifyDisappearance(GameController gameController)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out var failure))
        {
            Finish(failure, clicked: true);
            return;
        }

        var trackedId = _tracked!.PlayerOrderId!.Value;
        var trackedSnapshot = _baselineOrders[trackedId];
        var expected = _baselineOrders.Values.Where(order => order.PlayerOrderId != trackedId).ToArray();
        var expectedRemainingAfter = _remainingBefore - _batchAmount;
        if (expectedRemainingAfter == 0)
        {
            if (!orders.Any(order => SameOrderIgnoringId(order, trackedSnapshot)) &&
                SnapshotsEqualIgnoringIds(expected, orders, _siblingOrderIds))
            {
                BeginRelease(TrackedCollectionState.CollectedEvidence,
                    "Tracked order disappeared with every unrelated order ID unchanged.");
                return;
            }
        }
        else
        {
            var reduced = orders.Where(order =>
                SameOrderWithReducedProceeds(trackedSnapshot, order, expectedRemainingAfter)).ToArray();
            if (reduced.Length == 1 &&
                SnapshotsEqualIgnoringIds(
                    expected,
                    orders.Where(order => order.PlayerOrderId != reduced[0].PlayerOrderId).ToArray(),
                    _siblingOrderIds))
            {
                BeginRelease(TrackedCollectionState.CollectedEvidence,
                    $"Tracked order proceeds reduced exactly to {expectedRemainingAfter} with unrelated orders unchanged.");
                return;
            }
        }

        if (DateTimeOffset.UtcNow >= _deadline)
        {
            var reason = orders.Any(order => SameOrderIgnoringId(order, trackedSnapshot))
                ? "Tracked order did not change within three seconds after collection click."
                : "Order list did not stabilize to exact expected snapshots within three seconds after the collection click.";
            Finish(reason, clicked: true);
        }
    }

    private static bool SameOrderWithReducedProceeds(
        PlacedOrderSnapshot before,
        PlacedOrderSnapshot after,
        long expectedRemainingAfter) =>
        after.CreationDate == before.CreationDate &&
        after.OfferedMetadata == before.OfferedMetadata && after.OfferedHash == before.OfferedHash &&
        after.WantedMetadata == before.WantedMetadata && after.WantedHash == before.WantedHash &&
        after.OriginalOfferedAmount == before.OriginalOfferedAmount &&
        after.RemainingOfferedAmount == before.RemainingOfferedAmount &&
        after.ReceivedWantedAmount == expectedRemainingAfter &&
        after.OfferedRatioPart == before.OfferedRatioPart && after.WantedRatioPart == before.WantedRatioPart &&
        (after.GoldCost == before.GoldCost || after.GoldCost == 0) &&
        after.IsCompleted == before.IsCompleted && after.IsCanceled == before.IsCanceled;

    public bool VerifyUnrelatedOrders(GameController gameController, out string failure)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var current, out failure)) return false;
        var trackedId = _tracked?.PlayerOrderId;
        var expected = _baselineOrders.Values.Where(order => order.PlayerOrderId != trackedId).ToArray();
        var expectedRemainingAfter = _remainingBefore - _batchAmount;
        IReadOnlyList<PlacedOrderSnapshot> unrelated = current;
        if (expectedRemainingAfter > 0 && trackedId is { } id && _baselineOrders.TryGetValue(id, out var snapshot))
        {
            var reduced = current.Where(order =>
                SameOrderWithReducedProceeds(snapshot, order, expectedRemainingAfter)).ToArray();
            if (reduced.Length != 1)
            {
                failure = $"Expected one exactly reduced tracked row after the batch; found {reduced.Length}.";
                return false;
            }
            unrelated = current.Where(order => order.PlayerOrderId != reduced[0].PlayerOrderId).ToArray();
        }
        if (!SnapshotsEqualIgnoringIds(expected, unrelated, _siblingOrderIds))
        {
            failure = "Unrelated order snapshots changed after the collection click.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    public bool VerifyInventoryPostState(GameController gameController, out string failure)
    {
        if (_tracked is null || _inventoryBefore is null)
        {
            failure = "Collection batch lacked durable in-memory inventory evidence.";
            return false;
        }
        if (!InventoryStashTransferController.TryReadSnapshot(
                gameController, _tracked.WantedMetadata, _tracked.WantedMaxStackSize,
                out var current, out failure)) return false;
        // One message per condition, with the numbers. These three used to share a sentence, and a
        // live failure then could not say whether the batch was short, the stash had moved, or
        // something unrelated had shifted in the inventory - three different faults with three
        // different fixes.
        var expectedInventory = checked(_inventoryBefore.TargetInventoryAmount + _batchAmount);
        if (current.TargetInventoryAmount != expectedInventory)
        {
            failure = $"Collection batch expected {expectedInventory} {_tracked.WantedMetadata} in inventory " +
                $"({_inventoryBefore.TargetInventoryAmount} before + {_batchAmount} collected); " +
                $"found {current.TargetInventoryAmount}.";
            return false;
        }
        if (current.TargetVisibleStashAmount != _inventoryBefore.TargetVisibleStashAmount)
        {
            failure = $"Collection batch changed the visible stash's {_tracked.WantedMetadata} from " +
                $"{_inventoryBefore.TargetVisibleStashAmount} to {current.TargetVisibleStashAmount} " +
                $"(visible tab {_inventoryBefore.VisibleTabType} -> {current.VisibleTabType}).";
            return false;
        }
        if (InventoryTransferEvidence.NonTargetFingerprint(current, _tracked.WantedMetadata) !=
            InventoryTransferEvidence.NonTargetFingerprint(_inventoryBefore, _tracked.WantedMetadata))
        {
            failure = "Collection batch changed non-target inventory custody: " +
                InventoryTransferEvidence.DescribeNonTargetChange(
                    _inventoryBefore, current, _tracked.WantedMetadata) + ".";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    public static bool SnapshotsEqual(
        IEnumerable<PlacedOrderSnapshot> expected,
        IEnumerable<PlacedOrderSnapshot> actual) => SnapshotsEqual(expected, actual, null);

    /// <summary>
    /// Exact snapshot equality, except that an order the sweep owns is compared on its immutable half
    /// only - it is allowed to have taken a fill while this settlement ran. With no siblings this is
    /// the same whole-snapshot comparison it has always been.
    /// </summary>
    public static bool SnapshotsEqual(
        IEnumerable<PlacedOrderSnapshot> expected,
        IEnumerable<PlacedOrderSnapshot> actual,
        IReadOnlyCollection<int>? siblingOrderIds)
    {
        var left = expected.OrderBy(order => order.PlayerOrderId).ToArray();
        var right = actual.OrderBy(order => order.PlayerOrderId).ToArray();
        if (left.Length != right.Length) return false;
        for (var index = 0; index < left.Length; index++)
        {
            var sibling = siblingOrderIds is { Count: > 0 } &&
                siblingOrderIds.Contains(left[index].PlayerOrderId);
            if (sibling
                ? left[index].PlayerOrderId != right[index].PlayerOrderId ||
                  !SameOrderIdentityIgnoringId(left[index], right[index])
                : !left[index].Equals(right[index]))
            {
                return false;
            }
        }
        return true;
    }

    public static bool SnapshotsEqualIgnoringIds(
        IEnumerable<PlacedOrderSnapshot> expected,
        IEnumerable<PlacedOrderSnapshot> actual) => SnapshotsEqualIgnoringIds(expected, actual, null);

    public static bool SnapshotsEqualIgnoringIds(
        IEnumerable<PlacedOrderSnapshot> expected,
        IEnumerable<PlacedOrderSnapshot> actual,
        IReadOnlyCollection<int>? siblingOrderIds)
    {
        var unmatched = actual.ToList();
        var siblings = new List<PlacedOrderSnapshot>();
        foreach (var expectedOrder in expected)
        {
            if (siblingOrderIds is { Count: > 0 } && siblingOrderIds.Contains(expectedOrder.PlayerOrderId))
            {
                siblings.Add(expectedOrder);
                continue;
            }
            var index = unmatched.FindIndex(actualOrder => SameOrderIgnoringId(expectedOrder, actualOrder));
            if (index < 0) return false;
            unmatched.RemoveAt(index);
        }
        // Strict matches are consumed first, so a lenient sibling match can never absorb the row that
        // an unrelated order was supposed to prove.
        foreach (var siblingOrder in siblings)
        {
            var index = unmatched.FindIndex(
                actualOrder => SameOrderIdentityIgnoringId(siblingOrder, actualOrder));
            if (index < 0) return false;
            unmatched.RemoveAt(index);
        }
        return unmatched.Count == 0;
    }

    private static bool SameOrderIdentityIgnoringId(PlacedOrderSnapshot left, PlacedOrderSnapshot right) =>
        left.CreationDate == right.CreationDate &&
        left.OfferedMetadata == right.OfferedMetadata && left.OfferedHash == right.OfferedHash &&
        left.WantedMetadata == right.WantedMetadata && left.WantedHash == right.WantedHash &&
        left.OriginalOfferedAmount == right.OriginalOfferedAmount &&
        left.OfferedRatioPart == right.OfferedRatioPart && left.WantedRatioPart == right.WantedRatioPart;

    private static bool SameOrderIgnoringId(PlacedOrderSnapshot left, PlacedOrderSnapshot right) =>
        left.CreationDate == right.CreationDate &&
        left.OfferedMetadata == right.OfferedMetadata && left.OfferedHash == right.OfferedHash &&
        left.WantedMetadata == right.WantedMetadata && left.WantedHash == right.WantedHash &&
        left.OriginalOfferedAmount == right.OriginalOfferedAmount &&
        left.RemainingOfferedAmount == right.RemainingOfferedAmount &&
        left.ReceivedWantedAmount == right.ReceivedWantedAmount &&
        left.OfferedRatioPart == right.OfferedRatioPart && left.WantedRatioPart == right.WantedRatioPart &&
        left.GoldCost == right.GoldCost && left.IsCompleted == right.IsCompleted && left.IsCanceled == right.IsCanceled;

    private void Finish(string reason, bool clicked)
    {
        Failure = reason;
        if (clicked)
        {
            var ambiguous = CloneTracked(_tracked!, TrackedOrderStatus.Ambiguous, reason);
            var persisted = _persist?.Invoke(ambiguous, "OrderCollectionAmbiguous") == true;
            BeginRelease(TrackedCollectionState.Ambiguous,
                persisted ? $"AMBIGUOUS: {reason}" : $"AMBIGUOUS AND PERSISTENCE FAILED: {reason}");
        }
        else
        {
            BeginRelease(TrackedCollectionState.Cancelled, $"Cancelled before collection click: {reason}");
        }
    }

    public static TrackedOrderState CloneTracked(
        TrackedOrderState source,
        TrackedOrderStatus status,
        string detail) => new()
    {
        SchemaVersion = source.SchemaVersion,
        League = source.League,
        Status = status,
        PlayerOrderId = source.PlayerOrderId,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        ClickedAtUtc = source.ClickedAtUtc,
        OfferedMetadata = source.OfferedMetadata,
        WantedMetadata = source.WantedMetadata,
        OfferedAmount = source.OfferedAmount,
        WantedAmount = source.WantedAmount,
        GoldCost = source.GoldCost,
        AttemptId = source.AttemptId,
        ProbeSessionId = source.ProbeSessionId,
        CandidateSignature = source.CandidateSignature,
        OfferedHash = source.OfferedHash,
        WantedHash = source.WantedHash,
        BaselineOrderIds = source.BaselineOrderIds.ToList(),
        Detail = detail,
        StashTransferIntent = source.StashTransferIntent is null ? null : new StashTransferIntentState
        {
            StashCustodyMode = source.StashTransferIntent.StashCustodyMode,
            Metadata = source.StashTransferIntent.Metadata,
            Amount = source.StashTransferIntent.Amount,
            InventoryAmountBefore = source.StashTransferIntent.InventoryAmountBefore,
            VisibleStashAmountBefore = source.StashTransferIntent.VisibleStashAmountBefore,
            AggregateOwnedBefore = source.StashTransferIntent.AggregateOwnedBefore,
            NonTargetInventoryFingerprint = source.StashTransferIntent.NonTargetInventoryFingerprint,
            AreaInstanceId = source.StashTransferIntent.AreaInstanceId,
            ArmedAtUtc = source.StashTransferIntent.ArmedAtUtc
        },
        OrderCreationDateUtc = source.OrderCreationDateUtc,
        PlacedOfferedRatioPart = source.PlacedOfferedRatioPart,
        PlacedWantedRatioPart = source.PlacedWantedRatioPart,
        WaitStartedAtUtc = source.WaitStartedAtUtc,
        WaitUntilUtc = source.WaitUntilUtc,
        TimeoutObservedAtUtc = source.TimeoutObservedAtUtc,
        LastObservedAtUtc = source.LastObservedAtUtc,
        LastRemainingOfferedAmount = source.LastRemainingOfferedAmount,
        LastReceivedWantedAmount = source.LastReceivedWantedAmount,
        TerminalObservedAtUtc = source.TerminalObservedAtUtc,
        TerminalRemainingOfferedAmount = source.TerminalRemainingOfferedAmount,
        TerminalReceivedWantedAmount = source.TerminalReceivedWantedAmount,
        LedgerCommittedAtUtc = source.LedgerCommittedAtUtc,
        CancelIntent = source.CancelIntent is null ? null : new CancelIntentState
        {
            IntentId = source.CancelIntent.IntentId,
            ArmedAtUtc = source.CancelIntent.ArmedAtUtc,
            AreaInstanceId = source.CancelIntent.AreaInstanceId,
            PlayerOrderIdAtArm = source.CancelIntent.PlayerOrderIdAtArm,
            RemainingOfferedAtArm = source.CancelIntent.RemainingOfferedAtArm,
            ReceivedWantedAtArm = source.CancelIntent.ReceivedWantedAtArm,
            UnrelatedOrdersFingerprint = source.CancelIntent.UnrelatedOrdersFingerprint,
            UnrelatedIdentityFingerprint = source.CancelIntent.UnrelatedIdentityFingerprint,
            SiblingOrderIds = [.. source.CancelIntent.SiblingOrderIds],
            ConfirmationOpenedAtUtc = source.CancelIntent.ConfirmationOpenedAtUtc,
            ConfirmClickAttemptedAtUtc = source.CancelIntent.ConfirmClickAttemptedAtUtc
        },
        CollectionAssetIntent = source.CollectionAssetIntent is null ? null : new CollectionAssetIntentState
        {
            IntentId = source.CollectionAssetIntent.IntentId,
            Metadata = source.CollectionAssetIntent.Metadata,
            Amount = source.CollectionAssetIntent.Amount,
            WantedSlot = source.CollectionAssetIntent.WantedSlot,
            TerminalStatus = source.CollectionAssetIntent.TerminalStatus,
            InventoryAmountBefore = source.CollectionAssetIntent.InventoryAmountBefore,
            VisibleStashAmountBefore = source.CollectionAssetIntent.VisibleStashAmountBefore,
            AggregateOwnedBefore = source.CollectionAssetIntent.AggregateOwnedBefore,
            NonTargetInventoryFingerprint = source.CollectionAssetIntent.NonTargetInventoryFingerprint,
            UnrelatedOrdersFingerprint = source.CollectionAssetIntent.UnrelatedOrdersFingerprint,
            UnrelatedIdentityFingerprint = source.CollectionAssetIntent.UnrelatedIdentityFingerprint,
            SiblingOrderIds = [.. source.CollectionAssetIntent.SiblingOrderIds],
            AreaInstanceId = source.CollectionAssetIntent.AreaInstanceId,
            ArmedAtUtc = source.CollectionAssetIntent.ArmedAtUtc
        },
        WantedAssetCollected = source.WantedAssetCollected,
        OfferedReturnCollected = source.OfferedReturnCollected,
        WantedAssetStashed = source.WantedAssetStashed,
        OfferedReturnStashed = source.OfferedReturnStashed,
        SettledWantedAmount = source.SettledWantedAmount,
        PendingWantedBatchAmount = source.PendingWantedBatchAmount,
        SettledReturnAmount = source.SettledReturnAmount,
        PendingReturnBatchAmount = source.PendingReturnBatchAmount,
        BulkCollectionOwnedBaseline = source.BulkCollectionOwnedBaseline,
        OfferedMaxStackSize = source.OfferedMaxStackSize,
        WantedMaxStackSize = source.WantedMaxStackSize
    };

    private void PressKey(Keys key)
    {
        _ownedKeys.Add(key);
        ExileInput.KeyDown(key);
    }

    private void ReleaseKey(Keys key)
    {
        if (!_ownedKeys.Remove(key)) return;
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

    private void BeginRelease(TrackedCollectionState target, string status)
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
            State = TrackedCollectionState.ReleasingInput;
            Status = "Collection input release pending; retrying every tick.";
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
                ExileInput.RightUp();
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
