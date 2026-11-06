using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Component.GUI;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace DippinDots;

/// <summary>
/// Phase 4: a small dot in the bottom-left of each retainer bag slot, colored by the slot's market value.
///
/// Mapping (Phase 1, H1): display index d = grid * 35 + row-major cell; sorter.Items[d] -> (page, slot) in
/// RetainerPage1 + page. Live grids come from InventoryRetainerLarge.TabIndex.
///
/// Value (Decisions 1 and 3): 7-day median sale price for the item's quality (HQ uses HQ sales only, no NQ
/// fallback), times the stack quantity unless ValueMode is PerUnit. No sales of that quality = "no data".
/// The median is cross-checked against the mean of the last 20 sales; see Classify.
///
/// Safety (Phase 5 groundwork): before drawing a slot, the icon the game is showing must equal the mapped item's
/// icon (+1,000,000 if HQ). A mismatch draws nothing for that slot. If the layout shape the mapping relies on
/// changes (sorter size, page size, grid slot count), the overlay draws nothing and logs once.
/// </summary>
internal sealed unsafe class PriceOverlay
{
    private const int SlotsPerGrid = 35;
    private const uint HqIconOffset = 1_000_000;
    private const uint UnpopulatedIcon = uint.MaxValue;

    private readonly Configuration config;
    private readonly PriceService prices;
    private readonly Dictionary<uint, (uint Icon, bool Marketable)> itemInfo = new();
    private readonly HashSet<string> reportedShapeProblems = new();
    private readonly Dictionary<string, DateTime> shapeFirstSeen = new();
    private readonly HashSet<string> shapeSeenThisFrame = new();
    private static readonly TimeSpan ShapeGrace = TimeSpan.FromSeconds(2);
    // ---- logging state ------------------------------------------------------------------------
    // Logging policy (Darion, 2026-11-05: meaningful, but don't inflate the log). Nothing logs per frame.
    //   Information: one summary line per window visit (on close); mismatch recovery after a warning.
    //   Warning:     icon-check mismatches that persist past MismatchGrace (once per episode); shape problems
    //                (once per distinct problem per session).
    //   Error:       overlay exception (once; the overlay turns itself off).
    //   Debug:       surface activation, short visits, transient mismatches (normal while items move).

    private static readonly TimeSpan MismatchGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MinVisitForSummary = TimeSpan.FromSeconds(1);

    /// <summary>Counts for one surface in one frame.</summary>
    private sealed class SurfaceStats
    {
        public int Dots, Hollow, Loading, NoData, Mismatch;
        public int[] Bands = [];
        public string? MismatchSample;
    }

    /// <summary>Edge-triggered state per drawn surface, so logs fire on changes, not frames.</summary>
    private sealed class Surface(string label)
    {
        public readonly string Label = label;
        public bool Active;
        public DateTime ActiveSince;
        public string Context = "";
        public SurfaceStats Last = new();
        public DateTime? MismatchSince;
        public bool MismatchWarned;
        public bool HadMismatchWarning;
    }

    private readonly Surface retainerSurface = new("retainer bag");
    private readonly Surface playerSurface = new("player inventory");

    public PriceOverlay(Configuration config, PriceService prices)
    {
        this.config = config;
        this.prices = prices;
    }

    private readonly record struct Slot(int Grid, int Cell, Vector2 Min, float Size, uint ShownIcon);

    public void Draw()
    {
        if (!config.OverlayEnabled)
        {
            // Close out any open visit so its summary isn't held until the overlay is re-enabled.
            Track(retainerSurface, null, null);
            Track(playerSurface, null, null);
            return;
        }
        try { DrawInner(); }
        catch (Exception ex)
        {
            config.OverlayEnabled = false;
            Plugin.Log.Error(ex, "[Dots] overlay failed; disabled until '/dots on' or a plugin reload");
            Plugin.Chat.PrintError("[Dots] overlay hit an error and turned itself off (see /xllog). '/dots on' to retry.");
        }
    }

    private sealed class Frame
    {
        public required ImDrawListPtr Dl;
        public required Vector2 Origin;
        public required TimeSpan Ttl;
        public required List<(Vector2 Min, Vector2 Max)> Occluders;
    }

