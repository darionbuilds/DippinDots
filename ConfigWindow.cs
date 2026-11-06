using System;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DippinDots;

/// <summary>Settings window: opened by '/dots config' and the plugin installer's buttons (main and config UI).</summary>
public sealed class ConfigWindow : Window
{
    private readonly Configuration config;
    private readonly PriceService prices;
    private string bandsText;
    private string? bandsError;

    public ConfigWindow(Configuration config, PriceService prices)
        : base("Dippin' Dots###DippinDotsConfig", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.config = config;
        this.prices = prices;
        bandsText = FormatBands(config.Thresholds);
    }

    public override void OnOpen() => bandsText = FormatBands(config.Thresholds);

    public override void Draw()
    {
        var changed = false;

        var on = config.OverlayEnabled;
        if (ImGui.Checkbox("Show price dots", ref on)) { config.OverlayEnabled = on; changed = true; }

        var sellOnly = config.SellOnly;
        if (ImGui.Checkbox("Only while the retainer sell list is open", ref sellOnly)) { config.SellOnly = sellOnly; changed = true; }

        var inv = config.PlayerInventory;
        if (ImGui.Checkbox("Also dot my inventory (Expanded layout)", ref inv)) { config.PlayerInventory = inv; changed = true; }

        ImGui.TextUnformatted("Value:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Stack (price x quantity)", config.ValueMode == ValueMode.Stack)) { config.ValueMode = ValueMode.Stack; changed = true; }
        ImGui.SameLine();
        if (ImGui.RadioButton("Per unit", config.ValueMode == ValueMode.PerUnit)) { config.ValueMode = ValueMode.PerUnit; changed = true; }

        ImGui.Separator();
        ImGui.TextUnformatted("Color bands (gil, ascending, 1-7 values; red below the first, green above the last):");
        ImGui.SetNextItemWidth(320);
        ImGui.InputText("##bands", ref bandsText, 128);
        ImGui.SameLine();
        if (ImGui.Button("Apply"))
        {
            if (Plugin.TryParseBands(bandsText.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries), out var values, out var err))
            {
                config.Thresholds = values;
                bandsText = FormatBands(values);
                bandsError = null;
                changed = true;
            }
            else bandsError = err;
        }
        ImGui.SameLine();
        if (ImGui.Button("Default"))
        {
            config.Thresholds = Configuration.DefaultThresholds;
            bandsText = FormatBands(config.Thresholds);
            bandsError = null;
            changed = true;
        }
        if (bandsError != null) ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), bandsError);

        var ratio = (float)config.ConfidenceRatio;
        ImGui.SetNextItemWidth(200);
        if (ImGui.SliderFloat("Hollow ring above (median vs. mean)", ref ratio, 1.1f, 5f, "%.1fx"))
        {
            config.ConfidenceRatio = Math.Round(ratio, 1);
            changed = true;
        }

        ImGui.Separator();
        ImGui.TextDisabled($"Prices: {prices.Status()}");
        ImGui.TextDisabled("Refreshed at most every 2 h per item. Hollow ring = thin or inconsistent sales.");

        if (changed)
        {
            config.Save();
            Plugin.Log.Information($"[Dots] settings changed in window: {Plugin.SettingsSummaryFor(config)}");
        }
    }

    private static string FormatBands(double[] values) =>
        string.Join(" ", values.Select(v => v.ToString("0", CultureInfo.InvariantCulture)));
}
