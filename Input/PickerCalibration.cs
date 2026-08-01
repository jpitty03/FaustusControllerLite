using System.Text.Json;

namespace FaustusControllerLite.Input;

public sealed record NormalizedUiPoint(double X, double Y)
{
    public bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public sealed class PickerCalibration
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public NormalizedUiPoint? OfferedButton { get; set; }
    public NormalizedUiPoint? WantedButton { get; set; }
    public NormalizedUiPoint? PlaceOrderButton { get; set; }
    public double? PlaceOrderPanelAspectRatio { get; set; }
    public NormalizedUiPoint? CollectionSlotOffset { get; set; }
    public double? CollectionRowAspectRatio { get; set; }
    public NormalizedUiPoint? CancelButtonOffset { get; set; }
    public double? CancelRowAspectRatio { get; set; }
    public NormalizedUiPoint? ReturnSlotOffset { get; set; }
    public double? ReturnRowAspectRatio { get; set; }

    public bool IsComplete => OfferedButton?.IsValid == true && WantedButton?.IsValid == true;
    public bool IsPlacementComplete => PlaceOrderButton?.IsValid == true;
    public bool IsCollectionComplete => IsSafeCollectionOffset(CollectionSlotOffset);
    public bool IsCancellationComplete => IsSafeCancelOffset(CancelButtonOffset);
    public bool IsReturnCollectionComplete => IsSafeReturnOffset(ReturnSlotOffset);

    public bool TryRecord(
        bool wantedSide,
        float panelX,
        float panelY,
        float panelWidth,
        float panelHeight,
        float cursorX,
        float cursorY,
        out string failure)
    {
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            failure = "Currency Exchange panel geometry is invalid.";
            return false;
        }

        var point = new NormalizedUiPoint(
            (cursorX - panelX) / panelWidth,
            (cursorY - panelY) / panelHeight);
        if (!point.IsValid)
        {
            failure = "Observed picker click was outside the Currency Exchange panel.";
            return false;
        }

        if (wantedSide)
        {
            WantedButton = point;
        }
        else
        {
            OfferedButton = point;
        }

