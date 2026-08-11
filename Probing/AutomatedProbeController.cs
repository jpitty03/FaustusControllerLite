using ExileCore;
using FaustusControllerLite.Input;
using System.Numerics;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite.Probing;

public enum AutomatedProbeState
{
    Idle,
    MovingToPickerButton,
    ClickingPickerButton,
    WaitingForPicker,
    FocusingSearch,
    WaitingForSearchFocus,
    SelectingExistingQuery,
    ClearingSearch,
    TypingQuery,
    WaitingForOption,
    MovingToOption,
    ClickingOption,
    WaitingForSelection,
    SamplingBook,
    ReleasingInput,
    Completed,
    Failed,
    Cancelled,
}

public sealed record ProbeInputPermissions(bool Probing, bool MouseMovement, bool Clicking, bool QueryInput)
{
    public bool Ready => Probing && MouseMovement && Clicking && QueryInput;

    public static ProbeInputPermissions From(FaustusControllerLiteSettings settings) => new(
        settings.AllowAutomatedProbing.Value,
        settings.AllowVerifiedMouseMovement.Value,
        settings.AllowVerifiedClicks.Value,
        settings.AllowQueryInput.Value);
}

public sealed record ProbeMarketPlan(CurrencyIdentity Offered, CurrencyIdentity Wanted);

public readonly record struct StableBookHead(
    string OfferedMetadata,
    string WantedMetadata,
    Rational MarketRate,
    Rational ImmediateRate,
    Rational CompetingRate,
    string LiquidityFingerprint);

public sealed class StableSampleCounter<T> where T : notnull
{
    private T? _last;
    private bool _hasLast;

    public int Count { get; private set; }

    public bool Observe(T sample, int required)
    {
        if (required <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(required));
        }

        if (!_hasLast || !EqualityComparer<T>.Default.Equals(_last!, sample))
        {
            _last = sample;
            _hasLast = true;
            Count = 1;
        }
        else
        {
            Count = checked(Count + 1);
        }

        return Count >= required;
    }

    public void Reset()
    {
        _last = default;
        _hasLast = false;
        Count = 0;
    }
}

public sealed class AutomatedProbeController
{
    private const float CursorTolerance = 8f;
    private const float GeometryTolerance = 5f;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BookTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeyInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(100);

    private readonly List<MarketCapture> _captures = new(3);
    private readonly StableSampleCounter<StableBookHead> _stableSamples = new();
    private IReadOnlyList<ProbeMarketPlan> _plans = Array.Empty<ProbeMarketPlan>();
    private Guid _sessionId;
    private string _league = string.Empty;
    private int _areaInstanceId;
    private int _pairIndex;
    private bool _selectingWanted;
    private int _queryIndex;
    private string _query = string.Empty;
    private CurrencyIdentity? _desiredCurrency;
    private Vector2 _moveStart;
    private Vector2 _moveTarget;
    private Vector2 _lastCommandedCursor;
    private DateTimeOffset _moveStartedAt;
    private TimeSpan _moveDuration;
    private DateTimeOffset _stepDeadline;
    private DateTimeOffset _overallDeadline;
    private DateTimeOffset _nextActionAt;
    private Vector2 _stableOptionCenter;
    private int _stableOptionReads;
    private bool _mouseDown;
    private readonly HashSet<Keys> _ownedKeys = new();
    private AutomatedProbeState _releaseTargetState;
    private string _releaseStatus = string.Empty;
    private bool _selectionOnly;
    private bool _forceOfferedPickerOpen;

    public AutomatedProbeState State { get; private set; } = AutomatedProbeState.Idle;
    public string Status { get; private set; } = "Idle.";
    public string Failure { get; private set; } = string.Empty;
    public bool FreshProbeRetryRecommended { get; private set; }
    public Guid SessionId => _sessionId;
    public bool IsRunning => State is not AutomatedProbeState.Idle and
        not AutomatedProbeState.Completed and
        not AutomatedProbeState.Failed and
        not AutomatedProbeState.Cancelled;
    public IReadOnlyList<MarketCapture> CompletedCaptures => _captures;

    public bool Start(
        GameController gameController,
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        CurrencyIdentity target,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        out string failure)
    {
        if (chaos.Equals(divine) || chaos.Equals(target) || divine.Equals(target))
        {
            failure = "Chaos, Divine, and target identities must be distinct.";
            return false;
        }

        return StartMarketProbe(
            gameController,
            CreateThreeMarketPlans(chaos, divine, target),
            calibration,
            permissions,
            conflictingControllerEnabled,
            cursorSpeed,
            Guid.NewGuid(),
            out failure);
    }

