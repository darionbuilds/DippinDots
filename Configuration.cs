using Dalamud.Configuration;

namespace DippinDots;

public enum ValueMode
{
    /// <summary>Unit price x quantity in the slot (Darion, 2026-11-05: "we'll go off of the stack worth").</summary>
    Stack,
    /// <summary>Unit price only.</summary>
    PerUnit,
}

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    /// <summary>
    /// World (or data center) name sent to Universalis. Empty = the character's home world, which is where
    /// retainer listings live. Decision 2 in the plan doc is still open; this is the interim override.
    /// </summary>
    public string WorldOverride { get; set; } = "";

    /// <summary>Per-item refresh interval. Darion, 2026-11-05: 2 hours.</summary>
    public double RefreshHours { get; set; } = 2.0;

    // ---- Phase 4 overlay ----

    public bool OverlayEnabled { get; set; } = true;

    public ValueMode ValueMode { get; set; } = ValueMode.Stack;

    /// <summary>Also dot the player's own inventory (Expanded layout).</summary>
    public bool PlayerInventory { get; set; } = true;

    /// <summary>Only draw while the retainer sell list is open (not during entrust/withdraw). Prices still
    /// prefetch at any retainer window so the dots are ready when selling starts.</summary>
    public bool SellOnly { get; set; } = true;

    /// <summary>
    /// Ascending gil boundaries. N thresholds give N+1 bands spread red -> yellow -> green.
    /// Default (Darion, 2026-11-05, stack value): 7-step gradient, red only under 250, green at 10k+
    /// ("anything over 5-10k is worth selling").
    /// </summary>
    public double[] Thresholds { get; set; } = DefaultThresholds;

    public static double[] DefaultThresholds => [250, 500, 1_000, 2_500, 5_000, 10_000];

    /// <summary>
    /// Median vs. 20-sale mean: above this ratio the dot is drawn hollow (low confidence). Default 2x; the
    /// Hingan Placard example (749 vs 466, 1.6x) stays solid at 2x.
    /// </summary>
    public double ConfidenceRatio { get; set; } = 2.0;

    /// <summary>v1 -> v2: replace the old 3-band default with the gradient. Returns true if anything changed.</summary>
    public bool Migrate()
    {
        if (Version >= 2) return false;
        Thresholds = DefaultThresholds;
        Version = 2;
        return true;
    }

    /// <summary>Dot radius and inset from the slot's bottom-left corner, in px at a 44 px slot.</summary>
    public float DotRadius { get; set; } = 4f;
    public float DotInset { get; set; } = 3f;

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
