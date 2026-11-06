using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DippinDots;

/// <summary>Which game windows are up, shared by the overlay, the price service and the probe.</summary>
internal static unsafe class Windows
{
    /// <summary>
    /// Any of these visible with a retainer selected counts as "at the retainer". RetainerSellList is included
    /// because "Sell items in your inventory on the market" may open the player bag next to the sell list
    /// without the retainer's own bag.
    /// </summary>
    internal static readonly string[] RetainerWindows = ["InventoryRetainerLarge", "InventoryRetainer", "RetainerSellList"];

    /// <summary>Expanded player inventory: all four grids on screen at once (Darion's layout, 2026-11-05).</summary>
    internal const string PlayerExpansion = "InventoryExpansion";
    internal static readonly string[] PlayerExpansionGrids = ["InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E"];

    internal static bool IsVisible(string addon)
    {
        var p = Plugin.GameGui.GetAddonByName(addon);
        return !p.IsNull && p.IsVisible;
    }

    internal static bool AtRetainer()
    {
        var rm = RetainerManager.Instance();
        return rm != null && rm->GetActiveRetainer() != null && RetainerWindows.Any(IsVisible);
    }

    /// <summary>
    /// The retainer's sell list is up: both "sell items in your retainer's inventory" and "sell items in your
    /// inventory" open it (probe dumps 18:29 and 19:54, 2026-11-05); entrust/withdraw does not
    /// (Screenshot 2026-11-05 204019.png). Darion: dots only while selling.
    /// </summary>
    internal static bool Selling() => AtRetainer() && IsVisible("RetainerSellList");

    /// <summary>True when RetainerManager and the item-order module agree on which retainer is open.</summary>
    internal static bool RetainerDataConsistent()
    {
        var rm = RetainerManager.Instance();
        var iom = ItemOrderModule.Instance();
        if (rm == null || iom == null) return false;
        var active = rm->GetActiveRetainer();
        return active != null && active->RetainerId == iom->ActiveRetainerId;
    }

    internal static InventoryType PlayerPage(int page) => (InventoryType)((uint)InventoryType.Inventory1 + (uint)page);
    internal static InventoryType RetainerPage(int page) => (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
}
