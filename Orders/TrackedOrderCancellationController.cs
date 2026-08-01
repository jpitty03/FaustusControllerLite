using ExileCore;
using ExileCore.PoEMemory;
using FaustusControllerLite.Input;
using System.Numerics;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum TrackedCancellationState
{
    Idle,
    MovingToCancel,
    ReadyToOpenConfirmation,
    WaitingForConfirmation,
    MovingToConfirm,
    ReadyToConfirm,
    WaitingForTerminal,
    ReleasingInput,
    TerminalObserved,
    Ambiguous,
    Cancelled,
}

public sealed record CancellationInputPermissions(
    bool MouseMovement,
    bool Clicking,
    bool Cancellation,
    bool Placement,
    bool Collection,
    bool StashTransfer,
    bool FullWorkflow)
{
    public bool Ready => MouseMovement && Clicking && Cancellation && !Placement && !Collection &&
        !StashTransfer && !FullWorkflow;

    public static CancellationInputPermissions From(FaustusControllerLiteSettings settings) => new(
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowOrderCancellation.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowOrderCollection.Value,
        settings.AllowStashTransfer.Value,
        settings.AllowFullWorkflow.Value);
}

public sealed class TrackedOrderCancellationController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan PopupTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TerminalTimeout = TimeSpan.FromSeconds(3);
    private TrackedOrderState? _tracked;
    private Func<TrackedOrderState, string, bool>? _persist;
    private IReadOnlyList<PlacedOrderSnapshot> _baselineOrders = [];
    private string _unrelatedFingerprint = string.Empty;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private Vector2 _moveStart;
    private Vector2 _target;
    private Vector2 _lastCommanded;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _deadline;
    private bool _mouseDown;
    private bool _cancelButtonClicked;
    private bool _confirmClickAttempted;
    private TrackedCancellationState _releaseTarget;
    private string _releaseStatus = string.Empty;

    public TrackedCancellationState State { get; private set; } = TrackedCancellationState.Idle;
    public string Status { get; private set; } = "Idle; server-order cancellation is disabled.";
    public string Failure { get; private set; } = string.Empty;
    public bool IsRunning => State is TrackedCancellationState.MovingToCancel or
        TrackedCancellationState.ReadyToOpenConfirmation or TrackedCancellationState.WaitingForConfirmation or
        TrackedCancellationState.MovingToConfirm or TrackedCancellationState.ReadyToConfirm or
        TrackedCancellationState.WaitingForTerminal or TrackedCancellationState.ReleasingInput;

    public bool Start(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        CancellationInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        Func<TrackedOrderState, string, bool> persist,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(persist);
        if (IsRunning || tracked.Status != TrackedOrderStatus.TimedOut || !permissions.Ready ||
            conflictingControllerEnabled || gameController.Game.IngameState.IngameUi.PopUpWindow.IsVisible)
        {
            failure = "Cancellation requires exact TimedOut state, complete isolated permissions, no popup, and controller exclusion.";
            return false;
        }
        if (!TryResolveCancelTarget(gameController, tracked, calibration, out var target, out var orders, out var order, out failure))
        {
            return false;
        }

        _tracked = tracked;
        _persist = persist;
        _baselineOrders = orders;
        _unrelatedFingerprint = TrackedOrderLifecycle.OrderSetFingerprint(
            orders.Where(candidate => !TrackedOrderLifecycle.IdentityMatches(tracked, candidate)));
        _league = gameController.Game.IngameState.ServerData.League;
        _areaInstanceId = gameController.Game.IngameState.ServerData.InstanceId;
        _cancelButtonClicked = false;
        _confirmClickAttempted = false;
        Failure = string.Empty;
        BeginMovement(target, cursorSpeed, TrackedCancellationState.MovingToCancel,
            "Moving to calibrated pending-row X; no click yet.");
        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerCalibration calibration,
        CancellationInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed)
    {
        if (!IsRunning) return;
        if (State == TrackedCancellationState.ReleasingInput)
        {
            if (TryReleaseMouse())
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
                Finish(failure);
                return;
            }
            switch (State)
            {
                case TrackedCancellationState.MovingToCancel:
                    TickMovement(gameController, calibration, cursorSpeed, cancelTarget: true);
                    break;
                case TrackedCancellationState.ReadyToOpenConfirmation:
                    OpenConfirmation(gameController, calibration);
                    break;
                case TrackedCancellationState.WaitingForConfirmation:
                    WaitForConfirmation(gameController, cursorSpeed);
                    break;
                case TrackedCancellationState.MovingToConfirm:
                    TickMovement(gameController, calibration, cursorSpeed, cancelTarget: false);
                    break;
                case TrackedCancellationState.ReadyToConfirm:
                    ConfirmCancellation(gameController);
                    break;
                case TrackedCancellationState.WaitingForTerminal:
                    ObserveTerminal(gameController);
                    break;
            }
        }
        catch (Exception exception)
        {
            Finish($"Cancellation failed closed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning || State == TrackedCancellationState.ReleasingInput) return;
        Finish(reason);
    }

    public void EmergencyStop(string reason)
    {
        if (IsRunning) Finish(reason);
        TryReleaseMouse();
    }

    public static bool TryResolvePendingRow(
        GameController gameController,
        TrackedOrderState tracked,
        out Element? row,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out PlacedOrderSnapshot? order,
        out string failure)
    {
        row = null;
        order = null;
        if (!SingleLegPlacementController.TryReadOrders(gameController, out orders, out failure)) return false;
        var matches = orders.Where(candidate => TrackedOrderLifecycle.IdentityMatches(tracked, candidate)).ToArray();
        if (matches.Length != 1 || matches[0].IsCompleted || matches[0].IsCanceled)
        {
            failure = $"Expected one exact pending order, found {matches.Length} identity matches.";
            return false;
        }
        var matchedOrder = matches[0];
        order = matchedOrder;
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.OrderElements.Count != orders.Count)
        {
            failure = "Order model and element counts were not parallel.";
            return false;
        }
        var index = orders.Select((candidate, candidateIndex) => (candidate, candidateIndex))
            .Single(pair => pair.candidate.PlayerOrderId == matchedOrder.PlayerOrderId).candidateIndex;
        row = panel.OrderElements[index];
        var matchingRows = panel.OrderElements.Where(element => element.IsVisible &&
            PendingRowTextsMatch(EnumerateText(element, 0), tracked)).ToArray();
        if (matchingRows.Length != 1 || !row.IsVisible || row.Address != matchingRows[0].Address)
        {
            row = null;
            failure = $"Exact pending model did not align with one unique status/amount/ratio row; found {matchingRows.Length}.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    public static bool PendingRowTextsMatch(IEnumerable<string> texts, TrackedOrderState tracked)
    {
        var values = texts.Select(text => text.Trim()).Where(text => text.Length > 0).ToArray();
        if (!values.Any(text => text.Equals("Order Listed", StringComparison.OrdinalIgnoreCase))) return false;
        var numeric = values.Select(text => text.Replace(",", string.Empty, StringComparison.Ordinal))
            .Where(text => long.TryParse(text, out _)).Select(long.Parse).ToHashSet();
        if (!numeric.Contains(tracked.OfferedAmount) || !numeric.Contains(tracked.WantedAmount)) return false;
        return values.Any(text =>
        {
            var parts = text.Split(':', StringSplitOptions.TrimEntries);
            return parts.Length == 2 && long.TryParse(parts[0].Replace(",", string.Empty), out var left) &&
                long.TryParse(parts[1].Replace(",", string.Empty), out var right) &&
                (PlacementOrderMatcher.RatiosEquivalent(left, right, tracked.OfferedAmount, tracked.WantedAmount) ||
                 PlacementOrderMatcher.RatiosEquivalent(left, right, tracked.WantedAmount, tracked.OfferedAmount));
        });
    }

    private static bool TryResolveCancelTarget(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        out Vector2 target,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out PlacedOrderSnapshot? order,
        out string failure)
    {
        target = default;
        orders = Array.Empty<PlacedOrderSnapshot>();
        order = null;
        if (!ValidateWindows(gameController, popupExpected: false, out failure) ||
            !TryResolvePendingRow(gameController, tracked, out var row, out orders, out order, out failure) || row is null)
        {
            return false;
        }
        var rect = row.GetClientRectCache;
        if (!calibration.TryResolveCancelButton(rect.X, rect.Y, rect.Width, rect.Height, out target, out failure))
        {
            return false;
        }
        var resolvedTarget = target;
        var controls = EnumerateElements(row, 0).Where(element =>
        {
            var candidate = element.GetClientRectCache;
            return element.IsVisible && element.ChildCount == 0 && candidate.Width is >= 16 and <= 20 &&
                candidate.Height is >= 16 and <= 20 && candidate.Contains(resolvedTarget.X, resolvedTarget.Y);
        }).ToArray();
        if (controls.Length != 1)
        {
            failure = $"Calibrated cancel point did not resolve to one visible 18x18 leaf control; found {controls.Length}.";
            return false;
        }
        return true;
    }

    private bool ValidateGlobal(
        GameController gameController,
        CancellationInputPermissions permissions,
        bool conflictingControllerEnabled,
        out string failure)
    {
        var server = gameController.Game.IngameState.ServerData;
        if (!permissions.Ready || conflictingControllerEnabled || ModifiersHeld() ||
            server.League != _league || server.InstanceId != _areaInstanceId || !gameController.Window.IsForeground())
        {
            failure = "Cancellation permission, controller exclusion, modifier, league, area, or foreground gate failed.";
            return false;
        }
        if (State != TrackedCancellationState.WaitingForConfirmation &&
            State != TrackedCancellationState.WaitingForTerminal &&
            Vector2.Distance(ExileInput.MousePositionNum, _lastCommanded) > CursorTolerance)
        {
            failure = "Manual cursor movement interrupted cancellation.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool ValidateWindows(GameController gameController, bool popupExpected, out string failure)
    {
        var ui = gameController.Game.IngameState.IngameUi;
        if (!ui.CurrencyExchangePanel.IsVisible || ui.CurrencyExchangePanel.CurrencyPicker.IsVisible ||
            !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible ||
            ui.PopUpWindow.IsVisible != popupExpected)
        {
            failure = popupExpected
                ? "Expected exact cancellation confirmation with exchange, stash, and inventory visible."
                : "Cancellation requires exchange, stash, inventory, closed picker, and no popup.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private void BeginMovement(Vector2 target, int cursorSpeed, TrackedCancellationState state, string status)
    {
        _target = target;
        _moveStart = ExileInput.MousePositionNum;
        _lastCommanded = _moveStart;
        _moveStartedAt = DateTimeOffset.UtcNow;
        _moveDuration = TimeSpan.FromSeconds(Math.Clamp(
            Vector2.Distance(_moveStart, target) / Math.Max(cursorSpeed, 1), 0.12, 0.65));
        State = state;
        Status = status;
    }

    private void TickMovement(GameController gameController, PickerCalibration calibration, int cursorSpeed, bool cancelTarget)
    {
        Vector2 fresh;
        string failure;
        if (cancelTarget)
        {
            if (!TryResolveCancelTarget(gameController, _tracked!, calibration, out fresh, out var orders, out _, out failure) ||
                !SnapshotsUnchanged(orders))
            {
                Finish(string.IsNullOrEmpty(failure) ? "Pending order snapshots changed during cancel movement." : failure);
                return;
            }
        }
        else if (!TryResolveConfirmButton(gameController, out fresh, out failure) || !PendingSnapshotStillExact(gameController, out failure))
        {
            Finish(failure);
            return;
        }
        if (Vector2.Distance(fresh, _target) > GeometryTolerance)
        {
            Finish("Cancellation target moved during cursor tween.");
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _target, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommanded = next;
        if (progress >= 1)
        {
            State = cancelTarget ? TrackedCancellationState.ReadyToOpenConfirmation : TrackedCancellationState.ReadyToConfirm;
            Status = cancelTarget
                ? "At pending-row X; persisting intent before first click."
                : "At typed confirmation OK; persisting confirm intent before second click.";
        }
    }

    private void OpenConfirmation(GameController gameController, PickerCalibration calibration)
    {
        if (!TryResolveCancelTarget(gameController, _tracked!, calibration, out var fresh, out var orders, out var order, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance || !SnapshotsUnchanged(orders))
        {
            Finish(string.IsNullOrEmpty(failure) ? "Final pending-row cancellation context changed." : failure);
            return;
        }
        var armed = TrackedOrderCollectionController.CloneTracked(
            _tracked!, TrackedOrderStatus.CancelArmed, "Cancellation intent persisted before opening confirmation.");
        armed.CancelIntent = new CancelIntentState
        {
            IntentId = Guid.NewGuid(),
            ArmedAtUtc = DateTimeOffset.UtcNow,
            AreaInstanceId = _areaInstanceId,
            PlayerOrderIdAtArm = order!.PlayerOrderId,
            RemainingOfferedAtArm = order.RemainingOfferedAmount,
            ReceivedWantedAtArm = order.ReceivedWantedAmount,
            UnrelatedOrdersFingerprint = _unrelatedFingerprint
        };
        if (!_persist!(armed, "TrackedOrderCancellationArmed"))
        {
            Finish("Could not persist cancellation intent before first click.");
            return;
        }
        _tracked = armed;
        if (!ClickLeftOnce(out failure))
        {
            _cancelButtonClicked = true;
            Finish(failure);
            return;
        }
        _cancelButtonClicked = true;
        _deadline = DateTimeOffset.UtcNow + PopupTimeout;
        State = TrackedCancellationState.WaitingForConfirmation;
        Status = "Clicked pending-row X once; verifying typed confirmation popup.";
    }

    private void WaitForConfirmation(GameController gameController, int cursorSpeed)
    {
        if (TryResolveConfirmButton(gameController, out var target, out var failure))
        {
            if (!PendingSnapshotStillExact(gameController, out failure))
            {
                Finish(failure);
                return;
            }
            _tracked!.CancelIntent!.ConfirmationOpenedAtUtc = DateTimeOffset.UtcNow;
            BeginMovement(target, cursorSpeed, TrackedCancellationState.MovingToConfirm,
                "Typed cancellation confirmation observed; moving to OK.");
            return;
        }
        if (DateTimeOffset.UtcNow >= _deadline) Finish(failure);
    }

    private void ConfirmCancellation(GameController gameController)
    {
        if (!TryResolveConfirmButton(gameController, out var fresh, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance ||
            !PendingSnapshotStillExact(gameController, out failure))
        {
            Finish(failure);
            return;
        }
        var clicked = TrackedOrderCollectionController.CloneTracked(
            _tracked!, TrackedOrderStatus.CancelClicked, "Persisted intent immediately before typed confirmation OK click.");
        clicked.CancelIntent!.ConfirmClickAttemptedAtUtc = DateTimeOffset.UtcNow;
        if (!_persist!(clicked, "TrackedOrderCancellationConfirmArmed"))
        {
            Finish("Could not persist confirmation intent before second click.");
            return;
        }
        _tracked = clicked;
        _confirmClickAttempted = true;
        if (!ClickLeftOnce(out failure))
        {
            Finish(failure);
            return;
        }
        _deadline = DateTimeOffset.UtcNow + TerminalTimeout;
        State = TrackedCancellationState.WaitingForTerminal;
        Status = "Clicked typed confirmation OK once; waiting for exact canceled/completed terminal state.";
    }

    private void ObserveTerminal(GameController gameController)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out var failure))
        {
            if (DateTimeOffset.UtcNow >= _deadline) Finish(failure);
            return;
        }
        var observation = TrackedOrderLifecycle.Evaluate(_tracked!, orders, DateTimeOffset.UtcNow);
        if (observation.Kind is LifecycleObservationKind.Canceled or LifecycleObservationKind.Completed &&
            observation.Order is { } terminal && UnrelatedOrdersUnchanged(orders, terminal) &&
            TryValidateTerminalRow(gameController, terminal, observation.Kind, out failure))
        {
            var status = observation.Kind == LifecycleObservationKind.Canceled
                ? TrackedOrderStatus.CanceledUncollected
                : TrackedOrderStatus.CompletedUncollected;
            var next = TrackedOrderCollectionController.CloneTracked(_tracked!, status, observation.Detail);
            next.PlayerOrderId = terminal.PlayerOrderId;
            next.LastObservedAtUtc = DateTimeOffset.UtcNow;
            next.LastRemainingOfferedAmount = terminal.RemainingOfferedAmount;
            next.LastReceivedWantedAmount = terminal.ReceivedWantedAmount;
            next.TerminalObservedAtUtc = next.LastObservedAtUtc;
            next.TerminalRemainingOfferedAmount = terminal.RemainingOfferedAmount;
            next.TerminalReceivedWantedAmount = terminal.ReceivedWantedAmount;
            next.CancelIntent = null;
            if (!_persist!(next, $"TrackedOrderCancellation{status}"))
            {
                Finish("Could not persist exact terminal cancellation state.");
                return;
            }
            _tracked = next;
            BeginRelease(TrackedCancellationState.TerminalObserved,
                $"Observed exact {status}: remaining={terminal.RemainingOfferedAmount}, received={terminal.ReceivedWantedAmount}.");
            return;
        }
        if (observation.Kind == LifecycleObservationKind.Ambiguous || DateTimeOffset.UtcNow >= _deadline)
        {
            Finish(observation.Detail);
        }
    }

    private static bool TryResolveConfirmButton(GameController gameController, out Vector2 target, out string failure)
    {
        target = default;
        if (!ValidateWindows(gameController, popupExpected: true, out failure)) return false;
        var ui = gameController.Game.IngameState.IngameUi;
        var popup = ui.PopUpWindow;
        var ok = popup.TwoButtonWindowOk;
        var cancel = popup.TwoButtonWindowCancel;
        var okRect = ok.GetClientRectCache;
        var cancelRect = cancel.GetClientRectCache;
        if (!popup.IsVisible ||
            !ok.IsVisible || !cancel.IsVisible || okRect.Width <= 0 || okRect.Height <= 0 ||
            cancelRect.Width <= 0 || cancelRect.Height <= 0 || okRect.Intersects(cancelRect))
        {
            failure = "Typed two-button destroy confirmation was not exact and visible.";
            return false;
        }
        var center = okRect.Center;
        target = new Vector2(center.X, center.Y);
        failure = string.Empty;
        return true;
    }

    private bool PendingSnapshotStillExact(GameController gameController, out string failure)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure)) return false;
        var matches = orders.Where(order => TrackedOrderLifecycle.IdentityMatches(_tracked!, order)).ToArray();
        if (matches.Length != 1 || matches[0].IsCompleted || matches[0].IsCanceled ||
            matches[0].RemainingOfferedAmount != _tracked!.CancelIntent!.RemainingOfferedAtArm ||
            matches[0].ReceivedWantedAmount != _tracked.CancelIntent.ReceivedWantedAtArm ||
            !UnrelatedOrdersUnchanged(orders, matches[0]))
        {
            failure = "Tracked or unrelated order changed while cancellation confirmation was open.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private bool SnapshotsUnchanged(IReadOnlyList<PlacedOrderSnapshot> orders) =>
        TrackedOrderCollectionController.SnapshotsEqual(_baselineOrders, orders);

    private bool UnrelatedOrdersUnchanged(IReadOnlyList<PlacedOrderSnapshot> orders, PlacedOrderSnapshot tracked) =>
        TrackedOrderLifecycle.OrderSetFingerprint(
            orders.Where(order => !TrackedOrderLifecycle.IdentityMatches(_tracked!, order))) == _unrelatedFingerprint;

    public static bool TryValidateTerminalRow(
        GameController gameController,
        PlacedOrderSnapshot terminal,
        LifecycleObservationKind kind,
        out string failure)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure) ||
            panel.OrderElements.Count != orders.Count)
        {
            return false;
        }
        var index = orders.Select((order, orderIndex) => (order, orderIndex))
            .Single(pair => pair.order.PlayerOrderId == terminal.PlayerOrderId).orderIndex;
        var expectedText = kind == LifecycleObservationKind.Canceled ? "Order Cancelled" : "Order Completed";
        var row = panel.OrderElements[index];
        var matchingRows = panel.OrderElements.Where(element => element.IsVisible &&
            TerminalRowTextsMatch(EnumerateText(element, 0), terminal, expectedText)).ToArray();
        if (matchingRows.Length != 1 || row.Address != matchingRows[0].Address)
        {
            failure = $"Terminal SDK model did not align with one unique visible {expectedText} row.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TerminalRowAmountsMatch(IEnumerable<string> texts, PlacedOrderSnapshot terminal)
    {
        var numeric = texts.Select(text => text.Trim().Replace(",", string.Empty, StringComparison.Ordinal))
            .Where(text => long.TryParse(text, out _)).Select(long.Parse).ToHashSet();
        return numeric.Contains(terminal.OriginalOfferedAmount) &&
            (terminal.ReceivedWantedAmount == 0 || numeric.Contains(terminal.ReceivedWantedAmount)) &&
            numeric.Contains(terminal.RemainingOfferedAmount);
    }

    public static bool TerminalRowTextsMatch(
        IEnumerable<string> texts,
        PlacedOrderSnapshot terminal,
        string expectedStatus)
    {
        var values = texts.ToArray();
        return values.Any(text => text.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase)) &&
            TerminalRowAmountsMatch(values, terminal);
    }

    private bool ClickLeftOnce(out string failure)
    {
        _mouseDown = true;
        try
        {
            ExileInput.LeftDown();
            ExileInput.LeftUp();
            _mouseDown = false;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Cancellation click/release was ambiguous: {exception.Message}";
            return false;
        }
    }

    private void Finish(string reason)
    {
        Failure = reason;
        if (_confirmClickAttempted)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _tracked!, TrackedOrderStatus.Ambiguous, reason);
            _persist?.Invoke(ambiguous, "TrackedOrderCancellationAmbiguousAfterConfirm");
            BeginRelease(TrackedCancellationState.Ambiguous, $"AMBIGUOUS after confirm input: {reason}");
        }
        else if (_cancelButtonClicked)
        {
            BeginRelease(TrackedCancellationState.Cancelled,
                $"Stopped after opening confirmation; durable CancelArmed state retained: {reason}");
        }
        else
        {
            BeginRelease(TrackedCancellationState.Cancelled, $"Cancelled before any cancellation click: {reason}");
        }
    }

    private void BeginRelease(TrackedCancellationState target, string status)
    {
        _releaseTarget = target;
        _releaseStatus = status;
        if (TryReleaseMouse())
        {
            State = target;
            Status = status;
        }
        else
        {
            State = TrackedCancellationState.ReleasingInput;
            Status = "Cancellation mouse release pending; retrying every tick.";
        }
    }

    private bool TryReleaseMouse()
    {
        if (_mouseDown)
        {
            try { ExileInput.LeftUp(); _mouseDown = false; } catch { }
        }
        return !_mouseDown;
    }

    private static IEnumerable<Element> EnumerateElements(Element element, int depth)
    {
        if (depth > 4) yield break;
        yield return element;
        foreach (var child in element.Children)
        foreach (var descendant in EnumerateElements(child, depth + 1)) yield return descendant;
    }

    private static IEnumerable<string> EnumerateText(Element element, int depth)
    {
        if (depth > 4 || (depth > 0 && !element.IsVisible)) yield break;
        if (!string.IsNullOrWhiteSpace(element.TextNoTags)) yield return element.TextNoTags.Trim();
        foreach (var child in element.Children)
        foreach (var text in EnumerateText(child, depth + 1)) yield return text;
    }

    private static bool ModifiersHeld() =>
        ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu);
}
