using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DippinDots;

/// <summary>
/// Price stats for one quality, computed from the last 20 sales (both qualities mixed) at fetch time.
/// Median = median price of this quality's sales in the last 7 days, or of its last 5 sales if fewer than 3
/// fall in the window. CheckAvg = plain mean of all of this quality's sales in the 20. Recent = sales in the
/// 7-day window. Zero median = no sales of this quality.
/// </summary>
public readonly record struct SaleStats(double Median, double CheckAvg, int Recent, int Total);

/// <summary>
/// One entry per item (NQ and HQ together). Zero prices mean "none" (Universalis convention).
/// NoData = Universalis listed the id in unresolvedItems.
/// </summary>
public sealed record PriceEntry(
    uint ItemId,
    long MinPriceNq, long MinPriceHq,
    double ListingAvgNq, double ListingAvgHq,
    double SaleAvgNq, double SaleAvgHq,
    double SaleVelocityNq, double SaleVelocityHq,
    long LastUploadMs,
    DateTime FetchedAtUtc,
    bool NoData,
    SaleStats Nq = default,
    SaleStats Hq = default);

/// <summary>
/// Thread-safe in-memory cache persisted per world as JSON. Reads (Draw / framework thread) never block on I/O;
/// saves happen from request completion only, written to a temp file then moved over the real one.
/// </summary>
public sealed class PriceCache
{
    private readonly ConcurrentDictionary<uint, PriceEntry> entries = new();
    private readonly object saveLock = new();
    private readonly string directory;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    public string World { get; private set; } = "";

    public PriceCache(string directory) => this.directory = directory;

    public int Count => entries.Count;

    public bool TryGet(uint itemId, out PriceEntry entry) => entries.TryGetValue(itemId, out entry!);

    public bool IsFresh(uint itemId, TimeSpan ttl) =>
        entries.TryGetValue(itemId, out var e) && DateTime.UtcNow - e.FetchedAtUtc < ttl;

    public int CountStale(TimeSpan ttl) => entries.Values.Count(e => DateTime.UtcNow - e.FetchedAtUtc >= ttl);

    public void Set(PriceEntry entry) => entries[entry.ItemId] = entry;

    private string PathFor(string world)
    {
        var safe = string.Concat(world.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        // v1 files were fetched with entries=0 (zeroed sale stats). v2 files lack the per-sale median/check stats.
        // v3 adds them; older files are ignored so everything refetches once.
        return Path.Combine(directory, $"prices-v3-{safe}.json");
    }

    /// <summary>Switches the cache to a world, loading its file. Never mixes worlds.</summary>
    public void Load(string world)
    {
        entries.Clear();
        World = world;
        var path = PathFor(world);
        if (!File.Exists(path))
        {
            Plugin.Log.Information($"[Dots] cache: no file for {world}, starting empty");
            return;
        }

        try
        {
            var list = JsonSerializer.Deserialize<List<PriceEntry>>(File.ReadAllText(path), JsonOptions) ?? [];
            foreach (var e in list) entries[e.ItemId] = e;
            Plugin.Log.Information($"[Dots] cache: loaded {entries.Count} entries for {world} from {path}");
        }
        catch (Exception ex)
        {
            // A corrupt cache only costs a refetch; don't fail the plugin over it.
            Plugin.Log.Warning(ex, $"[Dots] cache: could not read {path}, starting empty");
        }
    }

    public void Save()
    {
        if (World.Length == 0) return;
        lock (saveLock)
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(World);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries.Values.ToList(), JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
    }
}