        failure = string.Empty;
        return true;
    }

    public bool TryResolve(
        bool wantedSide,
        float panelX,
        float panelY,
        float panelWidth,
        float panelHeight,
        out System.Numerics.Vector2 point,
        out string failure)
    {
        var normalized = wantedSide ? WantedButton : OfferedButton;
        if (normalized?.IsValid != true || panelWidth <= 0 || panelHeight <= 0)
        {
            point = default;
            failure = $"The {(wantedSide ? "wanted" : "offered")} picker button is not calibrated.";
            return false;
        }

        point = new System.Numerics.Vector2(
            panelX + (float)(normalized.X * panelWidth),
            panelY + (float)(normalized.Y * panelHeight));
        failure = string.Empty;
        return true;
    }

    public bool TryRecordPlaceOrder(
        float panelX,
        float panelY,
        float panelWidth,
        float panelHeight,
        float cursorX,
        float cursorY,
        out string failure)
    {
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            failure = "Currency Exchange panel geometry is invalid.";
            return false;
        }

        var point = new NormalizedUiPoint(
            (cursorX - panelX) / panelWidth,
            (cursorY - panelY) / panelHeight);
        if (!point.IsValid)
        {
            failure = "Place Order calibration cursor was outside the exchange panel.";
            return false;
        }

        PlaceOrderButton = point;
        PlaceOrderPanelAspectRatio = panelWidth / panelHeight;
        failure = string.Empty;
        return true;
    }

    public bool TryResolvePlaceOrder(
        float panelX,
        float panelY,
        float panelWidth,
        float panelHeight,
        out System.Numerics.Vector2 point,
        out string failure)
    {
        var aspectRatio = panelHeight > 0 ? panelWidth / panelHeight : 0;
        if (PlaceOrderButton?.IsValid != true || panelWidth <= 0 || panelHeight <= 0 ||
            PlaceOrderPanelAspectRatio is not > 0 ||
            Math.Abs(aspectRatio - PlaceOrderPanelAspectRatio.Value) / PlaceOrderPanelAspectRatio.Value > 0.03)
        {
            point = default;
            failure = "The Place Order button is not calibrated.";
            return false;
        }

        point = new System.Numerics.Vector2(
            panelX + (float)(PlaceOrderButton.X * panelWidth),
            panelY + (float)(PlaceOrderButton.Y * panelHeight));
        failure = string.Empty;
        return true;
    }

    public bool TryRecordCollectionSlot(
        float rowX,
        float rowY,
        float rowWidth,
        float rowHeight,
        float cursorX,
        float cursorY,
        out string failure)
    {
        if (rowWidth <= 0 || rowHeight <= 0)
        {
            failure = "Tracked order row geometry is invalid.";
            return false;
        }

        var point = new NormalizedUiPoint((cursorX - rowX) / rowWidth, (cursorY - rowY) / rowHeight);
        if (!IsSafeCollectionOffset(point))
        {
            failure = "Collection calibration must be inside the left-side bought-currency slot of the tracked order row.";
            return false;
        }

        CollectionSlotOffset = point;
        CollectionRowAspectRatio = rowWidth / rowHeight;
        failure = string.Empty;
        return true;
    }

    public bool TryResolveCollectionSlot(
        float rowX,
        float rowY,
        float rowWidth,
        float rowHeight,
        out System.Numerics.Vector2 point,
        out string failure)
    {
        var aspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeCollectionOffset(CollectionSlotOffset) ||
            CollectionRowAspectRatio is not > 0 ||
            Math.Abs(aspect - CollectionRowAspectRatio.Value) / CollectionRowAspectRatio.Value > 0.03)
        {
            point = default;
            failure = "Collection slot calibration is missing or does not match the order-row layout.";
            return false;
        }

        point = new System.Numerics.Vector2(
            rowX + (float)(CollectionSlotOffset!.X * rowWidth),
            rowY + (float)(CollectionSlotOffset.Y * rowHeight));
        failure = string.Empty;
        return true;
    }

    private static bool IsSafeCollectionOffset(NormalizedUiPoint? point) =>
        point?.IsValid == true && point.X is >= 0.03 and <= 0.22 && point.Y is >= 0.20 and <= 0.80;

    public bool TryRecordCancelButton(
        float rowX, float rowY, float rowWidth, float rowHeight,
        float cursorX, float cursorY, out string failure)
    {
        var point = rowWidth > 0 && rowHeight > 0
            ? new NormalizedUiPoint((cursorX - rowX) / rowWidth, (cursorY - rowY) / rowHeight)
            : null;
        if (!IsSafeCancelOffset(point))
        {
            failure = "Cancel calibration must be inside the pending row's small right-edge X control.";
            return false;
        }
        CancelButtonOffset = point;
        CancelRowAspectRatio = rowWidth / rowHeight;
        failure = string.Empty;
        return true;
    }

    public bool TryResolveCancelButton(
        float rowX, float rowY, float rowWidth, float rowHeight,
        out System.Numerics.Vector2 point, out string failure)
    {
        var aspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeCancelOffset(CancelButtonOffset) || CancelRowAspectRatio is not > 0 ||
            Math.Abs(aspect - CancelRowAspectRatio.Value) / CancelRowAspectRatio.Value > 0.03)
        {
            point = default;
            failure = "Cancel button calibration is missing or does not match the pending-row layout.";
            return false;
        }
        point = new System.Numerics.Vector2(
            rowX + (float)(CancelButtonOffset!.X * rowWidth),
            rowY + (float)(CancelButtonOffset.Y * rowHeight));
        failure = string.Empty;
        return true;
    }

    private static bool IsSafeCancelOffset(NormalizedUiPoint? point) =>
        point?.IsValid == true && point.X is >= 0.93 and <= 0.99 && point.Y is >= 0.35 and <= 0.65;

    public bool TryRecordReturnSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        float cursorX, float cursorY, out string failure)
    {
        var point = rowWidth > 0 && rowHeight > 0
            ? new NormalizedUiPoint((cursorX - rowX) / rowWidth, (cursorY - rowY) / rowHeight)
            : null;
        if (!IsSafeReturnOffset(point))
        {
            failure = "Return calibration must be inside the canceled row's right offered-currency slot.";
            return false;
        }
        ReturnSlotOffset = point;
        ReturnRowAspectRatio = rowWidth / rowHeight;
        failure = string.Empty;
        return true;
    }

    public bool TryResolveReturnSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        out System.Numerics.Vector2 point, out string failure)
    {
        var aspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeReturnOffset(ReturnSlotOffset) || ReturnRowAspectRatio is not > 0 ||
            Math.Abs(aspect - ReturnRowAspectRatio.Value) / ReturnRowAspectRatio.Value > 0.03)
        {
            point = default;
            failure = "Return slot calibration is missing or does not match the canceled-row layout.";
            return false;
        }
        point = new System.Numerics.Vector2(
            rowX + (float)(ReturnSlotOffset!.X * rowWidth),
            rowY + (float)(ReturnSlotOffset.Y * rowHeight));
        failure = string.Empty;
        return true;
    }

    private static bool IsSafeReturnOffset(NormalizedUiPoint? point) =>
        point?.IsValid == true && point.X is >= 0.76 and <= 0.89 && point.Y is >= 0.20 and <= 0.80;
}

