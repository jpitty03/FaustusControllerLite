using ExileCore;
using ExileCore.PoEMemory;
using FaustusControllerLite.Input;
using System.Numerics;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum CanceledReturnCollectionState
{
    Idle,
    MovingToReturn,
    ReadyToClick,
    WaitingForEvidence,
    ReleasingInput,
    CollectedEvidence,
    Ambiguous,
    Cancelled,
}

public sealed class CanceledReturnCollectionController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan EvidenceTimeout = TimeSpan.FromSeconds(3);
    private readonly HashSet<Keys> _ownedKeys = [];
    private TrackedOrderState? _tracked;
    private Func<TrackedOrderState, string, bool>? _persist;
    private InventoryTransferSnapshot? _inventoryBefore;
    private string _unrelatedFingerprint = string.Empty;
    private long _aggregateOwnedBefore;
    private SettlementAsset? _asset;
    private bool _rowShouldDisappear;
    private long _sideRemainingBefore;
    private int _staticMaxStackSize;
    private TrackedOrderStatus _terminalStatus;
    private PickerCalibration? _calibration;
    private Vector2 _target;
    private Vector2 _moveStart;
    private Vector2 _lastCommanded;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _deadline;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private bool _mouseDown;
    private bool _clickAttempted;
    private CanceledReturnCollectionState _releaseTarget;
    private string _releaseStatus = string.Empty;

    public CanceledReturnCollectionState State { get; private set; } = CanceledReturnCollectionState.Idle;
    public string Status { get; private set; } = "Idle; canceled return collection is disabled.";
    public string Failure { get; private set; } = string.Empty;
    public bool IsRunning => State is CanceledReturnCollectionState.MovingToReturn or
        CanceledReturnCollectionState.ReadyToClick or CanceledReturnCollectionState.WaitingForEvidence or
        CanceledReturnCollectionState.ReleasingInput;

    public bool Start(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        CollectionInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        long aggregateOwnedBefore,
        Func<TrackedOrderState, string, bool> persist,
        out string failure)
    {
        var remainingWanted = TrackedOrderLifecycle.RemainingWantedToCollect(tracked);
        var remainingReturn = TrackedOrderLifecycle.RemainingReturnToCollect(tracked);
        if (IsRunning || tracked.Status is not TrackedOrderStatus.CanceledUncollected and
                not TrackedOrderStatus.CompletedUncollected ||
            remainingWanted <= 0 && remainingReturn <= 0 ||
            tracked.PendingWantedBatchAmount != 0 || tracked.PendingReturnBatchAmount != 0 ||
            !permissions.Ready || conflictingControllerEnabled || aggregateOwnedBefore < 0)
        {
            failure = "Terminal asset collection requires exact terminal state, a pending settlement amount, no batch awaiting stash, and isolated collection permissions.";
            return false;
        }
        var wantedSide = remainingWanted > 0;
        var sideRemaining = wantedSide ? remainingWanted : remainingReturn;
        var sideMetadata = wantedSide ? tracked.WantedMetadata : tracked.OfferedMetadata;
        var persistedMaxStackSize = wantedSide ? tracked.WantedMaxStackSize : tracked.OfferedMaxStackSize;
        if (!TryResolveTarget(gameController, tracked,
                new SettlementAsset(sideMetadata, sideRemaining, wantedSide), calibration,
                out var target, out var orders, out failure) ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, sideMetadata, persistedMaxStackSize, out var inventory, out failure))
        {
            return false;
        }
        if (inventory.TargetInventoryAmount != 0)
        {
            failure = "Terminal asset collection requires zero pre-existing target currency in inventory for exact custody.";
            return false;
        }
        var capacitySnapshot = inventory with
        {
            TargetMaxStackSize = inventory.TargetMaxStackSize > 0
                ? inventory.TargetMaxStackSize
                : persistedMaxStackSize
        };
        if (!InventoryTransferEvidence.TryGetConservativeCollectionCapacity(
                capacitySnapshot, out var capacity, out failure))
        {
            return false;
        }
        if (inventory.TargetMaxStackSize <= 0 && persistedMaxStackSize <= 0 && sideRemaining > capacity)
        {
            failure = $"First acquisition exceeds the {capacity}-unit capacity provable without an existing " +
                "trusted maximum-stack evidence for this asset.";
            return false;
        }
        var batch = Math.Min(capacity, sideRemaining);
        if (batch <= 0)
        {
            failure = "No verified free inventory capacity was available for a terminal collection batch.";
            return false;
        }
        var asset = new SettlementAsset(sideMetadata, batch, wantedSide);

        _tracked = tracked;
        _persist = persist;
        _inventoryBefore = inventory;
        _aggregateOwnedBefore = aggregateOwnedBefore;
        _asset = asset;
        _sideRemainingBefore = sideRemaining;
        _staticMaxStackSize = persistedMaxStackSize;
        _rowShouldDisappear = remainingWanted + remainingReturn - batch == 0;
        _terminalStatus = tracked.Status;
        _calibration = calibration;
        _unrelatedFingerprint = TrackedOrderLifecycle.OrderSetFingerprint(
            orders.Where(order => !TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)));
        _league = gameController.Game.IngameState.ServerData.League;
        _areaInstanceId = gameController.Game.IngameState.ServerData.InstanceId;
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
        bool conflictingControllerEnabled,
        int cursorSpeed)
    {
        if (!IsRunning) return;
        if (State == CanceledReturnCollectionState.ReleasingInput)
        {
            if (TryReleaseInput()) { State = _releaseTarget; Status = _releaseStatus; }
            return;
        }
        try
        {
            if (!permissions.Ready || conflictingControllerEnabled || !gameController.Window.IsForeground() ||
                ModifiersHeld() || gameController.Game.IngameState.ServerData.League != _league ||
                gameController.Game.IngameState.ServerData.InstanceId != _areaInstanceId)
            {
                Finish("Return collection permission, foreground, modifier, league, area, or exclusion gate failed.");
                return;
            }
            if (State != CanceledReturnCollectionState.WaitingForEvidence &&
                Vector2.Distance(ExileInput.MousePositionNum, _lastCommanded) > CursorTolerance)
            {
                Finish("Manual cursor movement interrupted return collection.");
                return;
            }
            if (State == CanceledReturnCollectionState.MovingToReturn) TickMovement(gameController, calibration, cursorSpeed);
            else if (State == CanceledReturnCollectionState.ReadyToClick) ClickOnce(gameController, calibration);
            else if (State == CanceledReturnCollectionState.WaitingForEvidence) VerifyEvidence(gameController);
        }
        catch (Exception exception)
        {
            Finish($"Canceled return collection failed closed: {exception.Message}");
        }
    }

    public void Cancel(string reason) { if (IsRunning && State != CanceledReturnCollectionState.ReleasingInput) Finish(reason); }
    public void EmergencyStop(string reason) { if (IsRunning) Finish(reason); TryReleaseInput(); }

    public static bool TryResolveTerminalAssetRow(
        GameController gameController,
        TrackedOrderState tracked,
        out Element? row,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out string failure)
    {
        row = null;
        if (!SingleLegPlacementController.TryReadOrders(gameController, out orders, out failure)) return false;
        var expectedLiveWanted = TrackedOrderLifecycle.RemainingWantedToCollect(tracked);
        var expectedLiveReturn = TrackedOrderLifecycle.RemainingReturnToCollect(tracked);
        var matches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order) &&
            order.IsCompleted &&
            order.RemainingOfferedAmount == expectedLiveReturn &&
            order.ReceivedWantedAmount == expectedLiveWanted).ToArray();
        if (matches.Length != 1)
        {
            failure = $"Expected one exact terminal settlement row; found {matches.Length}.";
            return false;
        }
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.OrderElements.Count != orders.Count)
        {
            failure = "Canceled return model and row counts were not parallel.";
            return false;
        }
        var index = orders.Select((order, orderIndex) => (order, orderIndex))
            .Single(pair => pair.order.PlayerOrderId == matches[0].PlayerOrderId).orderIndex;
        row = panel.OrderElements[index];
        var expectedStatus = matches[0].IsCanceled ? "Order Cancelled" : "Order Completed";
        if (!row.IsVisible || !EnumerateText(row, 0).Any(text =>
                text.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase)))
        {
            row = null;
            failure = "Parallel terminal settlement row lacked exact visible status evidence.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TryResolveTarget(
        GameController gameController,
        TrackedOrderState tracked,
        SettlementAsset asset,
        PickerCalibration calibration,
        out Vector2 target,
        out IReadOnlyList<PlacedOrderSnapshot> orders,
        out string failure)
    {
        target = default;
        orders = Array.Empty<PlacedOrderSnapshot>();
        failure = string.Empty;
        var ui = gameController.Game.IngameState.IngameUi;
        if (!ui.CurrencyExchangePanel.IsVisible || ui.CurrencyExchangePanel.CurrencyPicker.IsVisible ||
            ui.PopUpWindow.IsVisible || !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible ||
            !TryResolveTerminalAssetRow(gameController, tracked, out var row, out orders, out failure) || row is null)
        {
            failure = string.IsNullOrEmpty(failure)
                ? "Terminal asset collection requires exchange, stash, inventory, closed picker, no popup, and exact row."
                : failure;
            return false;
        }
        var rect = row.GetClientRectCache;
        var resolved = asset.WantedSlot
            ? calibration.TryResolveCollectionSlot(rect.X, rect.Y, rect.Width, rect.Height, out target, out failure)
            : calibration.TryResolveReturnSlot(rect.X, rect.Y, rect.Width, rect.Height, out target, out failure);
        if (!resolved) return false;
        var point = target;
        var slots = EnumerateElements(row, 0).Where(element =>
        {
            var candidate = element.GetClientRectCache;
            return element.IsVisible && candidate.Width is >= 68 and <= 76 && candidate.Height is >= 68 and <= 76 &&
                candidate.Contains(point.X, point.Y) && element.Children.Any(child =>
                {
                    var childRect = child.GetClientRectCache;
                    return child.IsVisible && childRect.Width >= 80 && childRect.Height >= 80;
                });
        }).ToArray();
        if (slots.Length != 1)
        {
            failure = $"Terminal asset calibration did not resolve to one slot with a visible collectible icon; found {slots.Length}.";
            return false;
        }
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
        State = CanceledReturnCollectionState.MovingToReturn;
        Status = $"Moving to calibrated {(_asset!.WantedSlot ? "wanted proceeds" : "offered return")} slot; no click yet.";
    }

    private void TickMovement(GameController gameController, PickerCalibration calibration, int cursorSpeed)
    {
        if (!TryResolveTarget(gameController, _tracked!, _asset!, calibration, out var fresh, out _, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance)
        {
            Finish(failure);
            return;
        }
        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _target, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommanded = next;
        if (progress >= 1)
        {
            State = CanceledReturnCollectionState.ReadyToClick;
            Status = "At terminal asset slot; persisting exact asset intent before click.";
        }
    }

    private void ClickOnce(GameController gameController, PickerCalibration calibration)
    {
        var tracked = _tracked!;
        var asset = _asset!;
        if (!TryResolveTarget(gameController, tracked, asset, calibration, out var fresh, out var orders, out var failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, asset.Metadata, _staticMaxStackSize, out var inventory, out failure) ||
            !_inventoryBefore!.Items.SequenceEqual(inventory.Items) ||
            _inventoryBefore.TargetVisibleStashAmount != inventory.TargetVisibleStashAmount)
        {
            Finish(failure);
            return;
        }
        var amount = asset.Amount;
        var armed = TrackedOrderCollectionController.CloneTracked(
            tracked, TrackedOrderStatus.CollectionArmed, "Persisted exact terminal settlement-asset collection intent.");
        armed.CollectionAssetIntent = new CollectionAssetIntentState
        {
            IntentId = Guid.NewGuid(),
            Metadata = asset.Metadata,
            Amount = amount,
            WantedSlot = asset.WantedSlot,
            TerminalStatus = _terminalStatus,
            InventoryAmountBefore = inventory.TargetInventoryAmount,
            VisibleStashAmountBefore = inventory.TargetVisibleStashAmount,
            AggregateOwnedBefore = _aggregateOwnedBefore,
            NonTargetInventoryFingerprint = InventoryTransferEvidence.NonTargetFingerprint(inventory, asset.Metadata),
            UnrelatedOrdersFingerprint = _unrelatedFingerprint,
            AreaInstanceId = _areaInstanceId,
            ArmedAtUtc = DateTimeOffset.UtcNow
        };
        if (!_persist!(armed, "TerminalAssetCollectionArmed"))
        {
            Finish("Could not persist terminal asset intent before click.");
            return;
        }
        _tracked = armed;
        if (!TryResolveTarget(gameController, armed, asset, calibration, out fresh, out orders, out failure) ||
            Vector2.Distance(fresh, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, fresh) > CursorTolerance ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, asset.Metadata, _staticMaxStackSize, out inventory, out failure) ||
            !_inventoryBefore.Items.SequenceEqual(inventory.Items) ||
            _inventoryBefore.TargetVisibleStashAmount != inventory.TargetVisibleStashAmount)
        {
            var reason = string.IsNullOrEmpty(failure) ? "Return collection context changed after durable arming." : failure;
            var disarmed = TrackedOrderCollectionController.CloneTracked(
                _tracked, _terminalStatus, $"Disarmed without terminal asset input: {reason}");
            disarmed.CollectionAssetIntent = null;
            if (_persist(disarmed, "TerminalAssetCollectionDisarmedBeforeClick")) _tracked = disarmed;
            Finish(reason);
            return;
        }
        _ownedKeys.Add(Keys.ControlKey);
        ExileInput.KeyDown(Keys.ControlKey);
        _mouseDown = true;
        _clickAttempted = true;
        try { ExileInput.RightDown(); ExileInput.RightUp(); _mouseDown = false; ExileInput.KeyUp(Keys.ControlKey); _ownedKeys.Remove(Keys.ControlKey); }
        catch (Exception exception) { Finish($"Canceled return click/release was ambiguous: {exception.Message}"); return; }
        _deadline = DateTimeOffset.UtcNow + EvidenceTimeout;
        State = CanceledReturnCollectionState.WaitingForEvidence;
        Status = "Ctrl-right-clicked terminal asset once; waiting for exact row/inventory evidence.";
    }

    private void VerifyEvidence(GameController gameController)
    {
        if (VerifyPostState(gameController, out _))
        {
            BeginRelease(CanceledReturnCollectionState.CollectedEvidence,
                "Exact canceled row disappeared and returned currency increased inventory by the terminal amount.");
            return;
        }
        if (DateTimeOffset.UtcNow >= _deadline) Finish("Canceled return evidence did not stabilize within three seconds.");
    }

    public bool VerifyPostState(GameController gameController, out string failure)
    {
        failure = string.Empty;
        if (gameController.Game.IngameState.ServerData.InstanceId != _areaInstanceId ||
            !SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure))
        {
            if (string.IsNullOrEmpty(failure)) failure = "Area or order snapshot changed during terminal asset collection.";
            return false;
        }
        var trackedMatches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(_tracked!, order)).ToArray();
        var expectedWantedAfter = TrackedOrderLifecycle.RemainingWantedToCollect(_tracked!) -
            (_asset!.WantedSlot ? _asset.Amount : 0);
        var expectedReturnAfter = TrackedOrderLifecycle.RemainingReturnToCollect(_tracked!) -
            (_asset.WantedSlot ? 0 : _asset.Amount);
        var batchClearsSlot = _asset!.Amount == _sideRemainingBefore;
        var rowEvidence = _rowShouldDisappear
            ? trackedMatches.Length == 0
            : trackedMatches.Length == 1 &&
              trackedMatches[0].ReceivedWantedAmount == expectedWantedAfter &&
              trackedMatches[0].RemainingOfferedAmount == expectedReturnAfter &&
              (batchClearsSlot
                ? ClickedSlotIconCleared(gameController, _tracked!, _asset, _calibration!, out failure)
                : TerminalSlotHasIcon(gameController, _tracked!, _asset, _calibration!, out failure));
        var unrelated = orders.Where(order => !TrackedOrderLifecycle.TerminalIdentityMatches(_tracked!, order));
        if (rowEvidence && TrackedOrderLifecycle.OrderSetFingerprint(unrelated) == _unrelatedFingerprint &&
            InventoryStashTransferController.TryReadSnapshot(
                gameController, _asset!.Metadata, _staticMaxStackSize, out var inventory, out failure) &&
            inventory.TargetInventoryAmount == checked(_inventoryBefore!.TargetInventoryAmount + _asset.Amount) &&
            inventory.TargetVisibleStashAmount == _inventoryBefore.TargetVisibleStashAmount &&
            InventoryTransferEvidence.NonTargetFingerprint(inventory, _asset.Metadata) ==
                InventoryTransferEvidence.NonTargetFingerprint(_inventoryBefore, _asset.Metadata))
        {
            failure = string.Empty;
            return true;
        }
        if (string.IsNullOrEmpty(failure)) failure = "Canceled return post-state did not match durable row/inventory/stash evidence.";
        return false;
    }

    public static bool VerifyInterruptedPostState(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        out SettlementAsset? collectedAsset,
        out string failure)
    {
        collectedAsset = null;
        failure = string.Empty;
        if (tracked.CollectionAssetIntent is not { } intent || intent.IntentId == Guid.Empty ||
            intent.TerminalStatus is not TrackedOrderStatus.CompletedUncollected and
                not TrackedOrderStatus.CanceledUncollected || intent.ArmedAtUtc == default ||
            tracked.TerminalRemainingOfferedAmount is not { } remaining ||
            tracked.TerminalReceivedWantedAmount is not { } received ||
            gameController.Game.IngameState.ServerData.InstanceId != intent.AreaInstanceId)
        {
            failure = "Interrupted terminal collection lacked exact durable intent or area identity.";
            return false;
        }
        var assets = TrackedOrderLifecycle.CreateSettlementAssets(tracked, remaining, received);
        var sideRemainingBefore = intent.WantedSlot
            ? TrackedOrderLifecycle.RemainingWantedToCollect(tracked)
            : TrackedOrderLifecycle.RemainingReturnToCollect(tracked);
        var sideMetadata = intent.WantedSlot ? tracked.WantedMetadata : tracked.OfferedMetadata;
        collectedAsset = intent.Metadata == sideMetadata && intent.Amount > 0 &&
            intent.Amount <= sideRemainingBefore &&
            assets.Any(asset => asset.WantedSlot == intent.WantedSlot)
                ? new SettlementAsset(intent.Metadata, intent.Amount, intent.WantedSlot)
                : null;
        if (collectedAsset is null || !SingleLegPlacementController.TryReadOrders(
                gameController, out var orders, out failure))
        {
            if (string.IsNullOrEmpty(failure)) failure = "Interrupted collection intent did not match one terminal asset.";
            return false;
        }
        var trackedMatches = orders.Where(order =>
            TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)).ToArray();
        var expectedWantedAfter = TrackedOrderLifecycle.RemainingWantedToCollect(tracked) -
            (intent.WantedSlot ? intent.Amount : 0);
        var expectedReturnAfter = TrackedOrderLifecycle.RemainingReturnToCollect(tracked) -
            (intent.WantedSlot ? 0 : intent.Amount);
        var rowShouldDisappear =
            TrackedOrderLifecycle.RemainingToCollect(tracked) - intent.Amount == 0;
        var rowEvidence = rowShouldDisappear
            ? trackedMatches.Length == 0
            : trackedMatches.Length == 1 &&
              trackedMatches[0].ReceivedWantedAmount == expectedWantedAfter &&
              trackedMatches[0].RemainingOfferedAmount == expectedReturnAfter &&
              (intent.Amount == sideRemainingBefore
                ? ClickedSlotIconCleared(gameController, tracked, collectedAsset, calibration, out failure)
                : TerminalSlotHasIcon(gameController, tracked, collectedAsset, calibration, out failure));
        var unrelated = orders.Where(order =>
            !TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order));
        if (!rowEvidence || TrackedOrderLifecycle.OrderSetFingerprint(unrelated) != intent.UnrelatedOrdersFingerprint ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, intent.Metadata, PersistedMaxStackSize(tracked, intent.WantedSlot),
                out var inventory, out failure) ||
            inventory.TargetInventoryAmount != checked(intent.InventoryAmountBefore + intent.Amount) ||
            inventory.TargetVisibleStashAmount != intent.VisibleStashAmountBefore ||
            InventoryTransferEvidence.NonTargetFingerprint(inventory, intent.Metadata) !=
                intent.NonTargetInventoryFingerprint ||
            checked(intent.InventoryAmountBefore + intent.VisibleStashAmountBefore) > intent.AggregateOwnedBefore ||
            checked(inventory.TargetInventoryAmount + inventory.TargetVisibleStashAmount) !=
                checked(intent.InventoryAmountBefore + intent.VisibleStashAmountBefore + intent.Amount))
        {
            if (string.IsNullOrEmpty(failure))
                failure = "Interrupted terminal collection did not match exact row/inventory/stash/ownership post-state.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    public static bool VerifyInterruptedPreState(
        GameController gameController,
        TrackedOrderState tracked,
        PickerCalibration calibration,
        out string failure)
    {
        failure = string.Empty;
        if (tracked.CollectionAssetIntent is not { } intent || intent.IntentId == Guid.Empty ||
            tracked.TerminalRemainingOfferedAmount is not { } remaining ||
            tracked.TerminalReceivedWantedAmount is not { } received ||
            gameController.Game.IngameState.ServerData.InstanceId != intent.AreaInstanceId)
        {
            failure = "Interrupted terminal collection lacked exact durable pre-click identity.";
            return false;
        }
        var preSideRemaining = intent.WantedSlot
            ? TrackedOrderLifecycle.RemainingWantedToCollect(tracked)
            : TrackedOrderLifecycle.RemainingReturnToCollect(tracked);
        var preSideMetadata = intent.WantedSlot ? tracked.WantedMetadata : tracked.OfferedMetadata;
        var asset = intent.Metadata == preSideMetadata && intent.Amount > 0 &&
            intent.Amount <= preSideRemaining &&
            TrackedOrderLifecycle.CreateSettlementAssets(tracked, remaining, received)
                .Any(candidate => candidate.WantedSlot == intent.WantedSlot)
                ? new SettlementAsset(intent.Metadata, intent.Amount, intent.WantedSlot)
                : null;
        if (asset is null || !SingleLegPlacementController.TryReadOrders(
                gameController, out var orders, out failure))
        {
            if (string.IsNullOrEmpty(failure)) failure = "Interrupted collection intent did not match one pre-click asset.";
            return false;
        }
        var trackedMatches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)).ToArray();
        var unrelated = orders.Where(order => !TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order));
        if (trackedMatches.Length != 1 ||
            trackedMatches[0].ReceivedWantedAmount != TrackedOrderLifecycle.RemainingWantedToCollect(tracked) ||
            trackedMatches[0].RemainingOfferedAmount != TrackedOrderLifecycle.RemainingReturnToCollect(tracked) ||
            TrackedOrderLifecycle.OrderSetFingerprint(unrelated) != intent.UnrelatedOrdersFingerprint ||
            !TerminalSlotHasIcon(gameController, tracked, asset, calibration, out failure) ||
            !InventoryStashTransferController.TryReadSnapshot(
                gameController, intent.Metadata, PersistedMaxStackSize(tracked, intent.WantedSlot),
                out var inventory, out failure) ||
            inventory.TargetInventoryAmount != intent.InventoryAmountBefore ||
            inventory.TargetVisibleStashAmount != intent.VisibleStashAmountBefore ||
            InventoryTransferEvidence.NonTargetFingerprint(inventory, intent.Metadata) !=
                intent.NonTargetInventoryFingerprint ||
            checked(inventory.TargetInventoryAmount + inventory.TargetVisibleStashAmount) > intent.AggregateOwnedBefore)
        {
            if (string.IsNullOrEmpty(failure))
                failure = "Interrupted terminal collection did not match exact durable pre-click evidence.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static int PersistedMaxStackSize(TrackedOrderState tracked, bool wantedSlot) =>
        wantedSlot ? tracked.WantedMaxStackSize : tracked.OfferedMaxStackSize;

    private static bool ClickedSlotIconCleared(
        GameController gameController,
        TrackedOrderState tracked,
        SettlementAsset asset,
        PickerCalibration calibration,
        out string failure)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure))
        {
            return false;
        }
        var matches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)).ToArray();
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (matches.Length != 1 || panel.OrderElements.Count != orders.Count)
        {
            failure = "Terminal row was not uniquely resolvable after first asset collection.";
            return false;
        }
        var index = orders.Select((order, orderIndex) => (order, orderIndex))
            .Single(pair => pair.order.PlayerOrderId == matches[0].PlayerOrderId).orderIndex;
        var row = panel.OrderElements[index];
        var expectedStatus = matches[0].IsCanceled ? "Order Cancelled" : "Order Completed";
        if (!row.IsVisible || !EnumerateText(row, 0).Any(text =>
                text.Equals(expectedStatus, StringComparison.OrdinalIgnoreCase)))
        {
            failure = "Terminal row lost exact visible status evidence after first asset collection.";
            return false;
        }
        var rect = row.GetClientRectCache;
        Vector2 point;
        var resolved = asset.WantedSlot
            ? calibration.TryResolveCollectionSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure)
            : calibration.TryResolveReturnSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure);
        if (!resolved) return false;
        var slots = EnumerateElements(row, 0).Where(element =>
        {
            var candidate = element.GetClientRectCache;
            return element.IsVisible && candidate.Width is >= 68 and <= 76 && candidate.Height is >= 68 and <= 76 &&
                candidate.Contains(point.X, point.Y);
        }).ToArray();
        if (slots.Length != 1 || slots[0].Children.Any(child =>
            {
                var childRect = child.GetClientRectCache;
                return child.IsVisible && childRect.Width >= 80 && childRect.Height >= 80;
            }))
        {
            failure = "Clicked terminal slot did not become uniquely icon-cleared.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TerminalSlotStackEquals(
        GameController gameController,
        TrackedOrderState tracked,
        SettlementAsset asset,
        PickerCalibration calibration,
        long expectedStack,
        out string failure)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure)) return false;
        var matches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)).ToArray();
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (matches.Length != 1 || panel.OrderElements.Count != orders.Count)
        {
            failure = "Terminal row was not uniquely resolvable after the batch collection.";
            return false;
        }
        var index = orders.Select((order, orderIndex) => (order, orderIndex))
            .Single(pair => pair.order.PlayerOrderId == matches[0].PlayerOrderId).orderIndex;
        var row = panel.OrderElements[index];
        var rect = row.GetClientRectCache;
        Vector2 point;
        var resolved = asset.WantedSlot
            ? calibration.TryResolveCollectionSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure)
            : calibration.TryResolveReturnSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure);
        if (!resolved) return false;
        var slots = EnumerateElements(row, 0).Where(element =>
        {
            var candidate = element.GetClientRectCache;
            return element.IsVisible && candidate.Width is >= 68 and <= 76 && candidate.Height is >= 68 and <= 76 &&
                candidate.Contains(point.X, point.Y);
        }).ToArray();
        if (slots.Length != 1 ||
            !slots[0].Children.Any(child =>
            {
                var childRect = child.GetClientRectCache;
                return child.IsVisible && childRect.Width >= 80 && childRect.Height >= 80;
            }) ||
            !EnumerateText(slots[0], 0).Any(text =>
                long.TryParse(text.Replace(",", string.Empty), out var stack) && stack == expectedStack))
        {
            failure = $"Clicked terminal slot did not retain one icon with an exact remaining stack of {expectedStack}.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private static bool TerminalSlotHasIcon(
        GameController gameController,
        TrackedOrderState tracked,
        SettlementAsset asset,
        PickerCalibration calibration,
        out string failure)
    {
        if (!SingleLegPlacementController.TryReadOrders(gameController, out var orders, out failure)) return false;
        var matches = orders.Where(order => TrackedOrderLifecycle.TerminalIdentityMatches(tracked, order)).ToArray();
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (matches.Length != 1 || panel.OrderElements.Count != orders.Count)
        {
            failure = "Terminal pre-click row was not uniquely resolvable.";
            return false;
        }
        var index = orders.Select((order, orderIndex) => (order, orderIndex))
            .Single(pair => pair.order.PlayerOrderId == matches[0].PlayerOrderId).orderIndex;
        var row = panel.OrderElements[index];
        var rect = row.GetClientRectCache;
        Vector2 point;
        var resolved = asset.WantedSlot
            ? calibration.TryResolveCollectionSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure)
            : calibration.TryResolveReturnSlot(rect.X, rect.Y, rect.Width, rect.Height, out point, out failure);
        if (!resolved) return false;
        var slots = EnumerateElements(row, 0).Where(element =>
        {
            var candidate = element.GetClientRectCache;
            return element.IsVisible && candidate.Width is >= 68 and <= 76 && candidate.Height is >= 68 and <= 76 &&
                candidate.Contains(point.X, point.Y);
        }).ToArray();
        if (slots.Length != 1 || !slots[0].Children.Any(child =>
            {
                var childRect = child.GetClientRectCache;
                return child.IsVisible && childRect.Width >= 80 && childRect.Height >= 80;
            }))
        {
            failure = "Terminal pre-click slot did not retain one exact visible asset icon.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private void Finish(string reason)
    {
        Failure = reason;
        if (_clickAttempted)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(_tracked!, TrackedOrderStatus.Ambiguous, reason);
            _persist?.Invoke(ambiguous, "CanceledReturnCollectionAmbiguous");
            BeginRelease(CanceledReturnCollectionState.Ambiguous, $"AMBIGUOUS: {reason}");
        }
        else BeginRelease(CanceledReturnCollectionState.Cancelled, $"Cancelled before return click: {reason}");
    }

    private void BeginRelease(CanceledReturnCollectionState target, string status)
    {
        _releaseTarget = target;
        _releaseStatus = status;
        if (TryReleaseInput()) { State = target; Status = status; }
        else { State = CanceledReturnCollectionState.ReleasingInput; Status = "Return collection input release pending."; }
    }

    private bool TryReleaseInput()
    {
        foreach (var key in _ownedKeys.ToArray()) { try { ExileInput.KeyUp(key); _ownedKeys.Remove(key); } catch { } }
        if (_mouseDown) { try { ExileInput.RightUp(); _mouseDown = false; } catch { } }
        return !_mouseDown && _ownedKeys.Count == 0;
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
