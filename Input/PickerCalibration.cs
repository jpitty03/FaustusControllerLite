using System.Text.Json;

namespace FaustusControllerLite.Input;

public sealed record NormalizedUiPoint(double X, double Y)
{
    public bool IsValid => X is >= 0 and <= 1 && Y is >= 0 and <= 1;
}

public sealed record UiControlRect(float X, float Y, float Width, float Height)
{
    public float CenterX => X + Width / 2f;
    public float CenterY => Y + Height / 2f;
    public bool Contains(float x, float y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

public sealed class PickerCalibration
{
    public const int CurrentSchemaVersion = 7;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public NormalizedUiPoint? OfferedButton { get; set; }
    public NormalizedUiPoint? WantedButton { get; set; }
    public NormalizedUiPoint? PlaceOrderButton { get; set; }
    public double? PlaceOrderPanelAspectRatio { get; set; }
    public NormalizedUiPoint? CollectionSlotOffset { get; set; }
    public double? CollectionRowAspectRatio { get; set; }
    public double? CollectionSlotWidthRatio { get; set; }
    public double? CollectionSlotHeightRatio { get; set; }
    public NormalizedUiPoint? CancelButtonOffset { get; set; }
    public double? CancelRowAspectRatio { get; set; }
    public double? CancelButtonWidthRatio { get; set; }
    public double? CancelButtonHeightRatio { get; set; }
    public NormalizedUiPoint? ReturnSlotOffset { get; set; }
    public double? ReturnRowAspectRatio { get; set; }
    public double? ReturnSlotWidthRatio { get; set; }
    public double? ReturnSlotHeightRatio { get; set; }

    public bool IsComplete => OfferedButton?.IsValid == true && WantedButton?.IsValid == true;
    public bool IsPlacementComplete => PlaceOrderButton?.IsValid == true;
    public bool IsCollectionComplete => IsSafeTerminalGeometry(
        CollectionSlotOffset, CollectionSlotWidthRatio, CollectionSlotHeightRatio,
        CollectionRowAspectRatio, wantedSlot: true);
    public bool IsCancellationComplete => IsSafeCancelGeometry(
        CancelButtonOffset, CancelButtonWidthRatio, CancelButtonHeightRatio, CancelRowAspectRatio);
    public bool IsReturnCollectionComplete => IsSafeTerminalGeometry(
        ReturnSlotOffset, ReturnSlotWidthRatio, ReturnSlotHeightRatio,
        ReturnRowAspectRatio, wantedSlot: false);

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
        float rowX, float rowY, float rowWidth, float rowHeight,
        UiControlRect control, out string failure) =>
        TryRecordTerminalSlot(rowX, rowY, rowWidth, rowHeight, control, wantedSlot: true, out failure);

    public bool TryResolveCollectionSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        IReadOnlyCollection<UiControlRect> candidateSlots,
        out System.Numerics.Vector2 point, out string failure) =>
        TryResolveTerminalSlot(rowX, rowY, rowWidth, rowHeight, candidateSlots,
            wantedSlot: true, out point, out failure);

