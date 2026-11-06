using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace DippinDots;

/// <summary>
/// Phase 1 probe. Read-only: it never writes to game memory and calls no game functions that mutate state.
/// Answers U1 (grid -> page), U2 (node -> bag index), U3 (empty slot shape), U4 (extra pages/grids),
/// U5 (HQ flag) by dumping three things side by side and scoring mapping hypotheses against them:
///   1. slot nodes of each RetainerGridN (position, size, scale, the icon id the game is showing, qty text)
///   2. every RetainerPage1..7 container slot (item id, qty, flags)
///   3. the active retainer's ItemOrderModule sorter (display order -> page/slot)
/// </summary>
internal sealed unsafe class Probe
{
    public bool OverlayEnabled { get; set; }

    private const int SlotsPerGrid = 35; // observed: 5 columns x 7 rows
    private const uint HqIconOffset = 1_000_000;

    private static readonly string[] GridAddons =
        Enumerable.Range(0, 7).Select(i => $"RetainerGrid{i}").ToArray(); // 5 and 6 probe U4

    // ---- data shapes -------------------------------------------------------------------------

    private sealed record SlotNode(
        string Addon, int Grid, int ListIndex, uint NodeId, int Cell,
        float X, float Y, float ScreenX, float ScreenY, ushort Width, ushort Height,
        float NodeScaleX, float AddonScale, bool Visible, uint IconId, string QtyText)
    {
        public int DisplayIndex => Grid * SlotsPerGrid + Cell;
    }

    private readonly record struct BagItem(
        int Page, int Slot, uint ItemId, int Quantity, InventoryItem.ItemFlags Flags, bool IsSymbolic)
    {
        public bool Hq => (Flags & InventoryItem.ItemFlags.HighQuality) != 0;
    }

    private readonly record struct SortEntry(int ListIndex, ushort Page, ushort Slot, ushort Index, byte Flags);

    private sealed record Snapshot(
        List<SlotNode> Nodes,
        Dictionary<(int Page, int Slot), BagItem> Bag,
        List<string> ContainerSummary,
        List<SortEntry> Sorter,
        string SorterSummary,
        string RetainerSummary);

    // ---- capture -----------------------------------------------------------------------------

    /// <summary>
    /// Every RetainerGridN reports IsVisible=true on every tab, and grids 0/2/4 and 1/3 share screen positions
    /// (probe 2026-11-05). The tab index on InventoryRetainerLarge is the only reliable signal:
    /// tab 0 -> grids 0,1; tab 1 -> grids 2,3; tab 2 -> grid 4 (+ RetainerCrystalGrid on the right).
    /// Returns null when the large retainer inventory isn't open.
    /// </summary>
    internal static int[]? ActiveGrids()
    {
        var p = Plugin.GameGui.GetAddonByName("InventoryRetainerLarge");
        if (p.IsNull || !p.IsVisible) return null;
        var tab = ((FFXIVClientStructs.FFXIV.Client.UI.AddonInventoryRetainerLarge*)p.Address)->TabIndex;
        return tab switch { 0 => [0, 1], 1 => [2, 3], 2 => [4], _ => [] };
    }

    /// <summary>IconId 0xFFFFFFFF = grid never populated (seen on grid 4 before tab 3 was opened).</summary>
    private const uint UnpopulatedIcon = uint.MaxValue;

