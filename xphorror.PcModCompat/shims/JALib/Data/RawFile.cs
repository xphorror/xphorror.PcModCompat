namespace JALib.Data;

public class RawFile : IDisposable
{
    private bool _disposed;

    public RawFile(string name, byte[] data)
    {
        Name = name;
        Data = data;
        Files = [];
    }

    public RawFile(string filePath)
    {
        Name = Path.GetFileName(Path.TrimEndingDirectorySeparator(filePath));
        Files = [];
        if (File.Exists(filePath))
        {
            Data = File.ReadAllBytes(filePath);
            return;
        }
        if (!Directory.Exists(filePath))
            throw new FileNotFoundException("RawFile source does not exist.", filePath);
        foreach (var path in Directory.EnumerateFileSystemEntries(filePath))
            Files.Add(new RawFile(path));
    }

    public RawFile(string name, RawFile[] files)
    {
        Name = name;
        Files = new List<RawFile>(files ?? throw new ArgumentNullException(nameof(files)));
    }

    public string Name { get; private set; }
    public byte[]? Data { get; private set; }
    public List<RawFile> Files { get; private set; }
    public bool IsFolder => Data == null;

    public void Save(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var target = Path.Combine(path, Name);
        if (IsFolder)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Files)
                file.Save(target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        File.WriteAllBytes(target, Data!);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var file in Files)
            file.Dispose();
        Files.Clear();
        Data = null;
        GC.SuppressFinalize(this);
    }
}
