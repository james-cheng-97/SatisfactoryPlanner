using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SatisfactoryPlanner;

/// <summary>Downloads recipe data and icons from https://satisfactory.wiki.gg/.</summary>
public static class WikiSync
{
    const string Base = "https://satisfactory.wiki.gg";
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("SatisfactoryPlanner/1.0 (personal desktop planner)");
        return c;
    }

    /// <summary>The wiki keeps game data in Template:Docs*.json pages (used by its Lua modules).</summary>
    public static async Task DownloadDataAsync()
    {
        Directory.CreateDirectory(GameData.DataDir);
        foreach (var name in new[] { "DocsItems.json", "DocsBuildings.json", "DocsRecipes.json" })
        {
            var json = await Http.GetStringAsync($"{Base}/index.php?title=Template:{name}&action=raw");
            JsonDocument.Parse(json).Dispose(); // validate before overwriting
            await File.WriteAllTextAsync(Path.Combine(GameData.DataDir, name), json);

            // localized copies from the language wikis (same class-name keys, translated names)
            foreach (var (lang, ns) in GameData.WikiLanguages)
            {
                try
                {
                    var loc = await Http.GetStringAsync($"{Base}/{lang}/index.php?title={Uri.EscapeDataString(ns + ":" + name)}&action=raw");
                    JsonDocument.Parse(loc).Dispose();
                    Directory.CreateDirectory(Path.Combine(GameData.DataDir, lang));
                    await File.WriteAllTextAsync(Path.Combine(GameData.DataDir, lang, name), loc);
                }
                catch (Exception) { } // translations are optional; keep the bundled copy
            }
        }
    }

    /// <summary>Fetches 64px icons for every item/building not yet cached. Returns number downloaded.</summary>
    public static async Task<int> DownloadImagesAsync(IProgress<string>? progress = null, bool force = false)
    {
        Directory.CreateDirectory(ImageCache.Dir);
        // wiki file names are English regardless of the UI language
        var names = GameData.Items.Values.Select(i => (i.ClassName, Name: i.EnglishName))
            .Concat(GameData.Buildings.Values.Select(b => (b.ClassName, Name: b.EnglishName)))
            .Where(x => force || (!File.Exists(ImageCache.PathFor(x.ClassName)) && !File.Exists(ImageCache.BundledPathFor(x.ClassName))))
            .GroupBy(x => x.Name).ToList();

        int done = 0;
        foreach (var chunk in names.Chunk(40))
        {
            var titles = string.Join("|", chunk.Select(g => "File:" + g.Key + ".png"));
            var url = $"{Base}/api.php?action=query&format=json&prop=imageinfo&iiprop=url&iiurlwidth=64&titles={Uri.EscapeDataString(titles)}";
            Dictionary<string, string> thumbs = new();
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var q = doc.RootElement.GetProperty("query");
                var norm = new Dictionary<string, string>();
                if (q.TryGetProperty("normalized", out var n))
                    foreach (var x in n.EnumerateArray()) norm[x.GetProperty("to").GetString()!] = x.GetProperty("from").GetString()!;
                foreach (var page in q.GetProperty("pages").EnumerateObject())
                {
                    if (!page.Value.TryGetProperty("imageinfo", out var ii)) continue;
                    var info = ii[0];
                    var t = info.TryGetProperty("thumburl", out var tu) ? tu.GetString() : info.GetProperty("url").GetString();
                    var title = page.Value.GetProperty("title").GetString()!;
                    title = norm.GetValueOrDefault(title, title);
                    thumbs[title["File:".Length..^".png".Length].Replace('_', ' ')] = t!;
                }
            }
            catch (Exception) { continue; }

            foreach (var g in chunk)
            {
                if (!thumbs.TryGetValue(g.Key.Replace('_', ' '), out var t)) continue;
                try
                {
                    var bytes = await Http.GetByteArrayAsync(t);
                    foreach (var (cls, _) in g) await File.WriteAllBytesAsync(ImageCache.PathFor(cls), bytes);
                    done++;
                    progress?.Report(Loc.T("status.downloadingIcons", done));
                }
                catch (Exception) { }
            }
        }
        ImageCache.Clear();
        return done;
    }
}

public static class ImageCache
{
    public static string Dir => Path.Combine(GameData.AppDir, "images");
    static readonly Dictionary<string, ImageSource?> Cache = new();

    public static string PathFor(string cls) => Path.Combine(Dir, cls + ".png");
    /// <summary>Icons shipped with the app, so it looks right offline; downloaded ones take precedence.</summary>
    public static string BundledPathFor(string cls) => Path.Combine(AppContext.BaseDirectory, "Data", "images", cls + ".png");
    public static void Clear() => Cache.Clear();

    public static ImageSource? Get(string cls)
    {
        if (Cache.TryGetValue(cls, out var img)) return img;
        img = null;
        var path = PathFor(cls);
        if (!File.Exists(path)) path = BundledPathFor(cls);
        if (File.Exists(path))
        {
            try
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.UriSource = new Uri(path);
                b.DecodePixelWidth = 64;
                b.EndInit();
                b.Freeze();
                img = b;
            }
            catch (Exception) { }
        }
        return Cache[cls] = img;
    }
}