    private void DrawInner()
    {
        var iom = ItemOrderModule.Instance();
        var im = InventoryManager.Instance();
        if (iom == null || im == null) return;

        // Gate (Darion, 2026-11-05): only while the retainer sell list is open, unless SellOnly is turned off.
        var gate = config.SellOnly ? Windows.Selling() : Windows.AtRetainer();
        var retainerGrids = Probe.ActiveGrids();
        var drawRetainer = gate && retainerGrids is { Length: > 0 } && Windows.RetainerDataConsistent();
        // Expanded player layout only for now.
        var drawPlayer = gate && config.PlayerInventory && Windows.IsVisible(Windows.PlayerExpansion);

        SurfaceStats? retainerStats = null, playerStats = null;
        if (drawRetainer || drawPlayer)
        {
            var origin = ImGui.GetMainViewport().Pos;
            var f = new Frame
            {
                Dl = ImGui.GetForegroundDrawList(),
                Origin = origin,
                Ttl = TimeSpan.FromHours(config.RefreshHours),
                Occluders = Occluders(origin),
            };

            if (drawRetainer)
            {
                retainerStats = NewStats();
                var sorter = iom->GetActiveRetainerSorter();
                if (sorter != null && ShapeOk("retainer", sorter, RetainerPageSize, exactCount: RetainerSorterCount))
                    foreach (var grid in retainerGrids!)
                        if (!DrawGrid(f, retainerStats, $"RetainerGrid{grid}", grid, sorter, Windows.RetainerPage, im)) break;
            }

            if (drawPlayer)
            {
                // H1 mapping, verified 2026-11-05 (probe-20261105-195447: 43/43 icon+qty on grids 0E-2E):
                // d = grid * 35 + cell -> InventorySorter.Items[d] -> (page, slot) in Inventory1 + page.
                playerStats = NewStats();
                var sorter = iom->InventorySorter;
                if (sorter != null && ShapeOk("player", sorter, PlayerPageSize, minCount: PlayerSorterMinCount))
                    for (var grid = 0; grid < Windows.PlayerExpansionGrids.Length; grid++)
                        if (!DrawGrid(f, playerStats, Windows.PlayerExpansionGrids[grid], grid, sorter, Windows.PlayerPage, im)) break;
            }
        }

        Track(retainerSurface, retainerStats, drawRetainer ? RetainerName : null);
        Track(playerSurface, playerStats, null);

        // A shape problem that cleared up restarts its grace period if it comes back.
        if (shapeFirstSeen.Count > 0)
            foreach (var key in shapeFirstSeen.Keys.Where(k => !shapeSeenThisFrame.Contains(k)).ToList())
                shapeFirstSeen.Remove(key);
        shapeSeenThisFrame.Clear();
    }

    private SurfaceStats NewStats() => new() { Bands = new int[config.Thresholds.Length + 1] };

    private static string RetainerName()
    {
        var rm = RetainerManager.Instance();
        var r = rm == null ? null : rm->GetActiveRetainer();
        return r == null ? "" : r->NameString;
    }

    /// <summary>Edge-triggered logging for one surface. stats == null means the surface isn't drawn this frame.</summary>
    private void Track(Surface s, SurfaceStats? stats, Func<string>? context)
    {
        var now = DateTime.UtcNow;

        if (stats != null && !s.Active)
        {
            s.Active = true;
            s.ActiveSince = now;
            s.Context = context?.Invoke() ?? "";
            s.MismatchSince = null;
            s.MismatchWarned = s.HadMismatchWarning = false;
            Plugin.Log.Debug($"[Dots] {s.Label} overlay active{Ctx(s)}");
        }

        if (stats == null)
        {
            if (!s.Active) return;
            s.Active = false;
            var visit = now - s.ActiveSince;
            var line = $"[Dots] {s.Label}{Ctx(s)} closed after {Dur(visit)}; last view: {Summarize(s.Last)}" +
                       (s.HadMismatchWarning ? " (had icon-check warnings this visit)" : "");
            if (visit >= MinVisitForSummary || s.HadMismatchWarning) Plugin.Log.Information(line);
            else Plugin.Log.Debug(line);
            return;
        }

        s.Last = stats;
        if (stats.Mismatch > 0)
        {
            s.MismatchSince ??= now;
            if (!s.MismatchWarned && now - s.MismatchSince.Value >= MismatchGrace)
            {
                s.MismatchWarned = s.HadMismatchWarning = true;
                Plugin.Log.Warning($"[Dots] {s.Label}{Ctx(s)}: {stats.Mismatch} slot(s) failing the icon check for " +
                                   $"{MismatchGrace.TotalSeconds:0}s+ (no dots drawn there). First: {stats.MismatchSample}. " +
                                   "If this persists the mapping may have changed; run /dots probe.");
            }
        }
        else if (s.MismatchSince != null)
        {
            var dur = now - s.MismatchSince.Value;
            if (s.MismatchWarned) Plugin.Log.Information($"[Dots] {s.Label}{Ctx(s)}: icon check passing again after {Dur(dur)}");
            else Plugin.Log.Debug($"[Dots] {s.Label}{Ctx(s)}: transient icon mismatch for {dur.TotalMilliseconds:0} ms (items moving)");
            s.MismatchSince = null;
            s.MismatchWarned = false;
        }
    }

