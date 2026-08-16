using ExileCore;
using FaustusControllerLite.Core;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using System.Numerics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Orders;

public enum InventoryStashTransferState
{
    Idle,
    MovingToStack,
    ReadyToClick,
    ModifierArmed,
    WaitingForTransfer,
    ReleasingInput,
    TransferEvidence,
    Ambiguous,
    Cancelled,
}

public sealed record StashTransferInputPermissions(
    bool MouseMovement,
    bool Clicking,
    bool Collection,
    bool StashTransfer,
    bool Cancellation,
    bool Placement,
    bool FullWorkflow,
    bool WorkflowAuthorized = false,
    bool SellSweep = false,
    bool SweepAuthorized = false)
{
    private CoordinatorOwnership Owner => new(FullWorkflow, WorkflowAuthorized, SellSweep, SweepAuthorized);

    public bool Ready => MouseMovement && Clicking && Collection && StashTransfer &&
        (Owner.None && !Cancellation && !Placement ||
         Owner.Authorized && Cancellation && Placement);

    public static StashTransferInputPermissions From(
        FaustusControllerLiteSettings settings,
        bool workflowAuthorized = false,
        bool sweepAuthorized = false) => new(
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowOrderCollection.Value,
        settings.AllowStashTransfer.Value,
        settings.AllowOrderCancellation.Value,
        settings.AllowOrderPlacement.Value,
        settings.AllowFullWorkflow.Value,
        workflowAuthorized,
        settings.AllowSellSweep.Value,
        sweepAuthorized);
}

public sealed record InventoryItemEvidence(
    long EntityAddress,
    string Metadata,
    int Amount,
    float X,
    float Y,
    float Width,
    float Height,
    bool ClearOfExchange)
{
    public Vector2 Center => new(X + Width / 2f, Y + Height / 2f);
}

public sealed record InventoryTransferSnapshot(
    IReadOnlyList<InventoryItemEvidence> Items,
    long TargetInventoryAmount,
    long TargetVisibleStashAmount,
    float InventorySlotWidth = 0,
    float InventorySlotHeight = 0,
    int TargetMaxStackSize = 0,
    string VisibleTabType = StashCustodyPolicy.CurrencyTabType)
{
    public IReadOnlyList<InventoryItemEvidence> NonTarget(string metadata) =>
        Items.Where(item => item.Metadata != metadata).OrderBy(item => item.EntityAddress).ToArray();
}

public static class InventoryTransferEvidence
{
    public const int InventorySlotCount = 60;

    public enum RecoveryKind
    {
        PreTransfer,
        PostTransfer,
        Ambiguous,
    }

