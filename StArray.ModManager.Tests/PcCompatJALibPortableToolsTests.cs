using System.IO.Compression;
using System.Text;
using JALib.Data;
using JALib.Tools;

namespace StArray.ModManager.Tests;

public sealed class PcCompatJALibPortableToolsTests
{
    [Test]
    public void RawFileZipAndCompressionRoundTripPreservesTreeAndBytes()
    {
        using var root = new RawFile("root", [
            new RawFile("plain.txt", Encoding.UTF8.GetBytes("plain")),
            new RawFile("nested", [
                new RawFile("value.bin", [1, 2, 3, 4])
            ])
        ]);

        var zipped = Zipper.Zip(root);
        using var unpacked = Zipper.Unzip("root", zipped);
        var gzip = Zipper.Gzip(zipped);
        var deflate = Zipper.Deflate(zipped);

        Assert.Multiple(() =>
        {
            Assert.That(unpacked.Files.Select(item => item.Name),
                Is.EquivalentTo(new[] { "plain.txt", "nested" }));
            Assert.That(
                unpacked.Files.Single(item => item.Name == "nested")
                    .Files.Single().Data,
                Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(Zipper.Gunzip(gzip), Is.EqualTo(zipped));
            Assert.That(Zipper.UnDeflate(deflate), Is.EqualTo(zipped));
        });
    }

    [Test]
    public void ZipExtractionRejectsEntryOutsideDestination()
    {
        using var archiveBuffer = new MemoryStream();
        using (var archive = new ZipArchive(archiveBuffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("../escape.txt");
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
            writer.Write("escape");
        }
        var folder = Path.Combine(
            TestContext.CurrentContext.WorkDirectory,
            "pccompat-zip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                Zipper.Unzip(archiveBuffer.ToArray(), folder));
            Assert.That(
                File.Exists(Path.Combine(Path.GetDirectoryName(folder)!, "escape.txt")),
                Is.False);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Test]
    public async Task ProgressStreamTracksSyncAndAsyncReads()
    {
        await using var source = new MemoryStream([1, 2, 3, 4, 5, 6]);
        await using var progress = new ProgressStream(source, source.Length);
        var buffer = new byte[4];

        Assert.That(progress.Read(buffer, 0, 2), Is.EqualTo(2));
        Assert.That(progress.NeedUpdate(out var first), Is.True);
        Assert.That(first, Is.EqualTo(2d / 6d));
        Assert.That(await progress.ReadAsync(buffer.AsMemory(0, 4)), Is.EqualTo(4));

        Assert.Multiple(() =>
        {
            Assert.That(progress.NeedUpdate(out var complete), Is.True);
            Assert.That(complete, Is.EqualTo(1d));
            Assert.That(progress.NeedUpdate(out _), Is.False);
        });
    }
}
