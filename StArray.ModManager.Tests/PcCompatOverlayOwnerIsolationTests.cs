using System.Collections;
using System.Globalization;
using System.Reflection;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

[NonParallelizable]
public sealed class PcCompatOverlayOwnerIsolationTests
{
    [SetUp]
    public void SetUp()
        => PcCompatOverlayRuntime.ClearProvider();

    [TearDown]
    public void TearDown()
        => PcCompatOverlayRuntime.ClearProvider();

    [Test]
    public void OwnersReceiveSeparateSnapshotObjects()
    {
        var shared = new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true,
            Generation = 42,
            Visible = true,
            TileBpm = 180f,
            Kps = 7.5f
        };
        PcCompatOverlayRuntime.RegisterProvider(() => shared);

        var first = PcCompatOverlayRuntime.Snapshot("owner-a");
        var second = PcCompatOverlayRuntime.Snapshot("owner-b");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.Not.SameAs(shared));
            Assert.That(second, Is.Not.SameAs(shared));
            Assert.That(first, Is.Not.SameAs(second));
            Assert.That(first.Generation, Is.EqualTo(42));
            Assert.That(second.TileBpm, Is.EqualTo(180f));
            Assert.That(second.Kps, Is.EqualTo(7.5f));
            Assert.That(GetProjectionCount(), Is.EqualTo(2));
        });
    }

    [Test]
    public void OwnerProviderIsSelectedBeforeLegacyProvider()
    {
        var legacyCalls = 0;
        var ownerCalls = new List<string>();
        PcCompatOverlayRuntime.RegisterProvider(
            () =>
            {
                legacyCalls++;
                return new PcCompatOverlaySnapshot
                {
                    ProviderAvailable = true,
                    Generation = 1
                };
            },
            ownerProvider: ownerId =>
            {
                ownerCalls.Add(ownerId);
                return new PcCompatOverlaySnapshot
                {
                    ProviderAvailable = true,
                    Generation = ownerId == "owner-a" ? 10u : 20u
                };
            });

        var first = PcCompatOverlayRuntime.Snapshot("owner-a");
        var second = PcCompatOverlayRuntime.Snapshot("owner-b");

        Assert.Multiple(() =>
        {
            Assert.That(first.Generation, Is.EqualTo(10));
            Assert.That(second.Generation, Is.EqualTo(20));
            Assert.That(ownerCalls, Is.EqualTo(new[] { "owner-a", "owner-b" }));
            Assert.That(legacyCalls, Is.Zero);
        });
    }

    [Test]
    public void RemovingOneOwnerDoesNotClearAnotherProjection()
    {
        PcCompatOverlayRuntime.RegisterProvider(() => new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true,
            Generation = 9
        });
        PcCompatOverlayRuntime.Snapshot("owner-a");
        PcCompatOverlayRuntime.Snapshot("owner-b");

        PcCompatOverlayRuntime.RemoveOwner("owner-a");

        Assert.Multiple(() =>
        {
            Assert.That(GetProjectionCount(), Is.EqualTo(1));
            Assert.That(PcCompatOverlayRuntime.Snapshot("owner-b").Generation, Is.EqualTo(9));
            Assert.That(GetProjectionCount(), Is.EqualTo(1));
        });
    }

    [Test]
    public void OwnerCloneCopiesEveryStoredSnapshotProperty()
    {
        var shared = new PcCompatOverlaySnapshot();
        var writableProperties = typeof(PcCompatOverlaySnapshot)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.SetMethod != null)
            .ToArray();

        foreach (var property in writableProperties)
            property.SetValue(shared, CreateNonDefaultValue(property.PropertyType));
        PcCompatOverlayRuntime.RegisterProvider(() => shared);

        var clone = PcCompatOverlayRuntime.Snapshot("complete-clone");

        Assert.Multiple(() =>
        {
            Assert.That(clone, Is.Not.SameAs(shared));
            foreach (var property in writableProperties)
            {
                Assert.That(
                    property.GetValue(clone),
                    Is.EqualTo(property.GetValue(shared)),
                    $"{property.Name} was omitted from the owner projection clone.");
            }
        });
    }

    [Test]
    public void ClearingProviderAlsoClearsAllOwnerProjections()
    {
        PcCompatOverlayRuntime.RegisterProvider(() => new PcCompatOverlaySnapshot
        {
            ProviderAvailable = true
        });
        PcCompatOverlayRuntime.Snapshot("owner-a");
        PcCompatOverlayRuntime.Snapshot("owner-b");

        PcCompatOverlayRuntime.ClearProvider();

        Assert.Multiple(() =>
        {
            Assert.That(GetProjectionCount(), Is.Zero);
            Assert.That(PcCompatOverlayRuntime.Snapshot("owner-a").ProviderAvailable, Is.False);
        });
    }

    private static object CreateNonDefaultValue(Type type)
    {
        if (type == typeof(bool))
            return true;
        if (type == typeof(uint))
            return 0x1234u;
        if (type == typeof(int))
            return 1234;
        if (type == typeof(long))
            return 1234L;
        if (type == typeof(float))
            return 1234.5f;
        if (type == typeof(double))
            return 1234.5d;
        // Enum-typed snapshot fields carry validity masks, so a zero value is exactly the value a
        // dropped field would also produce. Pick a defined non-zero member instead.
        if (type.IsEnum)
        {
            foreach (var candidate in Enum.GetValues(type))
            {
                if (Convert.ToUInt64(candidate, CultureInfo.InvariantCulture) != 0)
                    return candidate;
            }
            throw new InvalidOperationException($"Enum {type} has no non-zero member");
        }
        throw new InvalidOperationException($"Unhandled snapshot property type: {type}");
    }

    private static int GetProjectionCount()
    {
        var field = typeof(PcCompatOverlayRuntime).GetField(
            "OwnerProjections",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.That(field, Is.Not.Null);
        return ((IDictionary)field!.GetValue(null)!).Count;
    }
}
