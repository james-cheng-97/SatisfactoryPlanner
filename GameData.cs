using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace SatisfactoryPlanner;

public record Amount(string Item, double Value);

public class ItemDef
{
    public required string ClassName { get; init; }
    public required string Name { get; init; }
    public string EnglishName { get; init; } = "";
    public bool IsFluid { get; init; }
    public ImageSource? Icon => ImageCache.Get(ClassName);
    public override string ToString() => Name;
}

public class BuildingDef
{
    public required string ClassName { get; init; }
    public required string Name { get; init; }
    public string EnglishName { get; init; } = "";
    public double Power { get; init; }
    public int UnlockTier { get; init; }
    public ImageSource? Icon => ImageCache.Get(ClassName);
}

public class RecipeDef
{
    public required string ClassName { get; init; }
    public required string Name { get; init; }
    public double Duration { get; init; }
    public required List<Amount> In { get; init; }
    public required List<Amount> Out { get; init; }
    public required string Building { get; init; }
    public bool Alternate { get; init; }
    public string UnlockedBy { get; init; } = "";
    public int Tier { get; init; }
    public bool IsMam { get; init; }
    /// <summary>Earliest tier without using the MAM (99 = MAM only).</summary>
    public int NoMamTier { get; init; }
    public double? MinPower { get; init; }
    public double? MaxPower { get; init; }

    public double PerMin(double amount) => amount * 60.0 / Duration;

    /// <summary>Net units/min of an item for one machine at 100%.</summary>
    public double NetPerMachine(string item) =>
        PerMin(Out.Where(a => a.Item == item).Sum(a => a.Value)) -
        PerMin(In.Where(a => a.Item == item).Sum(a => a.Value));

    public string Display => (Alternate ? Loc.T("alt") : "") + Name + $"  (T{Tier}{(IsMam ? " MAM" : "")})";
    public override string ToString() => Display;
}

public static class GameData
{
    public static Dictionary<string, ItemDef> Items { get; private set; } = new();
    public static Dictionary<string, BuildingDef> Buildings { get; private set; } = new();
    public static List<RecipeDef> Recipes { get; private set; } = new();

    public static readonly string AppDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SatisfactoryPlanner");
    public static string DataDir => Path.Combine(AppDir, "data");

    public static readonly string[] RawResources =
    {
        "Desc_OreIron_C", "Desc_OreCopper_C", "Desc_Stone_C", "Desc_Coal_C", "Desc_OreGold_C", "Desc_RawQuartz_C",
        "Desc_Sulfur_C", "Desc_OreBauxite_C", "Desc_OreUranium_C", "Desc_SAM_C", "Desc_Water_C", "Desc_LiquidOil_C",
        "Desc_NitrogenGas_C"
    };

    /// <summary>Languages with their own wiki data (satisfactory.wiki.gg/de, /fr), and the template namespace name there.</summary>
    public static readonly (string lang, string ns)[] WikiLanguages = [("de", "Vorlage"), ("fr", "Modèle")];

    static string? FindFile(string name, string lang = "en")
    {
        var sub = lang == "en" ? name : Path.Combine(lang, name);
        var user = Path.Combine(DataDir, sub);
        if (File.Exists(user)) return user;
        var bundled = Path.Combine(AppContext.BaseDirectory, "Data", sub);
        return File.Exists(bundled) ? bundled : null;
    }