    public static bool TrySelectTerminalSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        float cursorX, float cursorY,
        IReadOnlyCollection<UiControlRect> candidateSlots,
        bool wantedSlot,
        out UiControlRect? control,
        out string failure)
    {
        control = null;
        var rowAspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        var matches = candidateSlots.Where(candidate =>
        {
            if (!candidate.Contains(cursorX, cursorY)) return false;
            var center = new NormalizedUiPoint(
                (candidate.CenterX - rowX) / rowWidth,
                (candidate.CenterY - rowY) / rowHeight);
            return IsSafeTerminalGeometry(center, candidate.Width / rowWidth,
                candidate.Height / rowHeight, rowAspect, wantedSlot);
        }).ToArray();
        if (matches.Length != 1)
        {
            failure = $"Terminal calibration cursor did not resolve to one visible scaled {(wantedSlot ? "collection" : "return")} slot; found {matches.Length}.";
            return false;
        }
        control = matches[0];
        failure = string.Empty;
        return true;
    }

    private bool TryRecordTerminalSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        UiControlRect control, bool wantedSlot, out string failure)
    {
        var rowAspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        var point = rowWidth > 0 && rowHeight > 0
            ? new NormalizedUiPoint((control.CenterX - rowX) / rowWidth, (control.CenterY - rowY) / rowHeight)
            : null;
        var widthRatio = rowWidth > 0 ? control.Width / rowWidth : 0;
        var heightRatio = rowHeight > 0 ? control.Height / rowHeight : 0;
        if (!IsSafeTerminalGeometry(point, widthRatio, heightRatio, rowAspect, wantedSlot))
        {
            failure = $"Terminal {(wantedSlot ? "collection" : "return")} calibration did not identify a safe square asset slot.";
            return false;
        }
        if (wantedSlot)
        {
            CollectionSlotOffset = point;
            CollectionRowAspectRatio = rowAspect;
            CollectionSlotWidthRatio = widthRatio;
            CollectionSlotHeightRatio = heightRatio;
        }
        else
        {
            ReturnSlotOffset = point;
            ReturnRowAspectRatio = rowAspect;
            ReturnSlotWidthRatio = widthRatio;
            ReturnSlotHeightRatio = heightRatio;
        }
        failure = string.Empty;
        return true;
    }

    private bool TryResolveTerminalSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        IReadOnlyCollection<UiControlRect> candidateSlots,
        bool wantedSlot,
        out System.Numerics.Vector2 point,
        out string failure)
    {
        var offset = wantedSlot ? CollectionSlotOffset : ReturnSlotOffset;
        var widthRatio = wantedSlot ? CollectionSlotWidthRatio : ReturnSlotWidthRatio;
        var heightRatio = wantedSlot ? CollectionSlotHeightRatio : ReturnSlotHeightRatio;
        var recordedAspect = wantedSlot ? CollectionRowAspectRatio : ReturnRowAspectRatio;
        var aspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeTerminalGeometry(offset, widthRatio, heightRatio, recordedAspect, wantedSlot) ||
            recordedAspect is not > 0 ||
            Math.Abs(aspect - recordedAspect.Value) / recordedAspect.Value > 0.03)
        {
            point = default;
            failure = $"Terminal {(wantedSlot ? "collection" : "return")} slot calibration is missing or does not match the order-row layout.";
            return false;
        }
        var matches = candidateSlots.Where(candidate =>
        {
            var center = new NormalizedUiPoint(
                (candidate.CenterX - rowX) / rowWidth,
                (candidate.CenterY - rowY) / rowHeight);
            return IsSafeTerminalGeometry(center, candidate.Width / rowWidth,
                    candidate.Height / rowHeight, aspect, wantedSlot) &&
                Math.Abs(center.X - offset!.X) <= 0.03 &&
                Math.Abs(center.Y - offset.Y) <= 0.08 &&
                RelativeDifference(candidate.Width / rowWidth, widthRatio!.Value) <= 0.20 &&
                RelativeDifference(candidate.Height / rowHeight, heightRatio!.Value) <= 0.20;
        }).ToArray();
        if (matches.Length != 1)
        {
            point = default;
            failure = $"Terminal asset calibration did not resolve to one visible scaled slot; found {matches.Length}.";
            return false;
        }
        point = new System.Numerics.Vector2(matches[0].CenterX, matches[0].CenterY);
        failure = string.Empty;
        return true;
    }

    private static bool IsSafeTerminalGeometry(
        NormalizedUiPoint? point,
        double? widthRatio,
        double? heightRatio,
        double? rowAspectRatio,
        bool wantedSlot) =>
        point?.IsValid == true &&
        point.X >= (wantedSlot ? 0.03 : 0.76) && point.X <= (wantedSlot ? 0.22 : 0.89) &&
        point.Y is >= 0.20 and <= 0.80 &&
        widthRatio is >= 0.08 and <= 0.25 && heightRatio is >= 0.25 and <= 0.75 &&
        rowAspectRatio is > 0 &&
        widthRatio.Value / heightRatio.Value * rowAspectRatio.Value is >= 0.80 and <= 1.25;

    public bool TryRecordCancelButton(
        float rowX, float rowY, float rowWidth, float rowHeight,
        UiControlRect control, out string failure)
    {
        var point = rowWidth > 0 && rowHeight > 0 && control.Width > 0 && control.Height > 0
            ? new NormalizedUiPoint((control.CenterX - rowX) / rowWidth, (control.CenterY - rowY) / rowHeight)
            : null;
        var widthRatio = rowWidth > 0 ? control.Width / rowWidth : 0;
        var heightRatio = rowHeight > 0 ? control.Height / rowHeight : 0;
        var rowAspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeCancelGeometry(point, widthRatio, heightRatio, rowAspect))
        {
            failure = "Cancel calibration did not identify a safe square right-edge leaf control.";
            return false;
        }
        CancelButtonOffset = point;
        CancelRowAspectRatio = rowWidth / rowHeight;
        CancelButtonWidthRatio = widthRatio;
        CancelButtonHeightRatio = heightRatio;
        failure = string.Empty;
        return true;
    }

    public static bool TrySelectCancelControl(
        float rowX, float rowY, float rowWidth, float rowHeight,
        float cursorX, float cursorY,
        IReadOnlyCollection<UiControlRect> visibleLeafControls,
        out UiControlRect? control,
        out string failure)
    {
        control = null;
        var rowAspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (rowWidth <= 0 || rowHeight <= 0)
        {
            failure = "Pending order row geometry is invalid.";
            return false;
        }

        var matches = visibleLeafControls.Where(candidate =>
        {
            if (candidate.Width <= 0 || candidate.Height <= 0 || !candidate.Contains(cursorX, cursorY))
                return false;
            var center = new NormalizedUiPoint(
                (candidate.CenterX - rowX) / rowWidth,
                (candidate.CenterY - rowY) / rowHeight);
            return IsSafeCancelGeometry(
                center, candidate.Width / rowWidth, candidate.Height / rowHeight, rowAspect);
        }).ToArray();
        if (matches.Length != 1)
        {
            failure = $"Cancel calibration cursor did not resolve to one visible square right-edge leaf control; found {matches.Length}.";
            return false;
        }

        control = matches[0];
        failure = string.Empty;
        return true;
    }

    public bool TryResolveCancelButton(
        float rowX, float rowY, float rowWidth, float rowHeight,
        IReadOnlyCollection<UiControlRect> visibleLeafControls,
        out System.Numerics.Vector2 point, out string failure)
    {
        var aspect = rowHeight > 0 ? rowWidth / rowHeight : 0;
        if (!IsSafeCancelGeometry(
                CancelButtonOffset, CancelButtonWidthRatio, CancelButtonHeightRatio, CancelRowAspectRatio) ||
            CancelRowAspectRatio is not > 0 ||
            Math.Abs(aspect - CancelRowAspectRatio.Value) / CancelRowAspectRatio.Value > 0.03)
        {
            point = default;
            failure = "Cancel button calibration is missing or does not match the pending-row layout.";
            return false;
        }

        var matches = visibleLeafControls.Where(control =>
        {
            if (control.Width <= 0 || control.Height <= 0) return false;
            var center = new NormalizedUiPoint(
                (control.CenterX - rowX) / rowWidth,
                (control.CenterY - rowY) / rowHeight);
            var widthRatio = control.Width / rowWidth;
            var heightRatio = control.Height / rowHeight;
            return IsSafeCancelGeometry(center, widthRatio, heightRatio, aspect) &&
                Math.Abs(center.X - CancelButtonOffset!.X) <= 0.015 &&
                Math.Abs(center.Y - CancelButtonOffset.Y) <= 0.05 &&
                RelativeDifference(widthRatio, CancelButtonWidthRatio!.Value) <= 0.20 &&
                RelativeDifference(heightRatio, CancelButtonHeightRatio!.Value) <= 0.20;
        }).ToArray();
        if (matches.Length != 1)
        {
            point = default;
            failure = $"Calibrated cancel geometry did not resolve to one visible scaled leaf control; found {matches.Length}.";
            return false;
        }

        point = new System.Numerics.Vector2(matches[0].CenterX, matches[0].CenterY);
        failure = string.Empty;
        return true;
    }

    private static bool IsSafeCancelOffset(NormalizedUiPoint? point) =>
        point?.IsValid == true && point.X is >= 0.93 and <= 0.99 && point.Y is >= 0.35 and <= 0.65;

    private static bool IsSafeCancelGeometry(
        NormalizedUiPoint? point,
        double? widthRatio,
        double? heightRatio,
        double? rowAspectRatio) =>
        IsSafeCancelOffset(point) && widthRatio is >= 0.015 and <= 0.08 &&
        heightRatio is >= 0.05 and <= 0.25 &&
        rowAspectRatio is > 0 &&
        widthRatio.Value / heightRatio.Value * rowAspectRatio.Value is >= 0.75 and <= 1.34;

    private static double RelativeDifference(double left, double right) =>
        Math.Abs(left - right) / right;

    public bool TryRecordReturnSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        UiControlRect control, out string failure) =>
        TryRecordTerminalSlot(rowX, rowY, rowWidth, rowHeight, control, wantedSlot: false, out failure);

    public bool TryResolveReturnSlot(
        float rowX, float rowY, float rowWidth, float rowHeight,
        IReadOnlyCollection<UiControlRect> candidateSlots,
        out System.Numerics.Vector2 point, out string failure) =>
        TryResolveTerminalSlot(rowX, rowY, rowWidth, rowHeight, candidateSlots,
            wantedSlot: false, out point, out failure);
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
        if (schemaVersion is not 1 and not 2 and not 3 and not 4 and not 5 and not 6 and
                not PickerCalibration.CurrentSchemaVersion ||
            calibration.OfferedButton is { IsValid: false } ||
            calibration.WantedButton is { IsValid: false } ||
            calibration.PlaceOrderButton is { IsValid: false } ||
            schemaVersion >= 7 && HasAnyTerminalGeometry(calibration, wantedSlot: true) &&
                !IsSafeLoadedTerminalGeometry(calibration, wantedSlot: true) ||
            schemaVersion >= 6 && HasAnyCancelGeometry(calibration) &&
                !IsSafeLoadedCancelGeometry(calibration) ||
            schemaVersion >= 7 && HasAnyTerminalGeometry(calibration, wantedSlot: false) &&
                !IsSafeLoadedTerminalGeometry(calibration, wantedSlot: false))
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
        if (schemaVersion < 6)
        {
            calibration.CancelButtonOffset = null;
            calibration.CancelRowAspectRatio = null;
            calibration.CancelButtonWidthRatio = null;
            calibration.CancelButtonHeightRatio = null;
        }
        if (schemaVersion < 7)
        {
            calibration.CollectionSlotOffset = null;
            calibration.CollectionRowAspectRatio = null;
            calibration.CollectionSlotWidthRatio = null;
            calibration.CollectionSlotHeightRatio = null;
            calibration.ReturnSlotOffset = null;
            calibration.ReturnRowAspectRatio = null;
            calibration.ReturnSlotWidthRatio = null;
            calibration.ReturnSlotHeightRatio = null;
        }
        if (schemaVersion < PickerCalibration.CurrentSchemaVersion) Save(path, calibration);

        return calibration;
    }

    private static bool HasAnyTerminalGeometry(PickerCalibration calibration, bool wantedSlot) =>
        wantedSlot
            ? calibration.CollectionSlotOffset is not null || calibration.CollectionRowAspectRatio is not null ||
                calibration.CollectionSlotWidthRatio is not null || calibration.CollectionSlotHeightRatio is not null
            : calibration.ReturnSlotOffset is not null || calibration.ReturnRowAspectRatio is not null ||
                calibration.ReturnSlotWidthRatio is not null || calibration.ReturnSlotHeightRatio is not null;

    private static bool IsSafeLoadedTerminalGeometry(PickerCalibration calibration, bool wantedSlot)
    {
        var point = wantedSlot ? calibration.CollectionSlotOffset : calibration.ReturnSlotOffset;
        var rowAspect = wantedSlot ? calibration.CollectionRowAspectRatio : calibration.ReturnRowAspectRatio;
        var widthRatio = wantedSlot ? calibration.CollectionSlotWidthRatio : calibration.ReturnSlotWidthRatio;
        var heightRatio = wantedSlot ? calibration.CollectionSlotHeightRatio : calibration.ReturnSlotHeightRatio;
        return point?.IsValid == true &&
            point.X >= (wantedSlot ? 0.03 : 0.76) && point.X <= (wantedSlot ? 0.22 : 0.89) &&
            point.Y is >= 0.20 and <= 0.80 && widthRatio is >= 0.08 and <= 0.25 &&
            heightRatio is >= 0.25 and <= 0.75 && rowAspect is > 0 &&
            widthRatio.Value / heightRatio.Value * rowAspect.Value is >= 0.80 and <= 1.25;
    }

    private static bool HasAnyCancelGeometry(PickerCalibration calibration) =>
        calibration.CancelButtonOffset is not null || calibration.CancelRowAspectRatio is not null ||
        calibration.CancelButtonWidthRatio is not null || calibration.CancelButtonHeightRatio is not null;

    private static bool IsSafeLoadedCancelGeometry(PickerCalibration calibration) =>
        calibration.CancelButtonOffset is { IsValid: true } point &&
        point.X is >= 0.93 and <= 0.99 && point.Y is >= 0.35 and <= 0.65 &&
        calibration.CancelButtonWidthRatio is >= 0.015 and <= 0.08 &&
        calibration.CancelButtonHeightRatio is >= 0.05 and <= 0.25 &&
        calibration.CancelRowAspectRatio is > 0 &&
        calibration.CancelButtonWidthRatio.Value / calibration.CancelButtonHeightRatio.Value *
            calibration.CancelRowAspectRatio.Value is >= 0.75 and <= 1.34;

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
