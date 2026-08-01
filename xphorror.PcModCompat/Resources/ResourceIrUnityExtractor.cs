using System.Security.Cryptography;
using AssetsTools.NET;
using AssetsTools.NET.Extra;

namespace Xphorror.PcModCompat.Resources;

public sealed record ResourceIrGameObjectChildShape(
    string Name,
    IReadOnlyList<string> ComponentKinds);

public sealed record ResourceIrGameObjectShape(
    string Name,
    IReadOnlyList<string> ComponentKinds,
    IReadOnlyList<ResourceIrGameObjectChildShape> Children);

public static class ResourceIrUnityExtractor
{
    private const int MaxDecodedTextureBytes = 256 * 1024 * 1024;
    private const int MaxPrefabNodes = 128;
    private const int MaxPrefabDepth = 32;
    private const int MaxPrefabComponentsPerNode = 16;

    public static ResourceIrDocument Enrich(
        ResourceCompileReport report,
        ResourceIrDocument document,
        string payloadOutputDirectory)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadOutputDirectory);
        var outputDirectory = Path.GetFullPath(payloadOutputDirectory);
        if (Directory.Exists(outputDirectory))
            Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);

        var updated = document.Assets.ToDictionary(asset => asset.Id, StringComparer.Ordinal);
        var payloads = new Dictionary<string, ResourceIrPayload>(StringComparer.Ordinal);
        var warnings = document.Warnings.ToList();
        var candidates = report.Candidates.ToDictionary(
            candidate => candidate.Sha256Hex,
            StringComparer.OrdinalIgnoreCase);

        foreach (var irBundle in document.Bundles.Where(bundle => bundle.SelectedForRuntime))
        {
            if (!candidates.TryGetValue(irBundle.CandidateSha256Hex, out var candidate))
            {
                warnings.Add($"Resource IR extraction candidate is missing: {irBundle.CandidateSha256Hex}");
                continue;
            }
            try
            {
                ExtractBundle(candidate, irBundle, updated, payloads, outputDirectory, warnings);
            }
            catch (Exception ex)
            {
                warnings.Add(
                    $"Resource IR extraction failed bundle={irBundle.SourceRelativePath}: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        return document with
        {
            Assets = document.Assets.Select(asset => updated[asset.Id]).ToArray(),
            Payloads = payloads.Values.OrderBy(payload => payload.Id, StringComparer.Ordinal).ToArray(),
            Warnings = warnings.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray()
        };
    }

    public static ResourceIrGameObjectShape InspectGameObject(
        string bundlePath,
        string assetsFileName,
        long pathId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundlePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsFileName);
        var fullPath = Path.GetFullPath(bundlePath);
        using var source = File.OpenRead(fullPath);
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(source, fullPath);
            var files = LoadSerializedFiles(manager, bundle);
            if (!files.TryGetValue(assetsFileName, out var file))
                throw new InvalidDataException($"Serialized assets file was not loaded: {assetsFileName}");
            var gameObject = ReadAssetByPath(manager, file, pathId);
            return ReadGameObjectShape(manager, file, gameObject);
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static void ExtractBundle(
        ResourceCandidateIndex candidate,
        ResourceIrBundle irBundle,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ICollection<string> warnings)
    {
        using var source = File.OpenRead(candidate.SourcePath);
        var manager = new AssetsManager();
        try
        {
            var bundle = manager.LoadBundleFile(source, candidate.SourcePath);
            var files = LoadSerializedFiles(manager, bundle);
            var bundleAssets = assets.Values
                .Where(asset => asset.BundleId == irBundle.Id)
                .ToDictionary(
                    asset => MakeSourceKey(asset.AssetsFileName, asset.PathId),
                    StringComparer.OrdinalIgnoreCase);
            var required = bundleAssets.Values.Where(asset => asset.RequiredByMod).ToArray();
            var processing = new HashSet<string>(StringComparer.Ordinal);
            foreach (var asset in required)
            {
                try
                {
                    if (asset.MaterializationKind != ResourceIrMaterializationKind.MetadataOnly)
                        continue;
                    if (asset.ExpectedType == "TMPro.TMP_FontAsset")
                    {
                        try
                        {
                            MaterializeTmpFont(
                                bundle,
                                manager,
                                files,
                                asset,
                                bundleAssets,
                                assets,
                                payloads,
                                outputDirectory,
                                processing);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add(
                                $"Static TMP font reconstruction unavailable asset={asset.Name}: " +
                                ex.GetType().Name + ": " + ex.Message + "; capability fallback selected.");
                            if (!TryMaterializeCapabilityFallback(manager, files, asset, assets))
                                throw;
                        }
                    }
                    else if (TryMaterializeCapabilityFallback(manager, files, asset, assets))
                    {
                        continue;
                    }
                    else if (asset.ExpectedType == "UnityEngine.Texture2D")
                    {
                        MaterializeTexture(manager, bundle, files, asset, assets, payloads, outputDirectory);
                    }
                    else if (asset.ExpectedType == "UnityEngine.Sprite")
                    {
                        MaterializeSprite(
                            bundle,
                            manager,
                            files,
                            asset,
                            bundleAssets,
                            assets,
                            payloads,
                            outputDirectory,
                            processing);
                    }
                    else if (asset.ExpectedType == "UnityEngine.Material")
                    {
                        MaterializeMaterial(
                            bundle,
                            manager,
                            files,
                            asset,
                            bundleAssets,
                            assets,
                            payloads,
                            outputDirectory,
                            processing);
                    }
                    else if (asset.ExpectedType == "UnityEngine.GameObject")
                    {
                        MaterializePrefabGraph(
                            bundle,
                            manager,
                            files,
                            asset,
                            bundleAssets,
                            assets,
                            payloads,
                            outputDirectory,
                            processing);
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"Resource asset extraction failed bundle={irBundle.SourceRelativePath} " +
                        $"asset={asset.Name} type={asset.ExpectedType}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            manager.UnloadAll(true);
        }
    }

    private static bool TryMaterializeCapabilityFallback(
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IDictionary<string, ResourceIrAsset> assets)
    {
        if (asset.ExpectedType == "TMPro.TMP_FontAsset")
        {
            var (_, field) = ReadAsset(manager, files, asset);
            var creation = field["m_CreationSettings"];
            var sequence = creation.IsDummy || creation["characterSequence"].IsDummy
                ? string.Empty
                : creation["characterSequence"].AsString;
            var style = field["m_FaceInfo"].IsDummy || field["m_FaceInfo"]["m_StyleName"].IsDummy
                ? string.Empty
                : field["m_FaceInfo"]["m_StyleName"].AsString;
            var stableId = SelectFontCapability(sequence, style);
            assets[asset.Id] = asset with
            {
                MaterializationKind = ResourceIrMaterializationKind.CapabilityReference,
                Compatibility = ResourceIrCompatibility.Compatible,
                CapabilityStableId = stableId,
                CloneCapabilityAsset = true,
                Tags = asset.Tags.Concat(["semantic-font-v1", stableId]).Distinct(StringComparer.Ordinal).ToArray()
            };
            return true;
        }
        return false;
    }

    private static string SelectFontCapability(string characterSequence, string style)
    {
        if (SequenceOverlaps(characterSequence, 0xAC00, 0xD7A3))
            return "font.adofai.korean";
        if (SequenceOverlaps(characterSequence, 0x3040, 0x30FF))
            return "font.adofai.japanese";
        if (SequenceOverlaps(characterSequence, 0x3400, 0x9FFF) ||
            SequenceOverlaps(characterSequence, 0x3100, 0x312F))
            return "font.adofai.cjk";
        return style.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            ? "font.adofai.inter_bold"
            : "font.tmp.liberation_sans";
    }

    private static bool SequenceOverlaps(string sequence, int minimum, int maximum)
    {
        foreach (var token in sequence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var bounds = token.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(bounds[0], out var start))
                continue;
            var end = bounds.Length == 2 && int.TryParse(bounds[1], out var parsedEnd)
                ? parsedEnd
                : start;
            if (start <= maximum && end >= minimum)
                return true;
        }
        return false;
    }

    private static void MaterializeTmpFont(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing)
    {
        if (assets[asset.Id].MaterializationKind == ResourceIrMaterializationKind.TmpFontFromAtlas)
            return;
        if (!processing.Add(asset.Id))
            throw new InvalidDataException($"TMP font dependency cycle detected: {asset.Id}");
        try
        {
            var (_, field) = ReadAsset(manager, files, asset);
            var face = Required(field, "m_FaceInfo");
            var glyphFields = EnumerateArray(Required(field, "m_GlyphTable"));
            var characterFields = EnumerateArray(Required(field, "m_CharacterTable"));
            if (glyphFields.Count == 0 || characterFields.Count == 0)
                throw new InvalidDataException("Static TMP font has no glyph or character table.");

            var featureTable = field["m_FontFeatureTable"];
            if (!featureTable.IsDummy)
            {
                RequireEmptyCollection(featureTable, "m_GlyphPairAdjustmentRecords");
                RequireEmptyCollection(featureTable, "m_LigatureSubstitutionRecords");
                RequireEmptyCollection(featureTable, "m_MultipleSubstitutionRecords");
                RequireEmptyCollection(featureTable, "m_MarkToBaseAdjustmentRecords");
                RequireEmptyCollection(featureTable, "m_MarkToMarkAdjustmentRecords");
            }

            var glyphs = glyphFields.Select(ReadTmpGlyph).ToArray();
            var characters = characterFields.Select(ReadTmpCharacter).ToArray();
            var payloadBytes = ResourceIrTmpFontPayloadBinary.Write(glyphs, characters);
            _ = ResourceIrTmpFontPayloadBinary.Read(payloadBytes);

            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var atlasTextureIds = new List<string>();
            foreach (var pointer in EnumerateArray(Required(field, "m_AtlasTextures")))
            {
                var texture = RequireLocalDependency(
                    pointer,
                    asset,
                    bundleAssets,
                    expectedSourceType: "Texture2D",
                    dependencyLabel: "TMP atlas");
                MaterializeTexture(manager, bundle, files, texture, assets, payloads, outputDirectory);
                atlasTextureIds.Add(texture.Id);
                dependencies.Add(texture.Id);
            }
            if (atlasTextureIds.Count == 0)
                throw new InvalidDataException("Static TMP font has no atlas texture.");

            var materialPointer = field["material"];
            if (materialPointer.IsDummy)
                materialPointer = Required(field, "m_Material");
            var material = RequireLocalDependency(
                materialPointer,
                asset,
                bundleAssets,
                expectedSourceType: "Material",
                dependencyLabel: "TMP material");
            MaterializeMaterial(
                bundle,
                manager,
                files,
                material,
                bundleAssets,
                assets,
                payloads,
                outputDirectory,
                processing);
            dependencies.Add(material.Id);

            var payloadId = "payload." + asset.Id[4..] + ".tmpfont";
            var fileName = asset.Id + ".tmpfont";
            File.WriteAllBytes(Path.Combine(outputDirectory, fileName), payloadBytes);
            payloads[payloadId] = new ResourceIrPayload
            {
                Id = payloadId,
                Kind = ResourceIrTmpFontPayloadBinary.PayloadKind,
                RelativePath = "resource_ir_blobs/" + fileName,
                Sha256Hex = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
                Length = payloadBytes.Length
            };

            var creation = field["m_CreationSettings"];
            var sequence = creation.IsDummy || creation["characterSequence"].IsDummy
                ? string.Empty
                : creation["characterSequence"].AsString;
            var styleName = Required(face, "m_StyleName").AsString;
            var shellCapability = SelectFontCapability(sequence, styleName);
            assets[asset.Id] = asset with
            {
                MaterializationKind = ResourceIrMaterializationKind.TmpFontFromAtlas,
                Compatibility = ResourceIrCompatibility.Compatible,
                CapabilityStableId = shellCapability,
                CloneCapabilityAsset = true,
                PayloadId = payloadId,
                DependencyIds = dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                TmpFont = new ResourceIrTmpFontInfo
                {
                    Face = new ResourceIrTmpFontFaceInfo
                    {
                        FaceIndex = ReadInt(face, "m_FaceIndex", 0),
                        FamilyName = Required(face, "m_FamilyName").AsString,
                        StyleName = styleName,
                        PointSize = ReadFloat(face, "m_PointSize", 0f),
                        Scale = ReadFloat(face, "m_Scale", 1f),
                        UnitsPerEm = ReadInt(face, "m_UnitsPerEM", 0),
                        LineHeight = ReadFloat(face, "m_LineHeight", 0f),
                        AscentLine = ReadFloat(face, "m_AscentLine", 0f),
                        CapLine = ReadFloat(face, "m_CapLine", 0f),
                        MeanLine = ReadFloat(face, "m_MeanLine", 0f),
                        Baseline = ReadFloat(face, "m_Baseline", 0f),
                        DescentLine = ReadFloat(face, "m_DescentLine", 0f),
                        SuperscriptOffset = ReadFloat(face, "m_SuperscriptOffset", 0f),
                        SuperscriptSize = ReadFloat(face, "m_SuperscriptSize", 0f),
                        SubscriptOffset = ReadFloat(face, "m_SubscriptOffset", 0f),
                        SubscriptSize = ReadFloat(face, "m_SubscriptSize", 0f),
                        UnderlineOffset = ReadFloat(face, "m_UnderlineOffset", 0f),
                        UnderlineThickness = ReadFloat(face, "m_UnderlineThickness", 0f),
                        StrikethroughOffset = ReadFloat(face, "m_StrikethroughOffset", 0f),
                        StrikethroughThickness = ReadFloat(face, "m_StrikethroughThickness", 0f),
                        TabWidth = ReadFloat(face, "m_TabWidth", 0f)
                    },
                    MaterialAssetId = material.Id,
                    AtlasTextureAssetIds = atlasTextureIds,
                    AtlasTextureIndex = ReadInt(field, "m_AtlasTextureIndex", 0),
                    AtlasPopulationMode = ReadInt(field, "m_AtlasPopulationMode", 0),
                    MultiAtlasTexturesEnabled = ReadBool(field, "m_IsMultiAtlasTexturesEnabled", false),
                    AtlasWidth = ReadInt(field, "m_AtlasWidth", 0),
                    AtlasHeight = ReadInt(field, "m_AtlasHeight", 0),
                    AtlasPadding = ReadInt(field, "m_AtlasPadding", 0),
                    AtlasRenderMode = ReadInt(field, "m_AtlasRenderMode", 0),
                    NormalStyle = ReadFloat(field, "normalStyle", 0f),
                    NormalSpacingOffset = ReadFloat(field, "normalSpacingOffset", 0f),
                    BoldStyle = ReadFloat(field, "boldStyle", 0f),
                    BoldSpacing = ReadFloat(field, "boldSpacing", 0f),
                    ItalicStyle = ReadInt(field, "italicStyle", 0),
                    TabSize = ReadInt(field, "tabSize", 0),
                    GlyphCount = glyphs.Length,
                    CharacterCount = characters.Length
                },
                Tags = asset.Tags.Concat([
                        "tmp-font-static-v1",
                        shellCapability,
                        $"glyphs:{glyphs.Length}",
                        $"characters:{characters.Length}"])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }
        finally
        {
            processing.Remove(asset.Id);
        }
    }

    private static ResourceIrTmpFontGlyph ReadTmpGlyph(AssetTypeValueField field)
    {
        var metrics = Required(field, "m_Metrics");
        var rect = Required(field, "m_GlyphRect");
        return new ResourceIrTmpFontGlyph(
            Required(field, "m_Index").AsUInt,
            Required(metrics, "m_Width").AsFloat,
            Required(metrics, "m_Height").AsFloat,
            Required(metrics, "m_HorizontalBearingX").AsFloat,
            Required(metrics, "m_HorizontalBearingY").AsFloat,
            Required(metrics, "m_HorizontalAdvance").AsFloat,
            Required(rect, "m_X").AsInt,
            Required(rect, "m_Y").AsInt,
            Required(rect, "m_Width").AsInt,
            Required(rect, "m_Height").AsInt,
            Required(field, "m_Scale").AsFloat,
            Required(field, "m_AtlasIndex").AsInt,
            ReadInt(field, "m_ClassDefinitionType", 0));
    }

    private static ResourceIrTmpFontCharacter ReadTmpCharacter(AssetTypeValueField field)
        => new(
            Required(field, "m_Unicode").AsUInt,
            Required(field, "m_GlyphIndex").AsUInt,
            Required(field, "m_Scale").AsFloat,
            ReadInt(field, "m_ElementType", 1));

    private static ResourceIrAsset RequireLocalDependency(
        AssetTypeValueField pointer,
        ResourceIrAsset owner,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        string expectedSourceType,
        string dependencyLabel)
    {
        if (ReadInt(pointer, "m_FileID", -1) != 0)
            throw new NotSupportedException($"External {dependencyLabel} dependency is unsupported.");
        var pathId = ReadPointerPathId(pointer);
        if (pathId == 0 ||
            !bundleAssets.TryGetValue(MakeSourceKey(owner.AssetsFileName, pathId), out var dependency) ||
            !dependency.SourceType.Equals(expectedSourceType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{dependencyLabel} dependency was not indexed pathId={pathId} type={expectedSourceType}");
        return dependency;
    }

    private static ResourceIrGameObjectShape ReadGameObjectShape(
        AssetsManager manager,
        AssetsFileInstance file,
        AssetTypeValueField gameObject)
    {
        if (!gameObject.TypeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected GameObject, got {gameObject.TypeName}");
        var name = Required(gameObject, "m_Name").AsString;
        var components = ReadComponentFields(manager, file, gameObject);
        var transform = components.FirstOrDefault(field =>
            field.TypeName.Equals("RectTransform", StringComparison.OrdinalIgnoreCase) ||
            field.TypeName.Equals("Transform", StringComparison.OrdinalIgnoreCase));
        if (transform == null)
            throw new InvalidDataException($"GameObject has no Transform: {name}");
        var children = new List<ResourceIrGameObjectChildShape>();
        foreach (var pointer in EnumerateArray(Required(transform, "m_Children")))
        {
            var childTransformPath = ReadPointerPathId(pointer);
            if (childTransformPath == 0)
                continue;
            var childTransform = ReadAssetByPath(manager, file, childTransformPath);
            var childGameObjectPath = ReadPointerPathId(Required(childTransform, "m_GameObject"));
            if (childGameObjectPath == 0)
                continue;
            var childGameObject = ReadAssetByPath(manager, file, childGameObjectPath);
            children.Add(new ResourceIrGameObjectChildShape(
                Required(childGameObject, "m_Name").AsString,
                ReadComponentFields(manager, file, childGameObject)
                    .Select(component => ClassifyComponent(manager, file, component))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }
        return new ResourceIrGameObjectShape(
            name,
            components.Select(component => ClassifyComponent(manager, file, component))
                .Distinct(StringComparer.Ordinal).ToArray(),
            children);
    }

    private static IReadOnlyList<AssetTypeValueField> ReadComponentFields(
        AssetsManager manager,
        AssetsFileInstance file,
        AssetTypeValueField gameObject)
        => ReadComponentEntries(manager, file, gameObject)
            .Select(entry => entry.Field)
            .ToArray();

    private static IReadOnlyList<(long PathId, AssetTypeValueField Field)> ReadComponentEntries(
        AssetsManager manager,
        AssetsFileInstance file,
        AssetTypeValueField gameObject)
        => EnumerateArray(Required(gameObject, "m_Component"))
            .Select(component => component["component"].IsDummy ? component : component["component"])
            .Select(ReadPointerPathId)
            .Where(pathId => pathId != 0)
            .Select(pathId => (pathId, ReadAssetByPath(manager, file, pathId)))
            .ToArray();

    private static string ClassifyComponent(
        AssetsManager manager,
        AssetsFileInstance file,
        AssetTypeValueField component)
    {
        if (component.TypeName.Equals("MonoBehaviour", StringComparison.OrdinalIgnoreCase))
        {
            var scriptPointer = component["m_Script"];
            if (!scriptPointer.IsDummy && ReadInt(scriptPointer, "m_FileID", -1) == 0)
            {
                var scriptPath = ReadPointerPathId(scriptPointer);
                if (scriptPath != 0)
                {
                    var script = ReadAssetByPath(manager, file, scriptPath);
                    var scriptName = script["m_Name"].IsDummy ? string.Empty : script["m_Name"].AsString;
                    if (scriptName == "Image" && !component["m_Sprite"].IsDummy)
                        return "UnityEngine.UI.Image";
                    if (scriptName == "RawImage" && !component["m_Texture"].IsDummy)
                        return "UnityEngine.UI.RawImage";
                    return "MonoBehaviour:" + scriptName;
                }
            }
            return "MonoBehaviour:unknown";
        }
        return component.TypeName;
    }

    private static AssetTypeValueField ReadAssetByPath(
        AssetsManager manager,
        AssetsFileInstance file,
        long pathId)
    {
        var info = file.file.GetAssetInfo(pathId)
                   ?? throw new InvalidDataException($"Referenced asset path id was not found: {pathId}");
        return manager.GetBaseField(file, info, AssetReadFlags.None);
    }

    private static long ReadPointerPathId(AssetTypeValueField pointer)
    {
        if (pointer.IsDummy)
            return 0;
        var path = pointer["m_PathID"];
        if (!path.IsDummy)
            return path.AsLong;
        var nested = pointer["component"];
        return nested.IsDummy || nested["m_PathID"].IsDummy ? 0 : nested["m_PathID"].AsLong;
    }

    private static IReadOnlyList<AssetTypeValueField> EnumerateArray(AssetTypeValueField vector)
    {
        var array = vector["Array"];
        return array.IsDummy ? Array.Empty<AssetTypeValueField>() : array.Children;
    }

    private static Dictionary<string, AssetsFileInstance> LoadSerializedFiles(
        AssetsManager manager,
        BundleFileInstance bundle)
    {
        var result = new Dictionary<string, AssetsFileInstance>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < bundle.file.BlockAndDirInfo.DirectoryInfos.Count; index++)
        {
            var name = bundle.file.BlockAndDirInfo.DirectoryInfos[index].Name ?? string.Empty;
            if (name.EndsWith(".resS", StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(".resource", StringComparison.OrdinalIgnoreCase))
                continue;
            AssetsFileInstance file;
            try { file = manager.LoadAssetsFileFromBundle(bundle, index, false); }
            catch { continue; }
            result[name] = file;
            if (!string.IsNullOrWhiteSpace(file.name))
                result[file.name] = file;
        }
        return result;
    }

    private static void MaterializePrefabGraph(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing)
    {
        if (assets[asset.Id].MaterializationKind == ResourceIrMaterializationKind.PrefabGraph)
            return;
        if (!processing.Add(asset.Id))
            throw new InvalidDataException($"Prefab dependency cycle detected: {asset.Id}");
        try
        {
            var (file, root) = ReadAsset(manager, files, asset);
            if (!root.TypeName.Equals("GameObject", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Expected GameObject, got {root.TypeName}");
            var nodes = new List<ResourceIrPrefabNode>();
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var visited = new HashSet<long>();
            AppendPrefabNode(
                bundle,
                manager,
                files,
                file,
                asset.AssetsFileName,
                asset.PathId,
                root,
                parentIndex: -1,
                expectedTransformPath: null,
                expectedParentTransformPath: 0,
                depth: 0,
                bundleAssets,
                assets,
                payloads,
                outputDirectory,
                processing,
                nodes,
                dependencies,
                visited);
            assets[asset.Id] = asset with
            {
                MaterializationKind = ResourceIrMaterializationKind.PrefabGraph,
                Compatibility = ResourceIrCompatibility.Compatible,
                CapabilityStableId = string.Empty,
                CloneCapabilityAsset = false,
                DependencyIds = dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                Prefab = new ResourceIrPrefabInfo { Nodes = nodes },
                Tags = asset.Tags.Concat(["prefab-graph-v1", "inactive-template-holder-v1"])
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }
        finally
        {
            processing.Remove(asset.Id);
        }
    }

    private static void AppendPrefabNode(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        AssetsFileInstance file,
        string assetsFileName,
        long gameObjectPath,
        AssetTypeValueField gameObject,
        int parentIndex,
        long? expectedTransformPath,
        long expectedParentTransformPath,
        int depth,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing,
        IList<ResourceIrPrefabNode> nodes,
        ISet<string> dependencies,
        ISet<long> visited)
    {
        if (depth > MaxPrefabDepth || nodes.Count >= MaxPrefabNodes)
            throw new NotSupportedException("Prefab graph exceeds node/depth budget.");
        if (!visited.Add(gameObjectPath))
            throw new InvalidDataException($"Prefab graph cycle or duplicate GameObject path: {gameObjectPath}");
        if (ReadInt(gameObject, "m_Tag", 0) != 0)
            throw new NotSupportedException("Prefab graph with a non-default tag is not supported.");

        var components = ReadComponentEntries(manager, file, gameObject);
        if (components.Count is < 1 or > MaxPrefabComponentsPerNode)
            throw new NotSupportedException("Prefab component count exceeds supported bounds.");
        var transforms = components.Where(entry =>
                entry.Field.TypeName.Equals("Transform", StringComparison.OrdinalIgnoreCase) ||
                entry.Field.TypeName.Equals("RectTransform", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (transforms.Length != 1)
            throw new InvalidDataException("Prefab GameObject must have exactly one Transform.");
        var transformEntry = transforms[0];
        if (expectedTransformPath.HasValue && transformEntry.PathId != expectedTransformPath.Value)
            throw new InvalidDataException("Prefab child Transform pointer disagrees with GameObject components.");
        var actualParentPath = ReadPointerPathId(Required(transformEntry.Field, "m_Father"));
        if (actualParentPath != expectedParentTransformPath)
            throw new InvalidDataException("Prefab Transform parent pointer is inconsistent.");

        AssetTypeValueField? canvasRenderer = null;
        AssetTypeValueField? image = null;
        AssetTypeValueField? rawImage = null;
        foreach (var component in components.Where(entry => entry.PathId != transformEntry.PathId))
        {
            switch (ClassifyComponent(manager, file, component.Field))
            {
                case "CanvasRenderer" when canvasRenderer == null:
                    canvasRenderer = component.Field;
                    break;
                case "UnityEngine.UI.Image" when image == null:
                    image = component.Field;
                    break;
                case "UnityEngine.UI.RawImage" when rawImage == null:
                    rawImage = component.Field;
                    break;
                default:
                    throw new NotSupportedException(
                        $"Prefab component is outside graph v1 whitelist: " +
                        ClassifyComponent(manager, file, component.Field));
            }
        }
        if (image != null && rawImage != null)
            throw new NotSupportedException("Prefab node cannot contain both Image and RawImage in graph v1.");

        var nodeIndex = nodes.Count;
        nodes.Add(new ResourceIrPrefabNode
        {
            Name = Required(gameObject, "m_Name").AsString,
            ParentIndex = parentIndex,
            Layer = ReadInt(gameObject, "m_Layer", 0),
            Active = ReadBool(gameObject, "m_IsActive", true),
            Transform = ReadPrefabTransform(transformEntry.Field),
            CanvasRenderer = canvasRenderer == null
                ? null
                : new ResourceIrPrefabCanvasRenderer
                {
                    CullTransparentMesh = ReadBool(canvasRenderer, "m_CullTransparentMesh", true)
                },
            Image = image == null
                ? null
                : ReadPrefabImage(
                    bundle,
                    manager,
                    files,
                    assetsFileName,
                    image,
                    bundleAssets,
                    assets,
                    payloads,
                    outputDirectory,
                    processing,
                    dependencies),
            RawImage = rawImage == null
                ? null
                : ReadPrefabRawImage(
                    bundle,
                    manager,
                    files,
                    assetsFileName,
                    rawImage,
                    bundleAssets,
                    assets,
                    payloads,
                    outputDirectory,
                    processing,
                    dependencies)
        });

        foreach (var childPointer in EnumerateArray(Required(transformEntry.Field, "m_Children")))
        {
            var childTransformPath = ReadPointerPathId(childPointer);
            if (childTransformPath == 0)
                throw new InvalidDataException("Prefab Transform contains a null child pointer.");
            var childTransform = ReadAssetByPath(manager, file, childTransformPath);
            var childGameObjectPath = ReadPointerPathId(Required(childTransform, "m_GameObject"));
            if (childGameObjectPath == 0)
                throw new InvalidDataException("Prefab child Transform has no GameObject.");
            var childGameObject = ReadAssetByPath(manager, file, childGameObjectPath);
            AppendPrefabNode(
                bundle,
                manager,
                files,
                file,
                assetsFileName,
                childGameObjectPath,
                childGameObject,
                nodeIndex,
                childTransformPath,
                transformEntry.PathId,
                depth + 1,
                bundleAssets,
                assets,
                payloads,
                outputDirectory,
                processing,
                nodes,
                dependencies,
                visited);
        }
    }

    private static ResourceIrPrefabTransform ReadPrefabTransform(AssetTypeValueField transform)
    {
        var position = Required(transform, "m_LocalPosition");
        var rotation = Required(transform, "m_LocalRotation");
        var scale = Required(transform, "m_LocalScale");
        var isRect = transform.TypeName.Equals("RectTransform", StringComparison.OrdinalIgnoreCase);
        var anchorMin = isRect ? Required(transform, "m_AnchorMin") : null;
        var anchorMax = isRect ? Required(transform, "m_AnchorMax") : null;
        var anchoredPosition = isRect ? Required(transform, "m_AnchoredPosition") : null;
        var sizeDelta = isRect ? Required(transform, "m_SizeDelta") : null;
        var pivot = isRect ? Required(transform, "m_Pivot") : null;
        return new ResourceIrPrefabTransform
        {
            IsRectTransform = isRect,
            LocalPositionX = Required(position, "x").AsFloat,
            LocalPositionY = Required(position, "y").AsFloat,
            LocalPositionZ = Required(position, "z").AsFloat,
            LocalRotationX = Required(rotation, "x").AsFloat,
            LocalRotationY = Required(rotation, "y").AsFloat,
            LocalRotationZ = Required(rotation, "z").AsFloat,
            LocalRotationW = Required(rotation, "w").AsFloat,
            LocalScaleX = Required(scale, "x").AsFloat,
            LocalScaleY = Required(scale, "y").AsFloat,
            LocalScaleZ = Required(scale, "z").AsFloat,
            AnchorMinX = isRect ? Required(anchorMin!, "x").AsFloat : 0f,
            AnchorMinY = isRect ? Required(anchorMin!, "y").AsFloat : 0f,
            AnchorMaxX = isRect ? Required(anchorMax!, "x").AsFloat : 1f,
            AnchorMaxY = isRect ? Required(anchorMax!, "y").AsFloat : 1f,
            AnchoredPositionX = isRect ? Required(anchoredPosition!, "x").AsFloat : 0f,
            AnchoredPositionY = isRect ? Required(anchoredPosition!, "y").AsFloat : 0f,
            SizeDeltaX = isRect ? Required(sizeDelta!, "x").AsFloat : 0f,
            SizeDeltaY = isRect ? Required(sizeDelta!, "y").AsFloat : 0f,
            PivotX = isRect ? Required(pivot!, "x").AsFloat : 0.5f,
            PivotY = isRect ? Required(pivot!, "y").AsFloat : 0.5f
        };
    }

    private static ResourceIrPrefabImage ReadPrefabImage(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        string assetsFileName,
        AssetTypeValueField image,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing,
        ISet<string> dependencies)
    {
        RequireEnabledGraphic(image);
        var graphic = ReadPrefabGraphic(
            bundle, manager, files, assetsFileName, image, bundleAssets, assets, payloads,
            outputDirectory, processing, dependencies);
        var spriteId = MaterializePrefabDependency(
            bundle, manager, files, assetsFileName, Required(image, "m_Sprite"), "Sprite",
            bundleAssets, assets, payloads, outputDirectory, processing);
        if (spriteId.Length != 0)
            dependencies.Add(spriteId);
        return new ResourceIrPrefabImage
        {
            Graphic = graphic,
            SpriteAssetId = spriteId,
            Type = ReadInt(image, "m_Type", 0),
            PreserveAspect = ReadBool(image, "m_PreserveAspect", false),
            FillCenter = ReadBool(image, "m_FillCenter", true),
            FillMethod = ReadInt(image, "m_FillMethod", 4),
            FillAmount = ReadFloat(image, "m_FillAmount", 1f),
            FillClockwise = ReadBool(image, "m_FillClockwise", true),
            FillOrigin = ReadInt(image, "m_FillOrigin", 0),
            UseSpriteMesh = ReadBool(image, "m_UseSpriteMesh", false),
            PixelsPerUnitMultiplier = ReadFloat(image, "m_PixelsPerUnitMultiplier", 1f)
        };
    }

    private static ResourceIrPrefabRawImage ReadPrefabRawImage(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        string assetsFileName,
        AssetTypeValueField image,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing,
        ISet<string> dependencies)
    {
        RequireEnabledGraphic(image);
        var graphic = ReadPrefabGraphic(
            bundle, manager, files, assetsFileName, image, bundleAssets, assets, payloads,
            outputDirectory, processing, dependencies);
        var textureId = MaterializePrefabDependency(
            bundle, manager, files, assetsFileName, Required(image, "m_Texture"), "Texture2D",
            bundleAssets, assets, payloads, outputDirectory, processing);
        if (textureId.Length != 0)
            dependencies.Add(textureId);
        var uv = Required(image, "m_UVRect");
        return new ResourceIrPrefabRawImage
        {
            Graphic = graphic,
            TextureAssetId = textureId,
            UvX = Required(uv, "x").AsFloat,
            UvY = Required(uv, "y").AsFloat,
            UvWidth = Required(uv, "width").AsFloat,
            UvHeight = Required(uv, "height").AsFloat
        };
    }

    private static ResourceIrPrefabGraphic ReadPrefabGraphic(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        string assetsFileName,
        AssetTypeValueField graphic,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing,
        ISet<string> dependencies)
    {
        var color = Required(graphic, "m_Color");
        var materialId = MaterializePrefabDependency(
            bundle, manager, files, assetsFileName, Required(graphic, "m_Material"), "Material",
            bundleAssets, assets, payloads, outputDirectory, processing);
        if (materialId.Length != 0)
            dependencies.Add(materialId);
        return new ResourceIrPrefabGraphic
        {
            ColorR = Required(color, "r").AsFloat,
            ColorG = Required(color, "g").AsFloat,
            ColorB = Required(color, "b").AsFloat,
            ColorA = Required(color, "a").AsFloat,
            RaycastTarget = ReadBool(graphic, "m_RaycastTarget", true),
            Maskable = ReadBool(graphic, "m_Maskable", true),
            MaterialAssetId = materialId
        };
    }

    private static string MaterializePrefabDependency(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        string assetsFileName,
        AssetTypeValueField pointer,
        string expectedSourceType,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing)
    {
        if (ReadInt(pointer, "m_FileID", -1) != 0)
            throw new NotSupportedException($"External prefab {expectedSourceType} dependency is unsupported.");
        var pathId = ReadPointerPathId(pointer);
        if (pathId == 0)
            return string.Empty;
        if (!bundleAssets.TryGetValue(MakeSourceKey(assetsFileName, pathId), out var dependency) ||
            !dependency.SourceType.Equals(expectedSourceType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Prefab {expectedSourceType} dependency was not indexed pathId={pathId}");
        switch (expectedSourceType)
        {
            case "Texture2D":
                MaterializeTexture(manager, bundle, files, dependency, assets, payloads, outputDirectory);
                break;
            case "Sprite":
                MaterializeSprite(
                    bundle, manager, files, dependency, bundleAssets, assets, payloads,
                    outputDirectory, processing);
                break;
            case "Material":
                MaterializeMaterial(
                    bundle, manager, files, dependency, bundleAssets, assets, payloads,
                    outputDirectory, processing);
                break;
            default:
                throw new NotSupportedException($"Unknown prefab dependency type: {expectedSourceType}");
        }
        return dependency.Id;
    }

    private static void RequireEnabledGraphic(AssetTypeValueField graphic)
    {
        if (!ReadBool(graphic, "m_Enabled", true))
            throw new NotSupportedException("Disabled prefab Graphic is not supported in graph v1.");
        var padding = graphic["m_RaycastPadding"];
        if (!padding.IsDummy && new[] { "x", "y", "z", "w" }.Any(name =>
                MathF.Abs(Required(padding, name).AsFloat) > 0.000001f))
            throw new NotSupportedException("Non-zero Graphic raycast padding is not supported in graph v1.");
        var stateChanged = graphic["m_OnCullStateChanged"];
        var calls = stateChanged.IsDummy ? null : stateChanged["m_PersistentCalls"]["m_Calls"];
        if (calls is { IsDummy: false } && EnumerateArray(calls).Count != 0)
            throw new NotSupportedException("Graphic persistent callbacks are not supported in prefab graph v1.");
    }

    private static void MaterializeTexture(
        AssetsManager manager,
        BundleFileInstance bundle,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory)
    {
        if (assets[asset.Id].MaterializationKind is
            ResourceIrMaterializationKind.TextureRgba32 or
            ResourceIrMaterializationKind.TextureAlpha8)
            return;
        var (_, field) = ReadAsset(manager, files, asset);
        if (!field.TypeName.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected Texture2D, got {field.TypeName}");
        var width = Required(field, "m_Width").AsInt;
        var height = Required(field, "m_Height").AsInt;
        var sourceFormat = Required(field, "m_TextureFormat").AsInt;
        var pixelCount = checked(width * height);
        if (pixelCount <= 0)
            throw new InvalidDataException($"Texture dimensions are invalid: {width}x{height}");
        var sourceBytes = ReadTextureBytes(bundle, field);
        byte[] payloadBytes;
        string payloadKind;
        string extension;
        string tag;
        ResourceIrMaterializationKind materializationKind;
        if (sourceFormat == 1)
        {
            if (pixelCount > MaxDecodedTextureBytes || sourceBytes.Length < pixelCount)
                throw new InvalidDataException("Alpha8 texture payload length is invalid.");
            payloadBytes = sourceBytes.Length == pixelCount
                ? sourceBytes
                : sourceBytes.AsSpan(0, pixelCount).ToArray();
            payloadKind = "alpha8";
            extension = ".alpha8";
            tag = "alpha8-v1";
            materializationKind = ResourceIrMaterializationKind.TextureAlpha8;
        }
        else
        {
            var decodedLength = checked(pixelCount * 4);
            if (decodedLength > MaxDecodedTextureBytes)
                throw new InvalidDataException($"Decoded texture exceeds limit: {decodedLength}");
            payloadBytes = TextureRgbaDecoder.Decode(sourceFormat, width, height, sourceBytes);
            if (payloadBytes.Length != decodedLength)
                throw new InvalidDataException("RGBA32 decoder returned an unexpected length.");
            payloadKind = "rgba32";
            extension = ".rgba32";
            tag = "rgba32-v1";
            materializationKind = ResourceIrMaterializationKind.TextureRgba32;
        }

        var payloadId = "payload." + asset.Id[4..];
        var fileName = asset.Id + extension;
        var outputPath = Path.Combine(outputDirectory, fileName);
        File.WriteAllBytes(outputPath, payloadBytes);
        payloads[payloadId] = new ResourceIrPayload
        {
            Id = payloadId,
            Kind = payloadKind,
            RelativePath = "resource_ir_blobs/" + fileName,
            Sha256Hex = Convert.ToHexString(SHA256.HashData(payloadBytes)).ToLowerInvariant(),
            Length = payloadBytes.Length
        };
        var settings = field["m_TextureSettings"];
        assets[asset.Id] = asset with
        {
            MaterializationKind = materializationKind,
            Compatibility = ResourceIrCompatibility.Exact,
            PayloadId = payloadId,
            Texture = new ResourceIrTextureInfo
            {
                Width = width,
                Height = height,
                SourceFormat = sourceFormat,
                MipCount = 1,
                FilterMode = ReadInt(settings, "m_FilterMode", 1),
                WrapU = ReadInt(settings, "m_WrapU", 0),
                WrapV = ReadInt(settings, "m_WrapV", 0),
                Linear = ReadInt(field, "m_ColorSpace", 1) == 0
            },
            Tags = asset.Tags.Concat([tag]).Distinct(StringComparer.Ordinal).ToArray()
        };
    }

    private static void MaterializeSprite(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing)
    {
        if (assets[asset.Id].MaterializationKind == ResourceIrMaterializationKind.SpriteFromTexture)
            return;
        if (!processing.Add(asset.Id))
            throw new InvalidDataException($"Sprite dependency cycle detected: {asset.Id}");
        try
        {
            var (_, field) = ReadAsset(manager, files, asset);
            if (!field.TypeName.Equals("Sprite", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Expected Sprite, got {field.TypeName}");
            var texturePointer = Required(Required(field, "m_RD"), "texture");
            if (ReadInt(texturePointer, "m_FileID", -1) != 0)
                throw new NotSupportedException("External Sprite texture dependency is not supported in IR v1.");
            var texturePathId = Required(texturePointer, "m_PathID").AsLong;
            if (!bundleAssets.TryGetValue(MakeSourceKey(asset.AssetsFileName, texturePathId), out var textureAsset) ||
                textureAsset.SourceType != "Texture2D")
                throw new InvalidDataException($"Sprite Texture2D dependency was not indexed pathId={texturePathId}");
            MaterializeTexture(manager, bundle, files, textureAsset, assets, payloads, outputDirectory);

            var rect = Required(field, "m_Rect");
            var pivot = Required(field, "m_Pivot");
            var border = Required(field, "m_Border");
            assets[asset.Id] = asset with
            {
                MaterializationKind = ResourceIrMaterializationKind.SpriteFromTexture,
                Compatibility = ResourceIrCompatibility.Exact,
                DependencyIds = [textureAsset.Id],
                Sprite = new ResourceIrSpriteInfo
                {
                    TextureAssetId = textureAsset.Id,
                    X = Required(rect, "x").AsFloat,
                    Y = Required(rect, "y").AsFloat,
                    Width = Required(rect, "width").AsFloat,
                    Height = Required(rect, "height").AsFloat,
                    PivotX = Required(pivot, "x").AsFloat,
                    PivotY = Required(pivot, "y").AsFloat,
                    PixelsPerUnit = Required(field, "m_PixelsToUnits").AsFloat,
                    BorderLeft = Required(border, "x").AsFloat,
                    BorderBottom = Required(border, "y").AsFloat,
                    BorderRight = Required(border, "z").AsFloat,
                    BorderTop = Required(border, "w").AsFloat,
                    Extrude = Required(field, "m_Extrude").AsUInt
                },
                Tags = asset.Tags.Concat(["sprite-v1"]).Distinct(StringComparer.Ordinal).ToArray()
            };
        }
        finally
        {
            processing.Remove(asset.Id);
        }
    }

    private static void MaterializeMaterial(
        BundleFileInstance bundle,
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset,
        IReadOnlyDictionary<string, ResourceIrAsset> bundleAssets,
        IDictionary<string, ResourceIrAsset> assets,
        IDictionary<string, ResourceIrPayload> payloads,
        string outputDirectory,
        ISet<string> processing)
    {
        if (assets[asset.Id].MaterializationKind == ResourceIrMaterializationKind.MaterialFromCapabilityShader)
            return;
        if (!processing.Add(asset.Id))
            throw new InvalidDataException($"Material dependency cycle detected: {asset.Id}");
        try
        {
            var (file, field) = ReadAsset(manager, files, asset);
            if (!field.TypeName.Equals("Material", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Expected Material, got {field.TypeName}");
            RequireEmptyCollection(field, "stringTagMap");
            RequireEmptyCollection(field, "disabledShaderPasses");
            RequireEmptyCollection(field, "m_BuildTextureStacks");

            var shaderPointer = Required(field, "m_Shader");
            if (ReadInt(shaderPointer, "m_FileID", -1) != 0)
                throw new NotSupportedException("External Material shader dependency is not supported in IR v1.");
            var shaderPathId = Required(shaderPointer, "m_PathID").AsLong;
            if (shaderPathId == 0)
                throw new InvalidDataException("Material shader dependency is null.");
            var shader = ReadAssetByPath(manager, file, shaderPathId);
            var shaderName = ReadShaderName(shader);
            var keywords = ReadStrings(field, "m_ValidKeywords");
            var capabilityId = ResolveMaterialCapability(shaderName, keywords);
            var propertyWhitelist = MaterialPropertyWhitelist(capabilityId);

            var saved = Required(field, "m_SavedProperties");
            var intMap = ReadMap(saved, "m_Ints");
            var floatMap = ReadMap(saved, "m_Floats");
            var colorMap = ReadMap(saved, "m_Colors");
            var textureMap = ReadMap(saved, "m_TexEnvs");
            var ints = intMap
                .Where(pair => propertyWhitelist == null || propertyWhitelist.Contains(pair.Name))
                .Select(pair => new ResourceIrMaterialInt(pair.Name, pair.Value.AsInt))
                .ToArray();
            var floats = floatMap
                .Where(pair => propertyWhitelist == null || propertyWhitelist.Contains(pair.Name))
                .Select(pair => new ResourceIrMaterialFloat(pair.Name, pair.Value.AsFloat))
                .ToArray();
            var colors = colorMap
                .Where(pair => propertyWhitelist == null || propertyWhitelist.Contains(pair.Name))
                .Select(pair => new ResourceIrMaterialColor(
                    pair.Name,
                    Required(pair.Value, "r").AsFloat,
                    Required(pair.Value, "g").AsFloat,
                    Required(pair.Value, "b").AsFloat,
                    Required(pair.Value, "a").AsFloat))
                .ToArray();
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var textures = new List<ResourceIrMaterialTexture>();
            foreach (var pair in textureMap.Where(pair =>
                         propertyWhitelist == null || propertyWhitelist.Contains(pair.Name)))
            {
                var texturePointer = Required(pair.Value, "m_Texture");
                if (ReadInt(texturePointer, "m_FileID", -1) != 0)
                    throw new NotSupportedException(
                        $"External Material texture dependency is not supported: {pair.Name}");
                var texturePathId = Required(texturePointer, "m_PathID").AsLong;
                var textureAssetId = string.Empty;
                if (texturePathId != 0)
                {
                    if (!bundleAssets.TryGetValue(
                            MakeSourceKey(asset.AssetsFileName, texturePathId),
                            out var textureAsset) ||
                        !textureAsset.SourceType.Equals("Texture2D", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new NotSupportedException(
                            $"Material texture is not an indexed Texture2D property={pair.Name} " +
                            $"pathId={texturePathId}");
                    }
                    MaterializeTexture(
                        manager,
                        bundle,
                        files,
                        textureAsset,
                        assets,
                        payloads,
                        outputDirectory);
                    textureAssetId = textureAsset.Id;
                    dependencies.Add(textureAssetId);
                }
                var offset = Required(pair.Value, "m_Offset");
                var scale = Required(pair.Value, "m_Scale");
                textures.Add(new ResourceIrMaterialTexture(
                    pair.Name,
                    textureAssetId,
                    Required(offset, "x").AsFloat,
                    Required(offset, "y").AsFloat,
                    Required(scale, "x").AsFloat,
                    Required(scale, "y").AsFloat));
            }

            assets[asset.Id] = asset with
            {
                MaterializationKind = ResourceIrMaterializationKind.MaterialFromCapabilityShader,
                Compatibility = ResourceIrCompatibility.Compatible,
                CapabilityStableId = capabilityId,
                DependencyIds = dependencies.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
                Material = new ResourceIrMaterialInfo
                {
                    CustomRenderQueue = ReadInt(field, "m_CustomRenderQueue", -1),
                    GlobalIlluminationFlags = ReadInt(field, "m_LightmapFlags", 0),
                    EnableInstancing = ReadBool(field, "m_EnableInstancingVariants", false),
                    DoubleSidedGi = ReadBool(field, "m_DoubleSidedGI", false),
                    Keywords = keywords,
                    Ints = ints,
                    Floats = floats,
                    Colors = colors,
                    Textures = textures
                },
                Tags = asset.Tags.Concat(new[]
                    {
                        "material-capability-v1",
                        capabilityId,
                        "source-shader:" + shaderName
                    })
                    .Concat(propertyWhitelist == null
                        ? Array.Empty<string>()
                        : intMap.Concat(floatMap).Concat(colorMap).Concat(textureMap)
                            .Select(pair => pair.Name)
                            .Where(name => !propertyWhitelist.Contains(name))
                            .Distinct(StringComparer.Ordinal)
                            .OrderBy(name => name, StringComparer.Ordinal)
                            .Select(name => "dropped-property:" + name))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()
            };
        }
        finally
        {
            processing.Remove(asset.Id);
        }
    }

    private static string ReadShaderName(AssetTypeValueField shader)
    {
        if (!shader.TypeName.Equals("Shader", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected Shader, got {shader.TypeName}");
        var parsed = shader["m_ParsedForm"];
        var parsedName = parsed.IsDummy ? null : parsed["m_Name"];
        var name = parsedName is { IsDummy: false } ? parsedName.AsString : string.Empty;
        if (string.IsNullOrWhiteSpace(name) && !shader["m_Name"].IsDummy)
            name = shader["m_Name"].AsString;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException("Serialized Shader name is empty.");
        return name;
    }

    private static string ResolveMaterialCapability(
        string shaderName,
        IReadOnlyCollection<string> keywords)
    {
        string capability;
        HashSet<string> supportedKeywords;
        switch (shaderName)
        {
            case "TextMeshPro/Distance Field":
            case "TextMeshPro/Mobile/Distance Field":
                capability = "material.compat.tmp_mobile";
                supportedKeywords = new HashSet<string>(StringComparer.Ordinal)
                {
                    "OUTLINE_ON",
                    "UNDERLAY_ON",
                    "UNDERLAY_INNER",
                    "MASK_SOFT",
                    "MASK_HARD",
                    "UNITY_UI_CLIP_RECT",
                    "UNITY_UI_ALPHACLIP"
                };
                break;
            case "UI/Default":
                capability = "material.compat.ui_default";
                supportedKeywords = new HashSet<string>(StringComparer.Ordinal);
                break;
            case "Sprites/Default":
                capability = "material.compat.sprite_default";
                supportedKeywords = new HashSet<string>(StringComparer.Ordinal);
                break;
            default:
                throw new NotSupportedException($"Material Shader has no Android capability: {shaderName}");
        }
        var unsupported = keywords.Where(keyword => !supportedKeywords.Contains(keyword)).ToArray();
        if (unsupported.Length != 0)
            throw new NotSupportedException(
                $"Material keywords are unsupported for {shaderName}: {string.Join(',', unsupported)}");
        return capability;
    }

    private static HashSet<string>? MaterialPropertyWhitelist(string capabilityId)
    {
        if (capabilityId != "material.compat.tmp_mobile")
            return null;
        return new HashSet<string>(StringComparer.Ordinal)
        {
            "_FaceColor", "_FaceDilate", "_OutlineColor", "_OutlineWidth", "_OutlineSoftness",
            "_UnderlayColor", "_UnderlayOffsetX", "_UnderlayOffsetY", "_UnderlayDilate",
            "_UnderlaySoftness", "_WeightNormal", "_WeightBold", "_ShaderFlags", "_ScaleRatioA",
            "_ScaleRatioB", "_ScaleRatioC", "_MainTex", "_TextureWidth", "_TextureHeight",
            "_GradientScale", "_ScaleX", "_ScaleY", "_PerspectiveFilter", "_Sharpness",
            "_VertexOffsetX", "_VertexOffsetY", "_ClipRect", "_MaskSoftnessX", "_MaskSoftnessY",
            "_StencilComp", "_Stencil", "_StencilOp", "_StencilWriteMask", "_StencilReadMask",
            "_CullMode", "_ColorMask"
        };
    }

    private static IReadOnlyList<(string Name, AssetTypeValueField Value)> ReadMap(
        AssetTypeValueField parent,
        string fieldName)
    {
        var vector = parent[fieldName];
        if (vector.IsDummy)
            return Array.Empty<(string, AssetTypeValueField)>();
        return EnumerateArray(vector)
            .Select(pair => (
                Name: Required(pair, "first").AsString,
                Value: Required(pair, "second")))
            .ToArray();
    }

    private static string[] ReadStrings(AssetTypeValueField parent, string fieldName)
    {
        var vector = parent[fieldName];
        return vector.IsDummy
            ? Array.Empty<string>()
            : EnumerateArray(vector).Select(value => value.AsString).ToArray();
    }

    private static void RequireEmptyCollection(AssetTypeValueField parent, string fieldName)
    {
        var vector = parent[fieldName];
        if (!vector.IsDummy && EnumerateArray(vector).Count != 0)
            throw new NotSupportedException($"Material collection is not supported: {fieldName}");
    }

    private static (AssetsFileInstance File, AssetTypeValueField Field) ReadAsset(
        AssetsManager manager,
        IReadOnlyDictionary<string, AssetsFileInstance> files,
        ResourceIrAsset asset)
    {
        if (!files.TryGetValue(asset.AssetsFileName, out var file) &&
            !files.TryGetValue(asset.Container, out file))
            throw new InvalidDataException($"Serialized assets file was not loaded: {asset.AssetsFileName}");
        var info = file.file.GetAssetInfo(asset.PathId)
                   ?? throw new InvalidDataException($"Asset path id was not found: {asset.PathId}");
        return (file, manager.GetBaseField(file, info, AssetReadFlags.None));
    }

    private static byte[] ReadTextureBytes(BundleFileInstance bundle, AssetTypeValueField texture)
    {
        foreach (var fieldName in new[] { "image data", "m_ImageData" })
        {
            var image = texture[fieldName];
            if (!image.IsDummy && image.AsByteArray is { Length: > 0 } inline)
                return inline;
        }
        var streamData = texture["m_StreamData"];
        if (streamData.IsDummy)
            throw new InvalidDataException("Texture has neither inline image data nor m_StreamData.");
        var path = Required(streamData, "path").AsString;
        var offset = checked((long)Required(streamData, "offset").AsULong);
        var size = checked((int)Required(streamData, "size").AsUInt);
        if (size <= 0 || size > MaxDecodedTextureBytes)
            throw new InvalidDataException($"Texture stream size is invalid: {size}");
        var resourceName = path.Replace('\\', '/').Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(resourceName))
            throw new InvalidDataException($"Texture stream path is invalid: {path}");
        var fileIndex = bundle.file.GetFileIndex(resourceName);
        if (fileIndex < 0)
            throw new FileNotFoundException("Texture stream entry is missing from UnityFS.", resourceName);
        bundle.file.GetFileRange(fileIndex, out var fileOffset, out var fileLength);
        if (offset < 0 || offset > fileLength || size > fileLength - offset)
            throw new InvalidDataException(
                $"Texture stream range is outside resource entry offset={offset} size={size} length={fileLength}");
        var result = new byte[size];
        lock (bundle.DataStream)
        {
            bundle.DataStream.Position = checked(fileOffset + offset);
            bundle.DataStream.ReadExactly(result);
        }
        return result;
    }

    private static AssetTypeValueField Required(AssetTypeValueField field, string name)
    {
        var child = field[name];
        if (child.IsDummy)
            throw new InvalidDataException($"Serialized field is missing: {field.FieldName}.{name}");
        return child;
    }

    private static int ReadInt(AssetTypeValueField field, string name, int fallback)
    {
        if (field.IsDummy)
            return fallback;
        var child = field[name];
        return child.IsDummy ? fallback : child.AsInt;
    }

    private static bool ReadBool(AssetTypeValueField field, string name, bool fallback)
    {
        if (field.IsDummy)
            return fallback;
        var child = field[name];
        return child.IsDummy ? fallback : child.AsBool;
    }

    private static float ReadFloat(AssetTypeValueField field, string name, float fallback)
    {
        if (field.IsDummy)
            return fallback;
        var child = field[name];
        return child.IsDummy ? fallback : child.AsFloat;
    }

    private static string MakeSourceKey(string assetsFileName, long pathId)
        => assetsFileName + "\0" + pathId;
}
