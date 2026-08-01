using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatResourceRecipeTests
{
    [Test]
    public void ReadsPublishedJipperResourceRecipe()
    {
        var path = Path.GetFullPath(Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "..", "..", "..", "..",
            "JipperResourcePack_release", ".pccompat", "resource_recipe.bin"));
        if (!File.Exists(path))
            Assert.Ignore("Jipper resource_recipe.bin is not published in this workspace.");

        Assert.That(PcCompatResourceRecipe.TryRead(path, out var document, out var error), Is.True, error);
        Assert.That(document.ModId, Does.StartWith("JipperResourcePack"));
        Assert.That(document.FeatureGroups.Select(group => group.Id), Does.Contain("overlay.progress_bar"));
        Assert.That(document.FeatureGroups.Select(group => group.Id), Does.Contain("overlay.font"));
        Assert.That(document.FeatureGroups.Select(group => group.Id), Does.Contain("keyviewer.sprites"));
        // Confidence may be "Proven" (string enums) or "1" (legacy numeric enums).
        Assert.That(document.Bindings.Count(binding =>
                binding.Confidence.Equals("Proven", StringComparison.OrdinalIgnoreCase) ||
                binding.Confidence == "1"),
            Is.GreaterThanOrEqualTo(6));
        Assert.That(document.Bindings.Any(binding =>
                binding.AssetName.Equals("ProgressBar", StringComparison.OrdinalIgnoreCase) &&
                binding.ExpectedType.Equals("GameObject", StringComparison.OrdinalIgnoreCase)),
            Is.True);
        Assert.That(document.Bindings.Any(binding =>
                binding.AssetName.Contains("MAPLESTORY", StringComparison.OrdinalIgnoreCase) &&
                binding.ExpectedType.Equals("TMP_FontAsset", StringComparison.OrdinalIgnoreCase)),
            Is.True);
    }
}