    public static bool TrySelectExactTarget(
        InventoryTransferSnapshot snapshot,
        string metadata,
        long expectedAmount,
        out InventoryItemEvidence? target,
        out string failure)
    {
        target = null;
        if (expectedAmount <= 0 || snapshot.TargetInventoryAmount != expectedAmount)
        {
            failure = $"Inventory held {snapshot.TargetInventoryAmount} target units; expected exactly {expectedAmount}.";
            return false;
        }

        var matches = snapshot.Items.Where(item => item.Metadata == metadata).ToArray();
        if (matches.Length == 0 || matches.Any(item => item.Amount <= 0))
        {
            failure = "No complete positive target-currency stack set was readable.";
            return false;
        }

        target = matches.Where(item => item.ClearOfExchange)
            .OrderBy(item => item.Y).ThenBy(item => item.X).FirstOrDefault();
        if (target is null)
        {
            failure = "Every target-currency inventory stack was covered by the exchange panel.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public static bool TryGetConservativeCollectionCapacity(
        InventoryTransferSnapshot snapshot,
        out long capacity,
        out string failure)
    {
        capacity = 0;
        if (snapshot.InventorySlotWidth <= 0 || snapshot.InventorySlotHeight <= 0 ||
            snapshot.Items.Any(item => item.Width <= 0 || item.Height <= 0))
        {
            failure = "Inventory slot geometry was unreadable for collection-capacity proof.";
            return false;
        }
        var occupied = snapshot.Items.Sum(item =>
            checked((int)Math.Ceiling(item.Width / snapshot.InventorySlotWidth - 0.01f) *
                    (int)Math.Ceiling(item.Height / snapshot.InventorySlotHeight - 0.01f)));
        if (occupied is < 0 or > InventorySlotCount)
        {
            failure = $"Inventory geometry implied {occupied} occupied slots; expected 0-{InventorySlotCount}.";
            return false;
        }
        var authenticatedStackSize = snapshot.TargetMaxStackSize > 0 ? snapshot.TargetMaxStackSize : 1;
        capacity = checked((long)(InventorySlotCount - occupied) * authenticatedStackSize);
        failure = string.Empty;
        return true;
    }

    public static bool TransferCompletedExactly(
        InventoryTransferSnapshot before,
        InventoryTransferSnapshot after,
        string metadata,
        long amount,
        StashCustodyMode custodyMode)
    {
        if (before.TargetInventoryAmount != amount || after.TargetInventoryAmount != 0 ||
            !before.NonTarget(metadata).SequenceEqual(after.NonTarget(metadata)))
        {
            return false;
        }
        try
        {
            return custodyMode switch
            {
                StashCustodyMode.VisibleCurrencyStashExact =>
                    after.TargetVisibleStashAmount == checked(before.TargetVisibleStashAmount + amount),
                // The visible same-type tab may be stale, the affinity destination, or a different tab.
                // Aggregate ownership is verified separately; local custody only requires inventory exit.
                StashCustodyMode.AffinityAggregate => true,
                _ => false,
            };
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    public static bool TransferCompletedExactly(
        InventoryTransferSnapshot before,
        InventoryTransferSnapshot after,
        string metadata,
        long amount) => TransferCompletedExactly(
            before, after, metadata, amount, StashCustodyMode.VisibleCurrencyStashExact);

    public static bool TryAuthenticateMaxStackSize(
        int staticMaxStackSize,
        IEnumerable<int> liveMaxStackSizes,
        out int authenticatedMaxStackSize,
        out string failure)
    {
        authenticatedMaxStackSize = 0;
        var live = liveMaxStackSizes.ToArray();
        if (live.Any(size => size <= 0) || live.Distinct().Take(2).Count() > 1)
        {
            failure = "Visible target maximum stack sizes were unreadable or inconsistent.";
            return false;
        }
        var staticSize = staticMaxStackSize > 0 ? staticMaxStackSize : 0;
        var liveSize = live.FirstOrDefault();
        if (staticSize > 0 && liveSize > 0 && staticSize != liveSize)
        {
            failure = $"Static maximum stack size {staticSize} conflicted with live size {liveSize}.";
            return false;
        }
        authenticatedMaxStackSize = liveSize > 0 ? liveSize : staticSize;
        failure = string.Empty;
        return true;
    }

    public static string NonTargetFingerprint(InventoryTransferSnapshot snapshot, string metadata)
    {
        var canonical = string.Join("\n", snapshot.NonTarget(metadata).Select(item => string.Join("|",
            item.EntityAddress.ToString(CultureInfo.InvariantCulture), item.Metadata,
            item.Amount.ToString(CultureInfo.InvariantCulture),
            item.X.ToString("R", CultureInfo.InvariantCulture), item.Y.ToString("R", CultureInfo.InvariantCulture),
            item.Width.ToString("R", CultureInfo.InvariantCulture), item.Height.ToString("R", CultureInfo.InvariantCulture),
            item.ClearOfExchange ? "1" : "0")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>
    /// Names the first non-target difference between two inventory reads. The fingerprint alone can
    /// only say "something moved", which is useless in a log; every occurrence of that message has
    /// cost a live investigation to work out whether an item appeared, a stack changed size, or a
    /// panel simply moved underneath the same items.
    /// </summary>
    public static string DescribeNonTargetChange(
        InventoryTransferSnapshot before,
        InventoryTransferSnapshot after,
        string metadata)
    {
        var left = before.NonTarget(metadata);
        var right = after.NonTarget(metadata);
        if (left.Count != right.Count)
        {
            return $"non-target item count moved {left.Count} -> {right.Count}";
        }
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] == right[index])
            {
                continue;
            }
            var a = left[index];
            var b = right[index];
            if (a.EntityAddress != b.EntityAddress || a.Metadata != b.Metadata)
            {
                return $"non-target slot {index} became a different item " +
                    $"({a.Metadata} -> {b.Metadata})";
            }
            if (a.Amount != b.Amount)
            {
                return $"non-target {a.Metadata} stack moved {a.Amount} -> {b.Amount}";
            }
            if (a.ClearOfExchange != b.ClearOfExchange)
            {
                return $"non-target {a.Metadata} crossed the exchange edge " +
                    $"(clearOfExchange {a.ClearOfExchange} -> {b.ClearOfExchange}); the panel moved, not the item";
            }
            return $"non-target {a.Metadata} moved from ({a.X},{a.Y} {a.Width}x{a.Height}) " +
                $"to ({b.X},{b.Y} {b.Width}x{b.Height})";
        }
        return "non-target custody hashed differently with no readable difference";
    }

    public static RecoveryKind ClassifyRecovery(
        StashTransferIntentState intent,
        InventoryTransferSnapshot current,
        string canonicalMetadata,
        long canonicalAmount,
        long observedOwned,
        int currentAreaInstanceId)
    {
        if (!StashCustodyPolicy.IsCustodyTabType(current.VisibleTabType) ||
            !StashCustodyPolicy.IsResolvableCustody(canonicalMetadata, intent.StashCustodyMode) ||
            intent.Metadata != canonicalMetadata || intent.Amount != canonicalAmount ||
            intent.InventoryAmountBefore != canonicalAmount || intent.AggregateOwnedBefore != observedOwned ||
            intent.AreaInstanceId != currentAreaInstanceId ||
            NonTargetFingerprint(current, canonicalMetadata) != intent.NonTargetInventoryFingerprint)
        {
            return RecoveryKind.Ambiguous;
        }
        if (current.TargetInventoryAmount == intent.InventoryAmountBefore &&
            current.TargetVisibleStashAmount == intent.VisibleStashAmountBefore)
        {
            return RecoveryKind.PreTransfer;
        }
        try
        {
            var postStashMatches = intent.StashCustodyMode switch
            {
                StashCustodyMode.VisibleCurrencyStashExact =>
                    current.TargetVisibleStashAmount == checked(intent.VisibleStashAmountBefore + intent.Amount),
                StashCustodyMode.AffinityAggregate => true,
                _ => false,
            };
            if (current.TargetInventoryAmount == 0 && postStashMatches)
                return RecoveryKind.PostTransfer;
        }
        catch (OverflowException)
        {
            return RecoveryKind.Ambiguous;
        }
        return RecoveryKind.Ambiguous;
    }
}

public sealed class InventoryStashTransferController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(3);
    private readonly HashSet<Keys> _ownedKeys = [];
    private TrackedOrderState? _tracked;
    private Func<TrackedOrderState, string, bool>? _persist;
    private InventoryTransferSnapshot? _baseline;
    private InventoryItemEvidence? _targetItem;
    private string _metadata = string.Empty;
    private long _amount;
    private long _aggregateOwnedBefore;
    private StashCustodyMode _custodyMode;
    /// <summary>Custody mode resolved from the visible stash tab when the run started.</summary>
    public StashCustodyMode CustodyMode => _custodyMode;
    private int _staticMaxStackSize;
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
    private InventoryStashTransferState _releaseTarget;
    private string _releaseStatus = string.Empty;

    public InventoryStashTransferState State { get; private set; } = InventoryStashTransferState.Idle;
    public string Status { get; private set; } = "Idle; inventory-to-stash transfer is disabled.";
    public string Failure { get; private set; } = string.Empty;
    public bool IsRunning => State is InventoryStashTransferState.MovingToStack or
        InventoryStashTransferState.ReadyToClick or InventoryStashTransferState.ModifierArmed or
        InventoryStashTransferState.WaitingForTransfer or
        InventoryStashTransferState.ReleasingInput;

    public bool Start(
        GameController gameController,
        TrackedOrderState tracked,
        StashTransferInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        string metadata,
        long amount,
        long aggregateOwnedBefore,
        Func<TrackedOrderState, string, bool> persist,
        out string failure)
    {
        return Start(gameController, tracked, permissions, conflictingControllerEnabled, cursorSpeed,
            metadata, amount, aggregateOwnedBefore, 0, persist, out failure);
    }

    public bool Start(
        GameController gameController,
        TrackedOrderState tracked,
        StashTransferInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        string metadata,
        long amount,
        long aggregateOwnedBefore,
        int staticMaxStackSize,
        Func<TrackedOrderState, string, bool> persist,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(tracked);
        ArgumentNullException.ThrowIfNull(persist);
        if (!StashCustodyPolicy.IsSupported(metadata) ||
            IsRunning || tracked.Status != TrackedOrderStatus.Collected || !permissions.Ready ||
            conflictingControllerEnabled || amount <= 0 || aggregateOwnedBefore < amount ||
            !(metadata == tracked.WantedMetadata && amount == tracked.PendingWantedBatchAmount ||
              metadata == tracked.OfferedMetadata && amount == tracked.PendingReturnBatchAmount))
        {
            failure = "Stash transfer requires collected inventory state, complete collection permissions, and controller exclusion.";
            return false;
        }

        if (!TryReadSnapshot(gameController, metadata, staticMaxStackSize, out var snapshot, out failure) ||
            !InventoryTransferEvidence.TrySelectExactTarget(
                snapshot, metadata, amount, out var target, out failure) || target is null)
        {
            return false;
        }

        // Custody is a property of the tab that is actually visible, so it can only be resolved
        // once the snapshot has observed it.
        if (!StashCustodyPolicy.TryResolve(
                metadata,
                snapshot.VisibleTabType,
                snapshot.TargetInventoryAmount,
                snapshot.TargetVisibleStashAmount,
                aggregateOwnedBefore,
                out var custodyMode))
        {
            failure = "Settlement asset has no supported stash custody policy for the visible stash tab.";
            return false;
        }

        _tracked = tracked;
        _persist = persist;
        _baseline = snapshot;
        _targetItem = target;
        _metadata = metadata;
        _amount = amount;
        _aggregateOwnedBefore = aggregateOwnedBefore;
        _custodyMode = custodyMode;
        _staticMaxStackSize = staticMaxStackSize;
        _league = gameController.Game.IngameState.ServerData.League;
        _areaInstanceId = gameController.Game.IngameState.ServerData.InstanceId;
        _clickAttempted = false;
        Failure = string.Empty;
        BeginMovement(target.Center, cursorSpeed);
        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        StashTransferInputPermissions permissions,
        bool conflictingControllerEnabled)
    {
        if (!IsRunning) return;
        if (State == InventoryStashTransferState.ReleasingInput)
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
                case InventoryStashTransferState.MovingToStack:
                    TickMovement(gameController);
                    break;
                case InventoryStashTransferState.ReadyToClick:
                    ClickOnce(gameController);
                    break;
                case InventoryStashTransferState.ModifierArmed:
                    ClickWithModifier(gameController);
                    break;
                case InventoryStashTransferState.WaitingForTransfer:
                    VerifyTransfer(gameController);
                    break;
            }
        }
        catch (Exception exception)
        {
            Finish($"Inventory-to-stash transfer failed closed: {exception.GetType().Name}: {exception.Message}", _clickAttempted);
        }
    }

