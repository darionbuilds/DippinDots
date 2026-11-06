using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using LuminaItem = Lumina.Excel.Sheets.Item;

namespace DippinDots;

/// <summary>
/// Keeps the price cache fresh for whatever retainer bag is open. Policy (Darion, 2026-11-05): normal usage only,
/// each item refreshed at most once per RefreshHours (default 2 h), persisted across restarts.
///
/// Every ~1 s on the framework thread, while a retainer window is open, it collects the open retainer's (and the
/// player's) marketable item ids that are missing/stale and not in flight, and fires async requests of <= 100 ids.
/// Any failure pauses all requests for 15 minutes.
/// </summary>
public sealed class PriceService : IDisposable
{
    private const string BaseUrl = "https://universalis.app/api/v2/";
    private const int ChunkSize = 100; // tested working 2026-11-05; larger not tried
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(15);

    private static readonly string[] FieldNames =
    [
        "minPriceNQ", "minPriceHQ", "currentAveragePriceNQ", "currentAveragePriceHQ",
        "averagePriceNQ", "averagePriceHQ", "nqSaleVelocity", "hqSaleVelocity", "lastUploadTime",
        "recentHistory.pricePerUnit", "recentHistory.hq", "recentHistory.timestamp",
    ];

    private const int HistoryEntries = 20;
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(7);
    private const int MinRecentSales = 3;
    private const int FallbackSales = 5;

    private readonly Configuration config;
    private readonly HttpClient http;
    private readonly CancellationTokenSource cts = new();
    private readonly ConcurrentDictionary<uint, byte> inFlight = new();
    private DateTime nextPoll = DateTime.MinValue;
    private DateTime backoffUntil = DateTime.MinValue;

    public PriceCache Cache { get; }