    private static string Ctx(Surface s) => s.Context.Length == 0 ? "" : $" ({s.Context})";

    private static string Dur(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds:00}s" : $"{t.TotalSeconds:0.0}s";

    private static string Summarize(SurfaceStats s) =>
        $"{s.Dots} dots [bands red->green {string.Join("/", s.Bands)}], {s.Hollow} hollow, " +
        $"{s.NoData} no data, {s.Loading} loading, {s.Mismatch} icon-check skips";

    private const int RetainerPageSize = 25;
    private const int RetainerSorterCount = 175;
    private const int PlayerPageSize = 35;      // assumption, checked by ShapeOk and /dots probe
    private const int PlayerSorterMinCount = 140; // 4 pages x 35

    private bool ShapeOk(string what, ItemOrderModuleSorter* sorter, int pageSize, int exactCount = -1, int minCount = -1)
    {
        var count = (int)sorter->Items.LongCount;
        if (sorter->ItemsPerPage == pageSize && (exactCount < 0 || count == exactCount) && (minCount < 0 || count >= minCount))
            return true;
        ShapeProblem($"{what} sorter shape: ItemsPerPage={sorter->ItemsPerPage} Items.Count={count} " +
                     $"(expected {pageSize} per page, count {(exactCount >= 0 ? $"={exactCount}" : $">={minCount}")})");
        return false;
    }

    /// <summary>Draws one grid's dots. Returns false if the layout looks wrong (overlay pauses for the frame).</summary>
    private bool DrawGrid(Frame f, SurfaceStats st, string addon, int grid, ItemOrderModuleSorter* sorter,
                          Func<int, InventoryType> pageType, InventoryManager* im)
    {
        var slots = ReadSlots(addon, grid);
        if (slots == null) return true;
        if (slots.Count != SlotsPerGrid)
        {
            ShapeProblem($"{addon} has {slots.Count} slots (expected {SlotsPerGrid})");
            return false;
        }

        var count = (int)sorter->Items.LongCount;
        foreach (var slot in slots)
        {
            if (slot.ShownIcon == UnpopulatedIcon || slot.ShownIcon == 0) continue;

            var d = grid * SlotsPerGrid + slot.Cell;
            if (d >= count) continue;
            var e = sorter->Items[d].Value;
            if (e == null) continue;
            var c = im->GetInventoryContainer(pageType(e->Page));
            if (c == null || !c->IsLoaded || e->Slot >= c->Size) continue;
            var it = c->Items + e->Slot;
            if (it->IsSymbolic || it->ItemId == 0) continue;

            var hq = (it->Flags & InventoryItem.ItemFlags.HighQuality) != 0;
            var info = Info(it->ItemId);
            var expected = info.Icon + (hq ? HqIconOffset : 0);
            if (expected != slot.ShownIcon)
            {
                st.Mismatch++;
                st.MismatchSample ??= $"{addon} cell {slot.Cell} -> item {it->ItemId}{(hq ? " HQ" : "")} expects icon {expected}, slot shows {slot.ShownIcon}";
                continue;
            }
            if (!info.Marketable) continue; // can't be sold on the board: no dot

            var state = Classify(it->ItemId, hq, it->Quantity, f.Ttl);
            switch (state.Kind)
            {
                case DotKind.Loading: st.Loading++; break;
                case DotKind.NoData: st.NoData++; break;
                default:
                    st.Dots++;
                    if (state.LowConfidence) st.Hollow++;
                    if (state.Band < st.Bands.Length) st.Bands[state.Band]++;
                    break;
            }

            var (center, r) = DotGeometry(f.Origin + slot.Min, slot.Size);
            if (IsOccluded(center, r + 1f, f.Occluders)) continue;
            DrawDot(f.Dl, center, r, slot.Size, state);
        }
        return true;
    }