    public void Cancel(string reason)
    {
        if (!IsRunning || State == InventoryStashTransferState.ReleasingInput) return;
        Finish(reason, _clickAttempted);
    }

    public void EmergencyStop(string reason)
    {
        if (IsRunning) Finish(reason, _clickAttempted);
        TryReleaseOwnedInput();
    }

    public static bool TryReadSnapshot(
        GameController gameController,
        string targetMetadata,
        out InventoryTransferSnapshot snapshot,
        out string failure) => TryReadSnapshot(gameController, targetMetadata, 0, out snapshot, out failure);

    public static bool TryReadSnapshot(
        GameController gameController,
        string targetMetadata,
        int staticMaxStackSize,
        out InventoryTransferSnapshot snapshot,
        out string failure)
    {
        snapshot = new InventoryTransferSnapshot([], 0, 0);
        var ui = gameController.Game.IngameState.IngameUi;
        var panel = ui.CurrencyExchangePanel;
        var inventoryPanel = ui.InventoryPanel;
        var stashElement = ui.StashElement;
        if (inventoryPanel is null || stashElement is null)
        {
            failure = "Inventory or stash UI was unavailable.";
            return false;
        }
            var inventory = inventoryPanel[InventoryIndex.PlayerInventory];
        var visibleStash = stashElement.VisibleStash;
        var visibleTabType = visibleStash?.InvType.ToString() ?? string.Empty;
        if (!gameController.Window.IsForeground() || !panel.IsVisible || panel.CurrencyPicker.IsVisible ||
            ui.PopUpWindow.IsVisible || !inventoryPanel.IsVisible || !stashElement.IsVisible || inventory is null ||
            visibleStash is null || !visibleStash.IsVisible ||
            !StashCustodyPolicy.IsCustodyTabType(visibleTabType))
        {
            failure = "Transfer requires foreground exchange, inventory, and a visible Currency or Fragment stash with picker closed.";
            return false;
        }

        var exchangeRight = panel.GetClientRectCache.Right;
            var inventoryRect = inventory.GetClientRectCache;
            var slotWidth = inventoryRect.Width / 12f;
            var slotHeight = inventoryRect.Height / 5f;
            if (slotWidth <= 0 || slotHeight <= 0)
            {
                failure = "Player inventory grid geometry was unreadable.";
                return false;
            }
            var evidence = new List<InventoryItemEvidence>();
        try
        {
            foreach (var item in inventory.VisibleInventoryItems)
            {
                var entity = item?.Item;
                var rect = item?.GetClientRectCache ?? default;
                if (item?.IsVisible != true || entity?.IsValid != true || string.IsNullOrWhiteSpace(entity.Path) ||
                    rect.Width <= 0 || rect.Height <= 0)
                {
                    failure = "Player inventory contained an invalid, hidden, or unreadable item.";
                    return false;
                }

                var amount = 1;
                if (entity.TryGetComponent<Stack>(out var stack)) amount = stack.Size;
                else if (entity.Path == targetMetadata)
                {
                    failure = "A target-currency item had no readable Stack component.";
                    return false;
                }
                if (amount <= 0)
                {
                    failure = "Player inventory contained a nonpositive stack amount.";
                    return false;
                }
                evidence.Add(new InventoryItemEvidence(
                    entity.Address, entity.Path, amount, rect.X, rect.Y, rect.Width, rect.Height,
                    rect.Left >= exchangeRight));
            }

            if (evidence.Select(item => item.EntityAddress).Distinct().Count() != evidence.Count)
            {
                failure = "Player inventory contained duplicate entity identities.";
                return false;
            }

            long stashTarget = 0;
            var liveMaxStackSizes = new List<int>();
            foreach (var item in visibleStash.VisibleInventoryItems.Where(item => item?.Item?.Path == targetMetadata))
            {
                if (item?.Item?.IsValid != true || !item.Item.TryGetComponent<Stack>(out var stack) || stack.Size <= 0)
                {
                    failure = "Visible stash target amount was unreadable.";
                    return false;
                }
                var currentMax = stack.Info.MaxStackSize;
                if (currentMax <= 0)
                {
                    failure = "Visible stash target maximum stack size was unreadable or inconsistent.";
                    return false;
                }
                liveMaxStackSizes.Add(currentMax);
                stashTarget = checked(stashTarget + stack.Size);
            }

            foreach (var item in inventory.VisibleInventoryItems.Where(item => item?.Item?.Path == targetMetadata))
            {
                if (item?.Item?.TryGetComponent<Stack>(out var stack) != true || stack.Info.MaxStackSize <= 0)
                {
                    failure = "Player inventory target maximum stack size was unreadable or inconsistent.";
                    return false;
                }
                liveMaxStackSizes.Add(stack.Info.MaxStackSize);
            }
            if (!InventoryTransferEvidence.TryAuthenticateMaxStackSize(
                staticMaxStackSize, liveMaxStackSizes, out var maxStackSize, out failure))
                return false;
            var inventoryTarget = evidence.Where(item => item.Metadata == targetMetadata)
                .Aggregate(0L, (total, item) => checked(total + item.Amount));
            snapshot = new InventoryTransferSnapshot(evidence.OrderBy(item => item.EntityAddress).ToArray(),
                inventoryTarget, stashTarget, slotWidth, slotHeight, maxStackSize, visibleTabType);
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Inventory/stash snapshot failed: {exception.Message}";
            return false;
        }
    }

