using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared.Enums;
using RectangleF = SharpDX.RectangleF;

namespace FaustusControllerLite.Probing;

public sealed record StashTabSlot(
    int Index,
    string Metadata,
    long StackSize,
    int MaxStackSize,
    RectangleF Rect,
    bool ClearOfExchange,
    bool StackReadable);

public sealed record StashTabHolding(
    string Metadata,
    long Amount,
    int SlotCount,
    int UnreadableSlots);

public sealed record StashTabScan(
    bool Readable,
    string FailureReason,
    InventoryType TabType,
    bool Visible,
    IReadOnlyList<StashTabSlot> Slots,
    IReadOnlyList<StashTabHolding> Holdings)
{
    public static StashTabScan Unreadable(string failureReason) => new(
        false, failureReason, default, false,
        Array.Empty<StashTabSlot>(), Array.Empty<StashTabHolding>());

    public bool TryGetHolding(string metadata, out StashTabHolding? holding)
    {
        holding = Holdings.FirstOrDefault(
            candidate => StringComparer.Ordinal.Equals(candidate.Metadata, metadata));
        return holding is not null;
    }
}

public static class StashTabReader
{
    public static StashTabScan Read(GameController gameController)
    {
        ArgumentNullException.ThrowIfNull(gameController);

        var visibleStash = gameController.Game.IngameState.IngameUi.StashElement?.VisibleStash;
        if (visibleStash is null)
        {
            return StashTabScan.Unreadable("Visible stash is unavailable.");
        }

        var items = visibleStash.VisibleInventoryItems;
        if (items is null)
        {
            return StashTabScan.Unreadable("Visible stash items are unavailable.");
        }

        var exchangeRight = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel
            .GetClientRectCache.Right;
        var slots = new List<StashTabSlot>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var entity = item?.Item;
            var metadata = entity?.Path ?? string.Empty;
            if (string.IsNullOrWhiteSpace(metadata))
            {
                continue;
            }

            Stack? stack = null;
            var stackReadable = entity is not null && entity.TryGetComponent(out stack);
            var rect = item?.GetClientRectCache ?? default;
            slots.Add(new StashTabSlot(
                index,
                metadata,
                stackReadable ? stack!.Size : 0,
                stackReadable ? stack!.Info.MaxStackSize : 0,
                rect,
                rect.Left >= exchangeRight,
                stackReadable));
        }

        var holdings = slots
            .GroupBy(slot => slot.Metadata, StringComparer.Ordinal)
            .Select(group => new StashTabHolding(
                group.Key,
                group.Where(slot => slot.StackReadable).Aggregate(0L, (total, slot) => checked(total + slot.StackSize)),
                group.Count(),
                group.Count(slot => !slot.StackReadable)))
            .OrderBy(holding => holding.Metadata, StringComparer.Ordinal)
            .ToArray();

        return new StashTabScan(true, string.Empty, visibleStash.InvType, visibleStash.IsVisible,
            slots, holdings);
    }
}
