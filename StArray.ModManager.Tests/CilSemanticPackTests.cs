using System.IO.Compression;
using System.Security.Cryptography;

namespace StArray.ModManager.Tests;

public sealed class CilSemanticPackTests
{
    [Test]
    public void MethodStreamAndArchiveAreDeterministic()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "starray-cil-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var assembly = typeof(CilSemanticPackTests).Assembly.Location;
            var methodsA = Path.Combine(root, "a.jsonl");
            var methodsB = Path.Combine(root, "b.jsonl");
            var summariesA = SemanticPackBuilder.WriteMethodStream([assembly], methodsA);
            var summariesB = SemanticPackBuilder.WriteMethodStream([assembly], methodsB);
            var manifest = new byte[] { (byte)'{', (byte)'}' };
            var archiveA = Path.Combine(root, "a.zip");
            var archiveB = Path.Combine(root, "b.zip");
            SemanticPackBuilder.WriteArchive(archiveA, manifest, methodsA);
            SemanticPackBuilder.WriteArchive(archiveB, manifest, methodsB);

            Assert.Multiple(() =>
            {
                Assert.That(Hash(methodsB), Is.EqualTo(Hash(methodsA)));
                Assert.That(Hash(archiveB), Is.EqualTo(Hash(archiveA)));
                Assert.That(summariesA.Single().Identity,
                    Is.EqualTo(summariesB.Single().Identity));
                Assert.That(summariesA.Single().MethodBodyCount, Is.GreaterThan(0));
                Assert.That(summariesA.Single().InstructionCount, Is.GreaterThan(0));
            });

            using var archive = ZipFile.OpenRead(archiveA);
            Assert.That(archive.Entries.Select(entry => entry.FullName),
                Is.EqualTo(new[] { "manifest.json", "methods.jsonl" }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public void SourceTreeHashUsesUtf8ContentAndRelativePath()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "starray-cil-source-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "nested"));
        try
        {
            File.WriteAllText(
                Path.Combine(root, "A.cs"),
                "class A { }\n",
                new System.Text.UTF8Encoding(false, true));
            File.WriteAllText(
                Path.Combine(root, "nested", "B.cs"),
                "class B { string Text = \"中文\"; }\n",
                new System.Text.UTF8Encoding(false, true));

            var first = SourceTreeIdentity.Read(root);
            var second = SourceTreeIdentity.Read(root);
            Assert.Multiple(() =>
            {
                Assert.That(first, Is.EqualTo(second));
                Assert.That(first.FileCount, Is.EqualTo(2));
                Assert.That(first.Sha256, Has.Length.EqualTo(64));
            });
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