    private bool ValidateGlobal(
        GameController gameController,
        StashTransferInputPermissions permissions,
        bool conflictingControllerEnabled,
        out string failure)
    {
        var server = gameController.Game.IngameState.ServerData;
        var foreignModifierHeld = ModifiersHeld() &&
            !(State == InventoryStashTransferState.ModifierArmed && _ownedKeys.Contains(Keys.ControlKey));
        if (!permissions.Ready || conflictingControllerEnabled || foreignModifierHeld ||
            server.League != _league || server.InstanceId != _areaInstanceId)
        {
            failure = "Transfer permission, controller exclusion, modifier, league, or area gate failed.";
            return false;
        }
        if (State != InventoryStashTransferState.WaitingForTransfer &&
            Vector2.Distance(ExileInput.MousePositionNum, _lastCommanded) > CursorTolerance)
        {
            failure = "Manual cursor movement interrupted inventory-to-stash transfer.";
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
        State = InventoryStashTransferState.MovingToStack;
        Status = $"Moving to one reachable stack after proving exactly {_amount} target units in inventory.";
    }

    private void TickMovement(GameController gameController)
    {
        if (!TryResolveUnchangedTarget(gameController, out var freshTarget, out var failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance)
        {
            Finish(string.IsNullOrEmpty(failure) ? "Inventory transfer target moved." : failure, clicked: false);
            return;
        }

        var progress = Math.Clamp(
            (DateTimeOffset.UtcNow - _moveStartedAt).TotalMilliseconds / _moveDuration.TotalMilliseconds, 0, 1);
        var next = Vector2.Lerp(_moveStart, _target, (float)progress);
        ExileInput.SetCursorPos(next);
        _lastCommanded = next;
        if (progress >= 1)
        {
            State = InventoryStashTransferState.ReadyToClick;
            Status = "At target stack; performing final inventory/stash revalidation.";
        }
    }

    private void ClickOnce(GameController gameController)
    {
        if (!TryResolveUnchangedTarget(gameController, out var freshTarget, out var failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshTarget) > CursorTolerance)
        {
            Finish(string.IsNullOrEmpty(failure) ? "Final transfer context changed." : failure, clicked: false);
            return;
        }

        var armed = TrackedOrderCollectionController.CloneTracked(
            _tracked!, TrackedOrderStatus.StashTransferArmed,
            _custodyMode == StashCustodyMode.AffinityAggregate
                ? $"Persisted intent to move exactly {_amount} {_metadata} through configured stash affinity."
                : $"Persisted intent to sweep exactly {_amount} {_metadata} from inventory to visible Currency Stash.");
        armed.StashTransferIntent = new StashTransferIntentState
        {
            StashCustodyMode = _custodyMode,
            Metadata = _metadata,
            Amount = _amount,
            InventoryAmountBefore = _baseline!.TargetInventoryAmount,
            VisibleStashAmountBefore = _baseline.TargetVisibleStashAmount,
            AggregateOwnedBefore = _aggregateOwnedBefore,
            NonTargetInventoryFingerprint = InventoryTransferEvidence.NonTargetFingerprint(_baseline, _metadata),
            AreaInstanceId = _areaInstanceId,
            ArmedAtUtc = DateTimeOffset.UtcNow
        };
        if (!_persist!(armed, "CollectedCurrencyStashTransferArmed"))
        {
            Finish("Could not persist stash-transfer intent before input.", clicked: false);
            return;
        }
        _tracked = armed;

        if (!TryResolveUnchangedTarget(gameController, out freshTarget, out failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshTarget) > CursorTolerance)
        {
            DisarmBeforeClick(string.IsNullOrEmpty(failure) ? "Transfer context changed after durable arming." : failure);
            return;
        }

        PressKey(Keys.ControlKey);
        _deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(60);
        State = InventoryStashTransferState.ModifierArmed;
        Status = "Ctrl armed; waiting one input interval before the stash-transfer right-click.";
    }

    private void ClickWithModifier(GameController gameController)
    {
        if (DateTimeOffset.UtcNow < _deadline) return;
        var failure = string.Empty;
        if (!ExileInput.IsKeyDown(Keys.ControlKey) ||
            !TryResolveUnchangedTarget(gameController, out var freshTarget, out failure) ||
            Vector2.Distance(freshTarget, _target) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshTarget) > CursorTolerance)
        {
            DisarmBeforeClick(string.IsNullOrEmpty(failure)
                ? "Ctrl chord or transfer context was not stable before the stash-transfer click."
                : failure);
            return;
        }

        _clickAttempted = true;
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
            try { ReleaseKey(Keys.ControlKey); } catch (Exception exception) { clickFailure ??= exception; }
        }

        if (clickFailure is not null)
        {
            Finish($"Stash-transfer click/release was ambiguous: {clickFailure.Message}", clicked: true);
            return;
        }

        _deadline = DateTimeOffset.UtcNow + TransferTimeout;
        State = InventoryStashTransferState.WaitingForTransfer;
        Status = _custodyMode == StashCustodyMode.AffinityAggregate
            ? "Sent one exact stash-transfer click; waiting for inventory exit evidence."
            : "Ctrl-right-clicked once; waiting for exact inventory and Currency Stash totals.";
    }