public sealed class PickerCalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public PickerCalibration Load(string path)
    {
        if (!File.Exists(path))
        {
            return new PickerCalibration();
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(PickerCalibration.SchemaVersion), out var schemaElement) ||
            !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidDataException("Picker calibration schema version was missing.");
        }

        var calibration = JsonSerializer.Deserialize<PickerCalibration>(json, JsonOptions)
            ?? throw new InvalidDataException("Picker calibration file was empty.");
        if (schemaVersion is not 1 and not 2 and not 3 and not 4 and not PickerCalibration.CurrentSchemaVersion ||
            calibration.OfferedButton is { IsValid: false } ||
            calibration.WantedButton is { IsValid: false } ||
            calibration.PlaceOrderButton is { IsValid: false } ||
            calibration.CollectionSlotOffset is not null && !IsSafeLoadedCollectionOffset(calibration.CollectionSlotOffset) ||
            calibration.CancelButtonOffset is not null && !IsSafeLoadedCancelOffset(calibration.CancelButtonOffset) ||
            calibration.ReturnSlotOffset is not null && !IsSafeLoadedReturnOffset(calibration.ReturnSlotOffset))
        {
            throw new InvalidDataException("Picker calibration schema or normalized coordinates are invalid.");
        }

        calibration.SchemaVersion = PickerCalibration.CurrentSchemaVersion;
        if (schemaVersion == 1)
        {
            calibration.PlaceOrderButton = null;
            calibration.PlaceOrderPanelAspectRatio = null;
        }
        if (schemaVersion < 3)
        {
            calibration.CollectionSlotOffset = null;
            calibration.CollectionRowAspectRatio = null;
        }
        if (schemaVersion < 4)
        {
            calibration.CancelButtonOffset = null;
            calibration.CancelRowAspectRatio = null;
        }
        if (schemaVersion < 5)
        {
            calibration.ReturnSlotOffset = null;
            calibration.ReturnRowAspectRatio = null;
        }
        if (schemaVersion < PickerCalibration.CurrentSchemaVersion) Save(path, calibration);

        return calibration;
    }

    private static bool IsSafeLoadedCollectionOffset(NormalizedUiPoint point) =>
        point.IsValid && point.X is >= 0.03 and <= 0.22 && point.Y is >= 0.20 and <= 0.80;

    private static bool IsSafeLoadedCancelOffset(NormalizedUiPoint point) =>
        point.IsValid && point.X is >= 0.93 and <= 0.99 && point.Y is >= 0.35 and <= 0.65;

    private static bool IsSafeLoadedReturnOffset(NormalizedUiPoint point) =>
        point.IsValid && point.X is >= 0.76 and <= 0.89 && point.Y is >= 0.20 and <= 0.80;

    public void Save(string path, PickerCalibration calibration)
    {
        ArgumentNullException.ThrowIfNull(calibration);
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Picker calibration path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(calibration, JsonOptions));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
