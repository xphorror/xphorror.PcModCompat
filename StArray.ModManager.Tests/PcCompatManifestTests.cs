using Xphorror.PcModCompat;

namespace StArray.ModManager.Tests;

public class PcCompatManifestTests
{
    [Test]
    public void ReadsJAModManifest()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "pccompat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        try
        {
            File.WriteAllText(Path.Combine(dir, "Info.json"), """
                {
                  "Id": "JipperResourcePack",
                  "DisplayName": "JipperResourcePack",
                  "Author": "Jongyeol",
                  "Version": "1.4.8.2",
                  "AssemblyName": "JAMod.Bootstrap.dll",
                  "EntryMethod": "JAMod.Bootstrap.Bootstrap.Setup",
                  "Requirements": ["UnityModManager", "OtherDependency"],
                  "LoadAfter": ["JALib"]
                }
                """);
            File.WriteAllText(Path.Combine(dir, "JAModInfo.json"), """
                {
                  "AssemblyPath": "JipperResourcePack.dll",
                  "ClassName": "JipperResourcePack.Main",
                  "AssemblyRequireModPath": true,
                  "Gid": 1313107549
                }
                """);

            var ok = PcModManifestReader.TryRead(dir, out var manifest, out var error);

            Assert.That(ok, Is.True, error);
            Assert.That(manifest.Kind, Is.EqualTo(PcModKind.JAMod));
            Assert.That(manifest.Id, Is.EqualTo("JipperResourcePack"));
            Assert.That(manifest.DisplayName, Is.EqualTo("JipperResourcePack"));
            Assert.That(manifest.AssemblyName, Is.EqualTo("JAMod.Bootstrap.dll"));
            Assert.That(manifest.EntryMethod, Is.EqualTo("JAMod.Bootstrap.Bootstrap.Setup"));
            Assert.That(manifest.JAModAssemblyPath, Is.EqualTo("JipperResourcePack.dll"));
            Assert.That(manifest.JAModClassName, Is.EqualTo("JipperResourcePack.Main"));
            Assert.That(manifest.JAModLocalizationGid, Is.EqualTo(1313107549));
            Assert.That(manifest.AssemblyRequireModPath, Is.True);
            Assert.That(manifest.Requirements, Is.EqualTo(new[] { "OtherDependency" }));
            Assert.That(manifest.LoadAfter, Is.EqualTo(new[] { "JALib" }));
            Assert.That(manifest.EntryAssemblyPath, Is.EqualTo(Path.Combine(dir, "JAMod.Bootstrap.dll")));
            Assert.That(manifest.JAModAssemblyFullPath, Is.EqualTo(Path.Combine(dir, "JipperResourcePack.dll")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
