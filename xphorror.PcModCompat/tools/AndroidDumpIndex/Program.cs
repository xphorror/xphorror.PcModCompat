using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xphorror.PcModCompat.AndroidDumpIndex;

var options = CommandLineOptions.Parse(args);
if (options is null)
    return 2;

var inputPath = Path.GetFullPath(options.InputPath);
var outputPath = Path.GetFullPath(options.OutputPath);
var catalogOutputPath = string.IsNullOrWhiteSpace(options.CatalogOutputPath)
    ? null
    : Path.GetFullPath(options.CatalogOutputPath);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input dump does not exist: {inputPath}");
    return 2;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
if (catalogOutputPath is not null)
{
    if (string.Equals(outputPath, catalogOutputPath, StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("--catalog-output must differ from --output.");
        return 2;
    }
    Directory.CreateDirectory(Path.GetDirectoryName(catalogOutputPath)!);
}
var temporaryPath = outputPath + ".tmp";
var catalogTemporaryPath = catalogOutputPath is null ? null : catalogOutputPath + ".tmp";
try
{
    var inputInfo = new FileInfo(inputPath);
    string sha256;
    using (var stream = File.OpenRead(inputPath))
        sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

    var source = new DumpIndexSource
    {
        Path = inputPath,
        Size = inputInfo.Length,
        Sha256 = sha256
    };
    var parser = new DumpIndexParser(inputPath);
    var images = parser.ReadImages();
    var summary = new DumpIndexSummary();
    var catalogTypes = catalogOutputPath is null ? null : new List<DumpTypeCatalogRecord>();
    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    var writerOptions = new JsonWriterOptions { Indented = options.Pretty };

    await using (var output = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
    await using (var writer = new Utf8JsonWriter(output, writerOptions))
    {
        writer.WriteStartObject();
        writer.WriteString("formatVersion", "xphorror.android-il2cpp-dump-index.v1");
        writer.WritePropertyName("source");
        JsonSerializer.Serialize(writer, source, jsonOptions);
        writer.WritePropertyName("filter");
        JsonSerializer.Serialize(writer, new DumpIndexFilter
        {
            AssemblyName = options.AssemblyName,
            TypeNames = options.TypeNames.OrderBy(name => name, StringComparer.Ordinal).ToArray()
        }, jsonOptions);
        writer.WritePropertyName("images");
        JsonSerializer.Serialize(writer, images, jsonOptions);
        writer.WriteStartArray("types");

        foreach (var type in parser.ReadTypes())
        {
            summary.TypesRead++;
            catalogTypes?.Add(new DumpTypeCatalogRecord(
                type.AssemblyName,
                type.Namespace,
                type.Name,
                type.FullName,
                type.Kind,
                type.Line));
            if (!options.Matches(type))
                continue;

            JsonSerializer.Serialize(writer, type, jsonOptions);
            summary.TypesWritten++;
            summary.FieldsWritten += type.Fields.Count;
            summary.PropertiesWritten += type.Properties.Count;
            summary.MethodsWritten += type.Methods.Count;
            summary.MethodsWithAuditAddress += type.Methods.Count(method => method.Audit is not null);
        }

        writer.WriteEndArray();
        summary.ParseWarningCount = parser.WarningCount;
        writer.WritePropertyName("summary");
        JsonSerializer.Serialize(writer, summary, jsonOptions);
        writer.WritePropertyName("parseWarnings");
        JsonSerializer.Serialize(writer, parser.Warnings, jsonOptions);
        writer.WriteEndObject();
        await writer.FlushAsync();
    }

    File.Move(temporaryPath, outputPath, true);
    if (catalogOutputPath is not null)
    {
        await using (var catalogStream = new FileStream(
                         catalogTemporaryPath!,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         true))
        {
            await JsonSerializer.SerializeAsync(
                catalogStream,
                new DumpTypeCatalog
                {
                    Source = source,
                    Types = catalogTypes!
                },
                jsonOptions);
            await catalogStream.FlushAsync();
        }
        File.Move(catalogTemporaryPath!, catalogOutputPath, true);
    }
    Console.WriteLine(
        $"Indexed {summary.TypesWritten}/{summary.TypesRead} types, {summary.MethodsWritten} methods, " +
        $"warnings={summary.ParseWarningCount} -> {outputPath}");

    if (options.TypeNames.Count != 0 && summary.TypesWritten == 0)
    {
        Console.Error.WriteLine("No requested type matched the Android dump.");
        return 3;
    }

    return parser.WarningCount == 0 ? 0 : 1;
}
catch (DecoderFallbackException exception)
{
    Console.Error.WriteLine($"Input is not valid UTF-8: {exception.Message}");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}
finally
{
    if (File.Exists(temporaryPath))
        File.Delete(temporaryPath);
    if (catalogTemporaryPath is not null && File.Exists(catalogTemporaryPath))
        File.Delete(catalogTemporaryPath);
}

internal sealed class CommandLineOptions
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public string? CatalogOutputPath { get; init; }
    public string? AssemblyName { get; init; }
    public HashSet<string> TypeNames { get; init; } = new(StringComparer.Ordinal);
    public bool Pretty { get; init; }

    public bool Matches(DumpTypeRecord type)
    {
        if (!string.IsNullOrWhiteSpace(AssemblyName) &&
            !string.Equals(NormalizeAssembly(AssemblyName), NormalizeAssembly(type.AssemblyName), StringComparison.OrdinalIgnoreCase))
            return false;

        return TypeNames.Count == 0 || TypeNames.Contains(type.FullName) || TypeNames.Contains(type.Name);
    }

    public static CommandLineOptions? Parse(string[] args)
    {
        string? input = null;
        string? output = null;
        string? catalogOutput = null;
        string? assembly = null;
        var types = new HashSet<string>(StringComparer.Ordinal);
        var pretty = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input" when index + 1 < args.Length:
                    input = args[++index];
                    break;
                case "--output" when index + 1 < args.Length:
                    output = args[++index];
                    break;
                case "--catalog-output" when index + 1 < args.Length:
                    catalogOutput = args[++index];
                    break;
                case "--assembly" when index + 1 < args.Length:
                    assembly = args[++index];
                    break;
                case "--type" when index + 1 < args.Length:
                    types.Add(args[++index]);
                    break;
                case "--type-file" when index + 1 < args.Length:
                    using (var reader = new StreamReader(
                               args[++index],
                               new UTF8Encoding(false, true),
                               true,
                               64 * 1024))
                    {
                        while (reader.ReadLine() is { } line)
                        {
                            var value = line.Trim();
                            if (value.Length != 0 && !value.StartsWith('#'))
                                types.Add(value);
                        }
                    }
                    break;
                case "--pretty":
                    pretty = true;
                    break;
                case "--help" or "-h":
                    PrintUsage();
                    return null;
                default:
                    Console.Error.WriteLine($"Unknown or incomplete argument: {args[index]}");
                    PrintUsage();
                    return null;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            PrintUsage();
            return null;
        }

        return new CommandLineOptions
        {
            InputPath = input,
            OutputPath = output,
            CatalogOutputPath = catalogOutput,
            AssemblyName = assembly,
            TypeNames = types,
            Pretty = pretty
        };
    }

    private static string NormalizeAssembly(string value)
        => value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static void PrintUsage()
    {
        Console.WriteLine(
            "AndroidDumpIndex --input <dump.cs> --output <index.json> " +
            "[--catalog-output <catalog.json>] [--assembly <name>] " +
            "[--type <full-name>] [--type-file <path>] [--pretty]");
    }
}