    private static Snapshot Capture()
    {
        var nodes = new List<SlotNode>();
        var active = ActiveGrids() ?? [];
        foreach (var name in GridAddons)
        {
            var grid = int.Parse(name["RetainerGrid".Length..]);
            nodes.AddRange(ReadGrid(name, grid, active.Contains(grid)));
        }

        var bag = new Dictionary<(int, int), BagItem>();
        var containerSummary = new List<string>();
        var im = InventoryManager.Instance();
        for (var page = 0; page < 7; page++)
        {
            var type = (InventoryType)((uint)InventoryType.RetainerPage1 + (uint)page);
            var c = im == null ? null : im->GetInventoryContainer(type);
            if (c == null)
            {
                containerSummary.Add($"{type} ({(int)type}): container NULL");
                continue;
            }

            var filled = 0;
            for (var s = 0; s < c->Size; s++)
            {
                var it = c->Items + s;
                var item = new BagItem(page, it->Slot, it->IsSymbolic ? 0 : it->ItemId, it->Quantity, it->Flags, it->IsSymbolic);
                bag[(page, s)] = item;
                if (item.ItemId != 0) filled++;
            }

            containerSummary.Add($"{type} ({(int)type}): Size={c->Size} IsLoaded={c->IsLoaded} Type={c->Type} filled={filled}");
        }

        var sorter = new List<SortEntry>();
        var sorterSummary = "ItemOrderModule NULL";
        var iom = ItemOrderModule.Instance();
        if (iom != null)
        {
            var srt = iom->GetActiveRetainerSorter();
            if (srt == null)
            {
                sorterSummary = $"ActiveRetainerId={iom->ActiveRetainerId:X} sorter NULL";
            }
            else
            {
                var count = (int)srt->Items.LongCount;
                for (var i = 0; i < count; i++)
                {
                    var e = srt->Items[i].Value;
                    if (e == null) { sorter.Add(new SortEntry(i, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, 0)); continue; }
                    sorter.Add(new SortEntry(i, e->Page, e->Slot, e->Index, e->Flags));
                }

                sorterSummary = $"ActiveRetainerId={iom->ActiveRetainerId:X} InventoryType={srt->InventoryType} " +
                                $"ItemsPerPage={srt->ItemsPerPage} Items.Count={count} SortFunctionIndex={srt->SortFunctionIndex} " +
                                $"PercentComplete={srt->PercentComplete}";
            }
        }

        var retainerSummary = "RetainerManager NULL";
        var rm = RetainerManager.Instance();
        if (rm != null)
        {
            var r = rm->GetActiveRetainer();
            retainerSummary = r == null
                ? $"LastSelectedRetainerId={rm->LastSelectedRetainerId:X} active=NULL"
                : $"LastSelectedRetainerId={rm->LastSelectedRetainerId:X} active={r->NameString} id={r->RetainerId:X} " +
                  $"ItemCount={r->ItemCount} MarketItemCount={r->MarketItemCount}";
        }

        return new Snapshot(nodes, bag, containerSummary, sorter, sorterSummary, retainerSummary);
    }

    private static List<SlotNode> ReadGrid(string addonName, int grid, bool gridActive)
    {
        var result = new List<SlotNode>();
        var ptr = Plugin.GameGui.GetAddonByName(addonName);
        if (ptr.IsNull) return result;

        var unit = (AtkUnitBase*)ptr.Address;
        var uld = unit->UldManager;
        var raw = new List<SlotNode>();

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue;

            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null || comp->UldManager.Objects == null) continue;
            if (((AtkUldComponentInfo*)comp->UldManager.Objects)->ComponentType != ComponentType.DragDrop) continue;

            var dd = (AtkComponentDragDrop*)comp;
            var icon = dd->AtkComponentIcon;
            uint iconId = 0;
            var qty = "";
            if (icon != null)
            {
                iconId = icon->IconId;
                if (icon->QuantityText != null) qty = icon->QuantityText->NodeText.ToString();
            }

