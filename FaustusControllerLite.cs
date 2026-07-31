using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using FaustusControllerLite.Core;
using FaustusControllerLite.Domain;
using FaustusControllerLite.Persistence;
using FaustusControllerLite.Probing;
using System.Numerics;

namespace FaustusControllerLite;

public sealed class FaustusControllerLite : BaseSettingsPlugin<FaustusControllerLiteSettings>
{
    private readonly CurrencyCatalogueBuilder _catalogueBuilder = new();
    private readonly LatestRateStore _rateStore = new();
    private readonly Dictionary<string, long> _liveOwnedByMetadata = new(StringComparer.Ordinal);
    private CurrencyCatalogue? _catalogue;
    private BankrollStore? _bankrollStore;
    private BankrollState _bankroll = BankrollState.Uninitialized;
    private bool _bankrollResetArmed;
    private DateTimeOffset _bankrollResetArmExpiresAtUtc;
    private DateTimeOffset _nextCatalogueAttemptUtc;
    private Guid _manualProbeSessionId = Guid.NewGuid();
    private string _latestRatePath = string.Empty;
    private string _diagnosticPath = string.Empty;
    private string _observedTargetLabel = string.Empty;
    private string _catalogueStatus = "Waiting for Currency Exchange catalogue.";
    private string _operationStatus = "Idle (Milestone 1; no automation implemented).";
    private string _lastFailure = "None";
    private string _lastCandidate = "None; capture all three markets in one area/session.";
    private string _trackedOrder = "None";

    public override bool Initialise()
    {
        Name = nameof(FaustusControllerLite);
        _bankrollStore = new BankrollStore(Path.Combine(ConfigDirectory, nameof(FaustusControllerLite)));
        _latestRatePath = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "latest-rates.json");
        _diagnosticPath = Path.Combine(ConfigDirectory, nameof(FaustusControllerLite), "sdk-diagnostic.txt");
        Settings.ArmBankrollReset.OnPressed += ArmBankrollReset;
        Settings.ApplyArmedBankrollReset.OnPressed += ApplyArmedBankrollReset;
        try
        {
            _rateStore.Load(_latestRatePath);
        }
        catch (Exception exception)
        {
            _lastFailure = $"Latest-rate cache load failed; evidence retained: {exception.Message}";
        }
        LoadBankrollForCurrentLeague();
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

        if (Settings.CaptureCurrentPairHotkey.PressedOnce())
        {
            CaptureCurrentPair();
        }

        if (Settings.DumpSdkReadsHotkey.PressedOnce())
        {
            DumpSdkReads();
        }

        if (Settings.ProbeMarketsHotkey.PressedOnce())
        {
            _lastFailure = "Automated probing is gated on manual SDK diagnostic validation; no input was sent.";
        }

        if (Settings.ExecuteSingleLegHotkey.PressedOnce() || Settings.FullWorkflowHotkey.PressedOnce())
        {
            _lastFailure = "Order input is not implemented before the read-only validation gate; no input was sent.";
        }

