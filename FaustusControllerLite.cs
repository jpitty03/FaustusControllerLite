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
    private bool _bankrollResetArmed;
    private DateTimeOffset _bankrollResetArmExpiresAtUtc;
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
    private string _operationStatus = "Idle (Milestone 8 verified tracked-order collection available; full workflow is blocked).";
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
    private DateTimeOffset _nextLifecyclePollAtUtc;
    private DateTimeOffset _collectionOwnershipPhaseStartedAtUtc;
    private string _collectionOwnershipMetadata = string.Empty;
    private string _stashTransferMetadata = string.Empty;
    private long _stashTransferAmount;

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
        Settings.ArmBankrollReset.OnPressed += ArmBankrollReset;
        Settings.ApplyArmedBankrollReset.OnPressed += ApplyArmedBankrollReset;
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
        if (_bankrollResetArmed && DateTimeOffset.UtcNow > _bankrollResetArmExpiresAtUtc)
        {
            _bankrollResetArmed = false;
            _operationStatus = "Bankroll reset arm expired without changing the ledger.";
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
            if (_trackedCancellation.IsRunning)
            {
                _trackedCancellation.Cancel("Full-workflow hotkey interrupted cancellation.");
            }
            if (IsPlacementFlowActive())
            {
                AbortPlacementFlow("Full-workflow hotkey interrupted placement preparation.");
            }
            if (_singleLegStaging.IsRunning)
            {
                _singleLegStaging.Cancel("Full-workflow hotkey interrupted single-leg staging.");
            }
            _lastFailure = "Full workflow remains blocked until later milestones; no order was placed.";
        }
        if (IsCollectionFlowActive() &&
            (!(IsStashTransferFlow()
                ? StashTransferInputPermissions.From(Settings).Ready
                : CollectionInputPermissions.From(Settings).Ready) ||
             !Settings.AllowQueryInput.Value))
        {
            AbortCollectionFlow("Collection/query permission changed during tracked-order collection.");
        }

        if (_placementPreparation is PlacementPreparationState.Probing or
                PlacementPreparationState.RefreshingFirstLeg or PlacementPreparationState.Restaging &&
            (!Settings.AllowOrderPlacement.Value || Settings.AllowFullWorkflow.Value))
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
            StagingInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value,
            Settings.StableRateSampleCount.Value);
        SynchronizeSingleLegStagingStatus();
        _singleLegPlacement.Tick(
            GameController,
            _pickerCalibration,
            PlacementInputPermissions.From(Settings),
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
            CollectionInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled());
        SynchronizeTrackedCollection();
        _inventoryStashTransfer.Tick(
            GameController,
            StashTransferInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled());
        SynchronizeInventoryStashTransfer();
        _trackedCancellation.Tick(
            GameController,
            _pickerCalibration,
            CancellationInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value);
        SynchronizeTrackedCancellation();
        _canceledReturnCollection.Tick(
            GameController,
            _pickerCalibration,
            CollectionInputPermissions.From(Settings),
            IsFullFaustusControllerEnabled(),
            Settings.CursorTweenSpeed.Value);
        SynchronizeCanceledReturnCollection();

        return base.Tick();
    }

    public override void OnUnload()
    {
        _trackedCancellation.EmergencyStop("Plugin unloading during cancellation.");
        _canceledReturnCollection.EmergencyStop("Plugin unloading during canceled return collection.");
        _inventoryStashTransfer.EmergencyStop("Plugin unloading during inventory-to-stash transfer.");
        _trackedCollection.EmergencyStop("Plugin unloading during tracked order collection.");
        base.OnUnload();
    }

    public override void OnPluginDestroyForHotReload()
    {
        _trackedCancellation.EmergencyStop("Plugin hot reload during cancellation.");
        _canceledReturnCollection.EmergencyStop("Plugin hot reload during canceled return collection.");
        _inventoryStashTransfer.EmergencyStop("Plugin hot reload during inventory-to-stash transfer.");
        _trackedCollection.EmergencyStop("Plugin hot reload during tracked order collection.");
        base.OnPluginDestroyForHotReload();
    }

    public override void AreaChange(AreaInstance area)
    {
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
        DrawStatus("FaustusControllerLite - Milestone 8 verified tracked-order collection", ref y, SharpDX.Color.Cyan);
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
        DrawStatus($"Tracked order: {_trackedOrder}", ref y, SharpDX.Color.Gray);
        DrawStatus($"Last candidate path: {_lastCandidate}", ref y, SharpDX.Color.Gray);
        DrawStatus($"Reset armed: {(_bankrollResetArmed ? "YES (apply within 10 seconds)" : "no")}", ref y, _bankrollResetArmed ? SharpDX.Color.OrangeRed : SharpDX.Color.Gray);

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
                CalculateCandidate();
                restageForPlacement = _placementPreparation == PlacementPreparationState.Probing;
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
            }
            if (_automatedProbe.State is AutomatedProbeState.Cancelled or AutomatedProbeState.Failed)
            {
                _automatedProbe.AcknowledgeCompletion();
            }
        }
    }

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
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true || _bankroll.HasUnresolvedOrder)
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
        CalculateCandidate();
        var leg = _selectedCandidate?.Legs.FirstOrDefault();
        if (leg is null)
        {
            _lastFailure = "No current accepted candidate leg is available to stage.";
            return;
        }

        if (!_singleLegStaging.Start(
                GameController,
                leg,
                _pickerCalibration,
                StagingInputPermissions.From(Settings),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                placementWorkflowArmed,
                out var failure))
        {
            _lastFailure = failure;
            _operationStatus = "Single-leg dry-run staging did not start; no amount input was sent.";
            return;
        }

        _operationStatus = $"Dry-run staging first candidate leg: {leg.InputSpent} {leg.Edge.From.Name} -> {leg.Output} {leg.Edge.To.Name}.";
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

        var leg = _selectedCandidate?.Legs.FirstOrDefault();
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
                CalculateCandidate();
                var refreshedLeg = _selectedCandidate?.Legs.FirstOrDefault();
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
                    _selectedCandidate!.Signature,
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

        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true || _bankroll.HasUnresolvedOrder)
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
            return;
        }

        if (!_singleLegPlacement.Start(
                GameController,
                stagedLeg,
                _pickerCalibration,
                PlacementInputPermissions.From(Settings),
                IsFullFaustusControllerEnabled(),
                Settings.CursorTweenSpeed.Value,
                Settings.CompetingOrderWaitMinutes.Value,
                _placementToken!.ProbeSessionId,
                _placementToken.CandidateSignature,
                leg => ValidatePlacementPreparation(leg, out var finalFailure)
                    ? (true, string.Empty)
                    : (false, finalFailure),
                PersistTrackedOrder,
                out var placementFailure))
        {
            _lastFailure = placementFailure;
            _placementPreparation = PlacementPreparationState.Idle;
            _placementToken = null;
            return;
        }

        _placementPreparation = PlacementPreparationState.Placing;
        _operationStatus = "Fresh probe/restage passed; one verified Place Order click is armed automatically.";
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
            _lastFailure = _singleLegPlacement.Failure;
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

        CalculateCandidate(invalidateStaging: false);
        var candidate = _selectedCandidate;
        var currentLeg = candidate?.Legs.FirstOrDefault();
        if (candidate is null || currentLeg is null ||
            !string.Equals(candidate.Signature, token.CandidateSignature, StringComparison.Ordinal) ||
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
            return false;
        }

        try
        {
            var next = CloneBankroll(_bankroll);
            var previous = next.TrackedOrder;
            if (state.Status == TrackedOrderStatus.Armed)
            {
                if (previous?.IsUnresolved == true || !TryMoveAvailableToReserved(next, state.OfferedMetadata, state.OfferedAmount))
                {
                    return false;
                }
            }
            else if (previous is null || previous.AttemptId != state.AttemptId || !previous.IsUnresolved)
            {
                return false;
            }

            if (state.Status is TrackedOrderStatus.CompletedUncollected or TrackedOrderStatus.CanceledUncollected &&
                previous!.Status is not TrackedOrderStatus.CompletedUncollected and not TrackedOrderStatus.CanceledUncollected and
                    not TrackedOrderStatus.CollectionArmed && state.LedgerCommittedAtUtc is null)
            {
                if (state.TerminalRemainingOfferedAmount is not { } remaining ||
                    state.TerminalReceivedWantedAmount is not { } received ||
                    !TrySettleTerminal(next, state, remaining, received))
                {
                    return false;
                }
                state.LedgerCommittedAtUtc = DateTimeOffset.UtcNow;
            }

            next.TrackedOrder = state;
            next.HasUnresolvedOrder = state.IsUnresolved;
            next.UpdatedAtUtc = DateTimeOffset.UtcNow;
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = state;
            _trackedOrder = $"{state.Status}: id={state.PlayerOrderId?.ToString() ?? "unknown"}, " +
                $"{state.OfferedAmount} {state.OfferedMetadata} -> {state.WantedAmount} {state.WantedMetadata}";
            try
            {
                _trackedOrderStore?.AppendAudit(state, eventType);
            }
            catch (Exception auditException)
            {
                _lastFailure = $"Canonical tracked state committed, but audit append failed: {auditException.Message}";
            }
            return true;
        }
        catch (Exception exception)
        {
            _lastFailure = $"Tracked-order persistence failed: {exception.Message}";
            return false;
        }
    }

    private void PollTrackedOrderLifecycle()
    {
        if (_trackedOrderState?.Status is not TrackedOrderStatus.Pending and not TrackedOrderStatus.TimedOut and
                not TrackedOrderStatus.CancelArmed and not TrackedOrderStatus.CancelClicked ||
            DateTimeOffset.UtcNow < _nextLifecyclePollAtUtc || _bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            _trackedCancellation.IsRunning)
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
        if (_trackedOrderState?.Status == TrackedOrderStatus.Ambiguous &&
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
            var assets = _trackedOrderState.TerminalRemainingOfferedAmount is { } remaining &&
                _trackedOrderState.TerminalReceivedWantedAmount is { } received
                    ? TrackedOrderLifecycle.CreateSettlementAssets(_trackedOrderState, remaining, received)
                    : Array.Empty<SettlementAsset>();
            var pending = assets.Where(asset => asset.WantedSlot
                ? !_trackedOrderState.WantedAssetCollected
                : !_trackedOrderState.OfferedReturnCollected).ToArray();
            if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || pending.Length == 0 ||
                pending[0].WantedSlot && !_pickerCalibration.IsCollectionComplete ||
                !pending[0].WantedSlot && !_pickerCalibration.IsReturnCollectionComplete)
            {
                _lastFailure = "Terminal settlement collection requires exact pending asset and its calibrated left/right slot.";
                return;
            }
            if (!CollectionInputPermissions.From(Settings).Ready || !Settings.AllowQueryInput.Value)
            {
                _lastFailure = "Enable movement, clicks, query input, and collection; disable placement/full workflow/cancellation.";
                return;
            }
            StartCollectionOwnershipRead(
                CollectionFlowState.ReadingCanceledReturnBaseline, pending[0].Metadata);
            return;
        }
        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked ||
            _trackedOrderState?.Status != TrackedOrderStatus.CompletedUncollected ||
            !_pickerCalibration.IsCollectionComplete)
        {
            _lastFailure = "Collection requires readable canonical CompletedUncollected state and calibrated tracked-order slot.";
            return;
        }
        if (!CollectionInputPermissions.From(Settings).Ready || !Settings.AllowQueryInput.Value)
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
        if (_trackedOrderState?.CollectionAssetIntent is not { } intent ||
            !CanceledReturnCollectionController.VerifyInterruptedPostState(
                GameController, _trackedOrderState, _pickerCalibration, out asset, out failure) || asset is null)
        {
            _lastFailure = string.IsNullOrEmpty(failure)
                ? "Interrupted terminal collection post-state was not exact; no retry or reconciliation performed."
                : failure;
            return;
        }
        var progress = TrackedOrderCollectionController.CloneTracked(
            _trackedOrderState, intent.TerminalStatus,
            $"Reconciled exact interrupted {(asset.WantedSlot ? "wanted proceeds" : "offered return")} post-state without retrying input.");
        progress.CollectionAssetIntent = null;
        if (asset.WantedSlot) progress.WantedAssetCollected = true;
        else progress.OfferedReturnCollected = true;
        var assets = TrackedOrderLifecycle.CreateSettlementAssets(
            progress, progress.TerminalRemainingOfferedAmount!.Value,
            progress.TerminalReceivedWantedAmount!.Value);
        var allCollected = assets.All(settlementAsset => settlementAsset.WantedSlot
            ? progress.WantedAssetCollected
            : progress.OfferedReturnCollected);
        if (!allCollected)
        {
            if (!PersistTrackedOrder(progress, "TerminalAssetCollectionInterruptedPostStateReconciled"))
            {
                _lastFailure = "Exact interrupted terminal collection evidence was observed but canonical progress could not be persisted.";
                return;
            }
            _operationStatus = "Interrupted terminal asset was reconciled from exact post-state without retry; authorize collection for the remaining slot.";
            _lastFailure = "None";
            return;
        }

        try
        {
            var next = CloneBankroll(_bankroll);
            foreach (var settlementAsset in assets)
            {
                if (!TryCreditCollected(next, settlementAsset.Metadata, settlementAsset.Amount))
                    throw new InvalidDataException("Recovered terminal assets did not match canonical completed buckets.");
            }
            progress.Status = TrackedOrderStatus.Collected;
            progress.Detail = "Reconciled final interrupted terminal asset and atomically credited every settlement asset without retrying input.";
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            next.TrackedOrder = progress;
            next.HasUnresolvedOrder = true;
            next.UpdatedAtUtc = progress.UpdatedAtUtc;
            _bankrollStore!.Save(next);
            _bankroll = next;
            _trackedOrderState = progress;
            _trackedOrder = "Collected all terminal assets after exact interrupted post-state reconciliation";
            try { _trackedOrderStore?.AppendAudit(progress, "TerminalAssetsInterruptedFinalPostStateReconciledAndCredited"); }
            catch (Exception auditException)
            {
                _lastFailure = $"Recovered assets settled canonically, audit append failed: {auditException.Message}";
            }
            _operationStatus = "Final interrupted terminal asset reconciled and all assets credited atomically; stash custody remains.";
            if (!_lastFailure.StartsWith("Recovered assets settled", StringComparison.Ordinal)) _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _lastFailure = $"Final interrupted terminal collection reconciliation failed closed: {exception.Message}";
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
                CancellationInputPermissions.From(Settings),
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
            _bankrollStore is null || _catalogue is null)
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
            var assets = _trackedOrderState.TerminalRemainingOfferedAmount is { } remaining &&
                _trackedOrderState.TerminalReceivedWantedAmount is { } received
                    ? TrackedOrderLifecycle.CreateSettlementAssets(_trackedOrderState, remaining, received)
                    : Array.Empty<SettlementAsset>();
            var pending = assets.Where(asset => asset.WantedSlot
                ? _trackedOrderState.WantedAssetCollected && !_trackedOrderState.WantedAssetStashed
                : _trackedOrderState.OfferedReturnCollected && !_trackedOrderState.OfferedReturnStashed).ToArray();
            if (pending.Length == 0)
            {
                _lastFailure = "No ownership-verified terminal asset remains pending stash custody.";
                return;
            }
            _stashTransferMetadata = pending[0].Metadata;
            _stashTransferAmount = pending[0].Amount;
        }
        else if (_trackedOrderState.StashTransferIntent is { } recoveryIntent)
        {
            _stashTransferMetadata = recoveryIntent.Metadata;
            _stashTransferAmount = recoveryIntent.Amount;
        }
        if (!StashTransferInputPermissions.From(Settings).Ready || !Settings.AllowQueryInput.Value)
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
                if (!_trackedCollection.Start(
                        GameController,
                        _trackedOrderState!,
                        _pickerCalibration,
                        CollectionInputPermissions.From(Settings),
                        IsFullFaustusControllerEnabled(),
                        Settings.CursorTweenSpeed.Value,
                        PersistTrackedOrder,
                        out failure))
                {
                    AbortCollectionFlow(failure);
                    return;
                }

                _collectionFlow = CollectionFlowState.ClickingTrackedOrder;
                _operationStatus = $"Pre-collection owned count {_collectionOwnedBaseline}; collecting exact tracked order once.";
            }
            else if (_collectionFlow == CollectionFlowState.ReadingAfter)
            {
                var expected = checked(_collectionOwnedBaseline + _trackedOrderState!.WantedAmount);
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
                        StashTransferInputPermissions.From(Settings),
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
                        CollectionInputPermissions.From(Settings),
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
            var assets = TrackedOrderLifecycle.CreateSettlementAssets(_trackedOrderState, remaining, received);
            if (!assets.Any(asset => asset.Metadata == intent.Metadata && asset.Amount == intent.Amount &&
                    asset.WantedSlot == intent.WantedSlot))
            {
                throw new InvalidDataException("Collection intent did not match a terminal settlement asset.");
            }
            var progress = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, intent.TerminalStatus,
                $"Verified terminal {(intent.WantedSlot ? "wanted proceeds" : "offered return")} entered inventory; owned count is {observedOwned}.");
            progress.CollectionAssetIntent = null;
            if (intent.WantedSlot) progress.WantedAssetCollected = true;
            else progress.OfferedReturnCollected = true;
            var allCollected = assets.All(asset => asset.WantedSlot
                ? progress.WantedAssetCollected
                : progress.OfferedReturnCollected);

            if (!allCollected)
            {
                if (!PersistTrackedOrder(progress, "TerminalAssetCollectionProgressVerified"))
                {
                    throw new InvalidDataException("Could not persist first terminal asset progress.");
                }
                _collectionFlow = CollectionFlowState.Idle;
                _operationStatus = "First terminal asset verified in inventory; authorize collection again for the remaining slot.";
                _lastFailure = "None";
                return;
            }

            var next = CloneBankroll(_bankroll);
            foreach (var asset in assets)
            {
                if (!TryCreditCollected(next, asset.Metadata, asset.Amount))
                {
                    throw new InvalidDataException("Completed-uncollected settlement assets did not match canonical buckets.");
                }
            }
            progress.Status = TrackedOrderStatus.Collected;
            progress.Detail = "All terminal settlement assets are ownership-verified in inventory and credited atomically.";
            progress.UpdatedAtUtc = DateTimeOffset.UtcNow;
            next.TrackedOrder = progress;
            next.HasUnresolvedOrder = progress.IsUnresolved;
            next.UpdatedAtUtc = progress.UpdatedAtUtc;
            _bankrollStore.Save(next);
            _bankroll = next;
            _trackedOrderState = progress;
            _trackedOrder = $"Collected {assets.Count} terminal settlement asset(s)";
            try { _trackedOrderStore?.AppendAudit(progress, "TerminalAssetsCollectedAndCreditedAtomically"); }
            catch (Exception auditException) { _lastFailure = $"Terminal assets settled canonically, audit append failed: {auditException.Message}"; }
            _collectionFlow = CollectionFlowState.Idle;
            _operationStatus = "All terminal assets collected and credited atomically; sequential stash custody remains required.";
            if (!_lastFailure.StartsWith("Terminal assets settled", StringComparison.Ordinal)) _lastFailure = "None";
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

        var assets = _trackedOrderState.TerminalRemainingOfferedAmount is { } remaining &&
            _trackedOrderState.TerminalReceivedWantedAmount is { } received
                ? TrackedOrderLifecycle.CreateSettlementAssets(_trackedOrderState, remaining, received)
                : Array.Empty<SettlementAsset>();
        var matchingAsset = assets.SingleOrDefault(asset =>
            asset.Metadata == intent.Metadata && asset.Amount == intent.Amount);
        if (matchingAsset is null)
        {
            MarkStashTransferAmbiguous("Stash intent did not match one terminal settlement asset.");
            return;
        }
        var stashed = TrackedOrderCollectionController.CloneTracked(
            _trackedOrderState,
            TrackedOrderStatus.Collected,
            $"Verified {intent.Amount} {intent.Metadata} left inventory, entered visible Currency Stash, and aggregate ownership remained {observedOwned}.");
        if (matchingAsset.WantedSlot) stashed.WantedAssetStashed = true;
        else stashed.OfferedReturnStashed = true;
        if (!_inventoryStashTransfer.VerifyPostState(GameController, out var custodyFailure))
        {
            MarkStashTransferAmbiguous(custodyFailure);
            return;
        }
        stashed.StashTransferIntent = null;
        var allStashed = assets.All(asset => asset.WantedSlot
            ? stashed.WantedAssetStashed
            : stashed.OfferedReturnStashed);
        if (allStashed) stashed.Status = TrackedOrderStatus.Stashed;
        var eventType = allStashed ? "TerminalAssetsStashedAndVerified" : "TerminalAssetStashProgressVerified";
        if (!PersistTrackedOrder(stashed, eventType))
        {
            MarkStashTransferAmbiguous("Could not persist verified stashed state.");
            return;
        }

        _collectionFlow = CollectionFlowState.Idle;
        _operationStatus = allStashed
            ? "Lifecycle custody complete: every terminal asset is verified in Currency Stash."
            : "One terminal asset is stashed; authorize stash transfer again for the remaining asset.";
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
            return;
        }

        if (recovery == InventoryTransferEvidence.RecoveryKind.PostTransfer)
        {
            var assets = _trackedOrderState.TerminalRemainingOfferedAmount is { } remaining &&
                _trackedOrderState.TerminalReceivedWantedAmount is { } received
                    ? TrackedOrderLifecycle.CreateSettlementAssets(_trackedOrderState, remaining, received)
                    : Array.Empty<SettlementAsset>();
            var matchingAsset = assets.SingleOrDefault(asset =>
                asset.Metadata == intent.Metadata && asset.Amount == intent.Amount);
            if (matchingAsset is null)
            {
                MarkStashTransferAmbiguous("Recovered stash intent did not match one terminal asset.");
                return;
            }
            var stashed = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState, TrackedOrderStatus.Collected,
                "Recovered interrupted stash transfer from exact post-state and unchanged aggregate ownership.");
            if (matchingAsset.WantedSlot) stashed.WantedAssetStashed = true;
            else stashed.OfferedReturnStashed = true;
            stashed.StashTransferIntent = null;
            var allStashed = assets.All(asset => asset.WantedSlot
                ? stashed.WantedAssetStashed
                : stashed.OfferedReturnStashed);
            if (allStashed) stashed.Status = TrackedOrderStatus.Stashed;
            if (!PersistTrackedOrder(stashed, allStashed
                    ? "TerminalAssetsStashRecoveredAndVerified"
                    : "TerminalAssetStashProgressRecovered"))
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

            var next = CloneBankroll(_bankroll);
            var amount = _trackedOrderState.WantedAmount;
            if (!TryCreditCollected(next, _trackedOrderState.WantedMetadata, amount))
            {
                throw new InvalidDataException("Completed-uncollected proceeds did not match canonical currency bucket.");
            }

            var collected = TrackedOrderCollectionController.CloneTracked(
                _trackedOrderState,
                TrackedOrderStatus.Collected,
                $"Verified exact order disappearance and owned-count increase to {observedOwned}.");
            collected.WantedAssetCollected = true;
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

    private void CalculateCandidate(bool invalidateStaging = true)
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
            return;
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
            return;
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
            return;
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
        }
        catch (Exception exception)
        {
            _lastCandidate = $"Calculation blocked: {exception.Message}";
        }
    }

    private void ArmBankrollReset()
    {
        if (_automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning || _singleLegPlacement.IsRunning ||
            IsCollectionFlowActive() ||
            _placementPreparation != PlacementPreparationState.Idle)
        {
            _lastFailure = "Bankroll reset cannot be armed during any input operation or placement preparation.";
            return;
        }

        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true || _bankroll.HasUnresolvedOrder)
        {
            _lastFailure = "Bankroll reset blocked: a tracked order is unresolved.";
            return;
        }

        _bankrollResetArmed = true;
        _bankrollResetArmExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10);
        _operationStatus = "Bankroll reset armed. Apply within 10 seconds to use current seed settings.";
    }

    private void ApplyArmedBankrollReset()
    {
        if (_automatedProbe.IsRunning || _placementLegRefresh.IsRunning || _singleLegStaging.IsRunning || _singleLegPlacement.IsRunning ||
            IsCollectionFlowActive() ||
            _placementPreparation != PlacementPreparationState.Idle)
        {
            _bankrollResetArmed = false;
            _lastFailure = "Bankroll reset cancelled because an input operation or placement preparation is active.";
            return;
        }

        if (!_bankrollResetArmed || DateTimeOffset.UtcNow > _bankrollResetArmExpiresAtUtc)
        {
            _bankrollResetArmed = false;
            _placementToken = null;
            _lastFailure = "Bankroll reset not applied: arm it first.";
            return;
        }

        if (_bankrollLoadBlocked || _trackedOrderLoadBlocked || _trackedOrderState?.IsUnresolved == true || _bankroll.HasUnresolvedOrder)
        {
            _bankrollResetArmed = false;
            _lastFailure = "Bankroll reset blocked: a tracked order is unresolved.";
            return;
        }

        var league = GetCurrentLeague();
        if (string.IsNullOrWhiteSpace(league) || _bankrollStore == null)
        {
            _bankrollResetArmed = false;
            _lastFailure = "Bankroll reset blocked until the current league is readable.";
            return;
        }

        try
        {
            _bankroll = BankrollState.Create(league, Settings.StartingChaos.Value, Settings.StartingDivine.Value);
            _bankrollStore.Save(_bankroll);
            _bankrollStore.AppendAudit(BankrollAuditEvent.Seeded(_bankroll));
            _bankrollResetArmed = false;
            _operationStatus = $"Isolated bankroll initialized/reset for {league}.";
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _bankrollResetArmed = false;
            _lastFailure = $"Bankroll reset failed: {exception.Message}";
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
            if (_trackedOrderState.Status == TrackedOrderStatus.CollectionArmed)
            {
                _trackedOrderLoadBlocked = true;
                _trackedOrder = "BLOCKED: collection intent was interrupted; manual reconciliation required";
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

        return $"{_bankroll.League}: ledger {_bankroll.AvailableChaos} Chaos/{_bankroll.AvailableDivine} Divine; " +
            $"I-have reads Chaos={DescribeOwned("Chaos Orb")}, Divine={DescribeOwned("Divine Orb")}; " +
            $"seeded {_bankroll.SeededChaos}/{_bankroll.SeededDivine}";
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
