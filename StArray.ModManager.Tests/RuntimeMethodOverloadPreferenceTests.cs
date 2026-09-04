using StArray.ModManager.RuntimeAbstractions;

namespace StArray.ModManager.Tests;

public sealed class RuntimeMethodOverloadPreferenceTests
{
    [Test]
    public void UnityTexture2DFourArgumentConstructorUsesLegacyTextureFormatAbi()
    {
        var signature = RuntimeMethodOverloadPreferences.Resolve(
            "UnityEngine",
            "Texture2D",
            ".ctor",
            4);

        Assert.That(signature, Is.EqualTo(new[]
        {
            "System.Int32",
            "System.Int32",
            "UnityEngine.TextureFormat",
            "System.Boolean"
        }));
    }

    [Test]
    public void UnitySpriteFourArgumentCreateUsesPublicTextureFirstAbi()
    {
        var signature = RuntimeMethodOverloadPreferences.Resolve(
            "UnityEngine",
            "Sprite",
            "Create",
            4);

        Assert.That(signature, Is.EqualTo(new[]
        {
            "UnityEngine.Texture2D",
            "UnityEngine.Rect",
            "UnityEngine.Vector2",
            "System.Single"
        }));
    }

    [Test]
    public void UnrelatedCountOnlyLookupKeepsRuntimeDefaultResolution()
    {
        Assert.That(
            RuntimeMethodOverloadPreferences.Resolve(
                "UnityEngine",
                "Sprite",
                "Create",
                5),
            Is.Null);
    }

    [Test]
    public void LegacyXPerfectMeterColorLookupUsesExplicitThreeArgumentCompatibility()
    {
        var compatibility = RuntimeMethodOverloadPreferences.ResolveCompatibility(
            string.Empty,
            "scrHitErrorMeter",
            "CalculateTickColor",
            2);

        Assert.Multiple(() =>
        {
            Assert.That(compatibility, Is.Not.Null);
            Assert.That(compatibility!.Value.ActualParameterTypes, Is.EqualTo(new[]
            {
                "System.Single",
                "System.Single",
                "scrFloor"
            }));
            Assert.That(
                compatibility.Value.Kind,
                Is.EqualTo(RuntimeMethodCompatibilityKind.CalculateTickColorWithoutHitFloor));
        });
    }
}
