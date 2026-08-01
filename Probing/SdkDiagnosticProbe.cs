using ExileCore;
using ExileCore.PoEMemory;
using ExileCore.PoEMemory.Components;
using FaustusControllerLite.Orders;
using ExileCore.Shared.Enums;
using System.Text;

namespace FaustusControllerLite.Probing;

public sealed record SdkDiagnosticResult(string Report, int IssueCount, string Summary);

public static class SdkDiagnosticProbe
{
    public static SdkDiagnosticResult Read(GameController gameController, CurrencyCatalogue? catalogue)
    {
        var report = new StringBuilder();
        var issues = new List<string>();
        report.AppendLine($"FaustusControllerLite SDK probe {DateTimeOffset.UtcNow:O}");
        var panel = gameController.Game.IngameState.IngameUi.CurrencyExchangePanel;

        Section("Session", () =>
        {
            var server = gameController.Game.IngameState.ServerData;
            report.AppendLine($"  league='{server.League}' areaInstanceId={server.InstanceId}");
        });

        Section("Panel and windows", () =>
        {
            var ui = gameController.Game.IngameState.IngameUi;
            Describe(report, "CurrencyExchangePanel", panel);
            Describe(report, "StashElement", ui.StashElement);
            Describe(report, "InventoryPanel", ui.InventoryPanel);
        });

        Section("Cursor and UI hover", () =>
        {
            var cursor = ExileCore.Input.MousePositionNum;
            var hover = gameController.Game.IngameState.UIHover;
            report.AppendLine($"  cursor=({cursor.X:F0},{cursor.Y:F0})");
            if (hover is null)
            {
                report.AppendLine("  hover=null");
                return;
            }
            report.AppendLine(
                $"  hover=0x{hover.Address:X} visible={hover.IsVisible} rect={Rectangle(hover.GetClientRectCache)} " +
                $"text='{hover.TextNoTags}' texture='{hover.TextureName}' children={hover.ChildCount}");
            for (var index = 0; index < panel.OrderElements.Count; index++)
            {
                if (TryFindElementPath(panel.OrderElements[index], hover.Address, $"OrderElements[{index}]", 0, out var path))
                {
                    report.AppendLine($"  hoverOrderPath={path}");
                    break;
                }
            }
        });

        Section("Exchange panel tree", () =>
        {
            DumpElement(report, panel, "  ", 0);
        });

        Section("Dialog-like UI properties", () =>
        {
            DumpDialogProperties(report, gameController.Game.IngameState.IngameUi, "IngameUi");
            DumpDialogProperties(report, panel, "CurrencyExchangePanel");
        });

        Section("Player inventory", () =>
        {
            var inventory = gameController.Game.IngameState.IngameUi.InventoryPanel?[InventoryIndex.PlayerInventory];
            if (inventory is null)
            {
                report.AppendLine("  player inventory=null");
                return;
            }

            var exchangeRight = panel.GetClientRectCache.Right;
            var items = inventory.VisibleInventoryItems;
            report.AppendLine($"  items={items.Count} exchangeRight={exchangeRight:F0}");
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                var entity = item?.Item;
                var path = entity?.Path ?? string.Empty;
                Stack? stack = null;
                var stackReadable = entity is not null && entity.TryGetComponent(out stack);
                var amount = stackReadable ? stack!.Size : 0;
                if (stackReadable && !string.IsNullOrWhiteSpace(path))
                {
                    totals[path] = checked(totals.GetValueOrDefault(path) + amount);
                }
                var rect = item?.GetClientRectCache ?? default;
                report.AppendLine(
                    $"    [{index}] ui=0x{item?.Address:X} entity=0x{entity?.Address:X} " +
                    $"valid={entity?.IsValid} visible={item?.IsVisible} path='{path}' " +
                    $"stack={(stackReadable ? amount : "unreadable")} " +
                    $"rect={Rectangle(rect)} clearOfExchange={rect.Left >= exchangeRight}");
            }
            foreach (var total in totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"  total path='{total.Key}' amount={total.Value}");
            }
        });

        Section("Visible stash", () =>
        {
            var visibleStash = gameController.Game.IngameState.IngameUi.StashElement?.VisibleStash;
            if (visibleStash is null)
            {
                report.AppendLine("  visible stash=null");
                return;
            }

            report.AppendLine($"  type={visibleStash.InvType} visible={visibleStash.IsVisible} items={visibleStash.VisibleInventoryItems.Count}");
            var totals = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var item in visibleStash.VisibleInventoryItems)
            {
                var entity = item?.Item;
                var path = entity?.Path ?? string.Empty;
                if (entity?.TryGetComponent<Stack>(out var stack) == true && !string.IsNullOrWhiteSpace(path))
                {
                    totals[path] = checked(totals.GetValueOrDefault(path) + stack.Size);
                }
            }
            foreach (var total in totals.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                report.AppendLine($"  total path='{total.Key}' amount={total.Value}");
            }
        });

        Section("Selected pair and rate", () =>
        {
            var offered = panel.OfferedItemType;
            var wanted = panel.WantedItemType;
            report.AppendLine($"  offered={Item(offered)}");
            report.AppendLine($"  wanted={Item(wanted)}");
            report.AppendLine($"  MarketRateGet={panel.MarketRateGet} MarketRateGive={panel.MarketRateGive}");
        });

        Section("Stock", () =>
        {
            var wanted = panel.WantedItemStock;
            var offered = panel.OfferedItemStock;
            report.AppendLine($"  WantedItemStock count={wanted.Count}");
            foreach (var level in wanted.Take(5))
            {
                report.AppendLine($"    get={level.Get} give={level.Give} listed={level.ListedCount}");
            }

            report.AppendLine($"  OfferedItemStock count={offered.Count}");
            foreach (var level in offered.Take(5))
            {
                report.AppendLine($"    get={level.Get} give={level.Give} listed={level.ListedCount}");
            }
        });

        Section("Exact normalized rates", () =>
        {
            var offered = panel.OfferedItemType;
            var wanted = panel.WantedItemType;
            if (offered is null || wanted is null ||
                string.IsNullOrWhiteSpace(offered.Metadata) ||
                string.IsNullOrWhiteSpace(wanted.Metadata) ||
                string.Equals(offered.Metadata, wanted.Metadata, StringComparison.Ordinal))
            {
                report.AppendLine("  unavailable: select two distinct currencies");
                return;
            }

            var server = gameController.Game.IngameState.ServerData;
            var capture = new MarketCapture(
                Guid.NewGuid(),
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                server.League ?? string.Empty,
                server.InstanceId,
                new CurrencyIdentity(offered.Metadata, offered.Hash, offered.BaseName),
                new CurrencyIdentity(wanted.Metadata, wanted.Hash, wanted.BaseName),
                panel.MarketRateGet,
                panel.MarketRateGive,
                panel.WantedItemStock.Select(level => new RawStockLevel(
                    RawBookSide.WantedItem,
                    level.Get,
                    level.Give,
                    level.ListedCount)).ToArray(),
                panel.OfferedItemStock.Select(level => new RawStockLevel(
                    RawBookSide.OfferedItem,
                    level.Get,
                    level.Give,
                    level.ListedCount)).ToArray());
            var marketRate = MarketCaptureNormalizer.MarketRate(capture);
            report.AppendLine(
                $"  selected market {capture.OfferedCurrency.Name} -> {capture.WantedCurrency.Name}: " +
                (marketRate is null
                    ? "unavailable"
                    : $"raw={capture.MarketRateGet}/{capture.MarketRateGive} reduced={marketRate}"));
            report.AppendLine(
                $"  reverse market {capture.WantedCurrency.Name} -> {capture.OfferedCurrency.Name}: " +
                (marketRate is null ? "unavailable" : $"reduced={marketRate.Value.Reverse()}"));

            foreach (var level in MarketCaptureNormalizer.CreateLevels(capture).Take(20))
            {
                report.AppendLine(
                    $"  {level.SourceSide}: {level.From.Name} -> {level.To.Name} " +
                    $"raw={level.Rate.RawNumerator}/{level.Rate.RawDenominator} " +
                    $"reduced={level.Rate} listed={level.ListedCount}");
            }

            report.AppendLine("  executable directed edges:");
            foreach (var edge in MarketCaptureNormalizer.CreateEdges(capture))
            {
                report.AppendLine(
                    $"    {edge.SourceBook} {edge.ExecutionIntent}: " +
                    $"{edge.From.Name} -> {edge.To.Name} rate={edge.Rate} " +
                    $"inputDepth={edge.ImmediateInputDepth} queueAhead={edge.CompetingQueueAhead}");
            }
        });

        Section("Picker", () =>
        {
            var picker = panel.CurrencyPicker;
            Describe(report, "CurrencyPicker", picker);
            report.AppendLine($"  side={(picker.IsPickingWantedCurrency ? "wanted" : "offered")}");
            Describe(report, "OptionContainer", picker.OptionContainer);
            report.AppendLine($"  options={picker.Options.Count}");
            foreach (var option in picker.Options.Take(30))
            {
                if (option is null)
                {
                    report.AppendLine("    null");
                    continue;
                }

                string metadata;
                string name;
                try
                {
                    metadata = option.ItemType?.Metadata ?? string.Empty;
                    name = option.ItemType?.BaseName ?? string.Empty;
                }
                catch (Exception exception)
                {
                    metadata = $"<read failed: {exception.GetType().Name}>";
                    name = string.Empty;
                }

                var rectangle = option.GetClientRectCache;
                var catalogueMatch = catalogue is not null &&
                    !string.IsNullOrWhiteSpace(metadata) &&
                    catalogue.TryGetByMetadata(metadata, out _);
                report.AppendLine(
                    $"    name='{name}' metadata='{metadata}' owned={option.Owned} " +
                    $"visible={option.IsVisible} rect={Rectangle(rectangle)} catalogue={catalogueMatch}");
            }
        });

        Section("Amount inputs", () =>
        {
            var offeredInput = panel.OfferedItemCountInput;
            var wantedInput = panel.WantedItemCountInput;
            var focused = gameController.Game.IngameState.FocusedInputElement;
            report.AppendLine($"  focusedAddress=0x{focused?.Address:X} focusedPath='{focused?.PathFromRoot}'");
            report.AppendLine($"  offeredDigits='{SingleLegStagingController.ReadDigits(offeredInput)}'");
            DumpElement(report, offeredInput, "    offered ", depth: 3);
            report.AppendLine($"  wantedDigits='{SingleLegStagingController.ReadDigits(wantedInput)}'");
            DumpElement(report, wantedInput, "    wanted  ", depth: 3);
        });

        Section("Orders and element trees", () =>
        {
            var orders = panel.Orders;
            var elements = panel.OrderElements;
            report.AppendLine($"  orders={orders.Count} elements={elements.Count}");
            for (var index = 0; index < orders.Count && index < 20; index++)
            {
                var order = orders[index];
                if (order is null)
                {
                    report.AppendLine($"  [{index}] null order");
                    continue;
                }

                var status = order.IsCanceled ? "Canceled" : order.IsCompleted ? "Completed" : "Pending";
                report.AppendLine(
                    $"  [{index}] id={order.PlayerOrderId} status={status} " +
                    $"offered={Item(order.OfferedItemType)} wanted={Item(order.WantedItemType)} " +
                    $"offeredStack={order.OfferedItemStackSize}/{order.OriginalOfferedItemStackSize} " +
                    $"wantedStack={order.WantedItemStackSize} " +
                    $"ratio={order.OfferedItemRatioPart}:{order.WantedItemRatioPart} " +
                    $"gold={order.GoldCost} created={order.CreationDate:O} " +
                    $"completed={order.IsCompleted} canceled={order.IsCanceled}");
                if (index < elements.Count)
                {
                    DumpElement(report, elements[index], "    ", depth: 2);
                }
            }
        });

        return new SdkDiagnosticResult(
            report.ToString(),
            issues.Count,
            issues.Count == 0 ? "all SDK sections read without exceptions" : string.Join("; ", issues));

        void Section(string name, Action read)
        {
            report.AppendLine($"-- {name}");
            try
            {
                read();
            }
            catch (Exception exception)
            {
                var issue = $"{name}: {exception.GetType().Name}: {exception.Message}";
                issues.Add(issue);
                report.AppendLine($"  EXCEPTION {issue}");
            }
        }
    }

    private static void Describe(StringBuilder report, string name, Element? element)
    {
        if (element is null)
        {
            report.AppendLine($"  {name}=null");
            return;
        }

        report.AppendLine(
            $"  {name}: visible={element.IsVisible} active={element.IsActive} " +
            $"rect={Rectangle(element.GetClientRectCache)}");
    }

    private static string Item(ExileCore.PoEMemory.Models.BaseItemType? item) => item is null
        ? "null"
        : $"'{item.BaseName}' ({item.Metadata}, hash={item.Hash})";

    private static string Rectangle(SharpDX.RectangleF rectangle) =>
        $"({rectangle.X:F0},{rectangle.Y:F0} {rectangle.Width:F0}x{rectangle.Height:F0})";

    private static void DumpElement(StringBuilder report, Element? element, string indent, int depth)
    {
        if (element is null)
        {
            report.AppendLine($"{indent}null element");
            return;
        }

        report.AppendLine(
            $"{indent}rect={Rectangle(element.GetClientRectCache)} visible={element.IsVisible} " +
            $"text='{element.TextNoTags}' texture='{element.TextureName}' children={element.ChildCount}");
        if (depth == 0 || element.Children is null)
        {
            return;
        }

        foreach (var child in element.Children.Take(12))
        {
            DumpElement(report, child, indent + "  ", depth - 1);
        }
    }

    private static bool TryFindElementPath(
        Element element,
        long address,
        string path,
        int depth,
        out string result)
    {
        if (element.Address == address)
        {
            result = path;
            return true;
        }
        if (depth >= 5)
        {
            result = string.Empty;
            return false;
        }
        for (var index = 0; index < element.Children.Count; index++)
        {
            if (TryFindElementPath(element.Children[index], address, $"{path}.Children[{index}]", depth + 1, out result))
            {
                return true;
            }
        }
        result = string.Empty;
        return false;
    }

    private static void DumpDialogProperties(StringBuilder report, object owner, string ownerName)
    {
        foreach (var property in owner.GetType().GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0 &&
                         (property.Name.Contains("Confirm", StringComparison.OrdinalIgnoreCase) ||
                          property.Name.Contains("Dialog", StringComparison.OrdinalIgnoreCase) ||
                          property.Name.Contains("Popup", StringComparison.OrdinalIgnoreCase) ||
                          property.Name.Contains("Message", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                var value = property.GetValue(owner);
                report.AppendLine($"  {ownerName}.{property.Name}: type={property.PropertyType.FullName} value={value}");
                if (value is Element element)
                {
                    DumpElement(report, element, "    ", 0);
                    if (property.Name.Equals("PopUpWindow", StringComparison.OrdinalIgnoreCase) && element.IsVisible)
                    {
                        DumpElementProperties(report, value, $"{ownerName}.{property.Name}");
                    }
                }
            }
            catch (Exception exception)
            {
                report.AppendLine($"  {ownerName}.{property.Name}: read failed: {exception.Message}");
            }
        }
    }

    private static void DumpElementProperties(StringBuilder report, object owner, string ownerName)
    {
        foreach (var property in owner.GetType().GetProperties()
                     .Where(property => property.GetIndexParameters().Length == 0 &&
                         property.Name is not "Children" and not "Parent"))
        {
            try
            {
                var value = property.GetValue(owner);
                if (value is Element element)
                {
                    report.AppendLine($"    {ownerName}.{property.Name}: element=0x{element.Address:X}");
                    DumpElement(report, element, "      ", 0);
                }
                else if (value is string or bool or int or long or uint or ulong or float or double or Enum)
                {
                    report.AppendLine($"    {ownerName}.{property.Name}: {value}");
                }
            }
            catch
            {
            }
        }
    }
}
