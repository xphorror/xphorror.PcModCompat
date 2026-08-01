using System.IO.Compression;
using System.Text;
using JALib.Data;

namespace JALib.Tools;

public static class Zipper
{
    static Zipper()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Encoding = Encoding.GetEncoding(949);
    }

    public static readonly Encoding Encoding;

    public static RawFile[] Unzip(byte[] zipData)
    {
        using var stream = new MemoryStream(zipData, writable: false);
        return Unzip(stream);
    }

    public static RawFile[] Unzip(Stream stream)
        => ReadRawFiles(stream, static name => name, static data => data);

    public static RawFile[] Unzip(
        byte[] zipData,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
    {
        using var stream = new MemoryStream(zipData, writable: false);
        return Unzip(stream, nameChanger, dataChanger);
    }

    public static RawFile[] Unzip(
        Stream stream,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => ReadRawFiles(stream, nameChanger, dataChanger);

    public static RawFile Unzip(string name, byte[] zipData)
        => new(name, Unzip(zipData));
    public static RawFile Unzip(string name, Stream stream)
        => new(name, Unzip(stream));
    public static RawFile Unzip(
        string name,
        byte[] zipData,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => new(name, Unzip(zipData, nameChanger, dataChanger));
    public static RawFile Unzip(
        string name,
        Stream stream,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => new(name, Unzip(stream, nameChanger, dataChanger));

    public static void Unzip(byte[] zipData, string path)
    {
        using var stream = new MemoryStream(zipData, writable: false);
        Unzip(stream, path);
    }

    public static void Unzip(Stream stream, string path)
        => Extract(stream, path, static name => name, null, null, null);

    public static void Unzip(
        byte[] zipData,
        string path,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
    {
        using var stream = new MemoryStream(zipData, writable: false);
        Unzip(stream, path, nameChanger, dataChanger);
    }

    public static void Unzip(
        Stream stream,
        string path,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => Extract(stream, path, nameChanger, dataChanger, null, null);

    public static void Unzip(
        byte[] zipData,
        string path,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
    {
        using var stream = new MemoryStream(zipData, writable: false);
        Unzip(stream, path, nameChanger, writeStream, readStream);
    }

    public static void Unzip(
        Stream stream,
        string path,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
        => Extract(stream, path, nameChanger, null, writeStream, readStream);

    public static void Unzip(string zipPath, string path)
    {
        using var stream = File.OpenRead(zipPath);
        Unzip(stream, path);
    }

    public static void Unzip(
        string zipPath,
        string path,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
    {
        using var stream = File.OpenRead(zipPath);
        Unzip(stream, path, nameChanger, dataChanger);
    }

    public static void Unzip(
        string zipPath,
        string path,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
    {
        using var stream = File.OpenRead(zipPath);
        Unzip(stream, path, nameChanger, writeStream, readStream);
    }

    public static byte[] Zip(IEnumerable<RawFile> files)
    {
        using var stream = new MemoryStream();
        Zip(files, stream);
        return stream.ToArray();
    }

    public static void Zip(IEnumerable<RawFile> files, Stream stream)
        => WriteArchive(files, stream, static name => name, null, null, null);

    public static byte[] Zip(
        IEnumerable<RawFile> files,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
    {
        using var stream = new MemoryStream();
        Zip(files, stream, nameChanger, dataChanger);
        return stream.ToArray();
    }

    public static byte[] Zip(
        IEnumerable<RawFile> files,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
    {
        using var stream = new MemoryStream();
        Zip(files, stream, nameChanger, writeStream, readStream);
        return stream.ToArray();
    }

    public static void Zip(
        IEnumerable<RawFile> files,
        Stream stream,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => WriteArchive(files, stream, nameChanger, dataChanger, null, null);

    public static void Zip(
        IEnumerable<RawFile> files,
        Stream stream,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
        => WriteArchive(files, stream, nameChanger, null, writeStream, readStream);

    public static byte[] Zip(RawFile file)
        => file.IsFolder ? Zip(file.Files) : Zip([file]);
    public static byte[] Zip(
        RawFile file,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
        => file.IsFolder
            ? Zip(file.Files, nameChanger, dataChanger)
            : Zip([file], nameChanger, dataChanger);
    public static byte[] Zip(
        RawFile file,
        Func<string, string> nameChanger,
        Stream writeStream,
        Stream readStream)
        => file.IsFolder
            ? Zip(file.Files, nameChanger, writeStream, readStream)
            : Zip([file], nameChanger, writeStream, readStream);

    public static byte[] Gunzip(byte[] gzipData)
    {
        using var stream = new MemoryStream(gzipData, writable: false);
        return Gunzip(stream);
    }

    public static byte[] Gunzip(Stream stream)
    {
        using var output = GunzipToMemoryStream(stream);
        return output.ToArray();
    }

    public static MemoryStream GunzipToMemoryStream(byte[] gzipData)
    {
        using var stream = new MemoryStream(gzipData, writable: false);
        return GunzipToMemoryStream(stream);
    }

    public static MemoryStream GunzipToMemoryStream(Stream stream)
    {
        var output = new MemoryStream();
        stream.Gunzip(output);
        output.Position = 0;
        return output;
    }

    public static void Gunzip(this Stream stream, Stream input)
    {
        using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
        gzip.CopyTo(input);
    }

    public static void Gunzip(this Stream stream, byte[] gzipData)
    {
        using var input = new MemoryStream(gzipData, writable: false);
        stream.Gunzip(input);
    }

    public static void Gunzip(byte[] gzipData, string path)
    {
        using var stream = new MemoryStream(gzipData, writable: false);
        Gunzip(stream, path);
    }

    public static void Gunzip(Stream stream, string path)
    {
        using var output = File.Create(path);
        stream.Gunzip(output);
    }

    public static byte[] Gzip(
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = GzipToMemoryStream(data, compressionLevel);
        return output.ToArray();
    }

    public static byte[] Gzip(
        Stream stream,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = GzipToMemoryStream(stream, compressionLevel);
        return output.ToArray();
    }

    public static MemoryStream GzipToMemoryStream(
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        output.Gzip(data, compressionLevel);
        output.Position = 0;
        return output;
    }

    public static MemoryStream GzipToMemoryStream(
        Stream stream,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        output.Gzip(stream, compressionLevel);
        output.Position = 0;
        return output;
    }

    public static void Gzip(
        this Stream stream,
        Stream input,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var gzip = new GZipStream(stream, compressionLevel, leaveOpen: true);
        input.CopyTo(gzip);
    }

    public static void Gzip(
        this Stream stream,
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var gzip = new GZipStream(stream, compressionLevel, leaveOpen: true);
        gzip.Write(data);
    }

    public static byte[] UnDeflate(byte[] deflateData)
    {
        using var stream = new MemoryStream(deflateData, writable: false);
        return UnDeflate(stream);
    }

    public static byte[] UnDeflate(Stream stream)
    {
        using var output = UnDeflateToMemoryStream(stream);
        return output.ToArray();
    }

    public static MemoryStream UnDeflateToMemoryStream(byte[] deflateData)
    {
        using var stream = new MemoryStream(deflateData, writable: false);
        return UnDeflateToMemoryStream(stream);
    }

    public static MemoryStream UnDeflateToMemoryStream(Stream stream)
    {
        var output = new MemoryStream();
        stream.UnDeflate(output);
        output.Position = 0;
        return output;
    }

    public static void UnDeflate(this Stream stream, Stream input)
    {
        using var deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
        deflate.CopyTo(input);
    }

    public static void UnDeflate(this Stream stream, byte[] deflateData)
    {
        using var input = new MemoryStream(deflateData, writable: false);
        stream.UnDeflate(input);
    }

    public static byte[] Deflate(
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = DeflateToMemoryStream(data, compressionLevel);
        return output.ToArray();
    }

    public static byte[] Deflate(
        Stream stream,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var output = DeflateToMemoryStream(stream, compressionLevel);
        return output.ToArray();
    }

    public static MemoryStream DeflateToMemoryStream(
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        output.Deflate(data, compressionLevel);
        output.Position = 0;
        return output;
    }

    public static MemoryStream DeflateToMemoryStream(
        Stream stream,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        var output = new MemoryStream();
        output.Deflate(stream, compressionLevel);
        output.Position = 0;
        return output;
    }

    public static void Deflate(
        this Stream stream,
        Stream input,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var deflate = new DeflateStream(stream, compressionLevel, leaveOpen: true);
        input.CopyTo(deflate);
    }

    public static void Deflate(
        this Stream stream,
        byte[] data,
        CompressionLevel compressionLevel = CompressionLevel.Optimal)
    {
        using var input = new MemoryStream(data, writable: false);
        stream.Deflate(input, compressionLevel);
    }

    private static RawFile[] ReadRawFiles(
        Stream stream,
        Func<string, string> nameChanger,
        Func<byte[], byte[]> dataChanger)
    {
        ArgumentNullException.ThrowIfNull(nameChanger);
        ArgumentNullException.ThrowIfNull(dataChanger);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding);
        var roots = new List<RawFile>();
        var folders = new Dictionary<string, RawFile>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            var changedName = NormalizeEntryName(nameChanger(entry.FullName));
            if (changedName.Length == 0)
                continue;
            var parts = changedName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            RawFile? parent = null;
            var folderPath = string.Empty;
            for (var index = 0; index < parts.Length - 1; ++index)
            {
                folderPath = folderPath.Length == 0
                    ? parts[index]
                    : folderPath + "/" + parts[index];
                if (!folders.TryGetValue(folderPath, out var folder))
                {
                    folder = new RawFile(parts[index], Array.Empty<RawFile>());
                    folders.Add(folderPath, folder);
                    (parent?.Files ?? roots).Add(folder);
                }
                parent = folder;
            }
            if (entry.FullName.EndsWith('/') || parts.Length == 0)
                continue;
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var file = new RawFile(parts[^1], dataChanger(buffer.ToArray()));
            (parent?.Files ?? roots).Add(file);
        }
        return roots.ToArray();
    }

    private static void Extract(
        Stream stream,
        string path,
        Func<string, string> nameChanger,
        Func<byte[], byte[]>? dataChanger,
        Stream? writeStream,
        Stream? readStream)
    {
        ArgumentNullException.ThrowIfNull(nameChanger);
        var root = Path.GetFullPath(path);
        Directory.CreateDirectory(root);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, Encoding);
        foreach (var entry in archive.Entries)
        {
            var changedName = NormalizeEntryName(nameChanger(entry.FullName));
            if (changedName.Length == 0)
                continue;
            var target = ConfinePath(root, changedName);
            if (entry.FullName.EndsWith('/'))
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open();
            using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            if (dataChanger != null)
            {
                using var buffer = new MemoryStream();
                source.CopyTo(buffer);
                output.Write(dataChanger(buffer.ToArray()));
            }
            else if (writeStream != null && readStream != null)
            {
                source.CopyTo(writeStream);
                readStream.CopyTo(output);
            }
            else
            {
                source.CopyTo(output);
            }
        }
    }

    private static void WriteArchive(
        IEnumerable<RawFile> files,
        Stream stream,
        Func<string, string> nameChanger,
        Func<byte[], byte[]>? dataChanger,
        Stream? writeStream,
        Stream? readStream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding);
        foreach (var file in files)
            WriteRawFile(file, archive, null, nameChanger, dataChanger, writeStream, readStream);
    }

    private static void WriteRawFile(
        RawFile file,
        ZipArchive archive,
        string? folder,
        Func<string, string> nameChanger,
        Func<byte[], byte[]>? dataChanger,
        Stream? writeStream,
        Stream? readStream)
    {
        var name = folder == null ? file.Name : folder + "/" + file.Name;
        if (file.IsFolder)
        {
            foreach (var child in file.Files)
                WriteRawFile(child, archive, name, nameChanger, dataChanger, writeStream, readStream);
            return;
        }
        var entry = archive.CreateEntry(NormalizeEntryName(nameChanger(name)));
        using var output = entry.Open();
        if (dataChanger != null)
        {
            output.Write(dataChanger(file.Data!));
        }
        else if (writeStream != null && readStream != null)
        {
            writeStream.Write(file.Data!);
            readStream.CopyTo(output);
        }
        else
        {
            output.Write(file.Data!);
        }
    }

    private static string NormalizeEntryName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return name.Replace('\\', '/').TrimStart('/');
    }

    private static string ConfinePath(string root, string entryName)
    {
        var target = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"ZIP entry escapes destination: {entryName}");
        }
        return target;
    }
}
