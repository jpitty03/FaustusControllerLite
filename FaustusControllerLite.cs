using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FaustusControllerLite.Core;
using FaustusControllerLite.Domain;
using FaustusControllerLite.Input;
using FaustusControllerLite.Orders;
using FaustusControllerLite.Persistence;
using FaustusControllerLite.Probing;
using System.Numerics;
using System.Windows.Forms;
using ExileInput = ExileCore.Input;

namespace FaustusControllerLite;

public sealed class FaustusControllerLite : BaseSettingsPlugin<FaustusControllerLiteSettings>
{
    private readonly CurrencyCatalogueBuilder _catalogueBuilder = new();
    private readonly LatestRateStore _rateStore = new();
    private readonly PickerCalibrationStore _pickerCalibrationStore = new();
    private readonly AutomatedProbeController _automatedProbe = new();
    private readonly AutomatedProbeController _placementLegRefresh = new();
    private readonly SingleLegStagingController _singleLegStaging = new();
    private readonly SingleLegPlacementController _singleLegPlacement = new();
    private readonly TrackedOrderCollectionController _trackedCollection = new();
    private readonly InventoryStashTransferController _inventoryStashTransfer = new();
    private readonly TrackedOrderCancellationController _trackedCancellation = new();
    private readonly CanceledReturnCollectionController _canceledReturnCollection = new();
    private readonly AutomatedProbeController _collectionOwnershipSelector = new();
    private readonly Dictionary<string, OwnershipObservation> _liveOwnedByMetadata = new(StringComparer.Ordinal);
    private CurrencyCatalogue? _catalogue;
    private BankrollStore? _bankrollStore;
    private TrackedOrderStore? _trackedOrderStore;
    private BankrollState _bankroll = BankrollState.Uninitialized;
    private bool _freshStateResetArmed;
    private DateTimeOffset _freshStateResetArmExpiresAtUtc;
    private DateTimeOffset _nextCatalogueAttemptUtc;
    private Guid _manualProbeSessionId = Guid.NewGuid();
    private string _latestRatePath = string.Empty;
    private string _diagnosticPath = string.Empty;
    private string _pickerCalibrationPath = string.Empty;
    private PickerCalibration _pickerCalibration = new();
    private CalibrationObservation? _calibrationObservation;
    private bool _latestRateCacheAvailable = true;
    private string _observedTargetLabel = string.Empty;
    private string _catalogueStatus = "Waiting for Currency Exchange catalogue.";
    private string _operationStatus = "Idle (Milestone 9 full-workflow orchestration available; all input remains permission-gated).";
    private string _lastFailure = "None";
    private string _lastCandidate = "None; capture all three markets in one area/session.";
    private string _trackedOrder = "None";
    private TrackedOrderState? _trackedOrderState;
    private bool _trackedOrderLoadBlocked;
    private bool _bankrollLoadBlocked;
    private RouteCandidate? _selectedCandidate;
    private SingleLegStagingState _lastObservedStagingState = SingleLegStagingState.Idle;
    private PlacementPreparationState _placementPreparation;
    private PlacementPreparationToken? _placementToken;
    private int _placementRefreshAttempts;
    private CollectionFlowState _collectionFlow;
    private long _collectionOwnedBaseline;
    private long _collectionBatchAmount;
    private DateTimeOffset _nextLifecyclePollAtUtc;
    private DateTimeOffset _collectionOwnershipPhaseStartedAtUtc;
    private string _collectionOwnershipMetadata = string.Empty;
    private string _stashTransferMetadata = string.Empty;
    private long _stashTransferAmount;
    private bool _fullWorkflowAuthorized;
    private bool _startingNewWorkflow;
    private DateTimeOffset? _nextWorkflowScanAtUtc;
    private PermissionSnapshot? _workflowAuthorization;
    private RouteLegResult? _workflowPreparedLeg;
    private string? _lastLoggedFailure;
    private ContinuousLoopAction? _lastLoopAction;
    private DateTimeOffset _nextLoopHeartbeatUtc;
    private DateTimeOffset? _stalePlacementLatchSinceUtc;

    private enum CollectionFlowState
    {
        Idle,
        ReadingBaseline,
        ClickingTrackedOrder,
        ReadingAfter,
        ReadingStashBaseline,
        TransferringToStash,
        ReadingStashAfter,
        ReadingStashRecovery,
        ReadingCanceledReturnBaseline,
        CollectingCanceledReturn,
        ReadingCanceledReturnAfter,
    }

    private sealed record PlacementPreparationToken(
        Guid ProbeSessionId,
        string CandidateSignature,
        string TargetMetadata,
        long MinimumProfitChaos,
        CurrencyIdentity From,
        CurrencyIdentity To,
        QuoteExecutionIntent ExecutionIntent,
        Rational Rate,
        long InputSpent,
        long Output,
        DateTimeOffset ExpiresAtUtc);

    private enum PlacementPreparationState
    {
        Idle,
        Probing,
        RefreshingFirstLeg,
        Restaging,
        Placing,
    }

    private sealed record OwnershipObservation(
        long Count,
        DateTimeOffset ObservedAtUtc,
        int AreaInstanceId,
        int StableReads);
    private sealed record CalibrationObservation(
        Vector2 Cursor,
        float PanelX,
        float PanelY,
        float PanelWidth,
        float PanelHeight,
        string League,
        int AreaInstanceId,
        DateTimeOffset Deadline);

