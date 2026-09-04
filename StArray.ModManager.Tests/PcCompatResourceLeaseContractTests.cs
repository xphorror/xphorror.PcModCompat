namespace StArray.ModManager.Tests;

public sealed class PcCompatResourceLeaseContractTests
{
    [Test]
    public void NativeResourceChangerUsesOwnerGenerationContributions()
    {
        var source = ReadNativeSource();

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("struct ResourceOwnerKey"));
            Assert.That(source, Does.Contain("struct ResourceContribution"));
            Assert.That(source, Does.Contain(
                "std::map<ResourceOwnerKey, ResourceContribution> g_resource_contributions"));
            Assert.That(source, Does.Contain("registration_sequence"));
            Assert.That(source, Does.Contain("select_resource_effective_state_locked"));
            Assert.That(source, Does.Contain("session_generation < latest->second"));
            Assert.That(source, Does.Contain("if (feature_mask == 0)"));
            Assert.That(source, Does.Contain("g_resource_contributions.erase(owner)"));
            Assert.That(source, Does.Not.Contain(
                "g_resource_change_rabbit.exchange(next_rabbit"));
        });
    }

    [Test]
    public void RabbitSpritesAreStoredPerOwnerAndProjectedFromTheActiveLease()
    {
        var source = ReadNativeSource();

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("struct ResourceSpriteContribution"));
            Assert.That(source, Does.Contain(
                "std::map<ResourceOwnerKey, ResourceSpriteContribution> " +
                "g_resource_sprite_contributions"));
            Assert.That(source, Does.Contain(
                "refresh_resource_rabbit_sprite_projection_locked"));
            Assert.That(source, Does.Contain(
                "g_resource_sprite_contributions.find(active_owner)"));
            Assert.That(source, Does.Contain(
                "g_resource_sprite_contributions.erase(contribution)"));
        });
    }

    [Test]
    public void LeaseHandoffRestoresBaselineThenReappliesTheSelectedOwner()
    {
        var source = ReadNativeSource();

        Assert.Multiple(() =>
        {
            Assert.That(source, Does.Contain("resource_transition_mask("));
            Assert.That(source, Does.Contain("g_resource_pending_restore_mask.fetch_or("));
            Assert.That(source, Does.Contain(
                "if (editor != nullptr && resource_change_rabbit_enabled())"));
            Assert.That(source, Does.Contain("apply_resource_editor_rabbit(editor)"));
            Assert.That(source, Does.Contain("apply_resource_planet_color(planet)"));
            Assert.That(source, Does.Contain("apply_resource_floor_color(floor)"));
            Assert.That(source, Does.Contain("resource_has_active_contribution()"));
            Assert.That(source, Does.Contain("set_resource_logo_clone_active(logo, false)"));
        });
    }

    private static string ReadNativeSource()
        => File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "Android",
            "library",
            "src",
            "main",
            "cpp",
            "core",
            "pccompat_hook_rules.cpp"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Android")) &&
                Directory.Exists(Path.Combine(directory.FullName, "xphorror.PcModCompat")))
                return directory.FullName;
            directory = directory.Parent;
        }

        Assert.Fail("Could not find StArray.ModManager repository root from test directory");
        return string.Empty;
    }
}