    private void DisarmBeforeClick(string reason)
    {
        var disarmed = TrackedOrderCollectionController.CloneTracked(
            _tracked!, TrackedOrderStatus.Collected, $"Disarmed without input: {reason}");
        disarmed.StashTransferIntent = null;
        if (_persist!(disarmed, "CollectedCurrencyStashTransferDisarmedBeforeClick")) _tracked = disarmed;
        Finish(reason, clicked: false);
    }

    private void VerifyTransfer(GameController gameController)
    {
        if (VerifyPostState(gameController, out var failure))
        {
            var proof = _custodyMode == StashCustodyMode.AffinityAggregate
                ? $"Verified inventory {_amount}->0 with a valid visible-or-hidden affinity destination; non-target inventory unchanged."
                : $"Verified inventory {_amount}->0 and visible Currency Stash +{_amount}; non-target inventory unchanged.";
            BeginRelease(InventoryStashTransferState.TransferEvidence, proof);
            return;
        }

        if (DateTimeOffset.UtcNow >= _deadline)
        {
            Finish(string.IsNullOrEmpty(failure)
                ? "Inventory and Currency Stash evidence did not stabilize to the exact transfer."
                : failure, clicked: true);
        }
    }

    public bool VerifyPostState(GameController gameController, out string failure)
    {
        failure = string.Empty;
        if (gameController.Game.IngameState.ServerData.InstanceId == _areaInstanceId &&
            TryReadSnapshot(gameController, _metadata, _staticMaxStackSize, out var current, out failure) &&
            InventoryTransferEvidence.TransferCompletedExactly(_baseline!, current, _metadata, _amount, _custodyMode))
        {
            failure = string.Empty;
            return true;
        }
        if (string.IsNullOrEmpty(failure)) failure = "Inventory-to-stash post-state no longer matched durable evidence.";
        return false;
    }