    public override bool Initialise()
    {
        Name = nameof(FaustusControllerLite);
        var persistenceDirectory = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite));
        _bankrollStore = new BankrollStore(persistenceDirectory);
        _trackedOrderStore = new TrackedOrderStore(persistenceDirectory);
        _latestRatePath = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "latest-rates.json");
        _diagnosticPath = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "sdk-diagnostic.txt");
        _pickerCalibrationPath = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "picker-calibration.json");
        Settings.ArmFreshStateReset.OnPressed += ArmFreshStateReset;
        Settings.ApplyArmedFreshStateReset.OnPressed += ApplyArmedFreshStateReset;
        try
        {
            _rateStore.Load(_latestRatePath);
        }
        catch (Exception exception)
        {
            _latestRateCacheAvailable = false;
            _lastFailure = $"Latest-rate cache load failed; evidence retained: {exception.Message}";
        }
        try
        {
            _pickerCalibration = _pickerCalibrationStore.Load(_pickerCalibrationPath);
        }
        catch (Exception exception)
        {
            _pickerCalibration = new PickerCalibration();
            _lastFailure = $"Picker calibration load failed; recalibration required: {exception.Message}";
        }
        LoadBankrollForCurrentLeague();
        LoadTrackedOrderForCurrentLeague();
        return true;
    }

    public override Job Tick()
    {
        if (_freshStateResetArmed && DateTimeOffset.UtcNow > _freshStateResetArmExpiresAtUtc)
        {
            _freshStateResetArmed = false;
            _operationStatus = "Fresh-state reset arm expired without changing any durable state.";
        }

        if (_catalogue == null && DateTimeOffset.UtcNow >= _nextCatalogueAttemptUtc)
        {
            RefreshCatalogue();
            _nextCatalogueAttemptUtc = DateTimeOffset.UtcNow.AddSeconds(1);
        }


        if (_catalogue != null && !string.Equals(_observedTargetLabel, Settings.TargetCurrency.Value, StringComparison.Ordinal))
        {
            PersistTargetSelection();
        }

        ObservePickerOwnership();
        ObservePickerCalibration();
        PollTrackedOrderLifecycle();

        if (Settings.CalibratePickerButtonHotkey.PressedOnce())
        {
            if (TryGetHotkeyConflict(out var conflict))
            {
                _lastFailure = conflict;
            }
            else if (IsCollectionFlowActive())
            {
                AbortCollectionFlow("Picker calibration interrupted tracked-order collection.");
            }
            else if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Calibration hotkey interrupted placement preparation.");
            }
            else if (_singleLegStaging.IsRunning)
            {
                _singleLegStaging.Cancel("Calibration hotkey interrupted single-leg staging.");
            }
            else if (_automatedProbe.IsRunning)
            {
                _automatedProbe.Cancel("Calibration hotkey interrupted the automated probe.");
            }
            else
            {
                ArmPickerCalibration();
            }
        }

        if (Settings.CalibratePlaceOrderHotkey.PressedOnce())
        {
            if (TryGetHotkeyConflict(out var conflict))
            {
                _lastFailure = conflict;
            }
            else if (IsCollectionFlowActive())
            {
                AbortCollectionFlow("Place Order calibration interrupted tracked-order collection.");
            }
            else if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Place Order calibration interrupted placement preparation.");
            }
            else if (_automatedProbe.IsRunning || _singleLegStaging.IsRunning || _singleLegPlacement.IsRunning)
            {
                _lastFailure = "Place Order calibration is blocked while another input operation is active.";
            }
            else
            {
                CalibratePlaceOrderTarget();
            }
        }
        if (Settings.CalibrateCollectionHotkey.PressedOnce())
        {
            CalibrateTrackedCollectionSlot();
        }
        if (Settings.CalibrateCancelHotkey.PressedOnce())
        {
            CalibrateTrackedCancelButton();
        }
        if (Settings.CalibrateReturnSlotHotkey.PressedOnce())
        {
            CalibrateCanceledReturnSlot();
        }

        if (Settings.CaptureCurrentPairHotkey.PressedOnce())
        {
            if (IsCollectionFlowActive())
            {
                AbortCollectionFlow("Manual capture interrupted tracked-order collection.");
            }
            else if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Manual capture hotkey interrupted placement preparation.");
            }
            else if (_singleLegStaging.IsRunning)
            {
                _singleLegStaging.Cancel("Manual capture hotkey interrupted single-leg staging.");
            }
            else if (_automatedProbe.IsRunning)
            {
                _automatedProbe.Cancel("Manual capture hotkey interrupted the automated probe.");
            }
            else
            {
                CaptureCurrentPair();
            }
        }

        if (Settings.DumpSdkReadsHotkey.PressedOnce())
        {
            DumpSdkReads();
        }

        if (Settings.ProbeMarketsHotkey.PressedOnce())
        {
            if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Probe hotkey interrupted placement preparation.");
            }
            else if (_singleLegStaging.IsRunning)
            {
                _singleLegStaging.Cancel("Probe hotkey interrupted single-leg staging.");
            }
            else if (_automatedProbe.IsRunning)
            {
                _automatedProbe.Cancel("Probe hotkey requested cancellation.");
            }
            else
            {
                StartAutomatedProbe();
            }
        }

        if (Settings.ExecuteSingleLegHotkey.PressedOnce())
        {
            if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Staging hotkey interrupted placement preparation.");
            }
            else if (_singleLegStaging.IsRunning)
            {
                _singleLegStaging.Cancel("Single-leg hotkey requested cancellation.");
            }
            else
            {
                StartSingleLegStaging();
            }
        }


        if (Settings.PlaceStagedLegHotkey.PressedOnce())
        {
            HandlePlaceStagedLegHotkey();
        }
        if (Settings.CollectTrackedOrderHotkey.PressedOnce())
        {
            HandleCollectTrackedOrderHotkey();
        }
        if (Settings.StashCollectedCurrencyHotkey.PressedOnce())
        {
            HandleStashCollectedCurrencyHotkey();
        }
        if (Settings.CancelTimedOutOrderHotkey.PressedOnce())
        {
            HandleCancelTimedOutOrderHotkey();
        }
        if (Settings.AdoptPendingOrderHotkey.PressedOnce())
        {
            AdoptUniquePendingOrderForLifecycle();
        }

        if (Settings.FullWorkflowHotkey.PressedOnce())
        {
            HandleFullWorkflowHotkey();
        }
        ValidateWorkflowAuthorizationBeforeInput();
        if (IsCollectionFlowActive() &&
            (!(IsStashTransferFlow()
                ? StashTransferInputPermissions.From(Settings, _fullWorkflowAuthorized).Ready
                : CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized).Ready) ||
             !Settings.AllowQueryInput.Value))
        {
            AbortCollectionFlow("Collection/query permission changed during tracked-order collection.");
        }

        if (_placementPreparation is PlacementPreparationState.Probing or
                PlacementPreparationState.RefreshingFirstLeg or PlacementPreparationState.Restaging &&
            (!Settings.AllowOrderPlacement.Value || Settings.AllowFullWorkflow.Value && !_fullWorkflowAuthorized))
        {
            AbortPlacementFlow("Placement/full-workflow permission changed during the authorized one-press sequence.");
        }

        _automatedProbe.Tick(
            GameController,
            _pickerCalibration,
            ProbeInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value,
            Settings.StableRateSampleCount.Value);
        SynchronizeAutomatedProbeStatus();
        _placementLegRefresh.Tick(
            GameController,
            _pickerCalibration,
            ProbeInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value,
            Settings.StableRateSampleCount.Value);
        SynchronizePlacementLegRefresh();
        _singleLegStaging.Tick(
            GameController,
            _pickerCalibration,
            StagingInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value,
            Settings.StableRateSampleCount.Value);
        SynchronizeSingleLegStagingStatus();
        _singleLegPlacement.Tick(
            GameController,
            _pickerCalibration,
            PlacementInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value);
        SynchronizeSingleLegPlacementStatus();
        _collectionOwnershipSelector.Tick(
            GameController,
            _pickerCalibration,
            new ProbeInputPermissions(
                true,
                Settings.AllowVerifiedMouseMovement.Value,
                Settings.AllowVerifiedClicks.Value,
                Settings.AllowQueryInput.Value),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value,
            stableSampleCount: 1);
        SynchronizeCollectionOwnershipRead();
        _trackedCollection.Tick(
            GameController,
            _pickerCalibration,
            CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled());
        SynchronizeTrackedCollection();
        _inventoryStashTransfer.Tick(
            GameController,
            StashTransferInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled());
        SynchronizeInventoryStashTransfer();
        _trackedCancellation.Tick(
            GameController,
            _pickerCalibration,
            CancellationInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value);
        SynchronizeTrackedCancellation();
        _canceledReturnCollection.Tick(
            GameController,
            _pickerCalibration,
            CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value);
        SynchronizeCanceledReturnCollection();
        DriveFullWorkflow();
        AppendFailureDiagnosticIfNeeded();

        return base.Tick();
    }

    public override void OnUnload()
    {
        _fullWorkflowAuthorized = false;
        _startingNewWorkflow = false;
        _nextWorkflowScanAtUtc = null;
        _automatedProbe.Cancel("Plugin unloading during probing.");
        _placementLegRefresh.Cancel("Plugin unloading during leg refresh.");
        _singleLegStaging.Cancel("Plugin unloading during staging.");
        _singleLegPlacement.EmergencyStop("Plugin unloading during placement.");
        _collectionOwnershipSelector.Cancel("Plugin unloading during ownership observation.");
        _trackedCancellation.EmergencyStop("Plugin unloading during cancellation.");
        _canceledReturnCollection.EmergencyStop("Plugin unloading during canceled return collection.");
        _inventoryStashTransfer.EmergencyStop("Plugin unloading during inventory-to-stash transfer.");
        _trackedCollection.EmergencyStop("Plugin unloading during tracked order collection.");
        base.OnUnload();
    }

    public override void OnPluginDestroyForHotReload()
    {
        _fullWorkflowAuthorized = false;
        _startingNewWorkflow = false;
        _nextWorkflowScanAtUtc = null;
        _automatedProbe.Cancel("Plugin hot reload during probing.");
        _placementLegRefresh.Cancel("Plugin hot reload during leg refresh.");
        _singleLegStaging.Cancel("Plugin hot reload during staging.");
        _singleLegPlacement.EmergencyStop("Plugin hot reload during placement.");
        _collectionOwnershipSelector.Cancel("Plugin hot reload during ownership observation.");
        _trackedCancellation.EmergencyStop("Plugin hot reload during cancellation.");
        _canceledReturnCollection.EmergencyStop("Plugin hot reload during canceled return collection.");
        _inventoryStashTransfer.EmergencyStop("Plugin hot reload during inventory-to-stash transfer.");
        _trackedCollection.EmergencyStop("Plugin hot reload during tracked order collection.");
        base.OnPluginDestroyForHotReload();
    }

    public override void AreaChange(AreaInstance area)
    {
        var wasAuthorized = _fullWorkflowAuthorized;
        _automatedProbe.Cancel("Area changed.");
        _placementLegRefresh.Cancel("Area changed.");
        _singleLegStaging.Invalidate("Area changed.");
        _singleLegPlacement.Cancel("Area changed.");
        _trackedCollection.Cancel("Area changed.");
        _inventoryStashTransfer.Cancel("Area changed.");
        _trackedCancellation.Cancel("Area changed.");
        _canceledReturnCollection.Cancel("Area changed.");
        _collectionOwnershipSelector.Cancel("Area changed.");
        _collectionFlow = CollectionFlowState.Idle;
        _fullWorkflowAuthorized = false;
        _workflowAuthorization = null;
        _workflowPreparedLeg = null;
        _startingNewWorkflow = false;
        _nextWorkflowScanAtUtc = null;
        _placementPreparation = PlacementPreparationState.Idle;
        _placementToken = null;
        _lastObservedStagingState = _singleLegStaging.State;
        _calibrationObservation = null;
        _manualProbeSessionId = Guid.NewGuid();
        _liveOwnedByMetadata.Clear();
        _selectedCandidate = null;
        LoadBankrollForCurrentLeague();
        LoadTrackedOrderForCurrentLeague();
        _operationStatus = "Area changed; manual probe session reset and cached rates retained.";
        _lastCandidate = "None; captures from the prior area cannot form a coherent matrix.";
        if (wasAuthorized)
        {
            RecordContinuousAuthorizationRevoked(
                "Area changed; continuous trading stopped locally. Any server-side order remains tracked " +
                "and the workflow hotkey must be pressed again.");
        }
    }

    public override void Render()
    {
        var panelVisible = false;
        try
        {
            panelVisible = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel.IsVisible;
        }
        catch (Exception exception)
        {
            _lastFailure = $"Panel visibility read failed: {exception.Message}";
        }

        var x = 100f;
        var y = 100f;
        DrawStatus("FaustusControllerLite - Milestone 9 full workflow", ref y, SharpDX.Color.Cyan);
        DrawStatus($"Exchange panel: {(panelVisible ? "visible" : "closed")}", ref y, SharpDX.Color.White);
        DrawStatus($"Catalogue: {_catalogueStatus}", ref y, _catalogue == null ? SharpDX.Color.Orange : SharpDX.Color.LimeGreen);
        DrawStatus($"Target: {Settings.TargetCurrencyDisplayName} | {Settings.TargetCurrencyMetadata}", ref y, SharpDX.Color.White);
        DrawStatus($"Operation: {_operationStatus}", ref y, SharpDX.Color.White);
        DrawStatus($"Last failure: {_lastFailure}", ref y, _lastFailure == "None" ? SharpDX.Color.Gray : SharpDX.Color.OrangeRed);
        DrawStatus($"Bankroll: {DescribeBankroll()}", ref y, _bankroll.IsInitialized ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
        DrawStatus($"Latest rates: {_rateStore.Captures.Count} canonical league/pair records", ref y, SharpDX.Color.White);
        DrawStatus($"Picker calibration: {DescribePickerCalibration()}", ref y,
            _pickerCalibration.IsComplete ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
        DrawStatus($"Place Order calibration: {(_pickerCalibration.IsPlacementComplete ? "ready" : "missing")}", ref y,
            _pickerCalibration.IsPlacementComplete ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
        DrawStatus($"Collection calibration: {(_pickerCalibration.IsCollectionComplete ? "ready" : "missing")}", ref y,
            _pickerCalibration.IsCollectionComplete ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
        DrawStatus($"Probe state: {_automatedProbe.State} | {_automatedProbe.Status}", ref y,
            _automatedProbe.IsRunning ? SharpDX.Color.Cyan : SharpDX.Color.Gray);
        DrawStatus($"Staging state: {_singleLegStaging.State} | {_singleLegStaging.Status}", ref y,
            _singleLegStaging.IsRunning ? SharpDX.Color.Cyan : SharpDX.Color.Gray);
        DrawStatus($"Placement: {_placementPreparation}/{_singleLegPlacement.State} | {_singleLegPlacement.Status}", ref y,
            _singleLegPlacement.IsRunning ? SharpDX.Color.Orange : SharpDX.Color.Gray);
        DrawStatus($"Collection: {_collectionFlow}/{_trackedCollection.State} | {_trackedCollection.Status}", ref y,
            _trackedCollection.IsRunning ? SharpDX.Color.Orange : SharpDX.Color.Gray);
        DrawStatus($"Stash transfer: {_inventoryStashTransfer.State} | {_inventoryStashTransfer.Status}", ref y,
            _inventoryStashTransfer.IsRunning ? SharpDX.Color.Orange : SharpDX.Color.Gray);
        DrawStatus($"Cancellation: {_trackedCancellation.State} | {_trackedCancellation.Status}", ref y,
            _trackedCancellation.IsRunning ? SharpDX.Color.Orange : SharpDX.Color.Gray);
        DrawStatus($"Canceled return: {_canceledReturnCollection.State} | {_canceledReturnCollection.Status}", ref y,
            _canceledReturnCollection.IsRunning ? SharpDX.Color.Orange : SharpDX.Color.Gray);
        DrawStatus($"Workflow: {DescribeWorkflow()}", ref y,
            _bankroll.Workflow?.IsActive == true ? SharpDX.Color.Cyan : SharpDX.Color.Gray);
        DrawStatus($"Continuous trading: {DescribeContinuousScan()}", ref y,
            _fullWorkflowAuthorized ? SharpDX.Color.Cyan : SharpDX.Color.Gray);
        DrawStatus($"Tracked order: {_trackedOrder}", ref y, SharpDX.Color.Gray);
        DrawStatus($"Last candidate path: {_lastCandidate}", ref y, SharpDX.Color.Gray);
        DrawStatus($"Fresh-state reset armed: {(_freshStateResetArmed ? "YES (apply within 10 seconds)" : "no")}", ref y, _freshStateResetArmed ? SharpDX.Color.OrangeRed : SharpDX.Color.Gray);

        void DrawStatus(string text, ref float currentY, SharpDX.Color color)
        {
            Graphics.DrawText(text, new Vector2(x, currentY), color);
            currentY += 20f;
        }
    }

    private void RefreshCatalogue()
    {
        try
        {
            if (!_catalogueBuilder.TryBuild(GameController, out var catalogue, out var failure))
            {
                _catalogueStatus = failure;
                return;
            }

            var loadedCatalogue = catalogue!;
            _catalogue = loadedCatalogue;
            Settings.TargetCurrency.Values = loadedCatalogue.Items.Select(item => item.Name).ToList();
            ResolvePersistedTarget();
            _catalogueStatus = $"ready ({loadedCatalogue.Items.Count} currencies)";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _catalogueStatus = "failed to load";
            _lastFailure = $"Catalogue load failed: {exception.Message}";
        }
    }

    private void ResolvePersistedTarget()
    {
        if (_catalogue == null)
        {
            return;
        }

        CurrencyIdentity? target = null;
        if (!string.IsNullOrWhiteSpace(Settings.TargetCurrencyMetadata))
        {
            _catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out target);
        }

        if (target == null)
        {
            _catalogue.TryGetUniqueByName(Settings.TargetCurrencyDisplayName, out target);
        }

        if (target == null)
        {
            _catalogue.TryGetUniqueByName("Orb of Alteration", out target);
        }

        if (target == null)
        {
            _lastFailure = "Orb of Alteration was not uniquely present in the exchange catalogue.";
            return;
        }

        Settings.TargetCurrency.Value = target.Name;
        _observedTargetLabel = target.Name;
        Settings.TargetCurrencyDisplayName = target.Name;
        Settings.TargetCurrencyMetadata = target.Metadata;
        _observedTargetLabel = target.Name;
    }

    private void PersistTargetSelection()
    {
        if (_fullWorkflowAuthorized)
        {
            StopFullWorkflowLocal("Target currency changed; local workflow authorization was revoked.");
        }
        if (IsPlacementFlowActive())
        {
            AbortPlacementFlow("Target currency changed during placement preparation.");
        }
        if (IsCollectionFlowActive())
        {
            AbortCollectionFlow("Target currency changed during tracked-order collection.");
        }

        if (_catalogue == null || !_catalogue.TryGetByLabel(Settings.TargetCurrency.Value, out var target) || target == null)
        {
            return;
        }

        Settings.TargetCurrencyDisplayName = target.Name;
        Settings.TargetCurrencyMetadata = target.Metadata;
        _operationStatus = $"Selected target {target.Name}; exact metadata stored.";
        _observedTargetLabel = target.Name;
        _manualProbeSessionId = Guid.NewGuid();
        _selectedCandidate = null;
        if (_automatedProbe.IsRunning)
        {
            _automatedProbe.Cancel("Target currency changed.");
        }
        if (_singleLegStaging.IsRunning)
        {
            _singleLegStaging.Cancel("Target currency changed.");
        }
        _lastCandidate = "None; target changed, so a new three-market session is required.";
    }

    private void CaptureCurrentPair()
    {
        if (!_latestRateCacheAvailable)
        {
            _lastFailure = "Latest-rate cache is blocked after a load failure; preserve or repair the file, then reload Lite.";
            _operationStatus = "Read-only capture blocked without reading or changing the cache.";
            return;
        }

        if (!CurrentMarketReader.TryCapture(GameController, _manualProbeSessionId, out var capture, out var failure))
        {
            _lastFailure = failure;
            _operationStatus = "Read-only capture stopped without changing the cache.";
            return;
        }

        try
        {
            var replaced = _rateStore.Store(capture!);
            _rateStore.Save(_latestRatePath);
            _operationStatus = $"Captured {capture!.OfferedCurrency.Name}/{capture.WantedCurrency.Name}; " +
                $"canonical pair {(replaced ? "overwritten" : "created")} ({_rateStore.Captures.Count} total).";
            _lastFailure = "None";
            CalculateCandidate();
        }
        catch (Exception exception)
        {
            _lastFailure = $"Capture persistence failed: {exception.Message}";
            _operationStatus = "Capture read succeeded but persistence did not complete.";
        }
    }

    private void DumpSdkReads()
    {
        try
        {
            var diagnostic = SdkDiagnosticProbe.Read(GameController, _catalogue);
            var directory = Path.GetDirectoryName(_diagnosticPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _diagnosticPath + ".tmp";
            File.WriteAllText(temporaryPath, diagnostic.Report);
            File.Move(temporaryPath, _diagnosticPath, overwrite: true);
            _operationStatus = $"SDK diagnostic: {diagnostic.Summary}; wrote {_diagnosticPath}";
            _lastFailure = diagnostic.IssueCount == 0 ? "None" : diagnostic.Summary;
        }
        catch (Exception exception)
        {
            _lastFailure = $"SDK diagnostic failed: {exception.Message}";
        }
    }

    private void ArmPickerCalibration()
    {
        try
        {
            var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            var cursor = ExileInput.MousePositionNum;
            var rect = panel.GetClientRectCache;
            if (!GameController.Window.IsForeground() || !panel.IsVisible || panel.CurrencyPicker.IsVisible ||
                rect.Width <= 0 || rect.Height <= 0 ||
                cursor.X < rect.X || cursor.X > rect.X + rect.Width ||
                cursor.Y < rect.Y || cursor.Y > rect.Y + rect.Height ||
                ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) ||
                ExileInput.IsKeyDown(Keys.Menu))
            {
                _lastFailure = "Calibration requires foreground Path of Exile, visible exchange, closed picker, cursor over the intended button, and no held modifiers.";
                return;
            }

            _calibrationObservation = new CalibrationObservation(
                cursor,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height,
                GetCurrentLeague(),
                GameController.Game.IngameState.ServerData.InstanceId,
                DateTimeOffset.UtcNow.AddSeconds(5));
            _operationStatus = "Calibration armed: manually click the picker button under the cursor within 5 seconds.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _calibrationObservation = null;
            _lastFailure = $"Calibration arm failed: {exception.Message}";
        }
    }

    private void ObservePickerCalibration()
    {
        if (_calibrationObservation is not { } observation)
        {
            return;
        }

        try
        {
            var now = DateTimeOffset.UtcNow;
            var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            var rect = panel.GetClientRectCache;
            if (now > observation.Deadline || !GameController.Window.IsForeground() || !panel.IsVisible ||
                !string.Equals(GetCurrentLeague(), observation.League, StringComparison.Ordinal) ||
                GameController.Game.IngameState.ServerData.InstanceId != observation.AreaInstanceId ||
                Vector2.Distance(ExileInput.MousePositionNum, observation.Cursor) > 8f ||
                Math.Abs(rect.X - observation.PanelX) > 5f || Math.Abs(rect.Y - observation.PanelY) > 5f ||
                Math.Abs(rect.Width - observation.PanelWidth) > 5f || Math.Abs(rect.Height - observation.PanelHeight) > 5f)
            {
                _calibrationObservation = null;
                _lastFailure = "Picker calibration observation expired or lost its live UI context.";
                return;
            }

            var picker = panel.CurrencyPicker;
            if (!picker.IsVisible)
            {
                return;
            }

            var candidate = new PickerCalibration
            {
                OfferedButton = _pickerCalibration.OfferedButton,
                WantedButton = _pickerCalibration.WantedButton,
                PlaceOrderButton = _pickerCalibration.PlaceOrderButton,
                PlaceOrderPanelAspectRatio = _pickerCalibration.PlaceOrderPanelAspectRatio,
                CollectionSlotOffset = _pickerCalibration.CollectionSlotOffset,
                CollectionRowAspectRatio = _pickerCalibration.CollectionRowAspectRatio,
                CancelButtonOffset = _pickerCalibration.CancelButtonOffset,
                CancelRowAspectRatio = _pickerCalibration.CancelRowAspectRatio,
                ReturnSlotOffset = _pickerCalibration.ReturnSlotOffset,
                ReturnRowAspectRatio = _pickerCalibration.ReturnRowAspectRatio
            };
            if (!candidate.TryRecord(
                    picker.IsPickingWantedCurrency,
                    observation.PanelX,
                    observation.PanelY,
                    observation.PanelWidth,
                    observation.PanelHeight,
                    observation.Cursor.X,
                    observation.Cursor.Y,
                    out var failure))
            {
                _calibrationObservation = null;
                _lastFailure = failure;
                return;
            }

            _pickerCalibrationStore.Save(_pickerCalibrationPath, candidate);
            _pickerCalibration = candidate;
            _calibrationObservation = null;
            _operationStatus = $"Recorded normalized {(picker.IsPickingWantedCurrency ? "wanted" : "offered")} picker button calibration.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _calibrationObservation = null;
            _lastFailure = $"Picker calibration failed: {exception.Message}";
        }
    }

    private void StartAutomatedProbe()
    {
        if (_trackedCancellation.IsRunning)
        {
            _lastFailure = "Automated probing is blocked while cancellation is active.";
            return;
        }
        if (IsCollectionFlowActive())
        {
            _lastFailure = "Automated probing is blocked while tracked-order collection is active.";
            return;
        }
        if (TryGetHotkeyConflict(out var hotkeyConflict))
        {
            _lastFailure = hotkeyConflict;
            return;
        }

        if (!_latestRateCacheAvailable)
        {
            _lastFailure = "Automated probing is blocked because the latest-rate cache failed to load.";
            return;
        }

        if (_catalogue is null ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null ||
            !_catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out var target) || target is null)
        {
            _lastFailure = "Automated probing requires a ready catalogue and exact Chaos, Divine, and target identities.";
            return;
        }

        _calibrationObservation = null;
        if (!_automatedProbe.Start(
                GameController,
                chaos,
                divine,
                target,
                _pickerCalibration,
                ProbeInputPermissions.From(Settings),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                out var failure))
        {
            _lastFailure = failure;
            _operationStatus = "Automated probe did not start; no input was sent.";
            return;
        }

        _operationStatus = "Automated three-market probe started; press the probe hotkey again to cancel.";
        _manualProbeSessionId = _automatedProbe.SessionId;
        _selectedCandidate = null;
        _lastCandidate = "None; a new automated probe session invalidated the prior candidate.";
        _lastFailure = "None";
    }

    private void SynchronizeAutomatedProbeStatus()
    {
        if (_automatedProbe.State == AutomatedProbeState.Completed)
        {
            var captures = _automatedProbe.CompletedCaptures.ToArray();
            var restageForPlacement = false;
            try
            {
                _rateStore.StoreBatchAtomically(_latestRatePath, captures);
                _manualProbeSessionId = captures[0].SessionId;
                _operationStatus = $"Atomically published automated probe session {_manualProbeSessionId:D}; three canonical pairs replaced.";
                _lastFailure = "None";
                var outcome = CalculateCandidate();
                restageForPlacement = _placementPreparation == PlacementPreparationState.Probing;
                if (restageForPlacement && _fullWorkflowAuthorized)
                {
                    var preparation = PrepareWorkflowAfterFullProbe(outcome);
                    if (preparation != WorkflowPreparationResult.Accepted)
                    {
                        restageForPlacement = false;
                        _placementPreparation = PlacementPreparationState.Idle;
                        _placementToken = null;
                        _workflowPreparedLeg = null;
                        if (ContinuousWorkflowLoop.IsRetryable(preparation))
                        {
                            ScheduleContinuousScanRetry();
                        }
                        else
                        {
                            _fullWorkflowAuthorized = false;
                            _workflowAuthorization = null;
                            _startingNewWorkflow = false;
                            _nextWorkflowScanAtUtc = null;
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                _lastFailure = $"Automated probe publication failed; prior cache retained: {exception.Message}";
                _operationStatus = "Probe completed but no partial snapshot was published.";
                _placementPreparation = PlacementPreparationState.Idle;
            }
            finally
            {
                _automatedProbe.AcknowledgeCompletion();
            }

            if (restageForPlacement)
            {
                StartPlacementLegRefresh();
            }

            return;
        }

        if (_automatedProbe.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed or
                AutomatedProbeState.ReleasingInput &&
            !string.IsNullOrWhiteSpace(_automatedProbe.Failure))
        {
            _lastFailure = _automatedProbe.Failure;
            _operationStatus = _automatedProbe.Status;
            if (_placementPreparation == PlacementPreparationState.Probing)
            {
                _placementPreparation = PlacementPreparationState.Idle;
                _placementToken = null;
                if (_fullWorkflowAuthorized)
                {
                    _fullWorkflowAuthorized = false;
                    _workflowAuthorization = null;
                    _startingNewWorkflow = false;
                }
            }
            if (_automatedProbe.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed)
            {
                _automatedProbe.AcknowledgeCompletion();
            }
        }
    }

    private WorkflowPreparationResult PrepareWorkflowAfterFullProbe(CandidateOutcome outcome)
    {
        if (_bankrollStore is null || !_fullWorkflowAuthorized)
        {
            _lastFailure = "Workflow probe completed without canonical authorization/store state.";
            return WorkflowPreparationResult.Failed;
        }
        if (_startingNewWorkflow)
        {
            var candidate = _selectedCandidate;
            var classification = ContinuousWorkflowLoop.ClassifyNewWorkflowProbe(outcome, candidate is not null);
            if (classification == WorkflowPreparationResult.NoCandidate)
            {
                _lastFailure = "None";
                return WorkflowPreparationResult.NoCandidate;
            }
            if (classification != WorkflowPreparationResult.Accepted || candidate is null)
            {
                _lastFailure = $"Fresh three-market probe could not produce an accepted full-workflow candidate: {_lastCandidate}";
                return WorkflowPreparationResult.Failed;
            }
            if (!TryValidateWorkflowInventoryCapacity(
                    candidate.Legs.Select(leg =>
                        (leg.Edge.From.Metadata, leg.Edge.To.Metadata, leg.InputSpent, leg.Output)),
                    out var capacityFailure))
            {
                _lastFailure = capacityFailure;
                return WorkflowPreparationResult.Failed;
            }
            try
            {
                var next = CloneBankroll(_bankroll);
                next.Workflow = WorkflowCoordinator.Create(
                    next.League, candidate, _manualProbeSessionId, DateTimeOffset.UtcNow);
                next.UpdatedAtUtc = DateTimeOffset.UtcNow;
                _bankrollStore.Save(next);
                _bankroll = next;
                _workflowPreparedLeg = candidate.Legs[0];
                _startingNewWorkflow = false;
                _nextWorkflowScanAtUtc = null;
                _operationStatus = $"Persisted workflow {next.Workflow.WorkflowId:D} with {candidate.Legs.Count} exact legs before placement.";
                return WorkflowPreparationResult.Accepted;
            }
            catch (Exception exception)
            {
                _lastFailure = $"Could not persist exact workflow plan: {exception.Message}";
                return WorkflowPreparationResult.Failed;
            }
        }

        // Later-leg revalidation is never no-trade scanning: a failed replan stops and requires reauthorization.
        return TryRefreshWorkflowPlan(out _lastFailure)
            ? WorkflowPreparationResult.Accepted
            : WorkflowPreparationResult.Failed;
    }

    private void ScheduleContinuousScanRetry()
    {
        var seconds = ContinuousWorkflowLoop.ResolveRetrySeconds(
            Settings.ContinuousWorkflowRetrySeconds.Value,
            Random.Shared.Next(ContinuousWorkflowLoop.MinimumJitterSeconds, ContinuousWorkflowLoop.MaximumJitterSeconds + 1));
        _nextWorkflowScanAtUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
        _startingNewWorkflow = true;
        _lastFailure = "None";
        _operationStatus = $"No accepted route this scan ({_lastCandidate}); reprobing in {seconds}s.";
    }

    private bool TryRefreshWorkflowPlan(out string failure)
    {
        var workflow = _bankroll.Workflow;
        if (workflow?.Phase != WorkflowExecutionPhase.ReadyForLeg || _bankrollStore is null)
        {
            failure = "Canonical workflow is not ready for a fresh leg plan.";
            return false;
        }
        if (!TryBuildCurrentQuoteMatrix(out var matrix, out failure) || matrix is null ||
            !TryGetWorkflowSpendCap(workflow, out var spendCap, out failure) ||
            !WorkflowCoordinator.TryRefreshRemainingPlan(
                workflow,
                matrix.Edges,
                _manualProbeSessionId,
                spendCap,
                Settings.MinimumProfitChaos.Value,
                DateTimeOffset.UtcNow,
                out var refreshed,
                out var currentLeg,
                out failure) || currentLeg is null)
        {
            return false;
        }
        if (!TryValidateWorkflowInventoryCapacity(
                refreshed.Legs.Skip(refreshed.CurrentLegIndex).Select(leg =>
                    (leg.FromMetadata, leg.ToMetadata, leg.InputSpent, leg.Output)),
                out failure))
        {
            return false;
        }
        try
        {
            var next = CloneBankroll(_bankroll);
            next.Workflow = refreshed;
            next.UpdatedAtUtc = refreshed.UpdatedAtUtc;
            _bankrollStore.Save(next);
            _bankroll = next;
            _workflowPreparedLeg = currentLeg;
            failure = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            failure = $"Fresh workflow plan persistence failed: {exception.Message}";
            return false;
        }
    }

    private bool TryBuildCurrentQuoteMatrix(out CoherentQuoteMatrix? matrix, out string failure)
    {
        matrix = null;
        if (_catalogue is null ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null ||
            !_catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out var target) || target is null)
        {
            failure = "Workflow quote matrix identities were unavailable.";
            return false;
        }
        return QuoteMatrixBuilder.TryBuild(
            _rateStore.Captures,
            GetCurrentLeague(),
            _manualProbeSessionId,
            GameController.Game.IngameState.ServerData.InstanceId,
            chaos,
            divine,
            target,
            DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(Settings.MaximumQuoteAgeSeconds.Value),
            out matrix,
            out failure);
    }

    private bool TryGetWorkflowSpendCap(WorkflowExecutionState workflow, out long spendCap, out string failure)
    {
        var metadata = workflow.Legs[workflow.CurrentLegIndex].FromMetadata;
        var ledger = metadata == GetCatalogueMetadata("Chaos Orb")
            ? _bankroll.AvailableChaos
            : metadata == GetCatalogueMetadata("Divine Orb")
                ? _bankroll.AvailableDivine
                : metadata == _bankroll.TargetMetadata
                    ? _bankroll.AvailableTarget
                    : -1;
        var now = DateTimeOffset.UtcNow;
        var area = GameController.Game.IngameState.ServerData.InstanceId;
        if (ledger < 0 || !_liveOwnedByMetadata.TryGetValue(metadata, out var ownership) ||
            ownership.AreaInstanceId != area || ownership.StableReads < 2 ||
            now - ownership.ObservedAtUtc < TimeSpan.Zero ||
            now - ownership.ObservedAtUtc > TimeSpan.FromSeconds(Settings.MaximumQuoteAgeSeconds.Value))
        {
            spendCap = 0;
            failure = $"Fresh exact ledger/live ownership cap was unavailable for workflow currency {metadata}.";
            return false;
        }
        spendCap = Math.Min(ledger, ownership.Count);
        failure = string.Empty;
        return spendCap > 0;
    }

    private bool TryValidateWorkflowInventoryCapacity(
        IEnumerable<(string FromMetadata, string ToMetadata, long InputAmount, long OutputAmount)> legs,
        out string failure)
    {
        var planned = legs.ToArray();
        var metadata = planned.SelectMany(leg => new[] { leg.FromMetadata, leg.ToMetadata })
            .Distinct(StringComparer.Ordinal).ToArray();
        var snapshots = new Dictionary<string, InventoryTransferSnapshot>(StringComparer.Ordinal);
        foreach (var currency in metadata)
        {
            if (!InventoryStashTransferController.TryReadSnapshot(
                    GameController, currency, out var current, out failure))
                return false;
            if (current.TargetInventoryAmount != 0)
            {
                failure = $"Workflow requires zero pre-existing {currency} in inventory before placement.";
                return false;
            }
            snapshots.Add(currency, current);
        }
        if (snapshots.Count == 0)
        {
            failure = "Workflow had no currencies for inventory-capacity validation.";
            return false;
        }
        foreach (var (currency, snapshot) in snapshots)
        {
            if (!InventoryTransferEvidence.TryGetConservativeCollectionCapacity(
                    snapshot, out var capacity, out failure)) return false;
            if (capacity <= 0)
            {
                failure = "Workflow requires at least one verified free inventory slot for batched collection custody.";
                return false;
            }
            if (snapshot.TargetMaxStackSize <= 0)
            {
                var maximumSettlement = planned.SelectMany(leg => new[]
                    {
                        leg.FromMetadata == currency ? leg.InputAmount : 0,
                        leg.ToMetadata == currency ? leg.OutputAmount : 0,
                    })
                    .Max();
                if (maximumSettlement > capacity)
                {
                    failure = $"Workflow can require {maximumSettlement} {currency}, exceeding the {capacity}-unit " +
                        "first-acquisition capacity provable without an existing visible Currency Stash stack.";
                    return false;
                }
            }
        }
        failure = string.Empty;
        return true;
    }

    private string GetCatalogueMetadata(string name) =>
        _catalogue?.TryGetUniqueByName(name, out var currency) == true && currency is not null
            ? currency.Metadata
            : string.Empty;

    private bool WorkflowUsesCurrentCurrencies(WorkflowExecutionState workflow)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            GetCatalogueMetadata("Chaos Orb"),
            GetCatalogueMetadata("Divine Orb"),
            Settings.TargetCurrencyMetadata,
        };
        return !allowed.Contains(string.Empty) && workflow.Legs.All(leg =>
            allowed.Contains(leg.FromMetadata) && allowed.Contains(leg.ToMetadata));
    }

    private RouteLegResult? GetCurrentPlacementLeg() =>
        _fullWorkflowAuthorized ? _workflowPreparedLeg : _selectedCandidate?.Legs.FirstOrDefault();

    private string GetCurrentPlacementSignature() =>
        _fullWorkflowAuthorized
            ? _bankroll.Workflow?.PlanFingerprint ?? string.Empty
            : _selectedCandidate?.Signature ?? string.Empty;

    private void StartSingleLegStaging(bool placementWorkflowArmed = false)
    {
        if (_trackedCancellation.IsRunning)
        {
            _lastFailure = "Staging is blocked while cancellation is active.";
            return;
        }
        if (IsCollectionFlowActive())
        {
            _lastFailure = "Staging is blocked while tracked-order collection is active.";
            return;
        }
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true ||
            _bankroll.HasUnresolvedOrder || _bankroll.Workflow?.IsActive == true && !_fullWorkflowAuthorized)
        {
            _lastFailure = "Single-leg staging is blocked by unresolved tracked-order state.";
            return;
        }

        if (TryGetHotkeyConflict(out var hotkeyConflict))
        {
            _lastFailure = hotkeyConflict;
            return;
        }

        if (_automatedProbe.IsRunning)
        {
            _lastFailure = "Single-leg staging is blocked while automated probing is running.";
            return;
        }

        _calibrationObservation = null;
        if (!_fullWorkflowAuthorized) CalculateCandidate();
        var leg = GetCurrentPlacementLeg();
        if (leg is null)
        {
            _lastFailure = "No current accepted candidate leg is available to stage.";
            return;
        }

        if (!_singleLegStaging.Start(
                GameController,
                leg,
                _pickerCalibration,
                StagingInputPermissions.From(Settings, _fullWorkflowAuthorized),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                placementWorkflowArmed,
                out var failure))
        {
            _lastFailure = failure;
            _operationStatus = "Single-leg dry-run staging did not start; no amount input was sent.";
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
            }
            return;
        }

        _operationStatus = $"Staging current leg: {leg.InputSpent} {leg.Edge.From.Name} -> {leg.Output} {leg.Edge.To.Name}.";
        _lastFailure = "None";
    }

    private void StartPlacementLegRefresh()
    {
        _placementRefreshAttempts++;
        if (_placementRefreshAttempts > 3)
        {
            _placementPreparation = PlacementPreparationState.Idle;
            _lastFailure = "First-leg selection changed repeatedly during refresh; preparation cancelled.";
            return;
        }

        var leg = GetCurrentPlacementLeg();
        var failure = string.Empty;
        if (leg is null || !_placementLegRefresh.StartSingleMarketProbe(
                GameController,
                leg.Edge.From,
                leg.Edge.To,
                _pickerCalibration,
                ProbeInputPermissions.From(Settings),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                _manualProbeSessionId,
                out failure))
        {
            _placementPreparation = PlacementPreparationState.Idle;
            _lastFailure = leg is null ? "Fresh probe produced no accepted first leg." : failure;
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
            }
            return;
        }

        _placementPreparation = PlacementPreparationState.RefreshingFirstLeg;
        _operationStatus = $"Refreshing selected first-leg market {leg.Edge.From.Name}/{leg.Edge.To.Name} immediately before restaging.";
    }

    private void SynchronizePlacementLegRefresh()
    {
        if (_placementLegRefresh.State == AutomatedProbeState.Completed)
        {
            var captures = _placementLegRefresh.CompletedCaptures.ToArray();
            var refreshAgain = false;
            try
            {
                if (captures.Length != 1 || captures[0].SessionId != _manualProbeSessionId)
                {
                    throw new InvalidDataException("First-leg refresh did not produce exactly one capture in the preparation session.");
                }

                _rateStore.Store(captures[0]);
                _rateStore.Save(_latestRatePath);
                if (_fullWorkflowAuthorized)
                {
                    if (!TryRefreshWorkflowPlan(out var workflowFailure))
                    {
                        throw new InvalidOperationException(workflowFailure);
                    }
                }
                else
                {
                    CalculateCandidate();
                }
                var refreshedLeg = GetCurrentPlacementLeg();
                if (refreshedLeg is null)
                {
                    throw new InvalidOperationException("First-leg refresh removed the accepted candidate.");
                }

                if (refreshedLeg.Edge.Pair != captures[0].Pair)
                {
                    refreshAgain = true;
                }
                else
                {
                    _placementPreparation = PlacementPreparationState.Restaging;
                    StartSingleLegStaging(placementWorkflowArmed: true);
                    if (!_singleLegStaging.IsRunning && _singleLegStaging.State != SingleLegStagingState.Staged)
                    {
                        _placementPreparation = PlacementPreparationState.Idle;
                    }
                }
            }
            catch (Exception exception)
            {
                _placementPreparation = PlacementPreparationState.Idle;
                _placementToken = null;
                _lastFailure = $"First-leg refresh/recalculation failed: {exception.Message}";
                if (_fullWorkflowAuthorized)
                {
                    _fullWorkflowAuthorized = false;
                    _workflowAuthorization = null;
                }
            }
            finally
            {
                _placementLegRefresh.AcknowledgeCompletion();
            }
            if (refreshAgain)
            {
                StartPlacementLegRefresh();
            }
            return;
        }

        if (_placementLegRefresh.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed &&
            _placementPreparation == PlacementPreparationState.RefreshingFirstLeg)
        {
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            _lastFailure = _placementLegRefresh.Failure;
            _placementLegRefresh.AcknowledgeCompletion();
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
            }
        }
    }

    private void SynchronizeSingleLegStagingStatus()
    {
        if (_singleLegStaging.IsRunning)
        {
            _operationStatus = _singleLegStaging.Status;
            _lastObservedStagingState = _singleLegStaging.State;
            return;
        }

        if (_singleLegStaging.State == _lastObservedStagingState)
        {
            return;
        }

        if (_singleLegStaging.State == SingleLegStagingState.Staged)
        {
            _operationStatus = _singleLegStaging.Status;
            _lastFailure = "None";
            _trackedOrder = "None (dry-run staged; no order appeared)";
            if (_placementPreparation == PlacementPreparationState.Restaging)
            {
                var leg = _singleLegStaging.StagedLeg!;
                _placementToken = new PlacementPreparationToken(
                    _manualProbeSessionId,
                    GetCurrentPlacementSignature(),
                    Settings.TargetCurrencyMetadata,
                    Settings.MinimumProfitChaos.Value,
                    leg.Edge.From,
                    leg.Edge.To,
                    leg.Edge.ExecutionIntent,
                    leg.Edge.Rate,
                    leg.InputSpent,
                    leg.Output,
                    DateTimeOffset.UtcNow.AddSeconds(30));
                StartPreparedPlacement(leg);
            }
        }
        else if (_singleLegStaging.State == SingleLegStagingState.Cancelled &&
            !string.IsNullOrWhiteSpace(_singleLegStaging.Failure))
        {
            _operationStatus = _singleLegStaging.Status;
            _lastFailure = _singleLegStaging.Failure;
            if (_placementPreparation == PlacementPreparationState.Restaging)
            {
                _placementPreparation = PlacementPreparationState.Idle;
                _placementToken = null;
                if (_fullWorkflowAuthorized)
                {
                    _fullWorkflowAuthorized = false;
                    _workflowAuthorization = null;
                }
            }
        }

        _lastObservedStagingState = _singleLegStaging.State;
    }

    private void HandlePlaceStagedLegHotkey()
    {
        if (_trackedCancellation.IsRunning)
        {
            _lastFailure = "Placement is blocked while cancellation is active.";
            return;
        }
        if (IsCollectionFlowActive())
        {
            _lastFailure = "Placement is blocked while tracked-order collection is active.";
            return;
        }
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }

        if (_singleLegPlacement.IsRunning)
        {
            _singleLegPlacement.Cancel("Placement hotkey interrupted an in-progress click/verification.");
            return;
        }

        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true ||
            _bankroll.HasUnresolvedOrder || _bankroll.Workflow?.IsActive == true)
        {
            _lastFailure = "Placement is blocked by unresolved or unreadable tracked-order state.";
            return;
        }

        if (_placementPreparation != PlacementPreparationState.Idle)
        {
            AbortPlacementFlow("Placement hotkey requested cancellation of the active one-press sequence.");
            return;
        }

        if (Settings.AllowFullWorkflow.Value)
        {
            _lastFailure = "Full workflow must remain disabled during single-leg placement preparation.";
            return;
        }

        if (!Settings.AllowOrderPlacement.Value)
        {
            _lastFailure = "Enable Allow Order Placement before pressing the one-shot placement hotkey.";
            return;
        }

        _placementPreparation = PlacementPreparationState.Probing;
        _placementToken = null;
        _placementRefreshAttempts = 0;
        StartAutomatedProbe();
        if (!_automatedProbe.IsRunning)
        {
            _placementPreparation = PlacementPreparationState.Idle;
            return;
        }

        _operationStatus = "Authorized one-press placement sequence started: probing before any possible click.";
    }

    private void HandleFullWorkflowHotkey()
    {
        if (_fullWorkflowAuthorized)
        {
            StopFullWorkflowLocal("Second workflow hotkey stopped local automation; any server-side order remains tracked.");
            return;
        }
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        var permissions = PermissionSnapshot.From(Settings);
        var ui = GameController.Game.IngameState.IngameUi;
        if (!permissions.ReadyForFullWorkflow || IsFullFaustusControllerEnabled() ||
            !GameController.Window.IsForeground() || !ui.CurrencyExchangePanel.IsVisible ||
            ui.CurrencyExchangePanel.CurrencyPicker.IsVisible || ui.PopUpWindow.IsVisible ||
            !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible)
        {
            _lastFailure = "Full workflow requires every input permission, exclusive Lite ownership, foreground exchange/stash/inventory, closed picker, and no popup.";
            return;
        }
        if (!_pickerCalibration.IsComplete || !_pickerCalibration.IsPlacementComplete ||
            !_pickerCalibration.IsCollectionComplete || !_pickerCalibration.IsCancellationComplete ||
            !_pickerCalibration.IsReturnCollectionComplete || _bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            !_bankroll.IsInitialized || IsAnyInputOperationActive())
        {
            _lastFailure = "Full workflow requires every picker/order calibration, readable initialized state, and no active input operation.";
            return;
        }
        if (_bankroll.Workflow?.Phase == WorkflowExecutionPhase.LegActive &&
            _bankroll.Workflow.CurrentAttemptId != _trackedOrderState?.AttemptId)
        {
            _lastFailure = "Active workflow and tracked-order identity disagree; manual reconciliation is required.";
            return;
        }
        if (_bankroll.Workflow?.IsActive == true && !WorkflowUsesCurrentCurrencies(_bankroll.Workflow))
        {
            _lastFailure = "Persisted workflow currencies do not match the configured target; recovery is blocked.";
            return;
        }
        if (_bankroll.Workflow?.IsActive != true &&
            (_bankroll.HasUnresolvedOrder || _trackedOrderState?.IsUnresolved == true))
        {
            _lastFailure = "A new workflow cannot start while an unrelated tracked order is unresolved.";
            return;
        }

        _fullWorkflowAuthorized = true;
        _workflowAuthorization = permissions;
        _workflowPreparedLeg = null;
        _lastFailure = "None";
        _lastLoggedFailure = null;
        AppendRuntimeDiagnostic("WorkflowAuthorizationStarted", "Full-workflow hotkey authorization accepted.");
        if (_bankroll.Workflow?.IsActive == true)
        {
            if (WorkflowCoordinator.CanReplaceBeforeFirstPlacement(
                    _bankroll.Workflow, _trackedOrderState) &&
                _bankroll.ReservedChaos == 0 && _bankroll.ReservedDivine == 0 && _bankroll.ReservedTarget == 0 &&
                !_bankroll.HasUnresolvedOrder)
            {
                _startingNewWorkflow = true;
                _operationStatus = "New authorization will replace the unstarted leg-1 route from a fresh coherent probe.";
                StartWorkflowProbe();
                return;
            }
            _operationStatus = $"Workflow {_bankroll.Workflow.WorkflowId:D} reauthorized; recovery will resume from durable {_bankroll.Workflow.Phase} state.";
            DriveFullWorkflow();
            return;
        }

        _startingNewWorkflow = true;
        StartWorkflowProbe();
    }

    private void StartWorkflowProbe()
    {
        if (!_fullWorkflowAuthorized || _placementPreparation != PlacementPreparationState.Idle)
        {
            return;
        }
        _nextWorkflowScanAtUtc = null;
        _placementPreparation = PlacementPreparationState.Probing;
        _placementRefreshAttempts = 0;
        _placementToken = null;
        _workflowPreparedLeg = null;
        StartAutomatedProbe();
        if (!_automatedProbe.IsRunning)
        {
            _placementPreparation = PlacementPreparationState.Idle;
            StopFullWorkflowLocal(_lastFailure);
            return;
        }
        _operationStatus = _startingNewWorkflow
            ? "Full workflow authorized: probing all three markets before persisting an exact route."
            : "Workflow next leg authorized: freshly probing all three markets before re-planning the remaining route.";
    }

    private void DriveFullWorkflow()
    {
        if (!_fullWorkflowAuthorized)
        {
            // No retry may stay armed once local automation is unauthorized, however it was revoked.
            _nextWorkflowScanAtUtc = null;
            return;
        }
        var currentPermissions = PermissionSnapshot.From(Settings);
        if (_workflowAuthorization is null || currentPermissions != _workflowAuthorization ||
            !currentPermissions.ReadyForFullWorkflow)
        {
            StopFullWorkflowLocal("A workflow permission changed; local input stopped without altering server-side order state.");
            return;
        }
        var workflow = _bankroll.Workflow;
        var settled = !ContinuousWorkflowLoop.TryDescribeUnsettledCanonicalState(
            _bankroll, _trackedOrderState, out var unsettledReason);
        var now = DateTimeOffset.UtcNow;
        var action = ContinuousWorkflowLoop.Decide(new ContinuousLoopSnapshot(
            _startingNewWorkflow,
            IsAnyInputOperationActive(),
            workflow?.Phase,
            settled,
            _nextWorkflowScanAtUtc,
            now));
        RecordContinuousLoopDecision(action, now, unsettledReason);
        switch (action)
        {
            case ContinuousLoopAction.Wait:
                if (_startingNewWorkflow && _nextWorkflowScanAtUtc is { } deadline && !IsAnyInputOperationActive())
                {
                    _operationStatus = $"Continuous trading idle: reprobing in " +
                        $"{Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds))}s ({_lastCandidate}).";
                }
                ReleaseStalePlacementLatch(now);
                return;
            case ContinuousLoopAction.StopWithoutDurableRoute:
                StopFullWorkflowLocal("Authorized workflow has no durable route state.");
                return;
            case ContinuousLoopAction.StopUnsafeTerminalState:
                StopFullWorkflowLocal(workflow?.Phase == WorkflowExecutionPhase.Ambiguous
                    ? "Workflow ended ambiguously; manual reconciliation is required before another workflow starts."
                    : $"Continuous trading stopped before a fresh scan: {unsettledReason}.");
                return;
            case ContinuousLoopAction.StartNewWorkflowScan:
                if (workflow is not null)
                {
                    _operationStatus = workflow.Phase == WorkflowExecutionPhase.Completed
                        ? $"Workflow complete: planned/actual terminal {workflow.PlannedRealizedChaos} Chaos, " +
                            $"profit {workflow.PlannedProfitChaos} Chaos; scanning for the next route."
                        : $"Workflow stopped safely: {workflow.Detail}; scanning for the next route.";
                }
                _startingNewWorkflow = true;
                _workflowPreparedLeg = null;
                StartWorkflowProbe();
                return;
            case ContinuousLoopAction.DriveActiveWorkflow:
                break;
        }

        switch (WorkflowCoordinator.Decide(workflow!, _trackedOrderState))
        {
            case WorkflowDirectiveKind.ReprobeAndPrepareCurrentLeg:
                StartWorkflowProbe();
                break;
            case WorkflowDirectiveKind.ObserveCurrentOrder:
                _operationStatus = "Workflow is observing its exact pending order; no additional input is active.";
                break;
            case WorkflowDirectiveKind.AuthorizeCancellation:
                InvokeWorkflowAction(HandleCancelTimedOutOrderHotkey);
                break;
            case WorkflowDirectiveKind.RecoverCancellationWithoutRetry:
                _operationStatus = "Workflow cancellation recovery is observation-only; no cancellation click will be retried.";
                break;
            case WorkflowDirectiveKind.AuthorizeSettlementCollection:
            case WorkflowDirectiveKind.RecoverSettlementCollectionWithoutRetry:
                InvokeWorkflowAction(HandleCollectTrackedOrderHotkey);
                break;
            case WorkflowDirectiveKind.AuthorizeStashTransfer:
            case WorkflowDirectiveKind.RecoverStashTransferWithoutRetry:
                InvokeWorkflowAction(HandleStashCollectedCurrencyHotkey);
                break;
            case WorkflowDirectiveKind.ManualReconciliationRequired:
                StopFullWorkflowLocal("Workflow reached an uncertain persisted input boundary; manual reconciliation is required and no click was retried.");
                break;
        }
    }

    private void ValidateWorkflowAuthorizationBeforeInput()
    {
        if (!_fullWorkflowAuthorized) return;
        var current = PermissionSnapshot.From(Settings);
        if (_workflowAuthorization is null || current != _workflowAuthorization || !current.ReadyForFullWorkflow)
        {
            StopFullWorkflowLocal("A workflow permission changed; local input stopped before the next controller step.");
        }
    }

    private void InvokeWorkflowAction(Action action)
    {
        var updatedBefore = _trackedOrderState?.UpdatedAtUtc;
        action();
        if (_fullWorkflowAuthorized && !IsAnyInputOperationActive() &&
            _trackedOrderState?.UpdatedAtUtc == updatedBefore && _lastFailure != "None")
        {
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
            _startingNewWorkflow = false;
            _nextWorkflowScanAtUtc = null;
            RecordContinuousAuthorizationRevoked($"Workflow stopped before another effect: {_lastFailure}");
        }
    }

    /// <summary>
    /// Records why continuous authorization ended. Every revocation must leave evidence: an idle
    /// plugin that logged nothing is indistinguishable from one that is still working.
    /// No server-side order state is touched here.
    /// </summary>
    private void RecordContinuousAuthorizationRevoked(string reason)
    {
        _operationStatus = reason;
        _lastLoopAction = null;
        _stalePlacementLatchSinceUtc = null;
        _nextLoopHeartbeatUtc = DateTimeOffset.MinValue;
        AppendRuntimeDiagnostic("ContinuousAuthorizationRevoked", reason);
    }

    /// <summary>
    /// An authorized loop that keeps deciding the same thing writes nothing, so a stall used to be
    /// indistinguishable from progress. Every decision change and a periodic heartbeat are recorded.
    /// </summary>
    private void RecordContinuousLoopDecision(ContinuousLoopAction action, DateTimeOffset now, string unsettledReason)
    {
        if (action != ContinuousLoopAction.Wait) _stalePlacementLatchSinceUtc = null;
        var changed = _lastLoopAction != action;
        _lastLoopAction = action;
        if (!changed && now < _nextLoopHeartbeatUtc) return;
        _nextLoopHeartbeatUtc = now.AddSeconds(ContinuousWorkflowLoop.IdleHeartbeatSeconds);
        var scan = _nextWorkflowScanAtUtc is { } deadline
            ? $"{Math.Max(0, (int)Math.Ceiling((deadline - now).TotalSeconds))}s"
            : "none";
        AppendRuntimeDiagnostic(
            "ContinuousLoop",
            $"{action}; startingNewWorkflow={_startingNewWorkflow}; blockedBy={DescribeActiveInputOperation()}; " +
            $"nextScanIn={scan}; unsettled={(string.IsNullOrWhiteSpace(unsettledReason) ? "none" : unsettledReason)}");
    }

    /// <summary>
    /// Releases a placement latch that no controller owns. Such a latch can hold no armed input, but
    /// it blocks every scan, which would otherwise stall continuous trading silently and indefinitely.
    /// </summary>
    private void ReleaseStalePlacementLatch(DateTimeOffset now)
    {
        var owningControllerRunning = _automatedProbe.IsRunning || _placementLegRefresh.IsRunning ||
            _singleLegStaging.IsRunning || _singleLegPlacement.IsRunning;
        if (!ContinuousWorkflowLoop.IsStalePlacementLatch(
                _placementPreparation != PlacementPreparationState.Idle, owningControllerRunning))
        {
            _stalePlacementLatchSinceUtc = null;
            return;
        }
        _stalePlacementLatchSinceUtc ??= now;
        if (!ContinuousWorkflowLoop.HasPersistedFor(
                _stalePlacementLatchSinceUtc, now, ContinuousWorkflowLoop.StalePlacementLatchSeconds))
        {
            return;
        }
        var stale = _placementPreparation;
        _placementPreparation = PlacementPreparationState.Idle;
        _placementToken = null;
        _stalePlacementLatchSinceUtc = null;
        AppendRuntimeDiagnostic(
            "ContinuousLoopLatchReleased",
            $"Placement preparation {stale} owned no running controller for " +
            $"{ContinuousWorkflowLoop.StalePlacementLatchSeconds}s; no input was armed, so the latch was released.");
    }

    /// <summary>Names whatever currently makes <see cref="IsAnyInputOperationActive"/> true.</summary>
    private string DescribeActiveInputOperation()
    {
        if (_automatedProbe.IsRunning) return $"probe {_automatedProbe.State}";
        if (_placementLegRefresh.IsRunning) return $"leg refresh {_placementLegRefresh.State}";
        if (_singleLegStaging.IsRunning) return $"staging {_singleLegStaging.State}";
        if (_singleLegPlacement.IsRunning) return $"placement {_singleLegPlacement.State}";
        if (_trackedCancellation.IsRunning) return $"cancellation {_trackedCancellation.State}";
        if (_trackedCollection.IsRunning) return $"collection {_trackedCollection.State}";
        if (_canceledReturnCollection.IsRunning) return $"return collection {_canceledReturnCollection.State}";
        if (_inventoryStashTransfer.IsRunning) return $"stash transfer {_inventoryStashTransfer.State}";
        if (_collectionOwnershipSelector.IsRunning) return $"ownership read {_collectionOwnershipSelector.State}";
        if (_collectionFlow != CollectionFlowState.Idle) return $"collection flow {_collectionFlow}";
        if (_placementPreparation != PlacementPreparationState.Idle) return $"placement preparation {_placementPreparation}";
        if (_calibrationObservation is not null) return "picker calibration observation";
        return "nothing";
    }

    private void StopFullWorkflowLocal(string reason)
    {
        if (_trackedCancellation.IsRunning) _trackedCancellation.Cancel(reason);
        if (IsCollectionFlowActive()) AbortCollectionFlow(reason);
        if (IsPlacementFlowActive() || _automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning)
            AbortPlacementFlow(reason);
        _fullWorkflowAuthorized = false;
        _workflowAuthorization = null;
        _workflowPreparedLeg = null;
        _startingNewWorkflow = false;
        _nextWorkflowScanAtUtc = null;
        _lastFailure = reason;
        _operationStatus = reason;
    }

    private void AppendFailureDiagnosticIfNeeded()
    {
        if (string.IsNullOrWhiteSpace(_lastFailure) || _lastFailure == "None")
        {
            _lastLoggedFailure = null;
            return;
        }
        if (_fullWorkflowAuthorized && IsAnyInputOperationActive()) return;
        if (_lastLoggedFailure == _lastFailure) return;
        _lastLoggedFailure = _lastFailure;
        AppendRuntimeDiagnostic("Failure", _lastFailure);
    }

    private void AppendRuntimeDiagnostic(string eventType, string detail)
    {
        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var workflow = _bankroll.Workflow;
            var diagnostic = new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                EventType = eventType,
                Detail = detail,
                OperationStatus = _operationStatus,
                FullWorkflowAuthorized = _fullWorkflowAuthorized,
                PlacementPreparation = _placementPreparation.ToString(),
                AutomatedProbe = _automatedProbe.State.ToString(),
                LegRefresh = _placementLegRefresh.State.ToString(),
                Staging = _singleLegStaging.State.ToString(),
                Placement = _singleLegPlacement.State.ToString(),
                Cancellation = _trackedCancellation.State.ToString(),
                CollectionFlow = _collectionFlow.ToString(),
                TrackedCollection = _trackedCollection.State.ToString(),
                TerminalCollection = _canceledReturnCollection.State.ToString(),
                StashTransfer = _inventoryStashTransfer.State.ToString(),
                WorkflowId = workflow?.WorkflowId,
                WorkflowPhase = workflow?.Phase.ToString(),
                WorkflowLeg = workflow is null ? (int?)null : workflow.CurrentLegIndex + 1,
                WorkflowLegCount = workflow?.Legs.Count,
                WorkflowDetail = workflow?.Detail,
                CandidateStatus = _lastCandidate,
                CandidatePath = _selectedCandidate is null
                    ? null
                    : string.Join(" -> ", _selectedCandidate.Path.Select(currency => currency.Name)),
                CandidateProfitChaos = _selectedCandidate?.ProfitChaos,
                CandidateCompetingLegs = _selectedCandidate?.CompetingEdgeCount,
                CandidateLegs = _selectedCandidate?.Legs.Select(leg => new
                {
                    From = leg.Edge.From.Name,
                    To = leg.Edge.To.Name,
                    leg.InputSpent,
                    leg.Output,
                    Rate = leg.Edge.Rate.ToString(),
                    Intent = leg.Edge.ExecutionIntent.ToString(),
                    leg.InputRemainder,
                }).ToArray(),
                TrackedStatus = _trackedOrderState?.Status.ToString(),
                _bankroll.AvailableChaos,
                _bankroll.AvailableDivine,
                _bankroll.AvailableTarget,
                _bankroll.ReservedChaos,
                _bankroll.ReservedDivine,
                _bankroll.ReservedTarget,
                _bankroll.CompletedUncollectedChaos,
                _bankroll.CompletedUncollectedDivine,
                _bankroll.CompletedUncollectedTarget,
                _bankroll.HasUnresolvedOrder,
            };
            File.AppendAllText(
                Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "workflow-runtime.log"),
                Newtonsoft.Json.JsonConvert.SerializeObject(diagnostic) + Environment.NewLine);
        }
        catch
        {
            // Runtime diagnostics must never alter workflow behavior.
        }
    }

    private void StartPreparedPlacement(RouteLegResult stagedLeg)
    {
        var preparationFailure = string.Empty;
        if (!_pickerCalibration.IsPlacementComplete ||
            !ValidatePlacementPreparation(stagedLeg, out preparationFailure))
        {
            _lastFailure = !_pickerCalibration.IsPlacementComplete
                ? "Place Order calibration is unavailable."
                : preparationFailure;
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
            }
            return;
        }

        if (!InventoryStashTransferController.TryReadSnapshot(
                GameController, stagedLeg.Edge.From.Metadata, out var offeredInventory, out preparationFailure) ||
            !InventoryStashTransferController.TryReadSnapshot(
                GameController, stagedLeg.Edge.To.Metadata, out var wantedInventory, out preparationFailure))
        {
            _lastFailure = $"Placement stack-size evidence was unavailable: {preparationFailure}";
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
            return;
        }
        if (!UnknownStackCapacityAllows(offeredInventory, stagedLeg.InputSpent, out preparationFailure) ||
            !UnknownStackCapacityAllows(wantedInventory, stagedLeg.Output, out preparationFailure))
        {
            _lastFailure = preparationFailure;
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
            return;
        }

        if (!_singleLegPlacement.Start(
                GameController,
                stagedLeg,
                _pickerCalibration,
                PlacementInputPermissions.From(Settings, _fullWorkflowAuthorized),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                Settings.CompetingOrderWaitMinutes.Value,
                _placementToken!.ProbeSessionId,
                _placementToken.CandidateSignature,
                offeredInventory.TargetMaxStackSize,
                wantedInventory.TargetMaxStackSize,
                leg => ValidatePlacementPreparation(leg, out var finalFailure)
                    ? (true, string.Empty)
                    : (false, finalFailure),
                PersistTrackedOrder,
                out var placementFailure))
        {
            _lastFailure = placementFailure;
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
            }
            return;
        }

        _placementPreparation = PlacementPreparationState.Placing;
        _operationStatus = "Fresh probe/restage passed; one verified Place Order click is armed automatically.";
    }

    private static bool UnknownStackCapacityAllows(
        InventoryTransferSnapshot snapshot,
        long settlementAmount,
        out string failure)
    {
        if (!InventoryTransferEvidence.TryGetConservativeCollectionCapacity(
                snapshot, out var capacity, out failure)) return false;
        if (snapshot.TargetMaxStackSize <= 0 && settlementAmount > capacity)
        {
            failure = $"Settlement amount {settlementAmount} exceeds the {capacity}-unit first-acquisition " +
                "capacity provable without an existing visible Currency Stash stack.";
            return false;
        }
        failure = string.Empty;
        return true;
    }

    private void SynchronizeSingleLegPlacementStatus()
    {
        if (_singleLegPlacement.IsRunning)
        {
            _operationStatus = _singleLegPlacement.Status;
            return;
        }

        if (_placementPreparation != PlacementPreparationState.Placing)
        {
            return;
        }

        _operationStatus = _singleLegPlacement.Status;
        if (_singleLegPlacement.State == SingleLegPlacementState.Completed)
        {
            _lastFailure = "None";
        }
        else if (_singleLegPlacement.State is SingleLegPlacementState.Ambiguous or SingleLegPlacementState.Cancelled)
        {
            // A blank controller failure must never become the reported reason: overwriting a real
            // failure with an empty string is what once made an aborted placement silently idle.
            _lastFailure = string.IsNullOrWhiteSpace(_singleLegPlacement.Failure)
                ? $"Placement ended {_singleLegPlacement.State} without reporting a reason."
                : _singleLegPlacement.Failure;
            if (_fullWorkflowAuthorized)
            {
                _fullWorkflowAuthorized = false;
                _workflowAuthorization = null;
                _startingNewWorkflow = false;
                _nextWorkflowScanAtUtc = null;
                RecordContinuousAuthorizationRevoked($"Placement did not resolve cleanly: {_lastFailure}");
            }
        }

        _placementPreparation = PlacementPreparationState.Idle;
        _placementToken = null;
    }

    private bool ValidatePlacementPreparation(RouteLegResult stagedLeg, out string failure)
    {
        var token = _placementToken;
        if (token is null || DateTimeOffset.UtcNow > token.ExpiresAtUtc ||
            token.ProbeSessionId != _manualProbeSessionId ||
            !string.Equals(token.TargetMetadata, Settings.TargetCurrencyMetadata, StringComparison.Ordinal) ||
            token.MinimumProfitChaos != Settings.MinimumProfitChaos.Value)
        {
            failure = "Fresh placement preparation expired or its session/settings changed; begin again.";
            return false;
        }

        if (!_fullWorkflowAuthorized) CalculateCandidate(invalidateStaging: false);
        var currentLeg = GetCurrentPlacementLeg();
        if (currentLeg is null ||
            !string.Equals(GetCurrentPlacementSignature(), token.CandidateSignature, StringComparison.Ordinal) ||
            !currentLeg.Edge.From.Equals(token.From) || !currentLeg.Edge.To.Equals(token.To) ||
            currentLeg.Edge.ExecutionIntent != token.ExecutionIntent || currentLeg.Edge.Rate != token.Rate ||
            currentLeg.InputSpent != token.InputSpent || currentLeg.Output != token.Output ||
            stagedLeg.InputSpent != token.InputSpent || stagedLeg.Output != token.Output)
        {
            failure = "The fresh profitable candidate or first-leg economics changed; begin placement preparation again.";
            return false;
        }

        failure = string.Empty;
        return true;
    }

    private bool PersistTrackedOrder(TrackedOrderState state, string eventType)
    {
        if (_bankrollStore is null || !_bankroll.IsInitialized)
        {
            return RejectTrackedOrderPersist(state, eventType, "Canonical bankroll is not loaded.");
        }

        try
        {
            var next = CloneBankroll(_bankroll);
            var previous = next.TrackedOrder;
            if (state.Status == TrackedOrderStatus.Armed)
            {
                if (previous?.IsUnresolved == true || !TryMoveAvailableToReserved(next, state.OfferedMetadata, state.OfferedAmount))
                {
                    return RejectTrackedOrderPersist(state, eventType,
                        $"Arming requires no unresolved order and {state.OfferedAmount} available " +
                        $"{state.OfferedMetadata} to reserve; previous={previous?.Status.ToString() ?? "none"}.");
                }
            }
            else if (previous is null || previous.AttemptId != state.AttemptId || !previous.IsUnresolved)
            {
                return RejectTrackedOrderPersist(state, eventType,
                    $"Transition requires the same unresolved attempt; previous={previous?.Status.ToString() ?? "none"}, " +
                    $"attemptMatches={previous?.AttemptId == state.AttemptId}.");
            }

            if (state.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
                previous!.Status is not TrackedOrderStatus.CompletedUncollected and not TrackedOrderStatus.CanceledUncollected and
                    not TrackedOrderStatus.CollectionArmed && state.LedgerCommittedAtUtc is null)
            {
                if (state.TerminalRemainingOfferedAmount is not { } remaining ||
                    state.TerminalReceivedWantedAmount is not { } received ||
                    !TrySettleTerminal(next, state, remaining, received))
                {
                    return RejectTrackedOrderPersist(state, eventType,
                        $"Terminal settlement refused for remaining={state.TerminalRemainingOfferedAmount?.ToString() ?? "unknown"}, " +
                        $"received={state.TerminalReceivedWantedAmount?.ToString() ?? "unknown"}.");
                }
                state.LedgerCommittedAtUtc = DateTimeOffset.UtcNow;
            }

            next.TrackedOrder = state;
            next.HasUnresolvedOrder = state.IsUnresolved;
            next.UpdatedAtUtc = DateTimeOffset.UtcNow;
            var priorWorkflowPhase = next.Workflow?.Phase;
            if (next.Workflow?.IsActive == true)
            {
                if (!WorkflowCoordinator.TryApplyTrackedState(
                        next.Workflow, state, next.UpdatedAtUtc, out var workflow, out var workflowFailure))
                {
                    return RejectTrackedOrderPersist(state, eventType, workflowFailure);
                }
                next.Workflow = workflow;
            }
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = state;
            _trackedOrder = $"{state.Status}: id={state.PlayerOrderId?.ToString() ?? "unknown"}, " +
                $"{state.OfferedAmount} {state.OfferedMetadata} -> {state.WantedAmount} {state.WantedMetadata}";
            try
            {
                _trackedOrderStore?.AppendAudit(state, eventType);
                if (next.Workflow is { } workflow && workflow.Phase != priorWorkflowPhase &&
                    workflow.Phase is WorkflowExecutionPhase.Completed or WorkflowExecutionPhase.Stopped)
                {
                    _bankrollStore.AppendWorkflowAudit(WorkflowAuditEvent.From(workflow, state));
                }
            }
            catch (Exception auditException)
            {
                _lastFailure = $"Canonical tracked state committed, but audit append failed: {auditException.Message}";
            }
            return true;
        }
        catch (Exception exception)
        {
            return RejectTrackedOrderPersist(state, eventType, $"Persistence threw: {exception.Message}");
        }
    }

    /// <summary>
    /// Refuses a durable write and leaves evidence. A rejected canonical write means the plugin's
    /// belief and the file have diverged; discarding that silently once froze a live order at
    /// <see cref="TrackedOrderStatus.Armed"/> with reserved principal and no log line anywhere.
    /// </summary>
    private bool RejectTrackedOrderPersist(TrackedOrderState state, string eventType, string reason)
    {
        _lastFailure = $"Tracked-order persistence refused ({eventType} -> {state.Status}): {reason}";
        AppendRuntimeDiagnostic("TrackedOrderPersistRejected", _lastFailure);
        return false;
    }

    private void PollTrackedOrderLifecycle()
    {
        if (_trackedOrderState is null) return;
        var placementNeedsReconciliation = _trackedOrderState is { } tracked &&
            ArmedPlacementReconciliation.CanReconcile(tracked);
        if (!placementNeedsReconciliation &&
            _trackedOrderState?.Status is not TrackedOrderStatus.Pending and
                not TrackedOrderStatus.TimedOut and not TrackedOrderStatus.CancelArmed and
                not TrackedOrderStatus.CancelClicked ||
            DateTimeOffset.UtcNow < _nextLifecyclePollAtUtc || _bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            _trackedCancellation.IsRunning)
        {
            return;
        }
        // An armed placement is still owned by the placement controller until that controller stops.
        if (_trackedOrderState.Status == TrackedOrderStatus.Armed &&
            (_singleLegPlacement.IsRunning || _placementPreparation != PlacementPreparationState.Idle))
        {
            return;
        }
        _nextLifecyclePollAtUtc = DateTimeOffset.UtcNow.AddMilliseconds(250);

        var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
        var failure = string.Empty;
        if (!panel.IsVisible || panel.CurrencyPicker.IsVisible ||
            GameController.Game.IngameState.IngameUi.PopUpWindow.IsVisible ||
            !SingleLegPlacementController.TryReadOrders(GameController, out var orders, out failure))
        {
            _operationStatus = string.IsNullOrEmpty(failure)
                ? "Lifecycle waiting: exchange/order list is not safely readable."
                : $"Lifecycle waiting: {failure}";
            return;
        }

        var observedAt = DateTimeOffset.UtcNow;
        if (placementNeedsReconciliation)
        {
            ReconcileArmedPlacement(orders, observedAt);
            return;
        }

        var observation = TrackedOrderLifecycle.Evaluate(_trackedOrderState, orders, observedAt);
        if (observation.Kind == LifecycleObservationKind.NotVisible)
        {
            _operationStatus = observation.Detail;
            return;
        }
        if (observation.Kind == LifecycleObservationKind.Ambiguous)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Ambiguous, observation.Detail);
            PersistTrackedOrder(ambiguous, "TrackedOrderLifecycleAmbiguous");
            return;
        }
        if (observation.Order is not { } order) return;
        if (observation.Kind == LifecycleObservationKind.Transitioning)
        {
            _operationStatus = observation.Detail;
            return;
        }

        if (_trackedOrderState.Status is TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked &&
            _trackedOrderState.CancelIntent is { } recoveryIntent)
        {
            var unrelatedFingerprint = TrackedOrderLifecycle.OrderSetFingerprint(
                orders.Where(candidate => !TrackedOrderLifecycle.IdentityMatches(_trackedOrderState, candidate)));
            if (unrelatedFingerprint != recoveryIntent.UnrelatedOrdersFingerprint)
            {
                var ambiguous = TrackedOrderCollectionController.CloneTracked(
                    _trackedOrderState, TrackedOrderStatus.Ambiguous,
                    "Interrupted cancellation recovery found changed unrelated orders.");
                PersistTrackedOrder(ambiguous, "TrackedOrderCancellationRecoveryUnrelatedAmbiguous");
                return;
            }
        }

        if (_trackedOrderState.Status is TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked &&
            observation.Kind is LifecycleObservationKind.Pending or LifecycleObservationKind.TimedOut)
        {
            _operationStatus = "Interrupted cancellation intent remains pending; no click will be retried automatically.";
            return;
        }

        if (observation.Kind == LifecycleObservationKind.Pending)
        {
            _operationStatus = $"Tracked order {order.PlayerOrderId} pending: " +
                $"remaining={order.RemainingOfferedAmount}, received={order.ReceivedWantedAmount}, " +
                $"deadline={_trackedOrderState.WaitUntilUtc:O}.";
            return;
        }

        var nextStatus = observation.Kind switch
        {
            LifecycleObservationKind.TimedOut => TrackedOrderStatus.TimedOut,
            LifecycleObservationKind.Completed => TrackedOrderStatus.CompletedUncollected,
            LifecycleObservationKind.Canceled => TrackedOrderStatus.CanceledUncollected,
            _ => _trackedOrderState.Status
        };
        if (nextStatus == _trackedOrderState.Status) return;

        if (nextStatus is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
            !TrackedOrderCancellationController.TryValidateTerminalRow(
                GameController, order, observation.Kind, out var terminalFailure))
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Ambiguous, terminalFailure);
            PersistTrackedOrder(ambiguous, "TrackedOrderLifecycleTerminalRowAmbiguous");
            return;
        }

        var next = TrackedOrderCollectionController.CloneTracked(_trackedOrderState, nextStatus, observation.Detail);
        next.PlayerOrderId = order.PlayerOrderId;
        next.LastObservedAtUtc = observedAt;
        next.LastRemainingOfferedAmount = order.RemainingOfferedAmount;
        next.LastReceivedWantedAmount = order.ReceivedWantedAmount;
        if (nextStatus == TrackedOrderStatus.TimedOut)
        {
            next.TimeoutObservedAtUtc = observedAt;
        }
        else
        {
            next.TerminalObservedAtUtc = observedAt;
            next.TerminalRemainingOfferedAmount = order.RemainingOfferedAmount;
            next.TerminalReceivedWantedAmount = order.ReceivedWantedAmount;
        }
        PersistTrackedOrder(next, $"TrackedOrderLifecycle{nextStatus}");
    }

    /// <summary>
    /// Binds an armed placement whose click already happened to the order it produced. The placement
    /// controller observes for only a few seconds; anything that outlives that window leaves reserved
    /// principal against a state that carries no creation date or placed ratio, which no other path
    /// can reconcile and which reset refuses to clear. Observation only: nothing here sends input.
    /// </summary>
    private void ReconcileArmedPlacement(IReadOnlyList<PlacedOrderSnapshot> orders, DateTimeOffset observedAt)
    {
        if (_trackedOrderState is not { } armed) return;
        var reconciliation = ArmedPlacementReconciliation.Evaluate(armed, orders, observedAt);
        if (reconciliation.Kind == ArmedReconciliationKind.Waiting)
        {
            _operationStatus = reconciliation.Detail;
            return;
        }
        if (reconciliation.Kind == ArmedReconciliationKind.Ambiguous || reconciliation.Order is not { } order)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                armed, TrackedOrderStatus.Ambiguous, reconciliation.Detail);
            PersistTrackedOrder(ambiguous, "TrackedOrderArmedReconciliationAmbiguous");
            return;
        }

        if (reconciliation.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
            !TrackedOrderCancellationController.TryValidateTerminalRow(
                GameController, order,
                reconciliation.Status == TrackedOrderStatus.CanceledUncollected
                    ? LifecycleObservationKind.Canceled
                    : LifecycleObservationKind.Completed,
                out var terminalFailure))
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                armed, TrackedOrderStatus.Ambiguous, terminalFailure);
            PersistTrackedOrder(ambiguous, "TrackedOrderArmedReconciliationTerminalRowAmbiguous");
            return;
        }

        var bound = TrackedOrderCollectionController.CloneTracked(armed, reconciliation.Status, reconciliation.Detail);
        bound.PlayerOrderId = order.PlayerOrderId;
        bound.GoldCost = order.GoldCost;
        bound.OrderCreationDateUtc = order.CreationDate;
        bound.PlacedOfferedRatioPart = order.OfferedRatioPart;
        bound.PlacedWantedRatioPart = order.WantedRatioPart;
        bound.WaitStartedAtUtc = order.CreationDate;
        bound.WaitUntilUtc = order.CreationDate.AddMinutes(Settings.CompetingOrderWaitMinutes.Value);
        bound.LastObservedAtUtc = observedAt;
        bound.LastRemainingOfferedAmount = order.RemainingOfferedAmount;
        bound.LastReceivedWantedAmount = order.ReceivedWantedAmount;
        if (reconciliation.Status != TrackedOrderStatus.Pending)
        {
            bound.TerminalObservedAtUtc = observedAt;
            bound.TerminalRemainingOfferedAmount = order.RemainingOfferedAmount;
            bound.TerminalReceivedWantedAmount = order.ReceivedWantedAmount;
        }
        if (PersistTrackedOrder(bound, $"TrackedOrderArmedReconciliation{reconciliation.Status}"))
        {
            AppendRuntimeDiagnostic("TrackedOrderArmedReconciled", reconciliation.Detail);
        }
    }

    private void CalibrateTrackedCollectionSlot()
    {
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (_collectionFlow != CollectionFlowState.Idle || _trackedCollection.IsRunning ||
            _automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning ||
            _singleLegPlacement.IsRunning || _placementPreparation != PlacementPreparationState.Idle)
        {
            _lastFailure = "Collection calibration is blocked while another input operation is active.";
            return;
        }
        var ui = GameController.Game.IngameState.IngameUi;
        var failure = string.Empty;
        if (!GameController.Window.IsForeground() || !ui.CurrencyExchangePanel.IsVisible ||
            ui.CurrencyExchangePanel.CurrencyPicker.IsVisible || !ui.StashElement.IsVisible ||
            !ui.InventoryPanel.IsVisible || ExileInput.IsKeyDown(Keys.ControlKey) ||
            ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu))
        {
            _lastFailure = "Collection calibration requires foreground exchange, stash, inventory, closed picker, and released modifiers.";
            return;
        }
        if (_trackedOrderState is null ||
            !TrackedOrderCollectionController.TryResolveTrackedRow(
                GameController, _trackedOrderState, out var row, out _, out failure) || row is null)
        {
            _lastFailure = _trackedOrderState is null ? "No tracked order is available for collection calibration." : failure;
            return;
        }

        var cursor = ExileInput.MousePositionNum;
        var rect = row.GetClientRectCache;
        var candidate = new PickerCalibration
        {
            OfferedButton = _pickerCalibration.OfferedButton,
            WantedButton = _pickerCalibration.WantedButton,
            PlaceOrderButton = _pickerCalibration.PlaceOrderButton,
            PlaceOrderPanelAspectRatio = _pickerCalibration.PlaceOrderPanelAspectRatio,
            CollectionSlotOffset = _pickerCalibration.CollectionSlotOffset,
            CollectionRowAspectRatio = _pickerCalibration.CollectionRowAspectRatio,
            CancelButtonOffset = _pickerCalibration.CancelButtonOffset,
            CancelRowAspectRatio = _pickerCalibration.CancelRowAspectRatio,
            ReturnSlotOffset = _pickerCalibration.ReturnSlotOffset,
            ReturnRowAspectRatio = _pickerCalibration.ReturnRowAspectRatio
        };
        if (!candidate.TryRecordCollectionSlot(
                rect.X, rect.Y, rect.Width, rect.Height, cursor.X, cursor.Y, out failure))
        {
            _lastFailure = failure;
            return;
        }

        try
        {
            _pickerCalibrationStore.Save(_pickerCalibrationPath, candidate);
            _pickerCalibration = candidate;
            _operationStatus = $"Recorded collection slot offset for exact tracked order {_trackedOrderState.PlayerOrderId}.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Collection calibration persistence failed: {exception.Message}";
        }
    }

    private void HandleCollectTrackedOrderHotkey()
    {
        if (_trackedCancellation.IsRunning)
        {
            _lastFailure = "Collection is blocked while cancellation is active.";
            return;
        }
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (_collectionFlow != CollectionFlowState.Idle || _trackedCollection.IsRunning ||
            _collectionOwnershipSelector.IsRunning)
        {
            AbortCollectionFlow("Collection hotkey requested cancellation.");
            return;
        }
        if (_automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning ||
            _singleLegPlacement.IsRunning || _placementPreparation != PlacementPreparationState.Idle ||
            _calibrationObservation is not null)
        {
            _lastFailure = "Collection is blocked while another input operation or calibration is active.";
            return;
        }
        if (_trackedOrderState?.Status is TrackedOrderStatus.CollectionArmed or TrackedOrderStatus.Ambiguous &&
            _trackedOrderState.CollectionAssetIntent is not null)
        {
            ReconcileInterruptedTerminalCollection();
            return;
        }
        if (_trackedOrderState?.Status is TrackedOrderStatus.CanceledUncollected or
                TrackedOrderStatus.CompletedUncollected &&
            (_trackedOrderState.TerminalRemainingOfferedAmount is > 0 ||
             _trackedOrderState.Status == TrackedOrderStatus.CanceledUncollected))
        {
            if (_trackedOrderState.PendingWantedBatchAmount > 0 || _trackedOrderState.PendingReturnBatchAmount > 0)
            {
                _lastFailure = "A collected batch is still pending stash custody; authorize stash transfer first.";
                return;
            }
            var wantedSidePending = TrackedOrderLifecycle.RemainingWantedToCollect(_trackedOrderState) > 0;
            var returnSidePending = TrackedOrderLifecycle.RemainingReturnToCollect(_trackedOrderState) > 0;
            var nextWantedSlot = wantedSidePending;
            if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || !wantedSidePending && !returnSidePending ||
                nextWantedSlot && !_pickerCalibration.IsCollectionComplete ||
                !nextWantedSlot && !_pickerCalibration.IsReturnCollectionComplete)
            {
                _lastFailure = "Terminal settlement collection requires exact pending amounts and the calibrated left/right slot.";
                return;
            }
            if (!CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized).Ready || !Settings.AllowQueryInput.Value)
            {
                _lastFailure = "Enable movement, clicks, query input, and collection; disable placement/full workflow/cancellation.";
                return;
            }
            StartCollectionOwnershipRead(
                CollectionFlowState.ReadingCanceledReturnBaseline,
                nextWantedSlot ? _trackedOrderState.WantedMetadata : _trackedOrderState.OfferedMetadata);
            return;
        }
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            _trackedOrderState?.Status != TrackedOrderStatus.CompletedUncollected ||
            !_pickerCalibration.IsCollectionComplete)
        {
            _lastFailure = "Collection requires readable canonical CompletedUncollected state and calibrated tracked-order slot.";
            return;
        }
        if (!CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized).Ready || !Settings.AllowQueryInput.Value)
        {
            _lastFailure = "Enable movement, clicks, query input, and collection; disable placement and full workflow.";
            return;
        }

        StartCollectionOwnershipRead(CollectionFlowState.ReadingBaseline);
    }

    private void ReconcileInterruptedTerminalCollection()
    {
        var failure = string.Empty;
        SettlementAsset? asset = null;
        if (_trackedOrderState?.CollectionAssetIntent is not { } intent)
        {
            _lastFailure = "Interrupted terminal collection lacked a durable asset intent.";
            return;
        }
        if (!CanceledReturnCollectionController.VerifyInterruptedPostState(
                GameController, _trackedOrderState, _pickerCalibration, out asset, out failure) || asset is null)
        {
            if (CanceledReturnCollectionController.VerifyInterruptedPreState(
                    GameController, _trackedOrderState, _pickerCalibration, out var preFailure))
            {
                var disarmed = TrackedOrderCollectionController.CloneTracked(
                    _trackedOrderState, intent.TerminalStatus,
                    "Recovered exact interrupted terminal-asset pre-click state; no input was retried.");
                disarmed.CollectionAssetIntent = null;
                if (PersistTrackedOrder(disarmed, "TerminalAssetCollectionInterruptedPreStateRecovered"))
                {
                    var wasAuthorized = _fullWorkflowAuthorized;
                    _fullWorkflowAuthorized = false;
                    _workflowAuthorization = null;
                    _startingNewWorkflow = false;
                    _nextWorkflowScanAtUtc = null;
                    _operationStatus = "Recovered exact terminal-asset pre-click state; a new hotkey authorization is required.";
                    _lastFailure = "None";
                    if (wasAuthorized) RecordContinuousAuthorizationRevoked(_operationStatus);
                }
                else
                {
                    _lastFailure = "Exact terminal-asset pre-click state was observed but could not be persisted.";
                }
                return;
            }
            _lastFailure = string.IsNullOrEmpty(failure)
                ? preFailure
                : $"{failure} Pre-click classification also failed: {preFailure}";
            return;
        }
        try
        {
            var next = CloneBankroll(_bankroll);
            if (!TryCreditCollected(next, intent.Metadata, intent.Amount))
                throw new InvalidDataException("Recovered batch did not match canonical completed buckets.");
            var progress = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Collected,
                $"Reconciled exact interrupted {(asset.WantedSlot ? "wanted proceeds" : "offered return")} batch of {intent.Amount} and credited it without retrying input.");
            progress.CollectionAssetIntent = null;
            if (asset.WantedSlot)
            {
                progress.PendingWantedBatchAmount = intent.Amount;
                progress.WantedAssetCollected =
                    progress.SettledWantedAmount + progress.PendingWantedBatchAmount ==
                    TrackedOrderLifecycle.TotalWantedProceeds(progress);
            }
            else
            {
                progress.PendingReturnBatchAmount = intent.Amount;
                progress.OfferedReturnCollected =
                    progress.SettledReturnAmount + progress.PendingReturnBatchAmount ==
                    TrackedOrderLifecycle.TotalOfferedReturn(progress);
            }
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            next.TrackedOrder = progress;
            next.HasUnresolvedOrder = true;
            next.UpdatedAtUtc = progress.UpdatedAtUtc;
            _bankrollStore!.Save(next);
            _bankroll = next;
            _trackedOrderState = progress;
            _trackedOrder = $"Reconciled and credited interrupted batch of {intent.Amount} {intent.Metadata}";
            try { _trackedOrderStore?.AppendAudit(progress, "TerminalAssetBatchInterruptedPostStateReconciledAndCredited"); }
            catch (Exception auditException)
            {
                _lastFailure = $"Recovered batch settled canonically, audit append failed: {auditException.Message}";
            }
            _operationStatus = "Interrupted collection batch reconciled and credited; verified stash custody remains.";
            if (!_lastFailure.StartsWith("Recovered batch settled", StringComparison.Ordinal)) _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Interrupted terminal batch reconciliation failed closed: {exception.Message}";
        }
    }

    private void CalibrateTrackedCancelButton()
    {
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (_trackedOrderState?.Status != TrackedOrderStatus.TimedOut || IsAnyInputOperationActive())
        {
            _lastFailure = "Cancel calibration requires exact canonical TimedOut state and no active input operation.";
            return;
        }
        if (!TrackedOrderCancellationController.TryResolvePendingRow(
                GameController, _trackedOrderState, out var row, out _, out _, out var failure) || row is null)
        {
            _lastFailure = failure;
            return;
        }
        var ui = GameController.Game.IngameState.IngameUi;
        if (!GameController.Window.IsForeground() || ui.PopUpWindow.IsVisible ||
            !ui.StashElement.IsVisible || !ui.InventoryPanel.IsVisible ||
            ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) || ExileInput.IsKeyDown(Keys.Menu))
        {
            _lastFailure = "Cancel calibration requires foreground exchange, stash, inventory, no popup, and released modifiers.";
            return;
        }

        var candidate = ClonePickerCalibration();
        var rect = row.GetClientRectCache;
        var cursor = ExileInput.MousePositionNum;
        if (!candidate.TryRecordCancelButton(
                rect.X, rect.Y, rect.Width, rect.Height, cursor.X, cursor.Y, out failure))
        {
            _lastFailure = failure;
            return;
        }
        try
        {
            _pickerCalibrationStore.Save(_pickerCalibrationPath, candidate);
            _pickerCalibration = candidate;
            _operationStatus = "Recorded exact pending-row cancel X calibration; no click occurred.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Cancel calibration persistence failed: {exception.Message}";
        }
    }

    private void CalibrateCanceledReturnSlot()
    {
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        var failure = string.Empty;
        if (_trackedOrderState?.Status is not TrackedOrderStatus.CanceledUncollected and
                not TrackedOrderStatus.CompletedUncollected || IsAnyInputOperationActive() ||
            _trackedOrderState.TerminalRemainingOfferedAmount is not > 0 ||
            !CanceledReturnCollectionController.TryResolveTerminalAssetRow(
                GameController, _trackedOrderState, out var row, out _, out failure) || row is null)
        {
            _lastFailure = string.IsNullOrEmpty(failure)
                ? "Return calibration requires exact terminal row with offered return and no active input operation."
                : failure;
            return;
        }
        var candidate = ClonePickerCalibration();
        var rect = row.GetClientRectCache;
        var cursor = ExileInput.MousePositionNum;
        if (!candidate.TryRecordReturnSlot(
                rect.X, rect.Y, rect.Width, rect.Height, cursor.X, cursor.Y, out failure))
        {
            _lastFailure = failure;
            return;
        }
        try
        {
            _pickerCalibrationStore.Save(_pickerCalibrationPath, candidate);
            _pickerCalibration = candidate;
            _operationStatus = "Recorded canceled offered-return slot calibration; no click occurred.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Return-slot calibration persistence failed: {exception.Message}";
        }
    }

    private PickerCalibration ClonePickerCalibration() => new()
    {
        OfferedButton = _pickerCalibration.OfferedButton,
        WantedButton = _pickerCalibration.WantedButton,
        PlaceOrderButton = _pickerCalibration.PlaceOrderButton,
        PlaceOrderPanelAspectRatio = _pickerCalibration.PlaceOrderPanelAspectRatio,
        CollectionSlotOffset = _pickerCalibration.CollectionSlotOffset,
        CollectionRowAspectRatio = _pickerCalibration.CollectionRowAspectRatio,
        CancelButtonOffset = _pickerCalibration.CancelButtonOffset,
        CancelRowAspectRatio = _pickerCalibration.CancelRowAspectRatio,
        ReturnSlotOffset = _pickerCalibration.ReturnSlotOffset,
        ReturnRowAspectRatio = _pickerCalibration.ReturnRowAspectRatio
    };

    private void HandleCancelTimedOutOrderHotkey()
    {
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (_trackedCancellation.IsRunning)
        {
            _trackedCancellation.Cancel("Cancellation hotkey requested stop; no automatic retry.");
            return;
        }
        if (IsAnyInputOperationActive() || _trackedOrderState?.Status != TrackedOrderStatus.TimedOut ||
            !_pickerCalibration.IsCancellationComplete || _bankrollLoadBlocked || _trackedOrderLoadBlocked)
        {
            _lastFailure = "Cancellation requires exact TimedOut state, cancel calibration, readable canonical state, and no other input operation.";
            return;
        }
        if (!_trackedCancellation.Start(
                GameController,
                _trackedOrderState,
                _pickerCalibration,
                CancellationInputPermissions.From(Settings, _fullWorkflowAuthorized),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                PersistTrackedOrder,
                out var failure))
        {
            _lastFailure = failure;
            return;
        }
        _operationStatus = _trackedCancellation.Status;
        _lastFailure = "None";
    }

    private void AdoptUniquePendingOrderForLifecycle()
    {
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (IsAnyInputOperationActive() || _bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            !_bankroll.IsInitialized || _bankroll.HasUnresolvedOrder || _trackedOrderState?.IsUnresolved == true ||
            _bankroll.Workflow?.IsActive == true || _bankrollStore is null || _catalogue is null)
        {
            _lastFailure = "Pending adoption requires initialized resolved bankroll and no active operation.";
            return;
        }
        var ui = GameController.Game.IngameState.IngameUi;
        var failure = string.Empty;
        if (!GameController.Window.IsForeground() || !ui.CurrencyExchangePanel.IsVisible ||
            ui.CurrencyExchangePanel.CurrencyPicker.IsVisible || ui.PopUpWindow.IsVisible ||
            !SingleLegPlacementController.TryReadOrders(GameController, out var orders, out failure) ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null ||
            !_catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out var target) || target is null)
        {
            _lastFailure = string.IsNullOrEmpty(failure)
                ? "Pending adoption requires foreground readable exchange and exact catalogue identities."
                : failure;
            return;
        }

        var candidates = orders.Where(order => order.PlayerOrderId > 0 && order.OfferedHash != 0 && order.WantedHash == target.Hash &&
            order.WantedMetadata == target.Metadata &&
            (order.OfferedMetadata == chaos.Metadata && order.OfferedHash == chaos.Hash ||
             order.OfferedMetadata == divine.Metadata && order.OfferedHash == divine.Hash) &&
            order.OfferedRatioPart > 0 && order.WantedRatioPart > 0 &&
            order.OriginalOfferedAmount > 0 && order.OriginalOfferedAmount % order.OfferedRatioPart == 0).ToArray();
        if (candidates.Length != 1)
        {
            _lastFailure = $"Lifecycle adoption requires exactly one matching core-to-target order; found {candidates.Length}.";
            return;
        }

        var order = candidates[0];
        var wanted = (BigInteger)(order.OriginalOfferedAmount / order.OfferedRatioPart) * order.WantedRatioPart;
        if (wanted <= 0 || wanted > long.MaxValue)
        {
            _lastFailure = "Pending adoption planned wanted amount overflowed or was nonpositive.";
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var tracked = new TrackedOrderState
        {
            League = GetCurrentLeague(),
            Status = TrackedOrderStatus.Pending,
            PlayerOrderId = order.PlayerOrderId,
            UpdatedAtUtc = now,
            ClickedAtUtc = order.CreationDate,
            OfferedMetadata = order.OfferedMetadata,
            WantedMetadata = order.WantedMetadata,
            OfferedAmount = order.OriginalOfferedAmount,
            WantedAmount = (long)wanted,
            GoldCost = order.GoldCost,
            AttemptId = Guid.NewGuid(),
            ProbeSessionId = Guid.NewGuid(),
            CandidateSignature = $"{order.OfferedMetadata}>{order.WantedMetadata}>{order.OfferedMetadata}",
            OfferedHash = order.OfferedHash,
            WantedHash = order.WantedHash,
            BaselineOrderIds = orders.Where(candidate => candidate.PlayerOrderId != order.PlayerOrderId)
                .Select(candidate => candidate.PlayerOrderId).Order().ToList(),
            Detail = "Explicit read-only adoption of one unique existing order for M7 lifecycle validation.",
            OrderCreationDateUtc = order.CreationDate,
            PlacedOfferedRatioPart = order.OfferedRatioPart,
            PlacedWantedRatioPart = order.WantedRatioPart,
            WaitStartedAtUtc = order.CreationDate,
            WaitUntilUtc = order.CreationDate.AddMinutes(Settings.CompetingOrderWaitMinutes.Value),
            LastObservedAtUtc = now,
            LastRemainingOfferedAmount = order.RemainingOfferedAmount,
            LastReceivedWantedAmount = order.ReceivedWantedAmount
        };

        var terminalObservation = TrackedOrderLifecycle.Evaluate(tracked, [order], now);
        var terminalStatus = terminalObservation.Kind switch
        {
            LifecycleObservationKind.Completed => TrackedOrderStatus.CompletedUncollected,
            LifecycleObservationKind.Canceled => TrackedOrderStatus.CanceledUncollected,
            _ => TrackedOrderStatus.Pending
        };
        if (order.IsCompleted || order.IsCanceled)
        {
            if (terminalStatus == TrackedOrderStatus.Pending)
            {
                _lastFailure = $"Terminal adoption rejected live state: {terminalObservation.Detail}";
                return;
            }
            tracked.Status = terminalStatus;
            tracked.Detail = $"Explicit read-only adoption of exact terminal order: {terminalObservation.Detail}";
            tracked.TerminalObservedAtUtc = now;
            tracked.TerminalRemainingOfferedAmount = order.RemainingOfferedAmount;
            tracked.TerminalReceivedWantedAmount = order.ReceivedWantedAmount;
            if (!CanceledReturnCollectionController.TryResolveTerminalAssetRow(
                    GameController, tracked, out _, out _, out var rowFailure))
            {
                _lastFailure = $"Terminal adoption rejected SDK/row mismatch: {rowFailure}";
                return;
            }
        }

        try
        {
            var next = CloneBankroll(_bankroll);
            if (!TryMoveAvailableToReserved(next, tracked.OfferedMetadata, tracked.OfferedAmount))
            {
                _lastFailure = "Isolated bankroll could not reserve the adopted pending offer.";
                return;
            }
            if (tracked.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected)
            {
                if (!TrySettleTerminal(
                        next, tracked, tracked.TerminalRemainingOfferedAmount!.Value,
                        tracked.TerminalReceivedWantedAmount!.Value))
                {
                    _lastFailure = "Isolated bankroll could not reconcile adopted terminal amounts.";
                    return;
                }
                tracked.LedgerCommittedAtUtc = now;
            }
            next.TrackedOrder = tracked;
            next.HasUnresolvedOrder = true;
            next.UpdatedAtUtc = now;
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = tracked;
            _trackedOrder = $"{tracked.Status} adopted: id={order.PlayerOrderId}, {tracked.OfferedAmount} -> {tracked.WantedAmount}";
            var adoptionEvent = tracked.Status == TrackedOrderStatus.Pending
                ? "ManualPendingOrderAdoptedForLifecycle"
                : "ManualTerminalOrderAdoptedForLifecycle";
            try { _trackedOrderStore?.AppendAudit(tracked, adoptionEvent); }
            catch (Exception auditException)
            {
                _lastFailure = $"Pending order adopted canonically, but audit append failed: {auditException.Message}";
                return;
            }
            _operationStatus = tracked.Status == TrackedOrderStatus.Pending
                ? "Adopted exact pending order without input; lifecycle polling will use its captured deadline."
                : "Adopted exact terminal order without input; terminal assets await deterministic collection.";
            _lastFailure = "None";
            _nextLifecyclePollAtUtc = DateTimeOffset.MinValue;
        }
        catch (Exception exception)
        {
            _lastFailure = $"Pending adoption persistence failed: {exception.Message}";
        }
    }

    private void SynchronizeTrackedCancellation()
    {
        if (_trackedCancellation.IsRunning)
        {
            _operationStatus = _trackedCancellation.Status;
            return;
        }
        if (_trackedCancellation.State == TrackedCancellationState.TerminalObserved)
        {
            _operationStatus = _trackedCancellation.Status;
            _lastFailure = "None";
        }
        else if (_trackedCancellation.State == TrackedCancellationState.Ambiguous)
        {
            _operationStatus = _trackedCancellation.Status;
            _lastFailure = _trackedCancellation.Failure;
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
        }
    }

    private bool IsAnyInputOperationActive() =>
        _automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning ||
        _singleLegPlacement.IsRunning || IsCollectionFlowActive() || _trackedCancellation.IsRunning ||
        _placementPreparation != PlacementPreparationState.Idle || _calibrationObservation is not null;

    private void HandleStashCollectedCurrencyHotkey()
    {
        if (_trackedCancellation.IsRunning)
        {
            _lastFailure = "Stash transfer is blocked while cancellation is active.";
            return;
        }
        if (TryGetHotkeyConflict(out var conflict))
        {
            _lastFailure = conflict;
            return;
        }
        if (IsCollectionFlowActive())
        {
            AbortCollectionFlow("Stash-transfer hotkey requested cancellation.");
            return;
        }
        if (_automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning ||
            _singleLegPlacement.IsRunning || _placementPreparation != PlacementPreparationState.Idle ||
            _calibrationObservation is not null)
        {
            _lastFailure = "Stash transfer is blocked while another input operation or calibration is active.";
            return;
        }
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            _trackedOrderState?.Status is not TrackedOrderStatus.Collected and not TrackedOrderStatus.StashTransferArmed ||
            !_bankroll.HasUnresolvedOrder)
        {
            _lastFailure = "Stash transfer requires canonical Collected or recoverable StashTransferArmed state marked unresolved.";
            return;
        }
        if (_trackedOrderState.Status == TrackedOrderStatus.Collected)
        {
            if (_trackedOrderState.PendingWantedBatchAmount > 0)
            {
                _stashTransferMetadata = _trackedOrderState.WantedMetadata;
                _stashTransferAmount = _trackedOrderState.PendingWantedBatchAmount;
            }
            else if (_trackedOrderState.PendingReturnBatchAmount > 0)
            {
                _stashTransferMetadata = _trackedOrderState.OfferedMetadata;
                _stashTransferAmount = _trackedOrderState.PendingReturnBatchAmount;
            }
            else
            {
                _lastFailure = "No ownership-verified collection batch remains pending stash custody.";
                return;
            }
        }
        else if (_trackedOrderState.StashTransferIntent is { } recoveryIntent)
        {
            _stashTransferMetadata = recoveryIntent.Metadata;
            _stashTransferAmount = recoveryIntent.Amount;
        }
        if (!StashTransferInputPermissions.From(Settings, _fullWorkflowAuthorized).Ready || !Settings.AllowQueryInput.Value)
        {
            _lastFailure = "Enable movement, clicks, query input, collection, and stash transfer; disable placement and full workflow.";
            return;
        }

        StartCollectionOwnershipRead(_trackedOrderState.Status == TrackedOrderStatus.StashTransferArmed
            ? CollectionFlowState.ReadingStashRecovery
            : CollectionFlowState.ReadingStashBaseline, _stashTransferMetadata);
    }

    private void StartCollectionOwnershipRead(CollectionFlowState phase, string? metadata = null)
    {
        var ownershipMetadata = metadata ?? _trackedOrderState?.WantedMetadata;
        if (_trackedOrderState is null || _catalogue is null || string.IsNullOrWhiteSpace(ownershipMetadata) ||
            !_catalogue.TryGetByMetadata(ownershipMetadata, out var wantedCurrency) || wantedCurrency is null ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null)
        {
            AbortCollectionFlow("Collection ownership identities were unavailable.");
            return;
        }

        var counterpart = wantedCurrency.Equals(divine) ? chaos : divine;
        _collectionOwnershipMetadata = wantedCurrency.Metadata;
        _liveOwnedByMetadata.Remove(wantedCurrency.Metadata);
        _collectionOwnershipPhaseStartedAtUtc = DateTimeOffset.UtcNow;
        var permissions = new ProbeInputPermissions(
            true,
            Settings.AllowVerifiedMouseMovement.Value,
            Settings.AllowVerifiedClicks.Value,
            Settings.AllowQueryInput.Value);
        if (!_collectionOwnershipSelector.StartOfferedOwnershipObservation(
                GameController,
                wantedCurrency,
                counterpart,
                _pickerCalibration,
                permissions,
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                out var failure))
        {
            AbortCollectionFlow(failure);
            return;
        }

        _collectionFlow = phase;
        _operationStatus = phase switch
        {
            CollectionFlowState.ReadingBaseline => $"Reading pre-collection owned {wantedCurrency.Name}.",
            CollectionFlowState.ReadingAfter => $"Reading post-collection owned {wantedCurrency.Name}.",
            CollectionFlowState.ReadingStashBaseline => $"Reading pre-stash-transfer owned {wantedCurrency.Name}.",
            CollectionFlowState.ReadingStashRecovery => $"Reading owned {wantedCurrency.Name} for interrupted stash-transfer recovery.",
            CollectionFlowState.ReadingCanceledReturnBaseline => $"Reading pre-return-collection owned {wantedCurrency.Name}.",
            CollectionFlowState.ReadingCanceledReturnAfter => $"Reading post-return-collection owned {wantedCurrency.Name}.",
            _ => $"Reading post-stash-transfer owned {wantedCurrency.Name}."
        };
    }

    private void SynchronizeCollectionOwnershipRead()
    {
        if (_collectionOwnershipSelector.State == AutomatedProbeState.Completed)
        {
            _collectionOwnershipSelector.AcknowledgeCompletion();
            if (!TryGetFreshOwned(_collectionOwnershipMetadata, out var owned, out var failure))
            {
                AbortCollectionFlow(failure);
                return;
            }

            if (_collectionFlow == CollectionFlowState.ReadingBaseline)
            {
                _collectionOwnedBaseline = owned;
                if (!InventoryStashTransferController.TryReadSnapshot(
                        GameController, _trackedOrderState!.WantedMetadata, out var inventory, out failure) ||
                    inventory.TargetInventoryAmount != 0)
                {
                    AbortCollectionFlow(string.IsNullOrEmpty(failure)
                        ? "Simple collection requires zero pre-existing wanted currency in inventory for exact custody."
                        : failure);
                    return;
                }
                if (!InventoryTransferEvidence.TryGetConservativeCollectionCapacity(
                        inventory with
                        {
                            TargetMaxStackSize = inventory.TargetMaxStackSize > 0
                                ? inventory.TargetMaxStackSize
                                : _trackedOrderState.WantedMaxStackSize
                        }, out var capacity, out failure))
                {
                    AbortCollectionFlow(failure);
                    return;
                }
                var remainingProceeds = _trackedOrderState.TerminalReceivedWantedAmount is null
                    ? _trackedOrderState.WantedAmount
                    : TrackedOrderLifecycle.RemainingWantedToCollect(_trackedOrderState);
                if (inventory.TargetMaxStackSize <= 0 && _trackedOrderState.WantedMaxStackSize <= 0 &&
                    remainingProceeds > capacity)
                {
                    AbortCollectionFlow(
                        $"First acquisition of {_trackedOrderState.WantedMetadata} exceeds the {capacity}-unit " +
                        "capacity provable without an existing stack; seed one visible Currency Stash stack first.");
                    return;
                }
                _collectionBatchAmount = Math.Min(capacity, remainingProceeds);
                if (_collectionBatchAmount <= 0)
                {
                    AbortCollectionFlow("No verified free inventory capacity was available for a collection batch.");
                    return;
                }
                if (!_trackedCollection.Start(
                        GameController,
                        _trackedOrderState!,
                        _pickerCalibration,
                        CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized),
                        IsFullFaustusControllerEnabled(),
                        Settings.CursorTweenSpeed.Value,
                        _collectionBatchAmount,
                        _collectionOwnedBaseline,
                        PersistTrackedOrder,
                        out failure))
                {
                    AbortCollectionFlow(failure);
                    return;
                }

                _collectionFlow = CollectionFlowState.ClickingTrackedOrder;
                _operationStatus = $"Pre-collection owned count {_collectionOwnedBaseline}; collecting one exact batch of {_collectionBatchAmount}.";
            }
            else if (_collectionFlow == CollectionFlowState.ReadingAfter)
            {
                var expected = checked(_collectionOwnedBaseline + _collectionBatchAmount);
                if (owned != expected)
                {
                    MarkCollectionAmbiguous($"Post-collection owned count was {owned}, expected exactly {expected}.");
                    return;
                }

                SettleVerifiedCollection(owned);
            }
            else if (_collectionFlow == CollectionFlowState.ReadingStashBaseline)
            {
                _collectionOwnedBaseline = owned;
                if (!_inventoryStashTransfer.Start(
                        GameController,
                        _trackedOrderState!,
                        StashTransferInputPermissions.From(Settings, _fullWorkflowAuthorized),
                        IsFullFaustusControllerEnabled(),
                        Settings.CursorTweenSpeed.Value,
                        _stashTransferMetadata,
                        _stashTransferAmount,
                        owned,
                        PersistTrackedOrder,
                        out failure))
                {
                    AbortCollectionFlow(failure);
                    return;
                }

                _collectionFlow = CollectionFlowState.TransferringToStash;
                _operationStatus = $"Pre-transfer owned count {owned}; moving exact collected inventory amount to Currency Stash.";
            }
            else if (_collectionFlow == CollectionFlowState.ReadingStashAfter)
            {
                if (owned != _collectionOwnedBaseline)
                {
                    MarkStashTransferAmbiguous(
                        $"Aggregate owned count changed across inventory-to-stash transfer: {_collectionOwnedBaseline} -> {owned}.");
                    return;
                }
                SettleVerifiedStashTransfer(owned);
            }
            else if (_collectionFlow == CollectionFlowState.ReadingStashRecovery)
            {
                ReconcileInterruptedStashTransfer(owned);
            }
            else if (_collectionFlow == CollectionFlowState.ReadingCanceledReturnBaseline)
            {
                _collectionOwnedBaseline = owned;
                if (!_canceledReturnCollection.Start(
                        GameController,
                        _trackedOrderState!,
                        _pickerCalibration,
                        CollectionInputPermissions.From(Settings, _fullWorkflowAuthorized),
                        IsFullFaustusControllerEnabled(),
                        Settings.CursorTweenSpeed.Value,
                        owned,
                        PersistTrackedOrder,
                        out failure))
                {
                    AbortCollectionFlow(failure);
                    return;
                }
                _collectionFlow = CollectionFlowState.CollectingCanceledReturn;
                _operationStatus = $"Pre-asset owned count {owned}; collecting next exact terminal settlement asset once.";
            }
            else if (_collectionFlow == CollectionFlowState.ReadingCanceledReturnAfter)
            {
                var amount = _trackedOrderState!.CollectionAssetIntent!.Amount;
                var expected = checked(_collectionOwnedBaseline + amount);
                if (owned != expected)
                {
                    MarkCollectionAmbiguous($"Post-asset owned count was {owned}, expected exactly {expected}.");
                    return;
                }
                SettleVerifiedCanceledReturn(owned);
            }
            return;
        }

        if (_collectionOwnershipSelector.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed &&
            _collectionFlow is (CollectionFlowState.ReadingBaseline or CollectionFlowState.ReadingAfter or
                CollectionFlowState.ReadingStashBaseline or CollectionFlowState.ReadingStashAfter or
                CollectionFlowState.ReadingStashRecovery or CollectionFlowState.ReadingCanceledReturnBaseline or
                CollectionFlowState.ReadingCanceledReturnAfter))
        {
            AbortCollectionFlow(_collectionOwnershipSelector.Failure);
        }
    }

    private void SynchronizeTrackedCollection()
    {
        if (_trackedCollection.IsRunning)
        {
            _operationStatus = _trackedCollection.Status;
            return;
        }

        if (_collectionFlow != CollectionFlowState.ClickingTrackedOrder)
        {
            return;
        }

        if (_trackedCollection.State == TrackedCollectionState.CollectedEvidence)
        {
            StartCollectionOwnershipRead(CollectionFlowState.ReadingAfter);
        }
        else if (_trackedCollection.State is TrackedCollectionState.Ambiguous or TrackedCollectionState.Cancelled)
        {
            AbortCollectionFlow(_trackedCollection.Failure);
        }
    }

    private void SynchronizeCanceledReturnCollection()
    {
        if (_canceledReturnCollection.IsRunning)
        {
            _operationStatus = _canceledReturnCollection.Status;
            return;
        }
        if (_collectionFlow != CollectionFlowState.CollectingCanceledReturn) return;
        if (_canceledReturnCollection.State == CanceledReturnCollectionState.CollectedEvidence)
        {
            StartCollectionOwnershipRead(
                CollectionFlowState.ReadingCanceledReturnAfter,
                _trackedOrderState!.CollectionAssetIntent!.Metadata);
        }
        else if (_canceledReturnCollection.State is CanceledReturnCollectionState.Ambiguous or
                 CanceledReturnCollectionState.Cancelled)
        {
            AbortCollectionFlow(_canceledReturnCollection.Failure);
        }
    }

    private void SettleVerifiedCanceledReturn(long observedOwned)
    {
        if (_bankrollStore is null || _trackedOrderState?.Status != TrackedOrderStatus.CollectionArmed ||
            _trackedOrderState.CollectionAssetIntent is not { } intent ||
            _trackedOrderState.TerminalRemainingOfferedAmount is not { } remaining ||
            _trackedOrderState.TerminalReceivedWantedAmount is not { } received)
        {
            MarkCollectionAmbiguous("Canonical terminal-asset collection intent was unavailable during settlement.");
            return;
        }
        try
        {
            if (!_canceledReturnCollection.VerifyPostState(GameController, out var custodyFailure))
            {
                MarkCollectionAmbiguous(custodyFailure);
                return;
            }
            var sideRemaining = intent.WantedSlot
                ? TrackedOrderLifecycle.RemainingWantedToCollect(_trackedOrderState)
                : TrackedOrderLifecycle.RemainingReturnToCollect(_trackedOrderState);
            if (intent.Amount <= 0 || intent.Amount > sideRemaining ||
                intent.Metadata != (intent.WantedSlot
                    ? _trackedOrderState.WantedMetadata
                    : _trackedOrderState.OfferedMetadata))
            {
                throw new InvalidDataException("Collection intent did not match the remaining terminal settlement amount.");
            }
            var next = CloneBankroll(_bankroll);
            if (!TryCreditCollected(next, intent.Metadata, intent.Amount))
            {
                throw new InvalidDataException("Completed-uncollected batch did not match canonical currency bucket.");
            }
            var progress = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Collected,
                $"Verified terminal {(intent.WantedSlot ? "wanted proceeds" : "offered return")} batch of {intent.Amount} entered inventory; owned count is {observedOwned}.");
            progress.CollectionAssetIntent = null;
            if (intent.WantedSlot)
            {
                progress.PendingWantedBatchAmount = intent.Amount;
                progress.WantedAssetCollected =
                    progress.SettledWantedAmount + progress.PendingWantedBatchAmount ==
                    TrackedOrderLifecycle.TotalWantedProceeds(progress);
            }
            else
            {
                progress.PendingReturnBatchAmount = intent.Amount;
                progress.OfferedReturnCollected =
                    progress.SettledReturnAmount + progress.PendingReturnBatchAmount ==
                    TrackedOrderLifecycle.TotalOfferedReturn(progress);
            }
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            next.TrackedOrder = progress;
            next.HasUnresolvedOrder = progress.IsUnresolved;
            next.UpdatedAtUtc = progress.UpdatedAtUtc;
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = progress;
            _trackedOrder = $"Collected terminal batch of {intent.Amount} {intent.Metadata}";
            try { _trackedOrderStore?.AppendAudit(progress, "TerminalAssetBatchCollectedAndCredited"); }
            catch (Exception auditException) { _lastFailure = $"Terminal batch settled canonically, audit append failed: {auditException.Message}"; }
            _collectionFlow = CollectionFlowState.Idle;
            _operationStatus = "Terminal batch collected and credited; verified stash custody is required before the next batch.";
            if (!_lastFailure.StartsWith("Terminal batch settled", StringComparison.Ordinal)) _lastFailure = "None";
        }
        catch (Exception exception)
        {
            MarkCollectionAmbiguous($"Terminal asset settlement failed: {exception.Message}");
        }
    }

    private void SynchronizeInventoryStashTransfer()
    {
        if (_inventoryStashTransfer.IsRunning)
        {
            _operationStatus = _inventoryStashTransfer.Status;
            return;
        }
        if (_collectionFlow != CollectionFlowState.TransferringToStash) return;

        if (_inventoryStashTransfer.State == InventoryStashTransferState.TransferEvidence)
        {
            StartCollectionOwnershipRead(CollectionFlowState.ReadingStashAfter, _stashTransferMetadata);
        }
        else if (_inventoryStashTransfer.State is InventoryStashTransferState.Ambiguous or
                 InventoryStashTransferState.Cancelled)
        {
            AbortCollectionFlow(_inventoryStashTransfer.Failure);
        }
    }

    private void SettleVerifiedStashTransfer(long observedOwned)
    {
        if (_trackedOrderState?.Status != TrackedOrderStatus.StashTransferArmed ||
            _trackedOrderState.StashTransferIntent is not { } intent)
        {
            MarkStashTransferAmbiguous("Canonical stash-transfer-armed state was unavailable during settlement.");
            return;
        }

        var wantedSide = intent.Metadata == _trackedOrderState.WantedMetadata;
        var pendingBatch = wantedSide
            ? _trackedOrderState.PendingWantedBatchAmount
            : _trackedOrderState.PendingReturnBatchAmount;
        if (pendingBatch <= 0 || intent.Amount != pendingBatch ||
            !wantedSide && intent.Metadata != _trackedOrderState.OfferedMetadata)
        {
            MarkStashTransferAmbiguous("Stash intent did not match the exact pending collection batch.");
            return;
        }
        var stashed = TrackedOrderCollectionController.CloneTracked(
            _trackedOrderState,
            TrackedOrderStatus.Collected,
            $"Verified {intent.Amount} {intent.Metadata} left inventory, entered visible Currency Stash, and aggregate ownership remained {observedOwned}.");
        if (wantedSide)
        {
            stashed.SettledWantedAmount = checked(stashed.SettledWantedAmount + pendingBatch);
            stashed.PendingWantedBatchAmount = 0;
            stashed.WantedAssetStashed =
                stashed.SettledWantedAmount == TrackedOrderLifecycle.TotalWantedProceeds(stashed);
        }
        else
        {
            stashed.SettledReturnAmount = checked(stashed.SettledReturnAmount + pendingBatch);
            stashed.PendingReturnBatchAmount = 0;
            stashed.OfferedReturnStashed =
                stashed.SettledReturnAmount == TrackedOrderLifecycle.TotalOfferedReturn(stashed);
        }
        if (!_inventoryStashTransfer.VerifyPostState(GameController, out var custodyFailure))
        {
            MarkStashTransferAmbiguous(custodyFailure);
            return;
        }
        stashed.StashTransferIntent = null;
        var remainingToCollect = TrackedOrderLifecycle.RemainingToCollect(stashed);
        var anotherBatchPending = stashed.PendingWantedBatchAmount > 0 || stashed.PendingReturnBatchAmount > 0;
        var allStashed = remainingToCollect == 0 &&
            !anotherBatchPending;
        stashed.Status = allStashed
            ? TrackedOrderStatus.Stashed
            : anotherBatchPending
                ? TrackedOrderStatus.Collected
                : TrackedOrderStatus.CompletedUncollected;
        var eventType = allStashed ? "TerminalAssetsStashedAndVerified" : "CollectionBatchStashProgressVerified";
        if (!PersistTrackedOrder(stashed, eventType))
        {
            MarkStashTransferAmbiguous("Could not persist verified stashed state.");
            return;
        }

        _collectionFlow = CollectionFlowState.Idle;
        _operationStatus = allStashed
            ? "Lifecycle custody complete: every collected batch is verified in Currency Stash."
            : $"Batch stashed; {remainingToCollect} settlement units remain to collect in further batches.";
        _lastFailure = "None";
    }

    private void ReconcileInterruptedStashTransfer(long observedOwned)
    {
        var intent = _trackedOrderState?.StashTransferIntent;
        if (_trackedOrderState?.Status != TrackedOrderStatus.StashTransferArmed || intent is null)
        {
            MarkStashTransferAmbiguous("Interrupted stash-transfer identity or aggregate ownership did not match durable intent.");
            return;
        }
        if (!InventoryStashTransferController.TryReadSnapshot(
                GameController, intent.Metadata, out var current, out var failure))
        {
            MarkStashTransferAmbiguous(failure);
            return;
        }
        var recovery = InventoryTransferEvidence.ClassifyRecovery(
            intent,
            current,
            intent.Metadata,
            intent.Amount,
            observedOwned,
            GameController.Game.IngameState.ServerData.InstanceId);
        if (recovery == InventoryTransferEvidence.RecoveryKind.PreTransfer)
        {
            var collected = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Collected,
                "Recovered interrupted stash-transfer intent: exact pre-click state remained; no retry performed.");
            collected.StashTransferIntent = null;
            if (!PersistTrackedOrder(collected, "CollectedCurrencyStashTransferRecoveredBeforeClick"))
            {
                MarkStashTransferAmbiguous("Could not persist recovered pre-click stash-transfer state.");
                return;
            }
            _collectionFlow = CollectionFlowState.Idle;
            _operationStatus = "Recovered exact pre-click inventory/stash state; press stash-transfer hotkey again for a new authorization.";
            _lastFailure = "None";
            var wasAuthorized = _fullWorkflowAuthorized;
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
            _startingNewWorkflow = false;
            _nextWorkflowScanAtUtc = null;
            if (wasAuthorized) RecordContinuousAuthorizationRevoked(_operationStatus);
            return;
        }

        if (recovery == InventoryTransferEvidence.RecoveryKind.PostTransfer)
        {
            var wantedSide = intent.Metadata == _trackedOrderState.WantedMetadata;
            var pendingBatch = wantedSide
                ? _trackedOrderState.PendingWantedBatchAmount
                : _trackedOrderState.PendingReturnBatchAmount;
            if (pendingBatch <= 0 || intent.Amount != pendingBatch ||
                !wantedSide && intent.Metadata != _trackedOrderState.OfferedMetadata)
            {
                MarkStashTransferAmbiguous("Recovered stash intent did not match the exact pending collection batch.");
                return;
            }
            var stashed = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Collected,
                "Recovered interrupted stash transfer from exact post-state and unchanged aggregate ownership.");
            if (wantedSide)
            {
                stashed.SettledWantedAmount = checked(stashed.SettledWantedAmount + pendingBatch);
                stashed.PendingWantedBatchAmount = 0;
                stashed.WantedAssetStashed =
                    stashed.SettledWantedAmount == TrackedOrderLifecycle.TotalWantedProceeds(stashed);
            }
            else
            {
                stashed.SettledReturnAmount = checked(stashed.SettledReturnAmount + pendingBatch);
                stashed.PendingReturnBatchAmount = 0;
                stashed.OfferedReturnStashed =
                    stashed.SettledReturnAmount == TrackedOrderLifecycle.TotalOfferedReturn(stashed);
            }
            stashed.StashTransferIntent = null;
            var allStashed = TrackedOrderLifecycle.RemainingToCollect(stashed) == 0 &&
                stashed.PendingWantedBatchAmount == 0 && stashed.PendingReturnBatchAmount == 0;
            var anotherBatchPending = stashed.PendingWantedBatchAmount > 0 || stashed.PendingReturnBatchAmount > 0;
            stashed.Status = allStashed
                ? TrackedOrderStatus.Stashed
                : anotherBatchPending
                    ? TrackedOrderStatus.Collected
                    : TrackedOrderStatus.CompletedUncollected;
            if (!PersistTrackedOrder(stashed, allStashed
                    ? "TerminalAssetsStashRecoveredAndVerified"
                    : "CollectionBatchStashProgressRecovered"))
            {
                MarkStashTransferAmbiguous("Could not persist recovered post-transfer state.");
                return;
            }
            _collectionFlow = CollectionFlowState.Idle;
            _operationStatus = allStashed
                ? "Lifecycle custody complete after exact stash-transfer recovery."
                : "Recovered one stashed terminal asset; authorize the remaining stash transfer separately.";
            _lastFailure = "None";
            return;
        }

        MarkStashTransferAmbiguous("Interrupted stash transfer matched neither exact durable pre-state nor exact post-state.");
    }

    private void MarkStashTransferAmbiguous(string reason)
    {
        if (_trackedOrderState is not null && _trackedOrderState.Status != TrackedOrderStatus.Ambiguous)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Ambiguous, reason);
            if (!PersistTrackedOrder(ambiguous, "CollectedCurrencyStashTransferOwnershipAmbiguous"))
            {
                _trackedOrderLoadBlocked = true;
                reason = $"{reason} Canonical ambiguity persistence failed; hard block retained.";
            }
        }
        _collectionFlow = CollectionFlowState.Idle;
        _lastFailure = reason;
        _operationStatus = $"AMBIGUOUS stash transfer: {reason}";
    }

    private bool TryGetFreshOwned(string metadata, out long owned, out string failure)
    {
        if (!_liveOwnedByMetadata.TryGetValue(metadata, out var observation) ||
            observation.AreaInstanceId != GameController.Game.IngameState.ServerData.InstanceId ||
            observation.ObservedAtUtc < _collectionOwnershipPhaseStartedAtUtc || observation.StableReads < 2)
        {
            owned = 0;
            failure = "Fresh exact ownership observation was unavailable.";
            return false;
        }

        var age = DateTimeOffset.UtcNow - observation.ObservedAtUtc;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(Settings.MaximumQuoteAgeSeconds.Value))
        {
            owned = 0;
            failure = "Exact ownership observation was stale or future-dated.";
            return false;
        }

        owned = observation.Count;
        failure = string.Empty;
        return true;
    }

    private void SettleVerifiedCollection(long observedOwned)
    {
        if (_bankrollStore is null || _trackedOrderState is null ||
            _trackedOrderState.Status != TrackedOrderStatus.CollectionArmed)
        {
            MarkCollectionAmbiguous("Canonical collection-armed state was unavailable during settlement.");
            return;
        }

        try
        {
            if (!_trackedCollection.VerifyUnrelatedOrders(GameController, out var unrelatedFailure))
            {
                MarkCollectionAmbiguous(unrelatedFailure);
                return;
            }
            if (!_trackedCollection.VerifyInventoryPostState(GameController, out var custodyFailure))
            {
                MarkCollectionAmbiguous(custodyFailure);
                return;
            }

            var next = CloneBankroll(_bankroll);
            var amount = _collectionBatchAmount;
            if (amount <= 0 || !TryCreditCollected(next, _trackedOrderState.WantedMetadata, amount))
            {
                throw new InvalidDataException("Completed-uncollected batch proceeds did not match canonical currency bucket.");
            }

            var totalProceeds = TrackedOrderLifecycle.TotalWantedProceeds(_trackedOrderState);
            var collected = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState,
                TrackedOrderStatus.Collected,
                $"Verified exact batch of {amount} proceeds entered inventory and owned count rose to {observedOwned}.");
            collected.CollectionAssetIntent = null;
            collected.PendingWantedBatchAmount = amount;
            collected.WantedAssetCollected =
                collected.SettledWantedAmount + collected.PendingWantedBatchAmount == totalProceeds;
            next.TrackedOrder = collected;
            next.HasUnresolvedOrder = collected.IsUnresolved;
            next.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = collected;
            _trackedOrder = $"Collected: id={collected.PlayerOrderId}, credited {amount} {collected.WantedMetadata}";
            _collectionFlow = CollectionFlowState.Idle;
            _operationStatus = $"Collected and credited {amount} in inventory; verified stash transfer remains required.";
            _lastFailure = "None";
            try
            {
                _trackedOrderStore?.AppendAudit(collected, "OrderCollectionVerifiedAndCredited");
            }
            catch (Exception auditException)
            {
                _lastFailure = $"Collection settled canonically, but audit append failed: {auditException.Message}";
            }
        }
        catch (Exception exception)
        {
            MarkCollectionAmbiguous($"Collection settlement persistence failed: {exception.Message}");
        }
    }

    private bool TryCreditCollected(BankrollState state, string metadata, long amount)
    {
        return _catalogue?.TryGetUniqueByName("Chaos Orb", out var chaos) == true && chaos is not null &&
            _catalogue.TryGetUniqueByName("Divine Orb", out var divine) && divine is not null &&
            BankrollAccounting.TryCreditCollected(state, metadata, amount, chaos.Metadata, divine.Metadata);
    }

    private void MarkCollectionAmbiguous(string reason)
    {
        if (_trackedOrderState is not null)
        {
            var ambiguous = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Ambiguous, reason);
            if (!PersistTrackedOrder(ambiguous, "OrderCollectionOwnershipAmbiguous"))
            {
                _trackedOrderLoadBlocked = true;
                reason = $"{reason} Canonical ambiguity persistence failed; hard block retained.";
            }
        }
        _collectionFlow = CollectionFlowState.Idle;
        _lastFailure = reason;
        _operationStatus = $"AMBIGUOUS collection: {reason}";
    }

    private void AbortCollectionFlow(string reason)
    {
        _fullWorkflowAuthorized = false;
        _workflowAuthorization = null;
        var collectionAfterClick = _trackedOrderState?.Status == TrackedOrderStatus.CollectionArmed ||
            _trackedCollection.State == TrackedCollectionState.CollectedEvidence ||
            _collectionFlow == CollectionFlowState.ReadingAfter;
        var stashAfterClick = _trackedOrderState?.Status == TrackedOrderStatus.StashTransferArmed ||
            _inventoryStashTransfer.State == InventoryStashTransferState.TransferEvidence ||
            _collectionFlow == CollectionFlowState.ReadingStashAfter;
        if (_trackedCollection.IsRunning) _trackedCollection.Cancel(reason);
        if (_canceledReturnCollection.IsRunning) _canceledReturnCollection.Cancel(reason);
        if (_inventoryStashTransfer.IsRunning) _inventoryStashTransfer.Cancel(reason);
        if (_collectionOwnershipSelector.IsRunning) _collectionOwnershipSelector.Cancel(reason);
        if (stashAfterClick)
        {
            MarkStashTransferAmbiguous(reason);
            return;
        }
        if (collectionAfterClick)
        {
            MarkCollectionAmbiguous(reason);
            return;
        }
        _collectionFlow = CollectionFlowState.Idle;
        _lastFailure = reason;
        _operationStatus = $"Collection cancelled: {reason}";
    }

    private bool IsPlacementFlowActive() =>
        _placementPreparation != PlacementPreparationState.Idle || _singleLegPlacement.IsRunning;

    private bool IsCollectionFlowActive() =>
        _collectionFlow != CollectionFlowState.Idle || _trackedCollection.IsRunning ||
        _collectionOwnershipSelector.IsRunning || _inventoryStashTransfer.IsRunning ||
        _canceledReturnCollection.IsRunning;

    private bool IsStashTransferFlow() =>
        _collectionFlow is CollectionFlowState.ReadingStashBaseline or
            CollectionFlowState.TransferringToStash or CollectionFlowState.ReadingStashAfter or
            CollectionFlowState.ReadingStashRecovery ||
        _inventoryStashTransfer.IsRunning;

    private void AbortPlacementFlow(string reason)
    {
        _fullWorkflowAuthorized = false;
        _workflowAuthorization = null;
        if (_singleLegPlacement.IsRunning)
        {
            _singleLegPlacement.Cancel(reason);
        }
        if (_automatedProbe.IsRunning)
        {
            _automatedProbe.Cancel(reason);
        }
        if (_placementLegRefresh.IsRunning)
        {
            _placementLegRefresh.Cancel(reason);
        }
        if (_singleLegStaging.IsRunning)
        {
            _singleLegStaging.Cancel(reason);
        }
        _placementPreparation = PlacementPreparationState.Idle;
        _placementToken = null;
        _lastFailure = reason;
    }

    private static BankrollState CloneBankroll(BankrollState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        League = state.League,
        IsInitialized = state.IsInitialized,
        SeededChaos = state.SeededChaos,
        SeededDivine = state.SeededDivine,
        AvailableChaos = state.AvailableChaos,
        AvailableDivine = state.AvailableDivine,
        ReservedChaos = state.ReservedChaos,
        ReservedDivine = state.ReservedDivine,
        CompletedUncollectedChaos = state.CompletedUncollectedChaos,
        CompletedUncollectedDivine = state.CompletedUncollectedDivine,
        TargetMetadata = state.TargetMetadata,
        AvailableTarget = state.AvailableTarget,
        ReservedTarget = state.ReservedTarget,
        CompletedUncollectedTarget = state.CompletedUncollectedTarget,
        HasUnresolvedOrder = state.HasUnresolvedOrder,
        TrackedOrder = state.TrackedOrder,
        Workflow = state.Workflow is null ? null : WorkflowCoordinator.Clone(state.Workflow),
        UpdatedAtUtc = state.UpdatedAtUtc
    };

    private bool TryMoveAvailableToReserved(BankrollState state, string metadata, long amount)
    {
        return _catalogue?.TryGetUniqueByName("Chaos Orb", out var chaos) == true && chaos is not null &&
            _catalogue.TryGetUniqueByName("Divine Orb", out var divine) && divine is not null &&
            BankrollAccounting.TryReserve(state, metadata, amount, chaos.Metadata, divine.Metadata);
    }

    private bool TryMoveReservedToCompletedUncollected(
        BankrollState state,
        string offeredMetadata,
        long offeredAmount,
        string wantedMetadata,
        long wantedAmount)
    {
        if (_catalogue?.TryGetUniqueByName("Chaos Orb", out var chaos) != true ||
            _catalogue.TryGetUniqueByName("Divine Orb", out var divine) != true || chaos is null || divine is null)
        {
            return false;
        }

        return BankrollAccounting.TryCompleteUncollected(
            state, offeredMetadata, offeredAmount, wantedMetadata, wantedAmount,
            chaos.Metadata, divine.Metadata);
    }

    private bool TrySettleTerminal(
        BankrollState state,
        TrackedOrderState tracked,
        long remainingOffered,
        long receivedWanted)
    {
        return _catalogue?.TryGetUniqueByName("Chaos Orb", out var chaos) == true && chaos is not null &&
            _catalogue.TryGetUniqueByName("Divine Orb", out var divine) && divine is not null &&
            BankrollAccounting.TrySettleTerminal(
                state,
                tracked.OfferedMetadata,
                tracked.OfferedAmount,
                remainingOffered,
                tracked.WantedMetadata,
                receivedWanted,
                chaos.Metadata,
                divine.Metadata);
    }

    private bool TryGetHotkeyConflict(out string failure)
    {
        var bindings = new[]
        {
            Binding(nameof(Settings.CalibratePickerButtonHotkey), Settings.CalibratePickerButtonHotkey),
            Binding(nameof(Settings.CalibratePlaceOrderHotkey), Settings.CalibratePlaceOrderHotkey),
            Binding(nameof(Settings.CalibrateCollectionHotkey), Settings.CalibrateCollectionHotkey),
            Binding(nameof(Settings.CalibrateCancelHotkey), Settings.CalibrateCancelHotkey),
            Binding(nameof(Settings.CalibrateReturnSlotHotkey), Settings.CalibrateReturnSlotHotkey),
            Binding(nameof(Settings.ProbeMarketsHotkey), Settings.ProbeMarketsHotkey),
            Binding(nameof(Settings.CaptureCurrentPairHotkey), Settings.CaptureCurrentPairHotkey),
            Binding(nameof(Settings.DumpSdkReadsHotkey), Settings.DumpSdkReadsHotkey),
            Binding(nameof(Settings.ExecuteSingleLegHotkey), Settings.ExecuteSingleLegHotkey),
            Binding(nameof(Settings.PlaceStagedLegHotkey), Settings.PlaceStagedLegHotkey),
            Binding(nameof(Settings.CollectTrackedOrderHotkey), Settings.CollectTrackedOrderHotkey),
            Binding(nameof(Settings.StashCollectedCurrencyHotkey), Settings.StashCollectedCurrencyHotkey),
            Binding(nameof(Settings.CancelTimedOutOrderHotkey), Settings.CancelTimedOutOrderHotkey),
            Binding(nameof(Settings.AdoptPendingOrderHotkey), Settings.AdoptPendingOrderHotkey),
            Binding(nameof(Settings.FullWorkflowHotkey), Settings.FullWorkflowHotkey),
        };
        var duplicate = bindings
            .Where(binding => binding.Active)
            .GroupBy(binding => binding.Signature, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is null)
        {
            failure = string.Empty;
            return false;
        }

        failure = $"Hotkey conflict on {duplicate.Key}: {string.Join(", ", duplicate.Select(binding => binding.Name))}. No input was sent.";
        return true;

        static (string Name, string Signature, bool Active) Binding(
            string name,
            ExileCore.Shared.Nodes.HotkeyNodeV2 node)
        {
            var value = node.Value;
            var active = value.Key != Keys.None || !string.Equals(value.ControllerKey.ToString(), "None", StringComparison.Ordinal);
            return (name,
                $"key={value.Key};win={value.Win};controller={value.ControllerKey};modifier={value.ControllerModifierKey}",
                active);
        }
    }

    private bool IsFullFaustusControllerEnabled()
    {
        try
        {
            var wrapper = PluginManager.Plugins.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, "FaustusController", StringComparison.Ordinal));
            if (wrapper?.IsEnable != true)
            {
                return false;
            }

            if (!AnyLiteInputPermissionEnabled())
            {
                return true;
            }

            wrapper.Plugin._Settings.Enable.Value = false;
            if (!wrapper.Plugin._Settings.Enable.Value)
            {
                _operationStatus = "Disabled the full FaustusController to enforce exclusive Lite input ownership.";
                return false;
            }

            DisableLiteInputPermissions();
            return true;
        }
        catch
        {
            DisableLiteInputPermissions();
            return true;
        }
    }

    private bool AnyLiteInputPermissionEnabled() =>
        Settings.AllowAutomatedProbing.Value || Settings.AllowVerifiedMouseMovement.Value ||
        Settings.AllowVerifiedClicks.Value || Settings.AllowQueryInput.Value || Settings.AllowAmountInput.Value ||
        Settings.AllowOrderPlacement.Value || Settings.AllowOrderCancellation.Value ||
        Settings.AllowOrderCollection.Value || Settings.AllowStashTransfer.Value || Settings.AllowFullWorkflow.Value;

    private void DisableLiteInputPermissions()
    {
        Settings.AllowAutomatedProbing.Value = false;
        Settings.AllowVerifiedMouseMovement.Value = false;
        Settings.AllowVerifiedClicks.Value = false;
        Settings.AllowQueryInput.Value = false;
        Settings.AllowAmountInput.Value = false;
        Settings.AllowOrderPlacement.Value = false;
        Settings.AllowOrderCancellation.Value = false;
        Settings.AllowOrderCollection.Value = false;
        Settings.AllowStashTransfer.Value = false;
        Settings.AllowFullWorkflow.Value = false;
    }

    private string DescribePickerCalibration() =>
        $"offered={(_pickerCalibration.OfferedButton?.IsValid == true ? "ready" : "missing")}, " +
        $"wanted={(_pickerCalibration.WantedButton?.IsValid == true ? "ready" : "missing")}";

    private void CalibratePlaceOrderTarget()
    {
        try
        {
            var panel = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel;
            var rect = panel.GetClientRectCache;
            var cursor = ExileInput.MousePositionNum;
            if (!GameController.Window.IsForeground() || !panel.IsVisible || panel.CurrencyPicker.IsVisible ||
                rect.Width <= 0 || rect.Height <= 0 ||
                cursor.X < rect.X || cursor.X > rect.X + rect.Width ||
                cursor.Y < rect.Y || cursor.Y > rect.Y + rect.Height ||
                ExileInput.IsKeyDown(Keys.ControlKey) || ExileInput.IsKeyDown(Keys.ShiftKey) ||
                ExileInput.IsKeyDown(Keys.Menu))
            {
                _lastFailure = "Place Order calibration requires foreground exchange, closed picker, and cursor over the intended button.";
                return;
            }

            var candidate = new PickerCalibration
            {
                OfferedButton = _pickerCalibration.OfferedButton,
                WantedButton = _pickerCalibration.WantedButton,
                PlaceOrderButton = _pickerCalibration.PlaceOrderButton,
                PlaceOrderPanelAspectRatio = _pickerCalibration.PlaceOrderPanelAspectRatio,
                CollectionSlotOffset = _pickerCalibration.CollectionSlotOffset,
                CollectionRowAspectRatio = _pickerCalibration.CollectionRowAspectRatio,
                CancelButtonOffset = _pickerCalibration.CancelButtonOffset,
                CancelRowAspectRatio = _pickerCalibration.CancelRowAspectRatio,
                ReturnSlotOffset = _pickerCalibration.ReturnSlotOffset,
                ReturnRowAspectRatio = _pickerCalibration.ReturnRowAspectRatio
            };
            if (!candidate.TryRecordPlaceOrder(
                    rect.X, rect.Y, rect.Width, rect.Height, cursor.X, cursor.Y, out var failure))
            {
                _lastFailure = failure;
                return;
            }

            _pickerCalibrationStore.Save(_pickerCalibrationPath, candidate);
            _pickerCalibration = candidate;
            _operationStatus = "Recorded normalized Place Order target without clicking it.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Place Order calibration failed: {exception.Message}";
        }
    }

    private void ObservePickerOwnership()
    {
        try
        {
            var picker = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
            if (!picker.IsVisible || picker.IsPickingWantedCurrency)
            {
                return;
            }

            var reads = picker.Options
                .Where(option => option?.ItemType is not null &&
                    !string.IsNullOrWhiteSpace(option.ItemType.Metadata) && option.Owned >= 0)
                .GroupBy(option => option!.ItemType!.Metadata, StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var area = GameController.Game.IngameState.ServerData.InstanceId;
            foreach (var group in reads)
            {
                var ownedValues = group.Select(option => (long)option!.Owned).Distinct().ToArray();
                if (ownedValues.Length != 1)
                {
                    _liveOwnedByMetadata.Remove(group.Key);
                    _lastFailure = $"Picker returned conflicting owned counts for {group.Key}.";
                    continue;
                }

                var owned = ownedValues[0];
                var stableReads = _liveOwnedByMetadata.TryGetValue(group.Key, out var previous) &&
                    previous.Count == owned && previous.AreaInstanceId == area
                        ? checked(previous.StableReads + 1)
                        : 1;
                _liveOwnedByMetadata[group.Key] = new OwnershipObservation(owned, now, area, stableReads);
            }
        }
        catch (Exception exception)
        {
            _lastFailure = $"Picker ownership read failed: {exception.Message}";
        }
    }

    /// <summary>
    /// Recalculates the best route. <see cref="CandidateOutcome.NoneAccepted"/> means the planner ran to
    /// completion and accepted nothing; every unavailable prerequisite or error is <see cref="CandidateOutcome.Blocked"/>.
    /// </summary>
    private CandidateOutcome CalculateCandidate(bool invalidateStaging = true)
    {
        if (invalidateStaging && _singleLegStaging.State == SingleLegStagingState.Staged)
        {
            _singleLegStaging.Invalidate("Candidate was recalculated.");
        }

        _selectedCandidate = null;
        if (_catalogue is null || !_bankroll.IsInitialized ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null ||
            !_catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out var target) || target is null)
        {
            _lastCandidate = "Blocked: catalogue, target, or explicitly initialized bankroll is unavailable.";
            return CandidateOutcome.Blocked;
        }

        var now = DateTimeOffset.UtcNow;
        var maximumAge = TimeSpan.FromSeconds(Settings.MaximumQuoteAgeSeconds.Value);
        var area = GameController.Game.IngameState.ServerData.InstanceId;
        if (!_liveOwnedByMetadata.TryGetValue(chaos.Metadata, out var chaosOwnership) ||
            !_liveOwnedByMetadata.TryGetValue(divine.Metadata, out var divineOwnership) ||
            chaosOwnership.AreaInstanceId != area || divineOwnership.AreaInstanceId != area ||
            now - chaosOwnership.ObservedAtUtc < TimeSpan.Zero || now - chaosOwnership.ObservedAtUtc > maximumAge ||
            now - divineOwnership.ObservedAtUtc < TimeSpan.Zero || now - divineOwnership.ObservedAtUtc > maximumAge)
        {
            _lastCandidate = "Blocked: refresh picker reads for exact live Chaos and Divine ownership.";
            return CandidateOutcome.Blocked;
        }

        var league = GetCurrentLeague();
        if (!QuoteMatrixBuilder.TryBuild(
                _rateStore.Captures,
                league,
                _manualProbeSessionId,
                area,
                chaos,
                divine,
                target,
                now,
                maximumAge,
                out var matrix,
                out var matrixFailure))
        {
            _lastCandidate = $"Blocked: {matrixFailure}";
            return CandidateOutcome.Blocked;
        }

        try
        {
            var result = FaustusRoutePlanner.Evaluate(new RoutePlannerRequest(
                chaos,
                divine,
                target,
                new CurrencyBankroll(_bankroll.AvailableChaos, chaosOwnership.Count),
                new CurrencyBankroll(_bankroll.AvailableDivine, divineOwnership.Count),
                matrix!.Edges,
                now,
                maximumAge,
                _manualProbeSessionId.ToString("D"),
                area.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Settings.MinimumProfitChaos.Value));
            var best = result.Best;
            _selectedCandidate = best;
            _lastCandidate = best is null
                ? $"None accepted; spend caps Chaos={Math.Min(_bankroll.AvailableChaos, chaosOwnership.Count)} " +
                    $"(ledger {_bankroll.AvailableChaos}, live {chaosOwnership.Count}), " +
                    $"Divine={Math.Min(_bankroll.AvailableDivine, divineOwnership.Count)} " +
                    $"(ledger {_bankroll.AvailableDivine}, live {divineOwnership.Count}); " +
                    $"reasons {DescribeRejections(result)}"
                : $"{DescribeCandidatePath(best)}; realized {best.ProfitChaos} Chaos profit; residuals " +
                    string.Join(", ", best.Remainders.Select(item => $"{item.Value} {item.Key.Name}")) +
                    $"; competing legs {best.CompetingEdgeCount}; " +
                    $"expected gold {(best.ExpectedGold?.ToString() ?? "unknown")}";
            return best is null ? CandidateOutcome.NoneAccepted : CandidateOutcome.Accepted;
        }
        catch (Exception exception)
        {
            _lastCandidate = $"Calculation blocked: {exception.Message}";
            return CandidateOutcome.Blocked;
        }
    }

    private bool TryGetFreshStateResetBlock(out string failure)
    {
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked)
        {
            failure = "Fresh-state reset blocked: corrupt canonical state must be preserved and repaired first.";
            return true;
        }
        if (_bankroll.Workflow?.IsActive == true)
        {
            failure = "Fresh-state reset blocked: a workflow is still active.";
            return true;
        }
        if (ContinuousWorkflowLoop.TryDescribeUnsettledCanonicalState(_bankroll, _trackedOrderState, out var reason))
        {
            failure = $"Fresh-state reset blocked: {reason}.";
            return true;
        }

        failure = string.Empty;
        return false;
    }

    private void ArmFreshStateReset()
    {
        if (IsAnyInputOperationActive())
        {
            _lastFailure = "Fresh-state reset cannot be armed during any input operation or placement preparation.";
            return;
        }

        if (TryGetFreshStateResetBlock(out var block))
        {
            _lastFailure = block;
            return;
        }

        // An idle continuous cooldown must not fire a probe between arming and applying.
        if (_fullWorkflowAuthorized)
        {
            StopFullWorkflowLocal("Continuous trading stopped so a fresh-state reset can be armed without racing a scheduled scan.");
        }

        _freshStateResetArmed = true;
        _freshStateResetArmExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        _operationStatus = "Fresh-state reset armed. Apply within 10 seconds to reseed from the configured bankroll seeds.";
    }

    private void ApplyArmedFreshStateReset()
    {
        if (IsAnyInputOperationActive())
        {
            _freshStateResetArmed = false;
            _lastFailure = "Fresh-state reset cancelled because an input operation or placement preparation is active.";
            return;
        }

        if (!_freshStateResetArmed || DateTimeOffset.UtcNow > _freshStateResetArmExpiresAtUtc)
        {
            _freshStateResetArmed = false;
            _placementToken = null;
            _lastFailure = "Fresh-state reset not applied: arm it first.";
            return;
        }

        if (TryGetFreshStateResetBlock(out var block))
        {
            _freshStateResetArmed = false;
            _lastFailure = block;
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league) || _bankrollStore == null)
        {
            _freshStateResetArmed = false;
            _lastFailure = "Fresh-state reset blocked until the current league is readable.";
            return;
        }

        try
        {
            // Create drops the resolved workflow and tracked-order records and reseeds balances;
            // rates, calibrations, audit, and runtime evidence on disk are untouched.
            _bankroll = BankrollState.Create(league, Settings.StartingChaos.Value, Settings.StartingDivine.Value);
            _bankrollStore.Save(_bankroll);
            _bankrollStore.AppendAudit(BankrollAuditEvent.Seeded(_bankroll));
            _freshStateResetArmed = false;
            _trackedOrderState = null;
            _trackedOrder = "None";
            _fullWorkflowAuthorized = false;
            _workflowAuthorization = null;
            _workflowPreparedLeg = null;
            _startingNewWorkflow = false;
            _nextWorkflowScanAtUtc = null;
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            _placementRefreshAttempts = 0;
            _collectionFlow = CollectionFlowState.Idle;
            _collectionOwnedBaseline = 0;
            _collectionBatchAmount = 0;
            _collectionOwnershipMetadata = string.Empty;
            _stashTransferMetadata = string.Empty;
            _stashTransferAmount = 0;
            _selectedCandidate = null;
            _lastCandidate = "None; capture all three markets in one area/session.";
            _liveOwnedByMetadata.Clear();
            _manualProbeSessionId = Guid.NewGuid();
            _lastFailure = "None";
            _lastLoggedFailure = null;
            _operationStatus = $"Fresh state reset for {league}: seeded balances, no workflow, no tracked order.";
        }
        catch (Exception exception)
        {
            _freshStateResetArmed = false;
            _lastFailure = $"Fresh-state reset failed: {exception.Message}";
        }
    }

    private void LoadBankrollForCurrentLeague()
    {
        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league) || _bankrollStore == null)
        {
            _bankroll = BankrollState.Uninitialized;
            return;
        }

        try
        {
            _bankroll = _bankrollStore.Load(league) ?? BankrollState.Uninitialized;
            _bankrollLoadBlocked = false;
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _bankroll = BankrollState.Uninitialized;
            _bankrollLoadBlocked = true;
            _lastFailure = $"Bankroll state load failed; trading remains blocked: {exception.Message}";
        }
    }

    private void LoadTrackedOrderForCurrentLeague()
    {
        var league = GetCurrentLeague();
        _trackedOrderState = null;
        _trackedOrderLoadBlocked = false;
        _trackedOrder = "None";
        if (string.IsNullOrWhiteSpace(league))
        {
            return;
        }

        try
        {
            _trackedOrderState = _bankroll.TrackedOrder;
            if (_trackedOrderState is null)
            {
                var legacy = _trackedOrderStore?.Load(league);
                if (legacy is not null)
                {
                    _trackedOrderLoadBlocked = true;
                    _trackedOrder = "BLOCKED: standalone legacy tracked-order evidence requires reconciliation";
                    return;
                }
                if (_bankroll.HasUnresolvedOrder)
                {
                    _trackedOrderLoadBlocked = true;
                    _trackedOrder = "BLOCKED: bankroll reports unresolved order but tracked state is missing";
                }
                return;
            }

            _trackedOrder = $"{_trackedOrderState.Status}: id={_trackedOrderState.PlayerOrderId?.ToString() ?? "unknown"}, " +
                $"{_trackedOrderState.OfferedAmount} {_trackedOrderState.OfferedMetadata} -> " +
                $"{_trackedOrderState.WantedAmount} {_trackedOrderState.WantedMetadata}";
            if (_trackedOrderState.Status == TrackedOrderStatus.CollectionArmed &&
                _trackedOrderState.CollectionAssetIntent is null)
            {
                _trackedOrderLoadBlocked = true;
                _trackedOrder = "BLOCKED: collection intent was interrupted; manual reconciliation required";
            }
            else if (_trackedOrderState.Status == TrackedOrderStatus.CollectionArmed)
            {
                _trackedOrder = "RECOVERY: terminal-asset collection intent can be reconciled read-only without retrying input";
            }
            else if (_trackedOrderState.Status is TrackedOrderStatus.CancelArmed or TrackedOrderStatus.CancelClicked)
            {
                _trackedOrder = "RECOVERY: cancellation intent interrupted; observing terminal state without retrying input";
            }
            else if (_trackedOrderState.Status == TrackedOrderStatus.StashTransferArmed)
            {
                _trackedOrder = "RECOVERY: stash-transfer intent interrupted; use stash hotkey for exact pre/post reconciliation";
            }
            if (_trackedOrderState.IsUnresolved)
            {
                _bankroll.HasUnresolvedOrder = true;
            }
        }
        catch (Exception exception)
        {
            _trackedOrderLoadBlocked = true;
            _trackedOrder = "BLOCKED: tracked-order state unreadable";
            _lastFailure = $"Tracked-order state load failed; all trading remains blocked: {exception.Message}";
        }
    }

    private string GetCurrentLeague()
    {
        try
        {
            return GameController.Game.IngameState.ServerData.League ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string DescribeBankroll()
    {
        if (!_bankroll.IsInitialized)
        {
            return "not initialized; spend is zero until explicitly armed and applied";
        }

        return $"{_bankroll.League}: ledger {_bankroll.AvailableChaos} Chaos/{_bankroll.AvailableDivine} Divine/" +
            $"{_bankroll.AvailableTarget} target; " +
            $"I-have reads Chaos={DescribeOwned("Chaos Orb")}, Divine={DescribeOwned("Divine Orb")}; " +
            $"seeded {_bankroll.SeededChaos}/{_bankroll.SeededDivine}";
    }

    private string DescribeWorkflow()
    {
        var workflow = _bankroll.Workflow;
        if (workflow is null) return "none";
        var leg = workflow.CurrentLegIndex >= workflow.Legs.Count
            ? workflow.Legs.Count
            : workflow.CurrentLegIndex + 1;
        return $"{workflow.Phase}, leg {leg}/{workflow.Legs.Count}, authorized={_fullWorkflowAuthorized}, " +
            $"planned profit={workflow.PlannedProfitChaos} Chaos | {workflow.Detail}";
    }

    private string DescribeContinuousScan()
    {
        if (!_fullWorkflowAuthorized) return "stopped; press the workflow hotkey to authorize";
        if (_nextWorkflowScanAtUtc is not { } deadline)
        {
            var blocking = DescribeActiveInputOperation();
            if (blocking != "nothing") return $"authorized; waiting on {blocking}";
            return _startingNewWorkflow ? "authorized; scanning for a route" : "authorized; executing the current workflow";
        }

        var remaining = deadline - DateTimeOffset.UtcNow;
        return remaining <= TimeSpan.Zero
            ? "authorized; retry cooldown elapsed"
            : $"authorized; no accepted route, reprobing in {(int)Math.Ceiling(remaining.TotalSeconds)}s";
    }

    private string DescribeOwned(string currencyName)
    {
        if (_catalogue is null || !_catalogue.TryGetUniqueByName(currencyName, out var currency) || currency is null ||
            !_liveOwnedByMetadata.TryGetValue(currency.Metadata, out var observation))
        {
            return "unobserved";
        }

        var age = DateTimeOffset.UtcNow - observation.ObservedAtUtc;
        return age < TimeSpan.Zero
            ? $"{observation.Count} (future timestamp)"
            : $"{observation.Count} ({age.TotalSeconds:F0}s old)";
    }

    private static string DescribeRejections(RoutePlannerResult result)
    {
        var meaningful = result.Evaluations
            .Where(evaluation => evaluation.RejectionReason != RouteRejectionReason.TooManyCompetingEdges)
            .ToArray();
        if (meaningful.Length == 0)
        {
            meaningful = result.Evaluations.ToArray();
        }

        return string.Join("; ", meaningful
            .GroupBy(evaluation => evaluation.Path[0].Name, StringComparer.Ordinal)
            .Select(group => $"{group.Key} start [{string.Join(", ", group.Select(item => item.RejectionReason).Distinct())}]"));
    }

    private static string DescribeCandidatePath(RouteCandidate candidate)
    {
        var amounts = new List<string>(candidate.Legs.Count + 1)
        {
            $"{candidate.StartingPrincipal} {candidate.Path[0].Name}"
        };
        amounts.AddRange(candidate.Legs.Select(leg => $"{leg.Output} {leg.Edge.To.Name}"));
        return string.Join(" -> ", amounts);
    }
}
