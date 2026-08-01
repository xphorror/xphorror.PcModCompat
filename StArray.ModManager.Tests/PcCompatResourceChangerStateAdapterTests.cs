using System.Reflection;
using System.Reflection.Emit;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatResourceChangerStateAdapterTests
{
    [TearDown]
    public void TearDown()
    {
        PcCompatResourceChangerRuntime.ClearSettingsSink();
        PcCompatResourceChangerRuntime.Remove("JipperResourcePack");
    }

    [Test]
    public void PublishesOriginalSettingsAndJongyeolOverridesThenAcceptsFallbackWrites()
    {
        var fixture = CreateFixture();
        PcCompatResourceChangerState? published = null;
        var publishCount = 0;
        PcCompatResourceChangerRuntime.RegisterSettingsSink(state =>
        {
            published = state;
            publishCount++;
        });
        var adapter = PcCompatResourceChangerStateAdapter.TryCreate(
            fixture.ResourceChanger.Assembly,
            "JipperResourcePack",
            23,
            out var createError);

        Assert.That(adapter, Is.Not.Null, createError);
        Assert.That(adapter!.Refresh(out var refreshError), Is.True, refreshError);
        Assert.That(published!.PlanetColor.R, Is.EqualTo(0.1f));
        Assert.That(published.ResourcePackName, Is.EqualTo("Default"));
        Assert.That(adapter.Refresh(out refreshError), Is.True, refreshError);
        Assert.That(publishCount, Is.EqualTo(1));

        Assert.That(
            adapter.ApplySettings(false, true, false, out var applyError),
            Is.True,
            applyError);
        Assert.That(fixture.ChangeRabbit.GetValue(fixture.Settings), Is.False);
        Assert.That(fixture.ChangeBallColor.GetValue(fixture.Settings), Is.True);
        Assert.That(fixture.ChangeTileColor.GetValue(fixture.Settings), Is.False);

        SetColor(fixture, "PlanetColor", 0.6f, 0.7f, 1f, 1f);
        SetColor(fixture, "TitleColor", 0.5f, 0.8f, 0.9f, 1f);
        SetColor(fixture, "TileColor", 0.8f, 0.9f, 1f, 1f);
        fixture.ResourceChanger.GetField("ResourcePackName")!.SetValue(null, "Jongyeol");
        Assert.That(adapter.Refresh(out refreshError), Is.True, refreshError);
        Assert.Multiple(() =>
        {
            Assert.That(published!.SessionGeneration, Is.EqualTo(23));
            Assert.That(published.ManagedSource, Is.True);
            Assert.That(published.PlanetColor, Is.EqualTo(
                new PcCompatResourceColor(0.6f, 0.7f, 1f, 1f)));
            Assert.That(published.TitleColor.R, Is.EqualTo(0.5f));
            Assert.That(published.TileColor.G, Is.EqualTo(0.9f));
            Assert.That(published.ResourcePackName, Is.EqualTo("Jongyeol"));
        });
    }

    private static Fixture CreateFixture()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName("PcCompat.ResourceChanger.Fixture." + Guid.NewGuid().ToString("N")),
            AssemblyBuilderAccess.Run);
        var module = assembly.DefineDynamicModule("fixture");
        var settingBuilder = module.DefineType(
            "JipperResourcePack.ResourceChangerSettingFixture",
            TypeAttributes.Public | TypeAttributes.Class);
        var changeRabbitBuilder = settingBuilder.DefineField("ChangeRabbit", typeof(bool), FieldAttributes.Public);
        var changeBallBuilder = settingBuilder.DefineField("ChangeBallColor", typeof(bool), FieldAttributes.Public);
        var changeTileBuilder = settingBuilder.DefineField("ChangeTileColor", typeof(bool), FieldAttributes.Public);
        var settingType = settingBuilder.CreateType()!;

        var colorBuilder = module.DefineType(
            "JipperResourcePack.ResourceColorFixture",
            TypeAttributes.Public | TypeAttributes.Class);
        colorBuilder.DefineField("r", typeof(float), FieldAttributes.Public);
        colorBuilder.DefineField("g", typeof(float), FieldAttributes.Public);
        colorBuilder.DefineField("b", typeof(float), FieldAttributes.Public);
        colorBuilder.DefineField("a", typeof(float), FieldAttributes.Public);
        var colorType = colorBuilder.CreateType()!;

        var changerBuilder = module.DefineType(
            "JipperResourcePack.ResourceChanger",
            TypeAttributes.Public | TypeAttributes.Class);
        changerBuilder.DefineField("_settings", settingType, FieldAttributes.Private | FieldAttributes.Static);
        changerBuilder.DefineField("PlanetColor", colorType, FieldAttributes.Public | FieldAttributes.Static);
        changerBuilder.DefineField("TitleColor", colorType, FieldAttributes.Public | FieldAttributes.Static);
        changerBuilder.DefineField("TileColor", colorType, FieldAttributes.Public | FieldAttributes.Static);
        changerBuilder.DefineField("ResourcePackName", typeof(string), FieldAttributes.Public | FieldAttributes.Static);
        var changerType = changerBuilder.CreateType()!;

        var settings = Activator.CreateInstance(settingType)!;
        var changeRabbit = settingType.GetField(changeRabbitBuilder.Name)!;
        var changeBall = settingType.GetField(changeBallBuilder.Name)!;
        var changeTile = settingType.GetField(changeTileBuilder.Name)!;
        changeRabbit.SetValue(settings, true);
        changeBall.SetValue(settings, true);
        changeTile.SetValue(settings, true);
        changerType.GetField("_settings", BindingFlags.Static | BindingFlags.NonPublic)!
            .SetValue(null, settings);
        var fixture = new Fixture(changerType, colorType, settings, changeRabbit, changeBall, changeTile);
        SetColor(fixture, "PlanetColor", 0.1f, 0.2f, 0.3f, 1f);
        SetColor(fixture, "TitleColor", 0.2f, 0.3f, 0.4f, 1f);
        SetColor(fixture, "TileColor", 0.3f, 0.4f, 0.5f, 1f);
        changerType.GetField("ResourcePackName")!.SetValue(null, "Default");
        return fixture;
    }

    private static void SetColor(
        Fixture fixture,
        string fieldName,
        float r,
        float g,
        float b,
        float a)
    {
        var color = Activator.CreateInstance(fixture.Color)!;
        fixture.Color.GetField("r")!.SetValue(color, r);
        fixture.Color.GetField("g")!.SetValue(color, g);
        fixture.Color.GetField("b")!.SetValue(color, b);
        fixture.Color.GetField("a")!.SetValue(color, a);
        fixture.ResourceChanger.GetField(fieldName)!.SetValue(null, color);
    }

    private sealed record Fixture(
        Type ResourceChanger,
        Type Color,
        object Settings,
        FieldInfo ChangeRabbit,
        FieldInfo ChangeBallColor,
        FieldInfo ChangeTileColor);
}
