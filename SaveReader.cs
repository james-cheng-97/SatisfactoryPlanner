using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace SatisfactoryPlanner;

public class SaveInfo
{
    public string Name { get; init; } = "";
    public HashSet<string> Recipes { get; init; } = new();
    public int Tier { get; init; }
}

/// <summary>
/// Minimal reader for Satisfactory .sav files. The body is a series of zlib chunks; inside, the recipe manager's
/// "mAvailableRecipes" array lists every unlocked recipe and "mPurchasedSchematics" the bought milestones.
/// Handles both the pre-1.1 and 1.1+ (UE5 type-tree) property tag layouts. Read-only.
/// </summary>
public static class SaveReader
{
    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FactoryGame", "Saved", "SaveGames");

    public static SaveInfo Read(string path)
    {
        var body = Decompress(File.ReadAllBytes(path));
        var recipes = ReadObjectArray(body, "mAvailableRecipes").ToHashSet();
        if (recipes.Count == 0) throw new InvalidDataException(Loc.T("save.unsupported"));
        var tier = ReadObjectArray(body, "mPurchasedSchematics")
            .Select(s => Regex.Match(s, @"^Schematic_(\d+)-")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max();
        return new SaveInfo { Name = Path.GetFileNameWithoutExtension(path), Recipes = recipes, Tier = tier };
    }

    static byte[] Decompress(byte[] d)
    {
        var ms = new MemoryStream();
        int i = 0;
        while ((i = IndexOf(d, [0xC1, 0x83, 0x2A, 0x9E], i)) >= 0)
        {
            bool ok = false;
            for (int j = i + 4; j < i + 80 && j + 1 < d.Length; j++)
            {
                if (d[j] != 0x78 || d[j + 1] is not (0x9C or 0x01 or 0xDA or 0x5E)) continue;
                try
                {
                    var chunk = new MemoryStream();
                    using (var z = new ZLibStream(new MemoryStream(d, j, d.Length - j), CompressionMode.Decompress))
                        z.CopyTo(chunk);
                    chunk.WriteTo(ms);
                    i = j + 1; ok = true;
                    break;
                }
                catch (InvalidDataException) { }
            }
            if (!ok) i += 4;
        }
        return ms.ToArray();
    }

    static int IndexOf(byte[] d, byte[] pat, int start)
    {
        for (int i = Math.Max(0, start); i <= d.Length - pat.Length; i++)
        {
            int k = 0;
            while (k < pat.Length && d[i + k] == pat[k]) k++;
            if (k == pat.Length) return i;
        }
        return -1;
    }

    static List<string> ReadObjectArray(byte[] b, string prop)
    {
        var result = new List<string>();
        var key = Encoding.ASCII.GetBytes(prop + "\0");
        int at = 0;
        while ((at = IndexOf(b, key, at)) >= 0)
        {
            int p = at + key.Length;
            at = p;
            try
            {
                var (type, p2) = FStr(b, p); p = p2;
                if (type != "ArrayProperty") continue;
                if (BitConverter.ToInt32(b, p) == 1 && BitConverter.ToInt32(b, p + 4) == 15)
                {
                    // 1.1+: type tree (ArrayProperty -> ObjectProperty), then size, flags
                    p += 4; (_, p) = FStr(b, p); p += 4;
                    p += 4; var flags = b[p++];
                    if ((flags & 1) != 0) p += 16;
                }
                else
                {
                    // pre-1.1: size, index, inner type, guid flag
                    p += 8; (_, p) = FStr(b, p);
                    if (b[p++] != 0) p += 16;
                }
                int count = BitConverter.ToInt32(b, p); p += 4;
                if (count is < 0 or > 100000) continue;
                for (int k = 0; k < count; k++)
                {
                    (_, p) = FStr(b, p);
                    string path; (path, p) = FStr(b, p);
                    result.Add(path[(path.LastIndexOf('.') + 1)..]);
                }
            }
            catch (Exception) { }
        }
        return result;
    }

    static (string, int) FStr(byte[] b, int p)
    {
        int n = BitConverter.ToInt32(b, p); p += 4;
        if (n == 0) return ("", p);
        if (n > 0) return (Encoding.Latin1.GetString(b, p, n - 1), p + n);
        return (Encoding.Unicode.GetString(b, p, -2 * n - 2), p - 2 * n);
    }
}
