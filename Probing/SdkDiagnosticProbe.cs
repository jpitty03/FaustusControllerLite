using ExileCore;
using ExileCore.PoEMemory;
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

        Section("Panel and windows", () =>
        {
            var ui = gameController.Game.IngameState.IngameUi;
            Describe(report, "CurrencyExchangePanel", panel);
            Describe(report, "StashElement", ui.StashElement);
            Describe(report, "InventoryPanel", ui.InventoryPanel);
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
                    $"wantedStack={order.WantedItemStackSize}");
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
}