    /// <summary>
    /// Records a layout problem for this frame. It's only reported after it has persisted for ShapeGrace,
    /// so a window that is still setting up doesn't read as a game patch. Drawing pauses either way.
    /// </summary>
    private void ShapeProblem(string problem)
    {
        shapeSeenThisFrame.Add(problem);
        var now = DateTime.UtcNow;
        if (!shapeFirstSeen.TryGetValue(problem, out var since)) shapeFirstSeen[problem] = since = now;
        if (now - since < ShapeGrace) return;
        if (!reportedShapeProblems.Add(problem)) return; // each distinct problem reported once per session
        Plugin.Log.Warning($"[Dots] overlay not drawing: {problem}. Likely a game patch; run /dots probe.");
        Plugin.Chat.PrintError($"[Dots] overlay paused: {problem}");
    }

    /// <summary>Slot nodes of one grid in row-major order, or null if the addon isn't up.</summary>
    private static List<Slot>? ReadSlots(string addon, int grid)
    {
        var ptr = Plugin.GameGui.GetAddonByName(addon);
        if (ptr.IsNull || !ptr.IsVisible) return null;
        var unit = (AtkUnitBase*)ptr.Address;
        var uld = unit->UldManager;
        // Not built yet: on the frame InventoryRetainerLarge finishes setup, the RetainerGrid addons already report
        // visible but haven't loaded their nodes (seen 5x on 2026-11-05 as "RetainerGrid0 has 0 slots"). Treat
        // that as "not up yet" and try again next frame.
        if (!unit->IsReady || uld.LoadedState != AtkLoadState.Loaded || uld.NodeListCount == 0) return null;
        var raw = new List<(float Y, float X, Slot S)>(SlotsPerGrid);

        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var node = uld.NodeList[i];
            if (node == null || (ushort)node->Type < 1000) continue;
            var comp = ((AtkComponentNode*)node)->Component;
            if (comp == null || comp->UldManager.Objects == null) continue;
            if (((AtkUldComponentInfo*)comp->UldManager.Objects)->ComponentType != ComponentType.DragDrop) continue;

            var icon = ((AtkComponentDragDrop*)comp)->AtkComponentIcon;
            var shown = icon == null ? 0u : icon->IconId;
            var size = node->Width * node->ScaleX * unit->Scale;
            var visible = node->IsVisible();
            raw.Add((MathF.Round(node->Y), node->X,
                new Slot(grid, -1, new Vector2(node->ScreenX, node->ScreenY), size, visible ? shown : 0)));
        }