    /// <summary>Each wiki entry is an array of branch variants; prefer the stable one.</summary>
    static IEnumerable<(string key, JsonElement e)> Entries(string file, string lang = "en")
    {
        var path = FindFile(file, lang);
        if (path == null) return [];
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var list = new List<(string, JsonElement)>();
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            JsonElement? pick = null;
            foreach (var v in p.Value.EnumerateArray())
            {
                if (v.TryGetProperty("stable", out var s) && s.ValueKind == JsonValueKind.True) { pick = v; break; }
            }
            if (pick != null) list.Add((p.Name, pick.Value.Clone()));
        }
        return list;
    }

    static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    static double Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static double? NumOpt(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static bool Bool(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Localized name / unlock text keyed by class name; empty for English or when missing.</summary>
    static Dictionary<string, (string name, string unlock)> Localized(string file, string lang) =>
        lang == "en" ? new() : Entries(file, lang).ToDictionary(x => x.key, x => (Str(x.e, "name"), Str(x.e, "unlockedBy")));

    static string CleanWiki(string s) => Regex.Replace(s.Replace("<br>", " "), @"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", "$1");

    /// <summary>The wikis prefix/suffix alternates themselves; strip it so the app's own "Alt:" label is used.</summary>
    static string StripAlt(string name) =>
        Regex.Replace(name, @"^(Alternate|Alternativ|Alternative)\s*:\s*|\s*\((alternate|alternative)\)$", "", RegexOptions.IgnoreCase);

    public static void Load(string lang = "en")
    {
        // names: the game's own files (any installed language) beat the wiki's de/fr copies; unlock text is wiki-only
        var wikiLang = WikiLanguages.Any(w => w.lang == lang) ? lang : "en";
        var locItems = Localized("DocsItems.json", wikiLang);
        var locBuildings = Localized("DocsBuildings.json", wikiLang);
        var locRecipes = Localized("DocsRecipes.json", wikiLang);
        var game = lang == "en" ? new Dictionary<string, string>() : GameLocale.Names(lang);
        string L(Dictionary<string, (string name, string unlock)> map, string key, string en) =>
            game.TryGetValue(key, out var g) ? g
            : map.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v.name) ? v.name : en;

        Items = Entries("DocsItems.json").ToDictionary(x => x.key, x => new ItemDef
        {
            ClassName = x.key,
            Name = L(locItems, x.key, Str(x.e, "name")),
            EnglishName = Str(x.e, "name"),
            IsFluid = Str(x.e, "form") is "liquid" or "gas"
        });

        Buildings = Entries("DocsBuildings.json").ToDictionary(x => x.key, x => new BuildingDef
        {
            ClassName = x.key,
            Name = L(locBuildings, x.key, Str(x.e, "name")),
            EnglishName = Str(x.e, "name"),
            Power = Num(x.e, "powerUsage"),
            UnlockTier = ParseUnlock(Str(x.e, "unlockedBy")).tier
        });

        var recipes = new List<RecipeDef>();
        foreach (var (key, e) in Entries("DocsRecipes.json"))
        {
            if (key.StartsWith("TempRecipe") || Bool(e, "inBuildGun") || Bool(e, "inCustomizer")) continue;
            if (e.TryGetProperty("seasons", out var seasons) && seasons.GetArrayLength() > 0) continue;
            var building = e.GetProperty("producedIn").EnumerateArray().Select(b => b.GetString()!)
                .FirstOrDefault(b => Buildings.ContainsKey(b) && b != "Desc_GeneratorNuclear_C");
            if (building == null) continue;

            var unlock = Str(e, "unlockedBy");
            var (tier, mam, excluded, noMamTier) = ParseUnlockFull(unlock);
            if (excluded) continue;
            // a recipe can't be automated before its machine exists (e.g. Crystal Oscillator via the MAM still needs a Manufacturer, Tier 6)
            int machineTier = Buildings.TryGetValue(building, out var bdef) ? bdef.UnlockTier : 0;
            tier = Math.Max(tier, machineTier);
            noMamTier = noMamTier >= 99 ? 99 : Math.Max(noMamTier, machineTier);

            List<Amount> Amounts(string prop) => e.GetProperty(prop).EnumerateArray()
                .Select(a => new Amount(a.GetProperty("item").GetString()!, a.GetProperty("amount").GetDouble()))
                .Where(a => Items.ContainsKey(a.Item)).ToList();

            recipes.Add(new RecipeDef
            {
                ClassName = key,
                Name = StripAlt(L(locRecipes, key, Str(e, "name"))),
                Duration = Num(e, "duration"),
                In = Amounts("ingredients"),
                Out = Amounts("products"),
                Building = building,
                Alternate = Bool(e, "alternate"),
                UnlockedBy = CleanWiki(locRecipes.TryGetValue(key, out var lr) && lr.unlock != "" ? lr.unlock : unlock),
                Tier = tier,
                IsMam = mam,
                NoMamTier = noMamTier,
                MinPower = NumOpt(e, "minPower"),
                MaxPower = NumOpt(e, "maxPower"),
            });
        }
        Recipes = recipes.Where(r => r.Duration > 0 && r.Out.Count > 0).ToList();
    }

    /// <summary>
    /// Maps the wiki "unlockedBy" text to the earliest tier it can be reached.
    /// The text is a list of alternatives ("MAM Quartz Research - Crystal Oscillator OR Tier 7 - Bauxite Refinement"),
    /// each possibly a conjunction ("Hard Drive scanning after unlocking: MAM … AND Tier 5 - …").
    /// An alternative costs the highest tier among its AND parts; the recipe costs the cheapest alternative.
    /// Returns the cheapest tier overall, whether that path goes through the MAM, and the cheapest tier WITHOUT the MAM
    /// (used when MAM recipes are switched off; 99 if there is no such path).
    /// </summary>
    public static (int tier, bool mam, bool excluded) ParseUnlock(string u)
    {
        var (tier, mam, excluded, _) = ParseUnlockFull(u);
        return (tier, mam, excluded);
    }

    public static (int tier, bool mam, bool excluded, int noMamTier) ParseUnlockFull(string u)
    {
        if (string.IsNullOrWhiteSpace(u) || u == "Onboarding") return (0, false, false, 0);
        if (u.Contains("FICSMAS") || u.StartsWith("Equipment")) return (0, false, true, 99);
        var idx = u.IndexOf("after unlocking", StringComparison.Ordinal);
        var body = idx >= 0 ? u[(idx + "after unlocking".Length)..] : u;
        if (idx >= 0 && !Regex.IsMatch(body, @"Tier \d|Research")) return (0, false, false, 0); // plain hard drive

        int best = int.MaxValue, bestNoMam = 99; bool bestMam = false;
        foreach (var alt in Regex.Split(body, @"\bOR\b"))
        {
            int t = 0; bool m = false;
            foreach (var part in Regex.Split(alt, @"\bAND\b"))
            {
                var tm = Regex.Match(part, @"Tier (\d+)");
                if (tm.Success) t = Math.Max(t, int.Parse(tm.Groups[1].Value));
                else if (part.Contains("Research")) { m = true; t = Math.Max(t, part.Contains("Alien Technology") ? 8 : 1); }
            }
            if (t < best || (t == best && bestMam && !m)) { best = t; bestMam = m; }
            if (!m) bestNoMam = Math.Min(bestNoMam, t);
        }
        return best == int.MaxValue ? (0, false, false, 0) : (best, bestMam, false, bestNoMam);
    }

    public static ItemDef Item(string cls) =>
        Items.TryGetValue(cls, out var i) ? i : new ItemDef { ClassName = cls, Name = cls };
}
