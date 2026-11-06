using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace DippinDots;

/// <summary>
/// Price dots on retainer bag and Expanded inventory slots while selling. One command, '/dots', with options
/// (Darion, 2026-11-05: options on one command rather than separate commands).
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ICommandManager Commands { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IDataManager Data { get; private set; } = null!;
    [PluginService] internal static IChatGui Chat { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;

    private const string Command = "/dots";

    private const string Usage =
        "'/dots' toggles the dots. Options: config (settings window), on, off, stack, unit (value mode), " +
        "inv (your inventory on/off), sell (only while selling on/off), bands 250 500 1000 (ascending gil thresholds), " +
        "ratio 2 (hollow-ring cutoff), status, probe (diagnostic dump), probe overlay, probe prices.";

    // Debug-logged on setup, to help diagnose layout changes after a patch.
    internal static readonly string[] WatchedAddons =
    [
        "InventoryRetainer", "InventoryRetainerLarge",
        "RetainerGrid", "RetainerGrid0", "RetainerGrid1", "RetainerGrid2", "RetainerGrid3",
        "RetainerGrid4", "RetainerGrid5", "RetainerGrid6", "RetainerCrystalGrid",
    ];

    private readonly Probe probe;
    private readonly Configuration config;
    private readonly PriceService prices;
    private readonly PriceOverlay overlay;
    private readonly WindowSystem windows = new("DippinDots");
    private readonly ConfigWindow configWindow;

    public Plugin()
    {
        probe = new Probe();
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (config.Migrate()) config.Save();
        prices = new PriceService(config);

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, WatchedAddons, OnAddonSetup);

        Commands.AddHandler(Command, new CommandInfo(OnCommand) { HelpMessage = "Price dots. " + Usage });

        configWindow = new ConfigWindow(config, prices);
        windows.AddWindow(configWindow);

        overlay = new PriceOverlay(config, prices);
        PluginInterface.UiBuilder.Draw += probe.DrawOverlay;
        PluginInterface.UiBuilder.Draw += overlay.Draw;
        PluginInterface.UiBuilder.Draw += windows.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configWindow.Toggle;
        PluginInterface.UiBuilder.OpenMainUi += configWindow.Toggle;

        Log.Information($"[Dots] loaded: {SettingsSummaryFor(config)}");
    }

    internal static string SettingsSummaryFor(Configuration c) =>
        $"overlay {(c.OverlayEnabled ? "on" : "off")}, player inventory {(c.PlayerInventory ? "on" : "off")}, " +
        $"{(c.SellOnly ? "only while selling" : "any retainer window")}, " +
        $"value={c.ValueMode}, bands={string.Join(" / ", c.Thresholds.Select(t => t.ToString("N0", CultureInfo.InvariantCulture)))}, " +
        $"hollow above {c.ConfidenceRatio:0.##}x, refresh {c.RefreshHours:0.##} h" +
        (string.IsNullOrWhiteSpace(c.WorldOverride) ? "" : $", world override {c.WorldOverride}");

    /// <summary>Parses 1-7 positive gil thresholds; returns them sorted and de-duplicated.</summary>
    internal static bool TryParseBands(IEnumerable<string> parts, out double[] values, out string error)
    {
        var list = new List<double>();
        foreach (var p in parts)
        {
            if (!double.TryParse(p.Replace(",", "").Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0)
            {
                values = [];
                error = $"not a positive number: {p}";
                return false;
            }
            list.Add(v);
        }
        if (list.Count is < 1 or > 7)
        {
            values = [];
            error = "give 1-7 thresholds, e.g. 1000 10000";
            return false;
        }
        values = list.Distinct().Order().ToArray();
        error = "";
        return true;
    }

    private void OnCommand(string command, string args)
    {
        var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var verb = parts.Length == 0 ? "" : parts[0].ToLowerInvariant();
        switch (verb)
        {
            case "config":
            case "settings":
                configWindow.Toggle();
                return;
            case "probe":
                OnProbe(parts.Length > 1 ? parts[1].ToLowerInvariant() : "");
                return;
            case "help":
            case "?":
                Chat.Print($"[Dots] {Usage}");
                return;
            case "": config.OverlayEnabled = !config.OverlayEnabled; break;
            case "on": config.OverlayEnabled = true; break;
            case "off": config.OverlayEnabled = false; break;
            case "stack": config.ValueMode = ValueMode.Stack; break;
            case "unit": config.ValueMode = ValueMode.PerUnit; break;
            case "inv": config.PlayerInventory = !config.PlayerInventory; break;
            case "sell": config.SellOnly = !config.SellOnly; break;
            case "bands":
                if (!TryParseBands(parts.Skip(1), out var values, out var err))
                {
                    Chat.PrintError($"[Dots] {err} (e.g. '/dots bands 1000 10000')");
                    return;
                }
                config.Thresholds = values;
                break;
            case "ratio":
                if (parts.Length != 2 || !double.TryParse(parts[1].TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) || ratio < 1)
                {
                    Chat.PrintError("[Dots] give a ratio of 1 or more, e.g. '/dots ratio 1.5'");
                    return;
                }
                config.ConfidenceRatio = ratio;
                break;
            case "status": break;
            default:
                Chat.PrintError($"[Dots] unknown option '{verb}'. {Usage}");
                return;
        }

        config.Save();
        if (verb != "status") Log.Information($"[Dots] settings changed via /dots {args.Trim()}: {SettingsSummaryFor(config)}");
        Chat.Print($"[Dots] {SettingsSummaryFor(config)}; prices: {prices.Status()}");
    }

    private static void OnAddonSetup(AddonEvent type, AddonArgs args)
    {
        // ~11 lines per retainer open, so Debug only.
        Log.Debug($"[Dots] {args.AddonName} PostSetup");
    }

    private void OnProbe(string option)
    {
        try
        {
            switch (option)
            {
                case "overlay":
                    probe.OverlayEnabled = !probe.OverlayEnabled;
                    Chat.Print($"[Dots] probe overlay {(probe.OverlayEnabled ? "on" : "off")}");
                    return;
                case "prices":
                    Chat.Print($"[Dots] prices: {prices.Status()}");
                    return;
                case "":
                    Chat.Print($"[Dots] probe written: {probe.DumpToFile()}");
                    return;
                default:
                    Chat.PrintError($"[Dots] unknown probe option '{option}'. Use '/dots probe', '/dots probe overlay' or '/dots probe prices'.");
                    return;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Dots] probe failed");
            Chat.PrintError($"[Dots] probe failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.OpenMainUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.OpenConfigUi -= configWindow.Toggle;
        PluginInterface.UiBuilder.Draw -= windows.Draw;
        windows.RemoveAllWindows();
        prices.Dispose();
        PluginInterface.UiBuilder.Draw -= overlay.Draw;
        PluginInterface.UiBuilder.Draw -= probe.DrawOverlay;
        Commands.RemoveHandler(Command);
        AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, WatchedAddons, OnAddonSetup);
    }
}