    private bool TryResolveUnchangedTarget(GameController gameController, out Vector2 target, out string failure)
    {
        target = default;
        if (!TryReadSnapshot(gameController, _metadata, _staticMaxStackSize, out var current, out failure) ||
            !_baseline!.Items.SequenceEqual(current.Items) ||
            _baseline.TargetVisibleStashAmount != current.TargetVisibleStashAmount)
        {
            if (string.IsNullOrEmpty(failure)) failure = "Inventory or visible Currency Stash changed before transfer input.";
            return false;
        }

        var currentTarget = current.Items.SingleOrDefault(item => item.EntityAddress == _targetItem!.EntityAddress);
        if (currentTarget is null || !currentTarget.ClearOfExchange)
        {
            failure = "Exact target stack was no longer safely reachable.";
            return false;
        }
        target = currentTarget.Center;
        failure = string.Empty;
        return true;
    }

    private void Finish(string reason, bool clicked)
    {
        Failure = reason;
        if (clicked)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _tracked!, TrackedOrderStatus.Ambiguous, reason);
            var persisted = _persist?.Invoke(ambiguous, "CollectedCurrencyStashTransferAmbiguous") == true;
            BeginRelease(InventoryStashTransferState.Ambiguous,
                persisted ? $"AMBIGUOUS: {reason}" : $"AMBIGUOUS AND PERSISTENCE FAILED: {reason}");
        }
        else
        {
            BeginRelease(InventoryStashTransferState.Cancelled, $"Cancelled before stash-transfer input: {reason}");
        }
    }

    private void PressKey(Keys key)
    {
        _ownedKeys.Add(key);
        ExileInput.KeyDown(key);
    }

    private void ReleaseKey(Keys key)
    {
        if (!_ownedKeys.Remove(key)) return;
        try { ExileInput.KeyUp(key); }
        catch { _ownedKeys.Add(key); throw; }
    }

    private void BeginRelease(InventoryStashTransferState target, string status)
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
            State = InventoryStashTransferState.ReleasingInput;
            Status = "Inventory-to-stash input release pending; retrying every tick.";
        }
    }

    private bool TryReleaseOwnedInput()
    {
        foreach (var key in _ownedKeys.ToArray())
        {
            try { ExileInput.KeyUp(key); _ownedKeys.Remove(key); } catch { }
        }
        if (_mouseDown)
        {
            try
            {
                ExileInput.RightUp();
                _mouseDown = false;
            }
            catch { }
        }
        return _ownedKeys.Count == 0 && !_mouseDown;
    }

    private static bool ModifiersHeld() =>
        ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu);
}