        var ordered = raw.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
        var result = new List<Slot>(ordered.Count);
        for (var cell = 0; cell < ordered.Count; cell++)
            result.Add(ordered[cell].S with { Cell = cell });
        return result;
    }

    private (uint Icon, bool Marketable) Info(uint itemId)
    {
        if (itemInfo.TryGetValue(itemId, out var v)) return v;
        var row = Plugin.Data.GetExcelSheet<LuminaItem>().GetRowOrDefault(itemId);
        v = row == null ? (UnpopulatedIcon, false) : (row.Value.Icon, row.Value.ItemSearchCategory.RowId != 0);
        itemInfo[itemId] = v;
        return v;
    }

    private enum DotKind { Loading, NoData, Band }

    /// <summary>LowConfidence = draw the band color as a hollow ring instead of a filled dot.</summary>
    private readonly record struct DotState(DotKind Kind, int Band, bool LowConfidence = false);

    private DotState Classify(uint itemId, bool hq, int quantity, TimeSpan ttl)
    {
        if (!prices.Cache.TryGet(itemId, out var p)) return new(DotKind.Loading, 0);
        if (p.NoData) return new(DotKind.NoData, 0);
        // Decision 1 edge case: HQ with no HQ sales is "no data", never the NQ price.
        var s = hq ? p.Hq : p.Nq;
        if (s.Median <= 0) return new(DotKind.NoData, 0);

        // Cross-check (Darion, 2026-11-05): the median we color by vs. the plain mean of the last 20 sales.
        // Too few recent sales, or the two disagreeing by more than ConfidenceRatio, marks the color as shaky.
        var ratio = s.CheckAvg > 0 ? Math.Max(s.Median, s.CheckAvg) / Math.Min(s.Median, s.CheckAvg) : double.PositiveInfinity;
        var low = s.Recent < 3 || ratio > config.ConfidenceRatio;

        var value = config.ValueMode == ValueMode.PerUnit ? s.Median : s.Median * Math.Max(1, quantity);
        return new(DotKind.Band, BandFor(value), low);
    }

    /// <summary>Band index 0..N where N = number of thresholds. Value below Thresholds[0] = band 0 (red).</summary>
    private int BandFor(double value)
    {
        var band = 0;
        foreach (var t in config.Thresholds)
            if (value >= t) band++;
        return band;
    }

    private static readonly Vector3 Red = new(0.92f, 0.22f, 0.22f);
    private static readonly Vector3 Yellow = new(0.96f, 0.84f, 0.20f);
    private static readonly Vector3 Green = new(0.25f, 0.85f, 0.32f);

    /// <summary>Bands spread evenly red -> yellow -> green, so 3 bands are exactly red/yellow/green and more
    /// thresholds give the stepped gradient Darion offered.</summary>
    private uint BandColor(int band)
    {
        var n = config.Thresholds.Length;
        var t = n == 0 ? 1f : band / (float)n;
        var rgb = t < 0.5f ? Vector3.Lerp(Red, Yellow, t * 2) : Vector3.Lerp(Yellow, Green, (t - 0.5f) * 2);
        return ImGui.GetColorU32(new Vector4(rgb, 1f));
    }

    /// <summary>
    /// The foreground draw list sits above all game UI, so dots would show through tooltips and menus that open
    /// over the grid (seen 2026-11-05: ItemDetail over grid 0). Any dot touching one of these is skipped.
    /// </summary>
    private static readonly string[] OccluderAddons =
    [
        "ItemDetail", "Tooltip", "ActionDetail", "ContextMenu", "ContextIconMenu", "AddonContextSub", "AddonContextMenuTitle",
    ];

    private static List<(Vector2 Min, Vector2 Max)> Occluders(Vector2 origin)
    {
        var result = new List<(Vector2, Vector2)>();
        foreach (var name in OccluderAddons)
        {
            var p = Plugin.GameGui.GetAddonByName(name);
            if (p.IsNull || !p.IsVisible) continue;
            var unit = (AtkUnitBase*)p.Address;
            var root = unit->RootNode;
            if (root == null) continue;
            var min = origin + new Vector2(root->ScreenX, root->ScreenY);
            var size = new Vector2(root->Width * root->ScaleX, root->Height * root->ScaleY) * unit->Scale;
            if (size.X <= 0 || size.Y <= 0) continue;
            result.Add((min, min + size));
        }
        return result;
    }

    private static bool IsOccluded(Vector2 center, float r, List<(Vector2 Min, Vector2 Max)> occluders)
    {
        foreach (var (min, max) in occluders)
            if (center.X + r > min.X && center.X - r < max.X && center.Y + r > min.Y && center.Y - r < max.Y)
                return true;
        return false;
    }

    private (Vector2 Center, float Radius) DotGeometry(Vector2 slotMin, float slotSize)
    {
        var k = slotSize / 44f; // geometry tuned on the observed 44 px slot
        var r = config.DotRadius * k;
        return (slotMin + new Vector2(config.DotInset * k + r, slotSize - config.DotInset * k - r), r);
    }

    private void DrawDot(ImDrawListPtr dl, Vector2 center, float r, float slotSize, DotState state)
    {
        var k = slotSize / 44f;
        var outline = ImGui.GetColorU32(new Vector4(0f, 0f, 0f, 0.85f));

        switch (state.Kind)
        {
            case DotKind.Loading: // hollow grey ring
                dl.AddCircle(center, r, ImGui.GetColorU32(new Vector4(0.75f, 0.75f, 0.75f, 0.9f)), 12, 1.5f * k);
                break;
            case DotKind.NoData: // dim grey dot
                dl.AddCircleFilled(center, r + 1f * k, outline, 12);
                dl.AddCircleFilled(center, r, ImGui.GetColorU32(new Vector4(0.45f, 0.45f, 0.45f, 0.9f)), 12);
                break;
            case DotKind.Band when state.LowConfidence: // band-colored hollow ring with dark edges
                dl.AddCircle(center, r, outline, 12, 3.5f * k);
                dl.AddCircle(center, r, BandColor(state.Band), 12, 2f * k);
                break;
            default:
                dl.AddCircleFilled(center, r + 1f * k, outline, 12);
                dl.AddCircleFilled(center, r, BandColor(state.Band), 12);
                break;
        }
    }
}
