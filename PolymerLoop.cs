namespace SatisfactoryPlanner;

/// <summary>
/// The plastic ⇄ rubber recycling loop (the one X-shaped loop in the game), for a plan that makes exactly plastic and
/// rubber: crude → Heavy Oil Residue (alt) → Diluted Packaged Fuel (alt, with packaged water) → unpackaged fuel, which
/// Recycled Plastic / Recycled Rubber turn into more of each other (each refinery gives back twice what it takes).
/// About 80 plastic + rubber per 30 crude, against 40 with the plain recipes. It needs a starting stock of plastic,
/// rubber and empty canisters (the machines' input slots hold it); each producer feeds the other side's refineries
/// first and only the surplus (smart splitter Overflow) leaves as output, so the loop never runs dry.
/// </summary>
public static class PolymerLoop
{
    public const string Plastic = "Desc_Plastic_C", Rubber = "Desc_Rubber_C";

    /// <summary>Item → the recipe the loop pins for it.</summary>
    public static readonly (string item, string recipe)[] Recipes =
    [
        (Plastic, "Recipe_Alternate_Plastic_1_C"),                    // Recycled Plastic: rubber + fuel
        (Rubber, "Recipe_Alternate_RecycledRubber_C"),                // Recycled Rubber: plastic + fuel
        ("Desc_LiquidFuel_C", "Recipe_UnpackageFuel_C"),
        ("Desc_Fuel_C", "Recipe_Alternate_DilutedPackagedFuel_C"),    // packaged fuel from heavy oil + packaged water
        ("Desc_PackagedWater_C", "Recipe_PackagedWater_C"),
        ("Desc_HeavyOilResidue_C", "Recipe_Alternate_HeavyOilResidue_C"),
    ];

    /// <summary>The plan asks for plastic and rubber and nothing else.</summary>
    public static bool Applies(Settings s)
    {
        var t = s.Targets.Where(x => x.Rate > 0).Select(x => x.Item).Distinct().ToList();
        return t.Count == 2 && t.Contains(Plastic) && t.Contains(Rubber);
    }

    /// <summary>Every loop recipe is usable (unlocked in the save, or allowed without one).</summary>
    public static bool Available(Settings s) =>
        Recipes.All(p => GameData.Recipes.FirstOrDefault(r => r.ClassName == p.recipe) is { } r && s.IsAvailable(r));

    /// <summary>The loop's recipes are the ones pinned.</summary>
    public static bool IsOn(Settings s) => Recipes.All(p => s.RecipeOverrides.TryGetValue(p.item, out var r) && r == p.recipe);

    public static void Apply(Settings s)
    {
        foreach (var (item, recipe) in Recipes) s.RecipeOverrides[item] = recipe;
    }
}