        return base.Tick();
    }

    public override void AreaChange(AreaInstance area)
    {
        _manualProbeSessionId = Guid.NewGuid();
        _liveOwnedByMetadata.Clear();
        LoadBankrollForCurrentLeague();
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
        DrawStatus("FaustusControllerLite - Read-only milestones 1-3", ref y, SharpDX.Color.Cyan);
        DrawStatus($"Exchange panel: {(panelVisible ? "visible" : "closed")}", ref y, SharpDX.Color.White);
        DrawStatus($"Catalogue: {_catalogueStatus}", ref y, _catalogue == null ? SharpDX.Color.Orange : SharpDX.Color.LimeGreen);
        DrawStatus($"Target: {Settings.TargetCurrencyDisplayName} | {Settings.TargetCurrencyMetadata}", ref y, SharpDX.Color.White);
        DrawStatus($"Operation: {_operationStatus}", ref y, SharpDX.Color.White);
        DrawStatus($"Last failure: {_lastFailure}", ref y, _lastFailure == "None" ? SharpDX.Color.Gray : SharpDX.Color.OrangeRed);
        DrawStatus($"Bankroll: {DescribeBankroll()}", ref y, _bankroll.IsInitialized ? SharpDX.Color.LimeGreen : SharpDX.Color.Yellow);
        DrawStatus($"Latest rates: {_rateStore.Captures.Count} canonical league/pair records", ref y, SharpDX.Color.White);
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
        if (_catalogue == null || !_catalogue.TryGetByLabel(Settings.TargetCurrency.Value, out var target) || target == null)
        {
            return;
        }

        Settings.TargetCurrencyDisplayName = target.Name;
        Settings.TargetCurrencyMetadata = target.Metadata;
        _operationStatus = $"Selected target {target.Name}; exact metadata stored.";
        _observedTargetLabel = target.Name;
        _manualProbeSessionId = Guid.NewGuid();
        _lastCandidate = "None; target changed, so a new three-market session is required.";
    }

    private void CaptureCurrentPair()
    {
        if (!CurrentMarketReader.TryCapture(GameController, _manualProbeSessionId, out var capture, out var failure))
        {
            _lastFailure = failure;
            _operationStatus = "Read-only capture stopped without changing the cache.";
            return;
        }

        try
        {
            _rateStore.Store(capture!);
            _rateStore.Save(_latestRatePath);
            _operationStatus = $"Captured {capture!.OfferedCurrency.Name}/{capture.WantedCurrency.Name}; canonical pair replaced.";
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

    private void ObservePickerOwnership()
    {
        try
        {
            var picker = GameController.Game.IngameState.IngameUi.CurrencyExchangePanel.CurrencyPicker;
            if (!picker.IsVisible)
            {
                return;
            }

            foreach (var option in picker.Options)
            {
                var item = option?.ItemType;
                if (item is not null && !string.IsNullOrWhiteSpace(item.Metadata) && option!.Owned >= 0)
                {
                    _liveOwnedByMetadata[item.Metadata] = option.Owned;
                }
            }
        }
        catch (Exception exception)
        {
            _lastFailure = $"Picker ownership read failed: {exception.Message}";
        }
    }

    private void CalculateCandidate()
    {
        if (_catalogue is null || !_bankroll.IsInitialized ||
            !_catalogue.TryGetUniqueByName("Chaos Orb", out var chaos) || chaos is null ||
            !_catalogue.TryGetUniqueByName("Divine Orb", out var divine) || divine is null ||
            !_catalogue.TryGetByMetadata(Settings.TargetCurrencyMetadata, out var target) || target is null)
        {
            _lastCandidate = "Blocked: catalogue, target, or explicitly initialized bankroll is unavailable.";
            return;
        }

        if (!_liveOwnedByMetadata.TryGetValue(chaos.Metadata, out var liveChaos) ||
            !_liveOwnedByMetadata.TryGetValue(divine.Metadata, out var liveDivine))
        {
            _lastCandidate = "Blocked: open each picker so exact live Chaos and Divine ownership can cap the ledger.";
            return;
        }

        var league = GetCurrentLeague();
        var area = GameController.Game.IngameState.ServerData.InstanceId;
        var requiredPairs = new HashSet<CurrencyPairKey>
        {
            new(chaos, divine),
            new(chaos, target),
            new(divine, target)
        };
        var captures = _rateStore.Captures.Where(capture =>
            string.Equals(capture.League, league, StringComparison.Ordinal) &&
            capture.SessionId == _manualProbeSessionId &&
            capture.AreaInstanceId == area &&
            requiredPairs.Contains(capture.Pair)).ToArray();
        if (captures.Select(capture => capture.Pair).Distinct().Count() != 3)
        {
            _lastCandidate = "Blocked: capture Divine/Chaos, target/Chaos, and target/Divine in this session.";
            return;
        }

        try
        {
            var edges = captures.SelectMany(MarketCaptureNormalizer.CreateEdges).ToArray();
            var result = FaustusRoutePlanner.Evaluate(new RoutePlannerRequest(
                chaos,
                divine,
                target,
                new CurrencyBankroll(_bankroll.AvailableChaos, liveChaos),
                new CurrencyBankroll(_bankroll.AvailableDivine, liveDivine),
                edges,
                DateTimeOffset.UtcNow,
                TimeSpan.FromSeconds(Settings.MaximumQuoteAgeSeconds.Value),
                _manualProbeSessionId.ToString("D"),
                area.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Settings.MinimumProfitChaos.Value));
            var best = result.Best;
            _lastCandidate = best is null
                ? $"None accepted; {string.Join(", ", result.Evaluations.Select(item => item.RejectionReason).Distinct())}"
                : $"{string.Join(" -> ", best.Path.Select(item => item.Name))}; realized {best.ProfitChaos} Chaos profit; residuals " +
                    string.Join(", ", best.Remainders.Select(item => $"{item.Value} {item.Key.Name}"));
        }
        catch (Exception exception)
        {
            _lastCandidate = $"Calculation blocked: {exception.Message}";
        }
    }

    private void ArmBankrollReset()
    {
        if (_bankroll.HasUnresolvedOrder)
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
        if (!_bankrollResetArmed || DateTimeOffset.UtcNow > _bankrollResetArmExpiresAtUtc)
        {
            _bankrollResetArmed = false;
            _lastFailure = "Bankroll reset not applied: arm it first.";
            return;
        }

        if (_bankroll.HasUnresolvedOrder)
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
            _lastFailure = "None";
        }
        catch (Exception exception)
        {
            _bankroll = BankrollState.Uninitialized;
            _lastFailure = $"Bankroll state load failed; trading remains blocked: {exception.Message}";
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

    private string DescribeBankroll() => _bankroll.IsInitialized
        ? $"{_bankroll.League}: {_bankroll.AvailableChaos} Chaos, {_bankroll.AvailableDivine} Divine (seeded {_bankroll.SeededChaos}/{_bankroll.SeededDivine})"
        : "not initialized; spend is zero until explicitly armed and applied";
}