    public PriceService(Configuration config)
    {
        this.config = config;
        Cache = new PriceCache(Plugin.PluginInterface.GetPluginConfigDirectory());
        http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"DippinDots/{typeof(PriceService).Assembly.GetName().Version} (private Dalamud plugin)");
        Plugin.Framework.Update += OnUpdate;
    }

    private TimeSpan Ttl => TimeSpan.FromHours(config.RefreshHours);

    /// <summary>Resolved world for requests, or null if not known yet (not logged in).</summary>
    private static string? ResolveWorld(Configuration config)
    {
        if (!string.IsNullOrWhiteSpace(config.WorldOverride)) return config.WorldOverride.Trim();
        if (!Plugin.PlayerState.IsLoaded) return null;
        var name = Plugin.PlayerState.HomeWorld.ValueNullable?.Name.ExtractText();
        return string.IsNullOrEmpty(name) ? null : name;
    }

    public string Status()
    {
        var backoff = backoffUntil > DateTime.UtcNow ? $" backoff until {backoffUntil.ToLocalTime():HH:mm}" : "";
        return $"world={Cache.World} entries={Cache.Count} stale={Cache.CountStale(Ttl)} inFlight={inFlight.Count}{backoff}";
    }

    private void OnUpdate(IFramework framework)
    {
        var now = DateTime.UtcNow;
        if (now < nextPoll) return;
        nextPoll = now + PollInterval;

        try
        {
            var world = ResolveWorld(config);
            if (world == null) return;
            if (world != Cache.World) Cache.Load(world); // also covers first poll after login

            if (now < backoffUntil) return;
            if (backoffUntil != DateTime.MinValue)
            {
                backoffUntil = DateTime.MinValue;
                Plugin.Log.Information("[Dots] request pause over; price requests resumed");
            }

            if (!Windows.AtRetainer()) return;

            var wanted = CollectStaleIds();
            if (wanted.Count == 0) return;

            foreach (var id in wanted) inFlight[id] = 0;
            foreach (var chunk in wanted.Chunk(ChunkSize))
                _ = Task.Run(() => FetchChunk(world, chunk, cts.Token));
        }
        catch (Exception ex)
        {
            // Bounded: the backoff stops polling for 15 min, so this can log at most ~4x/hour.
            Plugin.Log.Error(ex, $"[Dots] price poll failed; pausing requests for {FailureBackoff.TotalMinutes} min");
            backoffUntil = DateTime.UtcNow + FailureBackoff;
        }
    }

    /// <summary>Framework thread only: reads the open retainer's bag and, if enabled, the player's bag.</summary>
    private unsafe List<uint> CollectStaleIds()
    {
        var result = new List<uint>();
        var im = InventoryManager.Instance();
        if (im == null) return result;

        var pages = new List<InventoryType>();
        // Only trust the retainer containers once the sorter and the retainer manager agree on who is open.
        if (Windows.RetainerDataConsistent())
            for (var p = 0; p < 7; p++) pages.Add(Windows.RetainerPage(p));
        if (config.PlayerInventory)
            for (var p = 0; p < 4; p++) pages.Add(Windows.PlayerPage(p));

        var sheet = Plugin.Data.GetExcelSheet<LuminaItem>();
        var seen = new HashSet<uint>();

        foreach (var type in pages)
        {
            var c = im->GetInventoryContainer(type);
            if (c == null || !c->IsLoaded) continue;
            for (var s = 0; s < c->Size; s++)
            {
                var it = c->Items + s;
                if (it->IsSymbolic || it->ItemId == 0) continue;
                var id = it->ItemId;
                if (!seen.Add(id)) continue;
                if (inFlight.ContainsKey(id) || Cache.IsFresh(id, Ttl)) continue;
                var row = sheet.GetRowOrDefault(id);
                if (row == null || row.Value.ItemSearchCategory.RowId == 0) continue; // not marketable
                result.Add(id);
            }
        }

        return result;
    }

    private async Task FetchChunk(string world, uint[] ids, CancellationToken token)
    {
        var sw = Stopwatch.StartNew();
        var single = ids.Length == 1;
        // Single-id responses are the bare item object, so the field filter must be unprefixed (Phase 2 results).
        var fields = single
            ? string.Join(',', FieldNames)
            : string.Join(',', FieldNames.Select(f => "items." + f).Append("unresolvedItems"));
        // listings=0 is safe (min/avg listing prices still computed). Do NOT send entries=0: it zeroes
        // averagePrice*/SaleVelocity (verified 2026-11-05). entries=20 plus the recentHistory.* field filter
        // returns only price/hq/timestamp per sale (verified 2026-11-05, ~1 KB per item).
        var url = $"{Uri.EscapeDataString(world)}/{string.Join(',', ids)}?listings=0&entries={HistoryEntries}&fields={fields}";

        try
        {
            using var resp = await http.GetAsync(url, token).ConfigureAwait(false);
            var fetchedAt = DateTime.UtcNow;

            if (single && resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                Cache.Set(NoData(ids[0], fetchedAt));
                SaveCache();
                Plugin.Log.Information($"[Dots] request {world} 1 id -> 404 (no data) {sw.ElapsedMilliseconds} ms");
                return;
            }

            if (!resp.IsSuccessStatusCode)
            {
                backoffUntil = DateTime.UtcNow + FailureBackoff;
                Plugin.Log.Warning($"[Dots] request {world} {ids.Length} ids -> HTTP {(int)resp.StatusCode}; pausing requests for {FailureBackoff.TotalMinutes} min");
                return;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            var root = doc.RootElement;
            int resolved = 0, unresolved = 0;

            if (single)
            {
                Cache.Set(Parse(ids[0], root, fetchedAt));
                resolved = 1;
            }
            else
            {
                var returned = new HashSet<uint>();
                if (root.TryGetProperty("items", out var items))
                {
                    foreach (var p in items.EnumerateObject())
                    {
                        if (!uint.TryParse(p.Name, out var id)) continue;
                        Cache.Set(Parse(id, p.Value, fetchedAt));
                        returned.Add(id);
                        resolved++;
                    }
                }

                // Anything we asked for and didn't get back (unresolvedItems or simply absent) is cached as no data
                // so it isn't re-requested every poll.
                foreach (var id in ids.Where(i => !returned.Contains(i)))
                {
                    Cache.Set(NoData(id, fetchedAt));
                    unresolved++;
                }
            }

            // Debug only: one line per thin-data item, bounded by the 2 h TTL (each item is fetched rarely).
            var thin = 0;
            foreach (var id in ids)
            {
                if (!Cache.TryGet(id, out var p) || p.NoData) continue;
                if (p.Nq.Total + p.Hq.Total == 0 || (p.Nq.Total > 0 && p.Nq.Recent < 3) || (p.Hq.Total > 0 && p.Hq.Recent < 3))
                {
                    thin++;
                    Plugin.Log.Debug($"[Dots] thin sales for item {id}: NQ median {p.Nq.Median:0} avg {p.Nq.CheckAvg:0} " +
                                     $"recent {p.Nq.Recent}/{p.Nq.Total}; HQ median {p.Hq.Median:0} avg {p.Hq.CheckAvg:0} recent {p.Hq.Recent}/{p.Hq.Total}");
                }
            }

            SaveCache();
            Plugin.Log.Information($"[Dots] request {world} {ids.Length} ids -> resolved {resolved}, no data {unresolved}, " +
                                   $"thin sales {thin}, {sw.ElapsedMilliseconds} ms");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // plugin unloading
        }
        catch (Exception ex)
        {
            backoffUntil = DateTime.UtcNow + FailureBackoff;
            Plugin.Log.Warning(ex, $"[Dots] request {world} {ids.Length} ids failed; pausing requests for {FailureBackoff.TotalMinutes} min");
        }
        finally
        {
            foreach (var id in ids) inFlight.TryRemove(id, out _);
        }
    }

    private DateTime lastSaveWarning = DateTime.MinValue;

    /// <summary>A failed save only costs a refetch after restart; log it (at most every 10 min) but don't back off.</summary>
    private void SaveCache()
    {
        try { Cache.Save(); }
        catch (Exception ex)
        {
            if (DateTime.UtcNow - lastSaveWarning < TimeSpan.FromMinutes(10)) return;
            lastSaveWarning = DateTime.UtcNow;
            Plugin.Log.Warning(ex, "[Dots] could not save the price cache; prices stay in memory and refetch after a restart");
        }
    }

    private static PriceEntry NoData(uint id, DateTime fetchedAt) =>
        new(id, 0, 0, 0, 0, 0, 0, 0, 0, 0, fetchedAt, NoData: true);

    private static PriceEntry Parse(uint id, JsonElement e, DateTime fetchedAt)
    {
        long L(string n) => e.TryGetProperty(n, out var v) && v.TryGetInt64(out var x) ? x : (long)D(n);
        double D(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        var sales = new List<(double Price, bool Hq, long Ts)>();
        if (e.TryGetProperty("recentHistory", out var hist) && hist.ValueKind == JsonValueKind.Array)
        {
            foreach (var h in hist.EnumerateArray())
            {
                if (!h.TryGetProperty("pricePerUnit", out var pp) || pp.ValueKind != JsonValueKind.Number) continue;
                var hq = h.TryGetProperty("hq", out var hv) && hv.ValueKind == JsonValueKind.True;
                var ts = h.TryGetProperty("timestamp", out var tv) && tv.TryGetInt64(out var t) ? t : 0;
                sales.Add((pp.GetDouble(), hq, ts));
            }
        }

        var cutoff = new DateTimeOffset(fetchedAt).ToUnixTimeSeconds() - (long)RecentWindow.TotalSeconds;
        return new PriceEntry(id,
            L("minPriceNQ"), L("minPriceHQ"),
            D("currentAveragePriceNQ"), D("currentAveragePriceHQ"),
            D("averagePriceNQ"), D("averagePriceHQ"),
            D("nqSaleVelocity"), D("hqSaleVelocity"),
            L("lastUploadTime"), fetchedAt, NoData: false,
            Nq: Stats(sales, hq: false, cutoff), Hq: Stats(sales, hq: true, cutoff));
    }

    /// <summary>Per-quality median (7-day window, else last 5 sales) and the check mean over all 20.</summary>
    internal static SaleStats Stats(List<(double Price, bool Hq, long Ts)> sales, bool hq, long cutoffUnix)
    {
        var mine = sales.Where(s => s.Hq == hq && s.Price > 0).OrderByDescending(s => s.Ts).ToList();
        if (mine.Count == 0) return default;
        var recent = mine.Where(s => s.Ts >= cutoffUnix).ToList();
        var basis = recent.Count >= MinRecentSales ? recent : mine.Take(FallbackSales).ToList();
        return new SaleStats(Median(basis.Select(s => s.Price)), mine.Average(s => s.Price), recent.Count, mine.Count);
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.Order().ToArray();
        if (v.Length == 0) return 0;
        var mid = v.Length / 2;
        return v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        cts.Cancel();
        http.Dispose();
        cts.Dispose();
    }
}
