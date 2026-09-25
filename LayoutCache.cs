using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SatisfactoryPlanner;

/// <summary>
/// Finished layouts on disk, each with the plan it was made from, so an unchanged plan isn't laid out again and a tab
/// keeps a list of the factories it has worked out: other plans (targets, recipes, rounding …) and, per plan, other layout
/// options (floors, blueprint size, machines may cross tiles). Picking one brings back its plan and options.
/// Files: &lt;AppDir&gt;/layouts/&lt;plan hash&gt;/&lt;options&gt;.json (the layout) and .meta.json (what the list shows).
/// </summary>
public static class LayoutCache
{
    /// <summary>Bump when the planner's output changes (older layouts are then ignored).</summary>
    public const int Engine = 4;

    public static string Dir => Path.Combine(GameData.AppDir, "layouts");

    static readonly JsonSerializerOptions Json = new() { IncludeFields = true };

    /// <summary>What a cached layout was made from and how it came out (no layout: the list reads only these).</summary>
    public sealed class Meta
    {
        public int Engine { get; set; }
        public string PlanHash { get; set; } = "";
        public string TabId { get; set; } = "";
        public string Tab { get; set; } = "";
        /// <summary>The tab's plan settings it was made with (targets, recipes, tier, rounding, layout options …).</summary>
        public Settings? Plan { get; set; }
        /// <summary>One line about the plan: targets and machine count.</summary>
        public string PlanSummary { get; set; } = "";
        public int Floors { get; set; }
        public int BlueprintTile { get; set; }
        public bool HandPlace { get; set; }
        public bool IndustrialTank { get; set; }
        public DateTime Created { get; set; }
        public int Blueprints { get; set; }
        public double Belt { get; set; }
        public double BuildSeconds { get; set; }
        public string OptionsKey => Key(Floors, BlueprintTile, HandPlace, IndustrialTank);
    }

    public static string Key(int floors, int tile, bool hand, bool bigTank = false) => $"f{floors}-t{tile}-{(hand ? "hand" : "tiles")}{(bigTank ? "-ind" : "")}";
    public static string Key(Settings s) => Key(s.Floors, s.BlueprintTile, s.BlueprintTile > 0 && s.HandPlaceAcrossTiles, s.IndustrialFluidBox);

    /// <summary>The plan's identity: its machines and lines, and every setting but the layout options.</summary>
    public static string PlanHash(Plan plan, Settings s)
    {
        var sb = new StringBuilder();
        foreach (var n in plan.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal))
            sb.Append($"N|{n.Key}|{n.Kind}|{n.Item}|{n.Building}|{n.Recipe?.ClassName}|{n.Machines}|{n.Rate:0.####}\n");
        foreach (var e in plan.Edges.OrderBy(e => e.From, StringComparer.Ordinal).ThenBy(e => e.To, StringComparer.Ordinal).ThenBy(e => e.Item, StringComparer.Ordinal))
            sb.Append($"E|{e.From}|{e.To}|{e.Item}|{e.Rate:0.####}\n");
        var bare = s.TabState(); bare.Floors = 1; bare.BlueprintTile = 0; bare.HandPlaceAcrossTiles = false; bare.IndustrialFluidBox = false;
        sb.Append(JsonSerializer.Serialize(bare)).Append('|').Append(Engine);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16].ToLowerInvariant();
    }

    static string PathOf(string planHash, string optionsKey, bool meta) => Path.Combine(Dir, planHash, optionsKey + (meta ? ".meta.json" : ".json"));

    public static Layout? Load(string planHash, string optionsKey)
    {
        try
        {
            var m = ReadMeta(PathOf(planHash, optionsKey, true));
            var p = PathOf(planHash, optionsKey, false);
            if (m == null || !File.Exists(p)) return null;
            return JsonSerializer.Deserialize<Layout>(File.ReadAllText(p), Json);
        }
        catch { return null; } // a damaged file: laid out again
    }

    public static void Save(Meta m, Layout layout)
    {
        try
        {
            m.Engine = Engine;
            Directory.CreateDirectory(Path.Combine(Dir, m.PlanHash));
            File.WriteAllText(PathOf(m.PlanHash, m.OptionsKey, false), JsonSerializer.Serialize(layout, Json));
            File.WriteAllText(PathOf(m.PlanHash, m.OptionsKey, true), JsonSerializer.Serialize(m, Json)); // (last: it marks the entry complete)
        }
        catch { } // (no cache is fine)
    }

    static Meta? ReadMeta(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = JsonSerializer.Deserialize<Meta>(File.ReadAllText(path), Json);
            return m != null && m.Engine == Engine ? m : null;
        }
        catch { return null; }
    }

    /// <summary>Every cached layout of a tab (all its plans), newest plan first; in a plan, fewest blueprints first.</summary>
    public static List<Meta> ForTab(string tabId)
    {
        var list = new List<Meta>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.meta.json", SearchOption.AllDirectories))
            if (ReadMeta(f) is { } m && m.TabId == tabId) list.Add(m);
        var newest = list.GroupBy(m => m.PlanHash).ToDictionary(g => g.Key, g => g.Max(m => m.Created));
        return list.OrderByDescending(m => newest[m.PlanHash]).ThenBy(m => m.PlanHash)
                   .ThenBy(m => m.Blueprints == 0 ? int.MaxValue : m.Blueprints).ThenBy(m => m.Belt).ToList();
    }

    public static void Delete(string planHash, string optionsKey)
    {
        try { File.Delete(PathOf(planHash, optionsKey, true)); File.Delete(PathOf(planHash, optionsKey, false)); } catch { }
    }
}
