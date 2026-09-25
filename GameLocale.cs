using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SatisfactoryPlanner;

/// <summary>
/// Official in-game names per language. Source: the local Satisfactory install's CommunityResources/Docs/&lt;lang&gt;.json
/// (keyed by the same class names the wiki uses), falling back to the compact copies bundled in Data/names so the
/// app works without the game installed. Read-only.
/// </summary>
public static class GameLocale
{
    static string? _docsDir;
    static bool _searched;

    public static string? DocsDir
    {
        get
        {
            if (!_searched) { _docsDir = Find(); _searched = true; }
            return _docsDir;
        }
    }

    static string BundledDir => Path.Combine(AppContext.BaseDirectory, "Data", "names");

    /// <summary>Language codes available from the game install or the bundled copies (e.g. "zh-Hans", "ja", "de").</summary>
    public static List<string> Languages()
    {
        IEnumerable<string> Codes(string? dir) => dir != null && Directory.Exists(dir)
            ? Directory.GetFiles(dir, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!) : [];
        return Codes(DocsDir).Concat(Codes(BundledDir)).Distinct()
            .Where(c => !c.StartsWith("en-") && IsRealCulture(c)).OrderBy(c => c).ToList();
    }

    static bool IsRealCulture(string code)
    {
        try { return !CultureInfo.GetCultureInfo(code).EnglishName.StartsWith("Unknown"); }
        catch (CultureNotFoundException) { return false; }
    }

    /// <summary>ClassName → display name. Buildings are listed as Build_X; they're mapped to the Desc_X keys we use.</summary>
    public static Dictionary<string, string> Names(string lang)
    {
        var result = new Dictionary<string, string>();
        var path = DocsDir == null ? null : Path.Combine(DocsDir, lang + ".json");
        if (path == null || !File.Exists(path)) return Bundled(lang);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path)); // UTF-16 with BOM; ReadAllText detects it
            var alts = new List<string>();
            foreach (var group in doc.RootElement.EnumerateArray())
            {
                if (!group.TryGetProperty("Classes", out var classes)) continue;
                foreach (var c in classes.EnumerateArray())
                {
                    if (!c.TryGetProperty("ClassName", out var cn) || !c.TryGetProperty("mDisplayName", out var dn)) continue;
                    var cls = cn.GetString()!; var name = dn.GetString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (cls.StartsWith("Build_")) result.TryAdd("Desc_" + cls["Build_".Length..], name);
                    result[cls] = name;
                    if (cls.StartsWith("Recipe_Alternate_")) alts.Add(cls);
                }
            }
            StripAltMarker(result, alts);
        }
        catch (Exception) { return Bundled(lang); }
        return result;
    }

    /// <summary>Bundled {ClassName: name} copy extracted from the game's files.</summary>
    static Dictionary<string, string> Bundled(string lang)
    {
        var path = Path.Combine(BundledDir, lang + ".json");
        if (!File.Exists(path)) return new();
        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            StripAltMarker(result, result.Keys.Where(k => k.StartsWith("Recipe_Alternate_")).ToList());
            return result;
        }
        catch (Exception) { return new(); }
    }

    /// <summary>
    /// Each language marks alternates its own way ("Alternate: X", "替代：X", "X (alternative)").
    /// Detect the shared prefix/suffix across all alternate recipes and remove it; the app adds its own label.
    /// </summary>
    static void StripAltMarker(Dictionary<string, string> names, List<string> alts)
    {
        // "Marker: Name" (either colon width) or "Name (marker)"; the marker word must be shared by most alternates,
        // so a real name that merely contains a colon is left alone. Translations mix ":" and "：", hence per-name matching.
        var prefix = new Regex(@"^\s*(?<m>[^:：]{1,20}?)\s*[:：]\s*");
        var suffix = new Regex(@"\s*[(（](?<m>[^)）]{1,25})[)）]\s*$");
        foreach (var rx in new[] { prefix, suffix })
        {
            var words = alts.Select(a => rx.Match(names[a])).Where(m => m.Success).Select(m => m.Groups["m"].Value.Trim()).ToList();
            var top = words.GroupBy(w => w, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (top == null || top.Count() < alts.Count * 0.6) continue;
            foreach (var a in alts)
            {
                var m = rx.Match(names[a]);
                if (m.Success && string.Equals(m.Groups["m"].Value.Trim(), top.Key, StringComparison.OrdinalIgnoreCase))
                    names[a] = rx.Replace(names[a], "", 1).Trim();
            }
        }
    }

    static string? Find()
    {
        var candidates = new List<string>();
        // Steam: default library + any extra library folders
        var steam = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam", "steamapps");
        candidates.Add(Path.Combine(steam, "common", "Satisfactory"));
        var vdf = Path.Combine(steam, "libraryfolders.vdf");
        if (File.Exists(vdf))
            foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                candidates.Add(Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps", "common", "Satisfactory"));
        // Epic: launcher manifests record the install location
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (Directory.Exists(manifests))
            foreach (var f in Directory.GetFiles(manifests, "*.item"))
            {
                try
                {
                    using var d = JsonDocument.Parse(File.ReadAllText(f));
                    if (d.RootElement.TryGetProperty("DisplayName", out var dn) && dn.GetString()?.Contains("Satisfactory") == true &&
                        d.RootElement.TryGetProperty("InstallLocation", out var loc))
                        candidates.Add(loc.GetString()!);
                }
                catch (Exception) { }
            }
        return candidates.Select(c => Path.Combine(c, "CommunityResources", "Docs")).FirstOrDefault(Directory.Exists);
    }
}