    public bool StartMarketProbe(
        GameController gameController,
        IReadOnlyList<ProbeMarketPlan> plans,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        Guid sessionId,
        out string failure)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (sessionId == Guid.Empty || plans.Count is < 1 or > 3 ||
            plans.Any(plan => plan.Offered.Equals(plan.Wanted)) ||
            plans.Select(plan => new CurrencyPairKey(plan.Offered, plan.Wanted)).Distinct().Count() != plans.Count)
        {
            failure = "A market probe requires one to three unique, distinct pairs and a non-empty session ID.";
            return false;
        }

        return StartPlans(
            gameController, plans, calibration, permissions, conflictingControllerEnabled, cursorSpeed,
            selectionOnly: false, requestedSessionId: sessionId,
            forceOfferedPickerOpen: RequiresOfferedOwnershipRefresh(plans), out failure);
    }

    public static bool RequiresOfferedOwnershipRefresh(IReadOnlyCollection<ProbeMarketPlan> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        return plans.Count > 0 && plans.Select(plan => plan.Offered.Metadata)
            .Distinct(StringComparer.Ordinal).Count() == 1;
    }

    public static IReadOnlyList<ProbeMarketPlan> CreateThreeMarketPlans(
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        CurrencyIdentity target) =>
    [
        new ProbeMarketPlan(divine, chaos),
        new ProbeMarketPlan(chaos, target),
        new ProbeMarketPlan(divine, target),
    ];

    public static IReadOnlyList<ProbeMarketPlan> CreateSweepBenchmarkPlans(
        CurrencyIdentity chaos,
        CurrencyIdentity divine) =>
    [
        new ProbeMarketPlan(divine, chaos),
    ];

    public static IReadOnlyList<ProbeMarketPlan> CreateDirectDivineCyclePlans(
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        CurrencyIdentity target) =>
    [
        new ProbeMarketPlan(divine, chaos),
        new ProbeMarketPlan(divine, target),
    ];

    public static IReadOnlyList<ProbeMarketPlan> CreateSweepCandidatePlans(
        CurrencyIdentity chaos,
        CurrencyIdentity divine,
        CurrencyIdentity target) =>
    [
        new ProbeMarketPlan(chaos, target),
        new ProbeMarketPlan(divine, target),
    ];

    public bool StartPairSelection(
        GameController gameController,
        CurrencyIdentity offered,
        CurrencyIdentity wanted,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        out string failure)
    {
        if (offered.Equals(wanted))
        {
            failure = "Pair selection requires distinct currency identities.";
            return false;
        }

        return StartPlans(gameController, [new ProbeMarketPlan(offered, wanted)], calibration, permissions,
            conflictingControllerEnabled, cursorSpeed, selectionOnly: true, requestedSessionId: null,
            forceOfferedPickerOpen: false, out failure);
    }

    public bool StartOfferedOwnershipObservation(
        GameController gameController,
        CurrencyIdentity offered,
        CurrencyIdentity wanted,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        out string failure)
    {
        if (offered.Equals(wanted))
        {
            failure = "Ownership observation requires a distinct counterpart currency.";
            return false;
        }

        return StartPlans(gameController, [new ProbeMarketPlan(offered, wanted)], calibration, permissions,
            conflictingControllerEnabled, cursorSpeed, selectionOnly: true, requestedSessionId: null,
            forceOfferedPickerOpen: true, out failure);
    }

    public bool StartSingleMarketProbe(
        GameController gameController,
        CurrencyIdentity offered,
        CurrencyIdentity wanted,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        Guid sessionId,
        out string failure)
    {
        if (offered.Equals(wanted) || sessionId == Guid.Empty)
        {
            failure = "Single-market refresh requires a distinct pair and existing session ID.";
            return false;
        }

        return StartPlans(gameController, [new ProbeMarketPlan(offered, wanted)], calibration, permissions,
            conflictingControllerEnabled, cursorSpeed, selectionOnly: false, requestedSessionId: sessionId,
            forceOfferedPickerOpen: false, out failure);
    }

    private bool StartPlans(
        GameController gameController,
        IReadOnlyList<ProbeMarketPlan> plans,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        bool selectionOnly,
        Guid? requestedSessionId,
        bool forceOfferedPickerOpen,
        out string failure)
    {
        if (IsRunning)
        {
            failure = "A verified pair operation is already running.";
            return false;
        }

        if (!permissions.Ready)
        {
            failure = "Verified pair selection requires movement, click, and query input permission.";
            return false;
        }

        if (!calibration.IsComplete)
        {
            failure = "Both picker buttons must be calibrated before pair selection.";
            return false;
        }

        if (conflictingControllerEnabled)
        {
            failure = "Disable the full FaustusController plugin before enabling Lite input automation.";
            return false;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var server = gameController.Game.IngameState.ServerData;
        if (!gameController.Window.IsForeground() || !panel.IsVisible || panel.CurrencyPicker.IsVisible ||
            string.IsNullOrWhiteSpace(server.League) || ModifiersHeld())
        {
            failure = "Start requires foreground Path of Exile, visible exchange, closed picker, readable league, and no held modifiers.";
            return false;
        }

        _plans = plans;
        _selectionOnly = selectionOnly;
        _forceOfferedPickerOpen = forceOfferedPickerOpen;
        _sessionId = requestedSessionId ?? Guid.NewGuid();
        _league = server.League;
        _areaInstanceId = server.InstanceId;
        _pairIndex = 0;
        _captures.Clear();
        _stableSamples.Reset();
        FreshProbeRetryRecommended = false;
        _lastCommandedCursor = ExileInput.MousePositionNum;
        _overallDeadline = DateTimeOffset.UtcNow + OverallTimeout;
        Failure = string.Empty;
        State = AutomatedProbeState.Idle;
        BeginCurrentPair(gameController, calibration, DateTimeOffset.UtcNow, cursorSpeed);
        if (!IsRunning && !(selectionOnly && State == AutomatedProbeState.Completed))
        {
            failure = Failure;
            return false;
        }

        failure = string.Empty;
        return true;
    }

    public void Tick(
        GameController gameController,
        PickerCalibration calibration,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        int cursorSpeed,
        int stableSampleCount)
    {
        if (!IsRunning)
        {
            return;
        }

        if (State == AutomatedProbeState.ReleasingInput)
        {
            if (TryReleaseOwnedInput())
            {
                State = _releaseTargetState;
                Status = _releaseStatus;
            }
            else
            {
                Status = "Input release pending; retrying every tick.";
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
                case AutomatedProbeState.MovingToPickerButton:
                    TickMovement(now, AutomatedProbeState.ClickingPickerButton);
                    break;
                case AutomatedProbeState.ClickingPickerButton:
                    ClickPickerButton(gameController, calibration, now);
                    break;
                case AutomatedProbeState.WaitingForPicker:
                    WaitForPicker(gameController, now);
                    break;
                case AutomatedProbeState.FocusingSearch:
                    FocusSearch(gameController, now);
                    break;
                case AutomatedProbeState.WaitingForSearchFocus:
                    WaitForSearchFocus(gameController, now);
                    break;
                case AutomatedProbeState.SelectingExistingQuery:
                    SelectExistingQuery(gameController, now);
                    break;
                case AutomatedProbeState.ClearingSearch:
                    ClearSearch(gameController, now);
                    break;
                case AutomatedProbeState.TypingQuery:
                    TypeQuery(gameController, now);
                    break;
                case AutomatedProbeState.WaitingForOption:
                    WaitForOption(gameController, now, cursorSpeed);
                    break;
                case AutomatedProbeState.MovingToOption:
                    TickMovement(now, AutomatedProbeState.ClickingOption);
                    break;
                case AutomatedProbeState.ClickingOption:
                    ClickOption(gameController, now);
                    break;
                case AutomatedProbeState.WaitingForSelection:
                    WaitForSelection(gameController, calibration, now, cursorSpeed);
                    break;
                case AutomatedProbeState.SamplingBook:
                    SampleBook(gameController, calibration, now, cursorSpeed, stableSampleCount);
                    break;
            }
        }
        catch (Exception exception)
        {
            Cancel($"Automated probe failed closed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    public void Cancel(string reason)
    {
        Failure = reason;
        _captures.Clear();
        BeginRelease(AutomatedProbeState.Cancelled, $"Cancelled: {reason}");
    }

    public void AcknowledgeCompletion()
    {
        _captures.Clear();
        BeginRelease(AutomatedProbeState.Idle, "Idle.");
    }

    private bool ValidateGlobalState(
        GameController gameController,
        ProbeInputPermissions permissions,
        bool conflictingControllerEnabled,
        DateTimeOffset now,
        out string failure)
    {
        if (!permissions.Ready)
        {
            failure = "A required probe input permission changed.";
            return false;
        }

        if (conflictingControllerEnabled)
        {
            failure = "The full FaustusController became enabled.";
            return false;
        }

        if (now > _overallDeadline)
        {
            failure = "Overall probe timeout expired.";
            return false;
        }

        if (!gameController.Window.IsForeground() || ModifiersHeld())
        {
            failure = "Foreground focus was lost or a modifier key was held.";
            return false;
        }

        var ui = gameController.Game.IngameState.IngameUi;
        var panel = ui.CurrencyExchangePanel;
        var server = gameController.Game.IngameState.ServerData;
        if (!panel.IsVisible || ui.PopUpWindow.IsVisible ||
            !string.Equals(server.League, _league, StringComparison.Ordinal) ||
            server.InstanceId != _areaInstanceId)
        {
            failure = "Exchange visibility, league, or area instance changed.";
            return false;
        }

        if (Vector2.Distance(ExileInput.MousePositionNum, _lastCommandedCursor) > CursorTolerance)
        {
            failure = "Manual cursor movement interrupted the probe.";
            return false;
        }

        var picker = panel.CurrencyPicker;
        var plan = _plans[_pairIndex];
        if (_selectingWanted &&
            !string.Equals(panel.OfferedItemType?.Metadata, plan.Offered.Metadata, StringComparison.Ordinal))
        {
            failure = "The locked offered currency changed unexpectedly.";
            return false;
        }

        if (State == AutomatedProbeState.SamplingBook &&
            !string.Equals(panel.WantedItemType?.Metadata, plan.Wanted.Metadata, StringComparison.Ordinal))
        {
            failure = "The locked wanted currency changed unexpectedly.";
            return false;
        }

        if (State is AutomatedProbeState.MovingToPickerButton or AutomatedProbeState.ClickingPickerButton &&
            picker.IsVisible)
        {
            failure = "The picker opened unexpectedly during button targeting.";
            return false;
        }

        if (State is AutomatedProbeState.FocusingSearch or AutomatedProbeState.WaitingForSearchFocus or
                AutomatedProbeState.SelectingExistingQuery or AutomatedProbeState.ClearingSearch or
                AutomatedProbeState.TypingQuery or
                AutomatedProbeState.WaitingForOption or AutomatedProbeState.MovingToOption or
                AutomatedProbeState.ClickingOption &&
            (!picker.IsVisible || picker.IsPickingWantedCurrency != _selectingWanted))
        {
            failure = "Picker visibility or side changed during option selection.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private void BeginCurrentPair(
        GameController gameController,
        PickerCalibration calibration,
        DateTimeOffset now,
        int cursorSpeed)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.CurrencyPicker.IsVisible)
        {
            Cancel("Picker was unexpectedly open before selecting a market.");
            return;
        }

        _selectingWanted = false;
        Status = $"Pair {_pairIndex + 1}/{_plans.Count}: selecting {_plans[_pairIndex].Offered.Name} as offered.";
        BeginSideSelection(gameController, calibration, now, cursorSpeed);
    }

    private void BeginSideSelection(
        GameController gameController,
        PickerCalibration calibration,
        DateTimeOffset now,
        int cursorSpeed)
    {
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        _desiredCurrency = _selectingWanted ? _plans[_pairIndex].Wanted : _plans[_pairIndex].Offered;
        var selected = _selectingWanted ? panel.WantedItemType : panel.OfferedItemType;
        if (string.Equals(selected?.Metadata, _desiredCurrency.Metadata, StringComparison.Ordinal) &&
            (_selectingWanted || !_forceOfferedPickerOpen))
        {
            CompleteSideSelection(gameController, calibration, now, cursorSpeed);
            return;
        }

        var rect = panel.GetClientRectCache;
        if (!calibration.TryResolve(_selectingWanted, rect.X, rect.Y, rect.Width, rect.Height,
                out var button, out var failure))
        {
            Cancel(failure);
            return;
        }

        BeginMovement(button, now, cursorSpeed, AutomatedProbeState.MovingToPickerButton);
    }

    private void CompleteSideSelection(
        GameController gameController,
        PickerCalibration calibration,
        DateTimeOffset now,
        int cursorSpeed)
    {
        if (!_selectingWanted)
        {
            _selectingWanted = true;
            Status = $"Pair {_pairIndex + 1}/{_plans.Count}: selecting {_plans[_pairIndex].Wanted.Name} as wanted.";
            BeginSideSelection(gameController, calibration, now, cursorSpeed);
            return;
        }

        if (_selectionOnly)
        {
            State = AutomatedProbeState.Completed;
            Status = $"Verified pair selected: {_plans[_pairIndex].Offered.Name}/{_plans[_pairIndex].Wanted.Name}.";
            return;
        }

        _stableSamples.Reset();
        _stepDeadline = now + BookTimeout;
        _nextActionAt = now + TimeSpan.FromMilliseconds(250);
        State = AutomatedProbeState.SamplingBook;
        Status = $"Pair {_pairIndex + 1}/{_plans.Count}: waiting for stable {_plans[_pairIndex].Offered.Name}/{_plans[_pairIndex].Wanted.Name} book.";
    }

    private void BeginMovement(
        Vector2 target,
        DateTimeOffset now,
        int cursorSpeed,
        AutomatedProbeState movingState)
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
        var seconds = Vector2.Distance(_moveStart, target) / cursorSpeed;
        _moveDuration = TimeSpan.FromSeconds(Math.Max(seconds, 0.01));
        _stepDeadline = now + _moveDuration + StepTimeout;
        State = movingState;
    }

    private void TickMovement(DateTimeOffset now, AutomatedProbeState completedState)
    {
        if (now > _stepDeadline)
        {
            Cancel("Cursor movement timeout expired.");
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

    private void ClickPickerButton(GameController gameController, PickerCalibration calibration, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Picker button click deadline expired.");
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.CurrencyPicker.IsVisible)
        {
            Cancel("Picker opened before its verified click.");
            return;
        }

        var rect = panel.GetClientRectCache;
        if (!calibration.TryResolve(_selectingWanted, rect.X, rect.Y, rect.Width, rect.Height,
                out var freshTarget, out var failure) ||
            Vector2.Distance(freshTarget, _moveTarget) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshTarget) > CursorTolerance)
        {
            Cancel(string.IsNullOrEmpty(failure) ? "Picker button geometry changed before click." : failure);
            return;
        }

        ClickLeft();
        _stepDeadline = now + StepTimeout;
        State = AutomatedProbeState.WaitingForPicker;
    }

    private void WaitForPicker(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Picker did not open before timeout.");
            return;
        }

        var picker = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
        if (!picker.IsVisible)
        {
            return;
        }

        if (picker.IsPickingWantedCurrency != _selectingWanted)
        {
            Cancel("Picker opened on the unexpected side.");
            return;
        }

        State = AutomatedProbeState.FocusingSearch;
        _stepDeadline = now + StepTimeout;
    }

    private void FocusSearch(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Search focus deadline expired.");
            return;
        }

        if (!ValidateOpenPicker(gameController, out var failure))
        {
            Cancel(failure);
            return;
        }

        SendControlChord(Keys.F);
        State = AutomatedProbeState.WaitingForSearchFocus;
        _stepDeadline = now + StepTimeout;
        _nextActionAt = now + TimeSpan.FromMilliseconds(50);
    }

    private void WaitForSearchFocus(GameController gameController, DateTimeOffset now)
    {
        if (now < _nextActionAt)
        {
            return;
        }

        if (!ValidateOpenPicker(gameController, out var failure))
        {
            Cancel(failure);
            return;
        }

        if (HasVerifiedSearchFocus(gameController))
        {
            State = AutomatedProbeState.SelectingExistingQuery;
            return;
        }

        if (now > _stepDeadline)
        {
            Cancel("Picker search did not receive verified input focus.");
        }
    }

    private void SelectExistingQuery(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Search clear deadline expired.");
            return;
        }

        if (!ValidateSearchFocus(gameController, out var failure))
        {
            Cancel(failure);
            return;
        }

        SendControlChord(Keys.A);
        State = AutomatedProbeState.ClearingSearch;
        _nextActionAt = now + TimeSpan.FromMilliseconds(50);
    }

    private void ClearSearch(GameController gameController, DateTimeOffset now)
    {
        if (now < _nextActionAt)
        {
            return;
        }

        if (now > _stepDeadline)
        {
            Cancel("Search clear deadline expired.");
            return;
        }

        if (!ValidateSearchFocus(gameController, out var failure))
        {
            Cancel(failure);
            return;
        }

        TapKey(Keys.Back);
        _query = $"\"{_desiredCurrency!.Name.ToLowerInvariant()}\"";
        if (_query.Any(character => !CanType(character)))
        {
            Cancel($"Currency query contains an unsupported character: '{_query}'.");
            return;
        }

        _queryIndex = 0;
        _nextActionAt = now + KeyInterval;
        _stepDeadline = now + StepTimeout;
        State = AutomatedProbeState.TypingQuery;
    }

    private void TypeQuery(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Currency query typing timed out.");
            return;
        }

        if (now < _nextActionAt)
        {
            return;
        }

        if (_queryIndex < _query.Length)
        {
            if (!ValidateSearchFocus(gameController, out var failure))
            {
                Cancel(failure);
                return;
            }

            var character = _query[_queryIndex++];
            if (character == '"')
            {
                SendShiftChord(Keys.Oem7);
            }
            else
            {
                TapKey(KeyFor(character));
            }
            _nextActionAt = now + KeyInterval;
            return;
        }

        _stableOptionReads = 0;
        _nextActionAt = now + TimeSpan.FromMilliseconds(100);
        _stepDeadline = now + StepTimeout;
        State = AutomatedProbeState.WaitingForOption;
    }

    private void WaitForOption(GameController gameController, DateTimeOffset now, int cursorSpeed)
    {
        if (now < _nextActionAt)
        {
            return;
        }

        if (!TryFindExactVisibleOption(gameController, out var center, out var failure))
        {
            if (now > _stepDeadline)
            {
                Cancel(failure);
            }

            return;
        }

        if (now > _stepDeadline)
        {
            Cancel("Exact currency option appeared after the selection deadline.");
            return;
        }

        if (_stableOptionReads == 0 || Vector2.Distance(center, _stableOptionCenter) > GeometryTolerance)
        {
            _stableOptionCenter = center;
            _stableOptionReads = 1;
            _nextActionAt = now + TimeSpan.FromMilliseconds(50);
            return;
        }

        _stableOptionReads++;
        if (_stableOptionReads < 2)
        {
            return;
        }

        BeginMovement(center, now, cursorSpeed, AutomatedProbeState.MovingToOption);
    }

    private void ClickOption(GameController gameController, DateTimeOffset now)
    {
        if (now > _stepDeadline)
        {
            Cancel("Currency option click deadline expired.");
            return;
        }

        if (!TryFindExactVisibleOption(gameController, out var freshCenter, out var failure) ||
            Vector2.Distance(freshCenter, _moveTarget) > GeometryTolerance ||
            Vector2.Distance(ExileInput.MousePositionNum, freshCenter) > CursorTolerance)
        {
            Cancel(string.IsNullOrEmpty(failure) ? "Currency option geometry changed before click." : failure);
            return;
        }

        ClickLeft();
        _stepDeadline = now + StepTimeout;
        State = AutomatedProbeState.WaitingForSelection;
    }

    private void WaitForSelection(
        GameController gameController,
        PickerCalibration calibration,
        DateTimeOffset now,
        int cursorSpeed)
    {
        if (now > _stepDeadline)
        {
            Cancel("Currency selection was not confirmed before timeout.");
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        if (panel.CurrencyPicker.IsVisible)
        {
            return;
        }

        var selected = _selectingWanted ? panel.WantedItemType : panel.OfferedItemType;
        if (!string.Equals(selected?.Metadata, _desiredCurrency!.Metadata, StringComparison.Ordinal))
        {
            Cancel("Picker closed without selecting the expected metadata identity.");
            return;
        }

        if (!_selectingWanted)
        {
            // A same-offered batch only needs one forced opening to refresh all "I have" counts.
            _forceOfferedPickerOpen = false;
        }

        CompleteSideSelection(gameController, calibration, now, cursorSpeed);
    }

    private void SampleBook(
        GameController gameController,
        PickerCalibration calibration,
        DateTimeOffset now,
        int cursorSpeed,
        int requiredSamples)
    {
        if (now > _stepDeadline)
        {
            CancelForFreshProbe("Stable book sampling timed out.");
            return;
        }

        if (now < _nextActionAt)
        {
            return;
        }

        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var plan = _plans[_pairIndex];
        if (panel.CurrencyPicker.IsVisible ||
            !string.Equals(panel.OfferedItemType?.Metadata, plan.Offered.Metadata, StringComparison.Ordinal) ||
            !string.Equals(panel.WantedItemType?.Metadata, plan.Wanted.Metadata, StringComparison.Ordinal))
        {
            Cancel("Selected pair changed during stable sampling.");
            return;
        }

        if (!CurrentMarketReader.TryCapture(gameController, _sessionId, out var capture, out var failure) ||
            capture is null)
        {
            if (CurrentMarketReader.IsTransientBookTransition(failure))
            {
                _stableSamples.Reset();
                Status = $"Pair {_pairIndex + 1}/{_plans.Count}: waiting for selected rate and immediate book head to agree.";
                _nextActionAt = now + SampleInterval;
                return;
            }

            CancelForFreshProbe(failure);
            return;
        }

        if (!TryCreateStableHead(capture, out var head, out var headFailure))
        {
            // The panel's identity fields flip client-side the moment the picker closes, but the book
            // rows and market rate only arrive on a server round-trip. An entirely empty capture is
            // therefore the book not having arrived yet, not an unreadable book, so it must fall
            // through to the existing poll rather than consume the whole BookTimeout budget on the
            // first sample. Anything partially populated still cancels immediately: that shape means
            // the read is genuinely wrong rather than merely early. If the book never arrives,
            // BookTimeout still adjudicates with an accurate sampling-timeout diagnosis.
            if (capture.WantedItemStock.Count == 0 && capture.OfferedItemStock.Count == 0 &&
                capture.MarketRateGet == 0 && capture.MarketRateGive == 0)
            {
                // Reset so pre-arrival state can never count toward the consecutive-sample requirement.
                _stableSamples.Reset();
                Status = $"Pair {_pairIndex + 1}/{_plans.Count}: awaiting first book rows from the server.";
                _nextActionAt = now + SampleInterval;
                return;
            }

            CancelForFreshProbe(headFailure);
            return;
        }

        if (_stableSamples.Observe(head, requiredSamples))
        {
            _captures.Add(capture);
            _pairIndex++;
            if (_pairIndex == _plans.Count)
            {
                State = AutomatedProbeState.Completed;
                Status = $"Completed coherent session {_sessionId:D}; awaiting atomic publication.";
                return;
            }

            BeginCurrentPair(gameController, calibration, now, cursorSpeed);
            return;
        }

        Status = $"Pair {_pairIndex + 1}/{_plans.Count}: stable sample {_stableSamples.Count}/{requiredSamples}.";
        _nextActionAt = now + SampleInterval;
    }

    private void CancelForFreshProbe(string reason)
    {
        FreshProbeRetryRecommended = true;
        Cancel(reason);
    }

    private bool ValidateOpenPicker(GameController gameController, out string failure)
    {
        var picker = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
        if (!picker.IsVisible || picker.IsPickingWantedCurrency != _selectingWanted)
        {
            failure = "Picker visibility or side changed during query input.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private bool ValidateSearchFocus(GameController gameController, out string failure)
    {
        if (!ValidateOpenPicker(gameController, out failure))
        {
            return false;
        }

        if (!HasVerifiedSearchFocus(gameController))
        {
            failure = "The picker search no longer owns keyboard focus.";
            return false;
        }

        return true;
    }

    private static bool HasVerifiedSearchFocus(GameController gameController)
    {
        var picker = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
        var focused = gameController.Game.IngameState.FocusedInputElement;
        var depth = 0;
        for (var element = focused; element is not null && depth++ < 32; element = element.Parent)
        {
            if (element.Address == picker.Address)
            {
                return true;
            }
        }

        return false;
    }

    private bool TryFindExactVisibleOption(
        GameController gameController,
        out Vector2 center,
        out string failure)
    {
        center = default;
        if (!ValidateOpenPicker(gameController, out failure))
        {
            return false;
        }

        var picker = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
        var container = picker.OptionContainer?.GetClientRectCache ?? default;
        if (container.Width <= 0 || container.Height <= 0)
        {
            failure = "Picker option container geometry is invalid.";
            return false;
        }

        var matches = new List<Vector2>();
        foreach (var option in picker.Options)
        {
            if (option?.IsVisible != true ||
                !string.Equals(option.ItemType?.Metadata, _desiredCurrency!.Metadata, StringComparison.Ordinal))
            {
                continue;
            }

            var rect = option.GetClientRectCache;
            var optionCenter = new Vector2(rect.X + rect.Width / 2f, rect.Y + rect.Height / 2f);
            if (rect.Width > 0 && rect.Height > 0 &&
                optionCenter.X >= container.X && optionCenter.X <= container.X + container.Width &&
                optionCenter.Y >= container.Y && optionCenter.Y <= container.Y + container.Height)
            {
                matches.Add(optionCenter);
            }
        }

        if (matches.Count == 0)
        {
            failure = "No visible exact metadata option was found.";
            return false;
        }

        center = SelectDeterministicOptionCenter(matches);
        failure = string.Empty;
        return true;
    }

    public static Vector2 SelectDeterministicOptionCenter(IEnumerable<Vector2> exactMetadataMatches)
    {
        ArgumentNullException.ThrowIfNull(exactMetadataMatches);
        var matches = exactMetadataMatches
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .ToArray();
        return matches.Length == 0
            ? throw new ArgumentException("At least one exact metadata match is required.", nameof(exactMetadataMatches))
            : matches[0];
    }

    public static bool TryCreateStableHead(MarketCapture capture, out StableBookHead head, out string failure)
    {
        var marketRate = MarketCaptureNormalizer.MarketRate(capture);
        var immediate = capture.WantedItemStock.FirstOrDefault(level => level.Get > 0 && level.Give > 0);
        var competing = capture.OfferedItemStock.FirstOrDefault(level => level.Get > 0 && level.Give > 0);
        if (marketRate is null || immediate is null || competing is null)
        {
            head = default;
            var missing = new List<string>(3);
            if (marketRate is null) missing.Add("market rate");
            if (immediate is null) missing.Add("wanted/immediate positive head");
            if (competing is null) missing.Add("offered/competing positive head");
            failure = $"Normalized head unreadable for {capture.OfferedCurrency.Name} " +
                $"({capture.OfferedCurrency.Metadata}) -> {capture.WantedCurrency.Name} " +
                $"({capture.WantedCurrency.Metadata}); missing {string.Join(", ", missing)}; " +
                $"raw market={capture.MarketRateGet}:{capture.MarketRateGive}, " +
                $"wantedRows={capture.WantedItemStock.Count}, offeredRows={capture.OfferedItemStock.Count}.";
            return false;
        }

        head = new StableBookHead(
            capture.OfferedCurrency.Metadata,
            capture.WantedCurrency.Metadata,
            marketRate.Value,
            new Rational(immediate.Get, immediate.Give),
            new Rational(competing.Give, competing.Get),
            CreateLiquidityFingerprint(capture));
        failure = string.Empty;
        return true;
    }

    private static string CreateLiquidityFingerprint(MarketCapture capture)
    {
        var raw = string.Join("|", capture.WantedItemStock.Concat(capture.OfferedItemStock)
            .Select(level => $"{level.Side}:{level.Get}/{level.Give}:{level.ListedCount}"));
        var normalized = string.Join("|", MarketCaptureNormalizer.CreateEdges(capture)
            .OrderBy(edge => edge.SourceBook)
            .ThenBy(edge => edge.ExecutionIntent)
            .ThenBy(edge => edge.From.Metadata, StringComparer.Ordinal)
            .Select(edge => $"{edge.SourceBook}:{edge.ExecutionIntent}:{edge.From.Metadata}>{edge.To.Metadata}:" +
                $"{edge.Rate}:{edge.ImmediateInputDepth}:{edge.CompetingQueueAhead}"));
        return raw + "||" + normalized;
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

    private void SendShiftChord(Keys key)
    {
        PressKey(Keys.ShiftKey);
        try
        {
            TapKey(key);
        }
        finally
        {
            ReleaseKey(Keys.ShiftKey);
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

    private void BeginRelease(AutomatedProbeState targetState, string completedStatus)
    {
        _releaseTargetState = targetState;
        _releaseStatus = completedStatus;
        if (TryReleaseOwnedInput())
        {
            State = targetState;
            Status = completedStatus;
        }
        else
        {
            State = AutomatedProbeState.ReleasingInput;
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
                // Continue releasing every input; fail-closed state is set by the caller.
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
                // Retain ownership so a later cancellation can retry release.
            }
        }

        return _ownedKeys.Count == 0 && !_mouseDown;
    }

    private static bool ModifiersHeld() =>
        ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu);

    private static bool CanType(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or ' ' or '\'' or '-' or '"';

    private static Keys KeyFor(char character) => character switch
    {
        >= 'a' and <= 'z' => (Keys)((int)Keys.A + (character - 'a')),
        >= '0' and <= '9' => (Keys)((int)Keys.D0 + (character - '0')),
        ' ' => Keys.Space,
        '\'' => Keys.Oem7,
        '-' => Keys.OemMinus,
        _ => throw new ArgumentOutOfRangeException(nameof(character))
    };
}
