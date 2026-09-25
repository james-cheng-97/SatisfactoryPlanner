using System.ComponentModel;
using System.Text.Json.Serialization;

namespace SatisfactoryPlanner;

/// <summary>
/// One plan tab = one factory. Everything in the left panel belongs to the tab (targets, recipe choices, tier,
/// alternates/MAM, resource nodes & limits, miner/clock, rounding, belts & splitters) and lives in <see cref="State"/>.
/// Only the loaded save (unlocked recipes) and the language are global.
/// </summary>
public class PlanTab : INotifyPropertyChanged
{
    /// <summary>Stable id (the layout cache files a tab's layouts under it). A copy gets a new one.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    string _name = "";
    public string Name { get => _name; set { _name = value; Raise(nameof(Name)); } }

    /// <summary>This tab's settings (globals stripped). Null only in files from older versions.</summary>
    public Settings? State { get; set; }

    // older versions stored just these per tab; kept so their files still load
    public List<TargetSpec> Targets { get; set; } = new();
    public Dictionary<string, string> RecipeOverrides { get; set; } = new();
    public bool Optimize { get; set; }
    public RoundingMode Mode { get; set; } = RoundingMode.Exact;

    /// <summary>Last computed headline ("53 machines · 643 MW"), shown under the tab name for quick comparison.</summary>
    string _summary = "";
    public string Summary { get => _summary; set { _summary = value; Raise(nameof(Summary)); } }

    bool _active;
    [JsonIgnore] public bool IsActive { get => _active; set { _active = value; Raise(nameof(IsActive)); } }

    public PlanTab Copy(string name) => new()
    {
        Name = name,
        State = State?.Clone(),
        Targets = Targets.Select(t => new TargetSpec { Item = t.Item, Rate = t.Rate }).ToList(),
        RecipeOverrides = new(RecipeOverrides),
        Optimize = Optimize,
        Mode = Mode,
        Summary = Summary,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    void Raise(string n) => PropertyChanged?.Invoke(this, new(n));
}