            raw.Add(new SlotNode(addonName, grid, i, node->NodeId, -1,
                node->X, node->Y, node->ScreenX, node->ScreenY, node->Width, node->Height,
                node->ScaleX, unit->Scale, gridActive && node->IsVisible() && unit->IsVisible, iconId, qty));
        }

        // Cell = reading order (row-major) by local position; does not assume anything about NodeId order.
        var ordered = raw.OrderBy(n => MathF.Round(n.Y)).ThenBy(n => n.X).ToList();
        for (var cell = 0; cell < ordered.Count; cell++)
            result.Add(ordered[cell] with { Cell = cell });
        return result;
    }

    // ---- mapping hypotheses ------------------------------------------------------------------

    private delegate (int Page, int Slot)? Mapping(int displayIndex, Snapshot s);

    private static readonly (string Name, Mapping Map)[] Hypotheses =
    [
        // H0: no sorter; display index walks the containers linearly (page = d / size).
        ("H0 linear d -> (d / pageSize, d % pageSize)", (d, s) =>
        {
            var size = s.Bag.Keys.Where(k => k.Page == 0).Count();
            return size == 0 ? null : (d / size, d % size);
        }),
        // H1: sorter list position is the display index.
        ("H1 sorter.Items[d] -> (Page, Slot)", (d, s) =>
            d < s.Sorter.Count && s.Sorter[d].Page != ushort.MaxValue ? (s.Sorter[d].Page, s.Sorter[d].Slot) : null),
        // H2: sorter entry whose Index field equals the display index.
        ("H2 sorter entry with Index == d -> (Page, Slot)", (d, s) =>
        {
            foreach (var e in s.Sorter)
                if (e.Index == d) return (e.Page, e.Slot);
            return null;
        }),
    ];

    private uint ExpectedIcon(BagItem item)
    {
        if (item.ItemId == 0) return 0;
        var row = Plugin.Data.GetExcelSheet<LuminaItem>().GetRowOrDefault(item.ItemId);
        if (row == null) return uint.MaxValue;
        return row.Value.Icon + (item.Hq ? HqIconOffset : 0);
    }

    private string ItemName(uint itemId)
    {
        if (itemId == 0) return "";
        var row = Plugin.Data.GetExcelSheet<LuminaItem>().GetRowOrDefault(itemId);
        return row == null ? "?" : row.Value.Name.ExtractText();
    }

    /// <summary>Quantity shown in the slot vs. bag quantity. Blank text is accepted for quantity 1 (non-stackables).</summary>
    private static bool QtyMatches(string shown, int quantity)
    {
        var digits = new string(shown.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? quantity <= 1 : digits == quantity.ToString();
    }

    private sealed record ScoreRow(string Scope, int Filled, int IconMatch, int IconQtyMatch, int Unmapped);

    /// <summary>
    /// Only slots where the shown icon or the predicted item is non-empty count ("filled"); empty-vs-empty pairs
    /// would otherwise inflate every hypothesis. Rows: all visible slots, then each grid separately (with visibility).
    /// </summary>
    private List<ScoreRow> Score(Mapping map, Snapshot s)
    {
        ScoreRow Tally(string scope, IEnumerable<SlotNode> nodes)
        {
            int filled = 0, icon = 0, iconQty = 0, unmapped = 0;
            foreach (var n in nodes)
            {
                if (n.IconId == UnpopulatedIcon) continue;
                var loc = map(n.DisplayIndex, s);
                BagItem item = default;
                var mapped = loc != null && s.Bag.TryGetValue(loc.Value, out item);
                var expected = mapped ? ExpectedIcon(item) : 0;
                if (expected == 0 && n.IconId == 0) continue;
                filled++;
                if (!mapped) { unmapped++; continue; }
                if (expected != n.IconId) continue;
                icon++;
                if (QtyMatches(n.QtyText, item.Quantity)) iconQty++;
            }
            return new ScoreRow(scope, filled, icon, iconQty, unmapped);
        }

        var rows = new List<ScoreRow> { Tally("visible (current tab)", s.Nodes.Where(n => n.Visible)) };
        foreach (var g in s.Nodes.GroupBy(n => n.Grid).OrderBy(g => g.Key))
        {
            var state = g.Any(n => n.Visible) ? "" : " (other tab)";
            if (g.All(n => n.IconId == UnpopulatedIcon)) state += " (never populated)";
            rows.Add(Tally($"grid{g.Key}{state}", g));
        }
        return rows;
    }

    // ---- dump --------------------------------------------------------------------------------

    public string DumpToFile()
    {
        var s = Capture();
        var sb = new StringBuilder();
        sb.AppendLine($"# Dippin' Dots probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Retainer: {s.RetainerSummary}");

        sb.AppendLine();
        sb.AppendLine("## Addons");
        foreach (var name in Plugin.WatchedAddons)
        {
            var p = Plugin.GameGui.GetAddonByName(name);
            if (p.IsNull) { sb.AppendLine($"{name}: not loaded"); continue; }
            var extra = "";
            if (name == "InventoryRetainerLarge")
                extra = $" TabIndex={((FFXIVClientStructs.FFXIV.Client.UI.AddonInventoryRetainerLarge*)p.Address)->TabIndex}";
            sb.AppendLine($"{name}: visible={p.IsVisible} pos=({p.X},{p.Y}) scale={p.Scale} size=({p.Width}x{p.Height}){extra}");
        }

        sb.AppendLine();
        sb.AppendLine("## Hypothesis scores");
        sb.AppendLine("Only 'filled' slots count (shown icon or predicted item non-empty). iconMatch = shown icon == predicted");
        sb.AppendLine("item icon (+1000000 if HQ); icon+qty also requires the shown quantity text to equal the bag quantity.");
        foreach (var (hname, map) in Hypotheses)
        {
            sb.AppendLine(hname);
            foreach (var r in Score(map, s))
                sb.AppendLine($"  {r.Scope}: iconMatch={r.IconMatch}/{r.Filled} icon+qty={r.IconQtyMatch}/{r.Filled} unmapped={r.Unmapped}");
        }

        sb.AppendLine();
        sb.AppendLine("## Containers (U1/U3/U4/U5)");
        s.ContainerSummary.ForEach(l => sb.AppendLine(l));
        sb.AppendLine("page slot itemId qty flags(hq) symbolic name");
        foreach (var it in s.Bag.Values.OrderBy(b => b.Page).ThenBy(b => b.Slot))
            sb.AppendLine($"{it.Page} {it.Slot} {it.ItemId} {it.Quantity} {(byte)it.Flags}({(it.Hq ? "HQ" : "-")}) {it.IsSymbolic} {ItemName(it.ItemId)}");

        sb.AppendLine();
        sb.AppendLine("## Item-order sorter (active retainer)");
        sb.AppendLine(s.SorterSummary);
        sb.AppendLine("listIndex page slot index flags");
        foreach (var e in s.Sorter)
            sb.AppendLine($"{e.ListIndex} {e.Page} {e.Slot} {e.Index} {e.Flags}");

        sb.AppendLine();
        sb.AppendLine("## Slot nodes (U2)");
        sb.AppendLine("addon listIdx nodeId cell display localXY screenXY WxH nodeScale addonScale visible iconId qtyText | H1 page/slot itemId name");
        foreach (var n in s.Nodes.OrderBy(n => n.DisplayIndex))
        {
            var h1 = Hypotheses[1].Map(n.DisplayIndex, s);
            var h1Text = "-";
            if (h1 != null && s.Bag.TryGetValue(h1.Value, out var item))
                h1Text = $"{h1.Value.Page}/{h1.Value.Slot} {item.ItemId}{(item.Hq ? " HQ" : "")} {ItemName(item.ItemId)}";
            sb.AppendLine($"{n.Addon} {n.ListIndex} {n.NodeId} {n.Cell} {n.DisplayIndex} ({n.X},{n.Y}) ({n.ScreenX},{n.ScreenY}) " +
                          $"{n.Width}x{n.Height} {n.NodeScaleX} {n.AddonScale} {n.Visible} {n.IconId} '{n.QtyText}' | {h1Text}");
        }

        AppendPlayer(sb);

        var dir = Plugin.PluginInterface.GetPluginConfigDirectory();
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(path, sb.ToString());
        Plugin.Log.Information($"[Dots] probe written to {path}");
        return path;
    }

    // ---- player inventory (Phase 4b, U8) -----------------------------------------------------

    private static readonly string[] PlayerAddons =
    [
        "Inventory", "InventoryLarge", "InventoryExpansion", "InventoryGrid",
        "InventoryGrid0", "InventoryGrid1", "InventoryGrid2", "InventoryGrid3",
        "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E",
        "RetainerSellList", "RetainerSell",
    ];

    /// <summary>
    /// Same method as the retainer section: Expanded grids 0E-3E, Inventory1-4, ItemOrderModule.InventorySorter,
    /// scored for H0 (linear, d -> (d / 35, d % 35)) and H1 (sorter.Items[d]) with d = grid * 35 + cell.
    /// </summary>
    private void AppendPlayer(StringBuilder sb)
    {
        sb.AppendLine();
        sb.AppendLine("# Player inventory");
        sb.AppendLine($"AtRetainer={Windows.AtRetainer()}");
        foreach (var name in PlayerAddons)
        {
            var p = Plugin.GameGui.GetAddonByName(name);
            sb.AppendLine(p.IsNull ? $"{name}: not loaded" : $"{name}: visible={p.IsVisible} pos=({p.X},{p.Y}) scale={p.Scale} size=({p.Width}x{p.Height})");
        }

        var expansionUp = Windows.IsVisible(Windows.PlayerExpansion);
        var nodes = new List<SlotNode>();
        for (var g = 0; g < Windows.PlayerExpansionGrids.Length; g++)
            nodes.AddRange(ReadGrid(Windows.PlayerExpansionGrids[g], g, expansionUp));

        var bag = new Dictionary<(int, int), BagItem>();
        var im = InventoryManager.Instance();
        sb.AppendLine();
        sb.AppendLine("## Containers");
        for (var page = 0; page < 4; page++)
        {
            var type = Windows.PlayerPage(page);
            var c = im == null ? null : im->GetInventoryContainer(type);
            if (c == null) { sb.AppendLine($"{type}: container NULL"); continue; }
            var filled = 0;
            for (var s = 0; s < c->Size; s++)
            {
                var it = c->Items + s;
                var item = new BagItem(page, it->Slot, it->IsSymbolic ? 0 : it->ItemId, it->Quantity, it->Flags, it->IsSymbolic);
                bag[(page, s)] = item;
                if (item.ItemId != 0) filled++;
            }
            sb.AppendLine($"{type} ({(int)type}): Size={c->Size} IsLoaded={c->IsLoaded} filled={filled}");
        }

        var sorter = new List<SortEntry>();
        var iom = ItemOrderModule.Instance();
        var srt = iom == null ? null : iom->InventorySorter;
        sb.AppendLine();
        sb.AppendLine("## InventorySorter");
        if (srt == null) sb.AppendLine("NULL");
        else
        {
            var count = (int)srt->Items.LongCount;
            for (var i = 0; i < count; i++)
            {
                var e = srt->Items[i].Value;
                sorter.Add(e == null ? new SortEntry(i, ushort.MaxValue, ushort.MaxValue, ushort.MaxValue, 0)
                                     : new SortEntry(i, e->Page, e->Slot, e->Index, e->Flags));
            }
            sb.AppendLine($"InventoryType={srt->InventoryType} ItemsPerPage={srt->ItemsPerPage} Items.Count={count}");
        }

        (int, int)? H0(int d) => (d / 35, d % 35);
        (int, int)? H1(int d) => d < sorter.Count && sorter[d].Page != ushort.MaxValue ? (sorter[d].Page, sorter[d].Slot) : null;

        sb.AppendLine();
        sb.AppendLine("## Hypothesis scores (Expanded grids; only filled slots count)");
        foreach (var (hname, map) in new (string, Func<int, (int, int)?>)[] { ("H0 linear", H0), ("H1 InventorySorter.Items[d]", H1) })
        {
            sb.AppendLine(hname);
            foreach (var g in nodes.GroupBy(n => n.Grid).OrderBy(g => g.Key))
            {
                int filled = 0, icon = 0, iconQty = 0;
                foreach (var n in g)
                {
                    if (n.IconId == UnpopulatedIcon) continue;
                    var loc = map(n.DisplayIndex);
                    BagItem item = default;
                    var mapped = loc != null && bag.TryGetValue(loc.Value, out item);
                    var expected = mapped ? ExpectedIcon(item) : 0;
                    if (expected == 0 && n.IconId == 0) continue;
                    filled++;
                    if (!mapped || expected != n.IconId) continue;
                    icon++;
                    if (QtyMatches(n.QtyText, item.Quantity)) iconQty++;
                }
                sb.AppendLine($"  grid{g.Key}E: iconMatch={icon}/{filled} icon+qty={iconQty}/{filled}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Slot nodes");
        sb.AppendLine("addon nodeId cell display screenXY WxH visible iconId qtyText | H1 page/slot itemId name");
        foreach (var n in nodes.OrderBy(n => n.DisplayIndex))
        {
            var loc = H1(n.DisplayIndex);
            var text = "-";
            if (loc != null && bag.TryGetValue(loc.Value, out var item))
                text = $"{loc.Value.Item1}/{loc.Value.Item2} {item.ItemId}{(item.Hq ? " HQ" : "")} {ItemName(item.ItemId)}";
            sb.AppendLine($"{n.Addon} {n.NodeId} {n.Cell} {n.DisplayIndex} ({n.ScreenX},{n.ScreenY}) {n.Width}x{n.Height} {n.Visible} {n.IconId} '{n.QtyText}' | {text}");
        }
    }

    // ---- overlay (probe labels only, no prices) ----------------------------------------------

    /// <summary>
    /// Draws each visible slot's rect, its display index, and the H1 (page/slot). Green = H1 icon matches what the
    /// slot shows, red = mismatch, grey = both empty. This is also the first test of the Phase 4 rect math.
    /// </summary>
    public void DrawOverlay()
    {
        if (!OverlayEnabled) return;

        Snapshot s;
        try { s = Capture(); }
        catch (Exception ex) { Plugin.Log.Error(ex, "[Dots] overlay capture failed"); OverlayEnabled = false; return; }

        var dl = ImGui.GetForegroundDrawList();
        var green = ImGui.GetColorU32(new Vector4(0.2f, 1f, 0.2f, 1f));
        var red = ImGui.GetColorU32(new Vector4(1f, 0.25f, 0.25f, 1f));
        var grey = ImGui.GetColorU32(new Vector4(0.6f, 0.6f, 0.6f, 0.6f));
        var white = ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 1f));
        var origin = ImGui.GetMainViewport().Pos; // (0,0) unless multi-viewport moves it

        foreach (var n in s.Nodes)
        {
            if (!n.Visible || n.IconId == UnpopulatedIcon) continue;
            var scale = n.NodeScaleX * n.AddonScale;
            var min = origin + new Vector2(n.ScreenX, n.ScreenY);
            var max = min + new Vector2(n.Width * scale, n.Height * scale);

            var loc = Hypotheses[1].Map(n.DisplayIndex, s);
            uint col = red;
            var label = $"{n.DisplayIndex}";
            if (loc != null && s.Bag.TryGetValue(loc.Value, out var item))
            {
                var expected = ExpectedIcon(item);
                col = expected == 0 && n.IconId == 0 ? grey : expected == n.IconId ? green : red;
                label += $"\n{loc.Value.Page}/{loc.Value.Slot}";
            }

            dl.AddRect(min, max, col, 0f, ImDrawFlags.None, 2f);
            dl.AddText(min + new Vector2(2, 2), white, label);
        }
    }
}
