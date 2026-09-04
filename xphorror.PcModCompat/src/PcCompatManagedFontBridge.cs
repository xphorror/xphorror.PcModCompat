using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Xphorror.PcModCompat;

/// <summary>
/// Validates runtime-created TMP fonts before a desktop MOD treats a non-null proxy as usable.
/// Unity can return a wrapper after FontEngine rejected the source face, leaving a font that can
/// never materialize a glyph.
/// </summary>
public static class PcCompatManagedFontBridge
{
    private const string WarmupCharacters =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly ConditionalWeakTable<Type, Factory> Factories = new();
    private static readonly ConditionalWeakTable<Type, Validator> Validators = new();
    private static readonly ConditionalWeakTable<Type, TextureValidator> TextureValidators = new();
    private static readonly ConditionalWeakTable<Type, TextBinder> TextBinders = new();
    private static readonly ConditionalWeakTable<Type, ConditionalWeakTable<Type, MaterialAtlasBinder>>
        MaterialAtlasBinders = new();

    public static object? CreateFontAsset(object sourceFont)
    {
        ArgumentNullException.ThrowIfNull(sourceFont);
        PcCompatDeepDebug.Write(
            "font-create",
            $"phase=begin source={PcCompatDeepDebug.DescribeObject(sourceFont)} " +
            PcCompatDeepDebug.ExecutionIdentity());
        object? asset = null;
        try
        {
            var factory = Factories.GetValue(sourceFont.GetType(), ResolveFactory);
            asset = factory.Create.Invoke(null, [sourceFont]);
            string? validation = null;
            var usable = asset != null && HasUsableFontFace(asset, out validation);
            PcCompatDeepDebug.Write(
                "font-create",
                $"phase=end source={PcCompatDeepDebug.DescribeObject(sourceFont)} " +
                $"asset={PcCompatDeepDebug.DescribeObject(asset)} usable={usable} " +
                $"validation=[{validation ?? "asset-null"}] " +
                PcCompatDeepDebug.ExecutionIdentity());
            return usable ? asset : null;
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            PcCompatDeepDebug.Write(
                "font-create",
                $"phase=failed source={PcCompatDeepDebug.DescribeObject(sourceFont)} " +
                $"asset={PcCompatDeepDebug.DescribeObject(asset)} " +
                $"error={unwrapped.GetType().Name}:{PcCompatDeepDebug.Sanitize(unwrapped.Message)} " +
                PcCompatDeepDebug.ExecutionIdentity());
            throw;
        }
    }

    /// <summary>
    /// Applies a TMP font and then restores the atlas/material invariant that can be lost when the
    /// generated proxy setter crosses from CoreCLR into IL2CPP. The original setter still owns the
    /// actual TMP state transition; the bridge only validates and repairs its observable result.
    /// </summary>
    public static void SetFont(object text, object? font)
    {
        ArgumentNullException.ThrowIfNull(text);
        var binder = TextBinders.GetValue(text.GetType(), ResolveTextBinder);
        var debugKey = PcCompatDeepDebug.ExecutionIdentity() + "\0" +
                       (text.GetType().FullName ?? text.GetType().Name) + "\0set-font";
        var sampled = PcCompatDeepDebug.ShouldSample(
            "font-setter",
            debugKey,
            out var invocation,
            first: 2,
            periodic: 4096);
        if (sampled)
        {
            PcCompatDeepDebug.Write(
                "font-setter",
                $"phase=before invocation={invocation} setter=font " +
                $"text={PcCompatDeepDebug.DescribeObject(text)} newFont={PcCompatDeepDebug.DescribeObject(font)} " +
                $"binding=[{DescribeTextBinding(binder, text)}] " +
                PcCompatDeepDebug.ExecutionIdentity());
        }
        try
        {
            binder.SetFont.Invoke(text, [font]);
            if (font != null)
            {
                var validator = Validators.GetValue(font.GetType(), ResolveValidator);
                var atlas = validator.GetAtlasTexture.Invoke(font, null)
                            ?? throw new InvalidOperationException(
                                "TMP font setter produced a font without an atlas texture.");
                var fontMaterial = validator.GetMaterial.Invoke(font, null);
                if (fontMaterial == null || !TryBindAtlasTexture(fontMaterial, atlas))
                {
                    throw new InvalidOperationException(
                        "TMP font material could not be bound to its atlas texture.");
                }

                var textMaterial = TryGetOptionalTextMaterial(binder, text, out var materialError);
                if (textMaterial != null)
                {
                    if (!TryBindAtlasTexture(textMaterial, atlas))
                    {
                        throw new InvalidOperationException(
                            "TMP text material could not be bound to the selected font atlas.");
                    }
                    // Re-applying through the real setter preserves TMP's material reference bookkeeping
                    // and dirties geometry on versions where set_font alone does not do so.
                    binder.SetMaterial.Invoke(text, [textMaterial]);
                }
                else if (materialError != null)
                {
                    PcCompatDeepDebug.WriteState(
                        "font-setter",
                        debugKey + "\0optional-material",
                        materialError,
                        $"phase=optional-material-unavailable setter=font " +
                        $"text={PcCompatDeepDebug.DescribeObject(text)} " +
                        $"error={PcCompatDeepDebug.Sanitize(materialError)} " +
                        PcCompatDeepDebug.ExecutionIdentity());
                }
                binder.SetAllDirty?.Invoke(text, null);
            }
            if (sampled)
            {
                PcCompatDeepDebug.Write(
                    "font-setter",
                    $"phase=after invocation={invocation} setter=font " +
                    $"text={PcCompatDeepDebug.DescribeObject(text)} newFont={PcCompatDeepDebug.DescribeObject(font)} " +
                    $"binding=[{DescribeTextBinding(binder, text)}] " +
                    PcCompatDeepDebug.ExecutionIdentity());
            }
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            PcCompatDeepDebug.Write(
                "font-setter",
                $"phase=failed invocation={invocation} setter=font " +
                $"text={PcCompatDeepDebug.DescribeObject(text)} newFont={PcCompatDeepDebug.DescribeObject(font)} " +
                $"binding=[{DescribeTextBinding(binder, text)}] " +
                $"error={unwrapped.GetType().Name}:{PcCompatDeepDebug.Sanitize(unwrapped.Message)} " +
                PcCompatDeepDebug.ExecutionIdentity());
            throw;
        }
    }

    internal static bool HasUsableFontFace(object fontAsset)
        => HasUsableFontFace(fontAsset, out _);

    private static bool HasUsableFontFace(object fontAsset, out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(fontAsset);
        var validator = Validators.GetValue(fontAsset.GetType(), ResolveValidator);
        try
        {
            var face = validator.GetFace.Invoke(fontAsset, null);
            if (face == null)
            {
                diagnostic = "reason=face-null";
                return false;
            }
            var pointSize = Convert.ToSingle(validator.PointSize.GetValue(face));
            var unitsPerEm = Convert.ToInt32(validator.UnitsPerEm.GetValue(face));
            var lineHeight = Convert.ToSingle(validator.LineHeight.GetValue(face));
            if (!float.IsFinite(pointSize) || pointSize <= 0f ||
                unitsPerEm <= 0 ||
                !float.IsFinite(lineHeight) || lineHeight <= 0f)
            {
                diagnostic = $"reason=invalid-face pointSize={pointSize} unitsPerEm={unitsPerEm} lineHeight={lineHeight}";
                return false;
            }

            // Runtime-created TMP assets can expose valid face metrics while their dynamic
            // atlas is still empty. Desktop TMP fills it lazily during rendering, but that
            // transition is not reliable across the CoreCLR/IL2CPP proxy boundary. Exercise
            // the public population API here and require at least one materialized glyph.
            object?[] arguments = [WarmupCharacters, null, false];
            var complete = Convert.ToBoolean(validator.TryAddCharacters.Invoke(
                fontAsset,
                arguments));
            var missing = arguments[1] as string;
            if (!complete &&
                (missing == null || missing.Length >= WarmupCharacters.Length))
            {
                diagnostic = $"reason=warmup-rejected complete={complete} missing={PcCompatDeepDebug.Sanitize(missing)} " +
                             $"pointSize={pointSize} unitsPerEm={unitsPerEm} lineHeight={lineHeight}";
                return false;
            }

            var characters = validator.GetCharacterTable.Invoke(fontAsset, null);
            var characterCount = characters == null ? 0 : ReadCollectionCount(characters);
            if (characterCount <= 0)
            {
                diagnostic = $"reason=character-table-empty complete={complete} missing={PcCompatDeepDebug.Sanitize(missing)}";
                return false;
            }
            var atlas = validator.GetAtlasTexture.Invoke(fontAsset, null);
            if (atlas == null)
            {
                diagnostic = $"reason=atlas-null characters={characterCount}";
                return false;
            }
            var texture = TextureValidators.GetValue(atlas.GetType(), ResolveTextureValidator);
            var width = Convert.ToInt32(texture.GetWidth.Invoke(atlas, null));
            var height = Convert.ToInt32(texture.GetHeight.Invoke(atlas, null));
            if (width <= 0 || height <= 0)
            {
                diagnostic = $"reason=atlas-size characters={characterCount} atlas={width}x{height}";
                return false;
            }
            var material = validator.GetMaterial.Invoke(fontAsset, null);
            var materialBound = material != null && TryBindAtlasTexture(material, atlas);
            diagnostic = $"reason={(materialBound ? "ready" : "material-atlas-bind-failed")} " +
                         $"pointSize={pointSize} unitsPerEm={unitsPerEm} lineHeight={lineHeight} " +
                         $"warmupComplete={complete} missing={PcCompatDeepDebug.Sanitize(missing)} " +
                         $"characters={characterCount} atlas={width}x{height} " +
                         $"atlasObject={PcCompatDeepDebug.DescribeObject(atlas)} " +
                         $"material={PcCompatDeepDebug.DescribeObject(material)}";
            return materialBound;
        }
        catch (TargetInvocationException exception)
        {
            // A wrapper around a rejected native face may throw while its face data is read.
            var unwrapped = Unwrap(exception);
            diagnostic = $"reason=proxy-invocation-failed error={unwrapped.GetType().Name}:" +
                         PcCompatDeepDebug.Sanitize(unwrapped.Message);
            return false;
        }
    }

    /// <summary>
    /// Preserves TMP's final rendering invariant when a desktop MOD assigns an instance material:
    /// the material presented to the text must sample the selected font's current atlas.
    /// </summary>
    public static void SetFontMaterial(object text, object? material)
        => SetFontMaterialCore(text, material, shared: false);

    /// <summary>
    /// Shared-material counterpart of <see cref="SetFontMaterial"/>.
    /// </summary>
    public static void SetFontSharedMaterial(object text, object? material)
        => SetFontMaterialCore(text, material, shared: true);

    private static void SetFontMaterialCore(object text, object? material, bool shared)
    {
        ArgumentNullException.ThrowIfNull(text);
        var binder = TextBinders.GetValue(text.GetType(), ResolveTextBinder);
        var setter = shared ? "fontSharedMaterial" : "fontMaterial";
        var debugKey = PcCompatDeepDebug.ExecutionIdentity() + "\0" +
                       (text.GetType().FullName ?? text.GetType().Name) + "\0" + setter;
        var sampled = PcCompatDeepDebug.ShouldSample(
            "font-setter",
            debugKey,
            out var invocation,
            first: 2,
            periodic: 4096);
        if (sampled)
        {
            PcCompatDeepDebug.Write(
                "font-setter",
                $"phase=before invocation={invocation} setter={setter} " +
                $"text={PcCompatDeepDebug.DescribeObject(text)} newMaterial={PcCompatDeepDebug.DescribeObject(material)} " +
                $"binding=[{DescribeTextBinding(binder, text)}] " +
                PcCompatDeepDebug.ExecutionIdentity());
        }
        try
        {
            if (material != null)
            {
                var font = binder.GetFont.Invoke(text, null);
                if (font != null)
                {
                    var validator = Validators.GetValue(font.GetType(), ResolveValidator);
                    var atlas = validator.GetAtlasTexture.Invoke(font, null);
                    if (atlas == null || !TryBindAtlasTexture(material, atlas))
                    {
                        throw new InvalidOperationException(
                            "TMP material could not be bound to the selected font atlas.");
                    }
                }
            }

            (shared ? binder.SetSharedMaterial : binder.SetMaterial).Invoke(text, [material]);
            if (sampled)
            {
                PcCompatDeepDebug.Write(
                    "font-setter",
                    $"phase=after invocation={invocation} setter={setter} " +
                    $"text={PcCompatDeepDebug.DescribeObject(text)} newMaterial={PcCompatDeepDebug.DescribeObject(material)} " +
                    $"binding=[{DescribeTextBinding(binder, text)}] " +
                    PcCompatDeepDebug.ExecutionIdentity());
            }
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            PcCompatDeepDebug.Write(
                "font-setter",
                $"phase=failed invocation={invocation} setter={setter} " +
                $"text={PcCompatDeepDebug.DescribeObject(text)} newMaterial={PcCompatDeepDebug.DescribeObject(material)} " +
                $"binding=[{DescribeTextBinding(binder, text)}] " +
                $"error={unwrapped.GetType().Name}:{PcCompatDeepDebug.Sanitize(unwrapped.Message)} " +
                PcCompatDeepDebug.ExecutionIdentity());
            throw;
        }
    }

    private static bool TryBindAtlasTexture(object material, object atlas)
    {
        try
        {
            var byAtlasType = MaterialAtlasBinders.GetValue(
                material.GetType(),
                static _ => new ConditionalWeakTable<Type, MaterialAtlasBinder>());
            var binder = byAtlasType.GetValue(
                atlas.GetType(),
                atlasType => ResolveMaterialAtlasBinder(material.GetType(), atlasType));
            binder.SetTexture.Invoke(material, ["_MainTex", atlas]);
            return true;
        }
        catch (TargetInvocationException)
        {
            return false;
        }
        catch (MissingMethodException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static object? TryGetOptionalTextMaterial(
        TextBinder binder,
        object text,
        out string? error)
    {
        try
        {
            error = null;
            return binder.GetMaterial.Invoke(text, null);
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            error = unwrapped.GetType().Name + ":" + unwrapped.Message;
            return null;
        }
    }

    private static string DescribeTextBinding(TextBinder binder, object text)
    {
        try
        {
            var font = binder.GetFont.Invoke(text, null);
            var textMaterial = binder.GetMaterial.Invoke(text, null);
            if (font == null)
            {
                return $"font=null textMaterial={PcCompatDeepDebug.DescribeObject(textMaterial)}";
            }

            var validator = Validators.GetValue(font.GetType(), ResolveValidator);
            var atlas = validator.GetAtlasTexture.Invoke(font, null);
            var fontMaterial = validator.GetMaterial.Invoke(font, null);
            var atlasSize = "unknown";
            if (atlas != null)
            {
                var texture = TextureValidators.GetValue(atlas.GetType(), ResolveTextureValidator);
                atlasSize = Convert.ToInt32(texture.GetWidth.Invoke(atlas, null)) + "x" +
                            Convert.ToInt32(texture.GetHeight.Invoke(atlas, null));
            }
            var characters = validator.GetCharacterTable.Invoke(font, null);
            var characterCount = characters == null ? -1 : ReadCollectionCount(characters);
            return $"font={PcCompatDeepDebug.DescribeObject(font)} " +
                   $"atlas={PcCompatDeepDebug.DescribeObject(atlas)} atlasSize={atlasSize} " +
                   $"characters={characterCount} fontMaterial={PcCompatDeepDebug.DescribeObject(fontMaterial)} " +
                   $"textMaterial={PcCompatDeepDebug.DescribeObject(textMaterial)}";
        }
        catch (Exception exception)
        {
            var unwrapped = Unwrap(exception);
            return $"snapshot-failed={unwrapped.GetType().Name}:" +
                   PcCompatDeepDebug.Sanitize(unwrapped.Message);
        }
    }

    private static Factory ResolveFactory(Type sourceFontType)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(sourceFontType.Assembly);
        var textMeshPro = loadContext?.Assemblies.FirstOrDefault(assembly =>
                              string.Equals(
                                  assembly.GetName().Name,
                                  "Unity.TextMeshPro",
                                  StringComparison.OrdinalIgnoreCase)) ??
                          AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                              ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), loadContext) &&
                              string.Equals(
                                  assembly.GetName().Name,
                                  "Unity.TextMeshPro",
                                  StringComparison.OrdinalIgnoreCase)) ??
                          throw new FileNotFoundException(
                              "Unity.TextMeshPro proxy assembly is unavailable in the MOD load context.");
        var fontAssetType = textMeshPro.GetType(
                                "TMPro.TMP_FontAsset",
                                throwOnError: false,
                                ignoreCase: false) ??
                            throw new TypeLoadException(
                                "TMPro.TMP_FontAsset proxy type is unavailable.");
        var candidates = fontAssetType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "CreateFontAsset" &&
                             method.GetParameters() is [{ ParameterType: var parameterType }] &&
                             parameterType.IsAssignableFrom(sourceFontType))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new MissingMethodException(
                $"Expected one TMP_FontAsset.CreateFontAsset({sourceFontType.FullName}), " +
                $"found {candidates.Length}.");
        }
        return new Factory(candidates[0]);
    }

    private static Validator ResolveValidator(Type fontAssetType)
    {
        var getFace = fontAssetType.GetMethod(
                          "get_faceInfo",
                          BindingFlags.Public | BindingFlags.Instance,
                          binder: null,
                          Type.EmptyTypes,
                          modifiers: null) ??
                      throw new MissingMethodException(fontAssetType.FullName, "get_faceInfo");
        var faceType = getFace.ReturnType;
        return new Validator(
            getFace,
            RequireMetric(faceType, "m_PointSize"),
            RequireMetric(faceType, "m_UnitsPerEM"),
            RequireMetric(faceType, "m_LineHeight"),
            RequireGetter(fontAssetType, "characterTable"),
            RequireGetter(fontAssetType, "atlasTexture"),
            RequireGetter(fontAssetType, "material"),
            fontAssetType.GetMethod(
                "TryAddCharacters",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                [typeof(string), typeof(string).MakeByRefType(), typeof(bool)],
                modifiers: null) ??
            throw new MissingMethodException(
                fontAssetType.FullName,
                "TryAddCharacters(String,String&,Boolean)"));
    }

    private static TextureValidator ResolveTextureValidator(Type textureType)
        => new(
            RequireGetter(textureType, "width"),
            RequireGetter(textureType, "height"));

    private static TextBinder ResolveTextBinder(Type textType)
        => new(
            RequireGetter(textType, "font"),
            RequireGetter(textType, "fontMaterial"),
            RequireSingleArgumentMethod(textType, "set_font"),
            RequireSingleArgumentMethod(textType, "set_fontMaterial"),
            RequireSingleArgumentMethod(textType, "set_fontSharedMaterial"),
            textType.GetMethod(
                "SetAllDirty",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                binder: null,
                Type.EmptyTypes,
                modifiers: null));

    private static MaterialAtlasBinder ResolveMaterialAtlasBinder(
        Type materialType,
        Type atlasType)
    {
        var candidates = materialType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == "SetTexture" &&
                             method.GetParameters() is
                             [
                                 { ParameterType: var propertyType },
                                 { ParameterType: var textureType }
                             ] &&
                             propertyType == typeof(string) &&
                             textureType.IsAssignableFrom(atlasType))
            .ToArray();
        if (candidates.Length != 1)
        {
            throw new MissingMethodException(
                materialType.FullName,
                $"SetTexture(String,{atlasType.FullName}) candidates={candidates.Length}");
        }
        return new MaterialAtlasBinder(candidates[0]);
    }

    private static MethodInfo RequireGetter(Type type, string propertyName)
        => type.GetMethod(
               "get_" + propertyName,
               BindingFlags.Public | BindingFlags.Instance,
               binder: null,
               Type.EmptyTypes,
               modifiers: null) ??
           type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetMethod ??
           throw new MissingMemberException(type.FullName, propertyName);

    private static MethodInfo RequireSingleArgumentMethod(Type type, string methodName)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == methodName && method.GetParameters().Length == 1)
            .ToArray();
        if (methods.Length != 1)
            throw new MissingMethodException(type.FullName, $"{methodName}(...) candidates={methods.Length}");
        return methods[0];
    }

    private static int ReadCollectionCount(object collection)
    {
        if (collection is System.Collections.ICollection typed)
            return typed.Count;
        var count = collection.GetType().GetProperty(
            "Count",
            BindingFlags.Public | BindingFlags.Instance);
        if (count == null)
            throw new MissingMemberException(collection.GetType().FullName, "Count");
        return Convert.ToInt32(count.GetValue(collection));
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is TargetInvocationException { InnerException: not null } invocation)
            exception = invocation.InnerException!;
        return exception;
    }

    private static PropertyInfo RequireMetric(Type faceType, string name)
        => faceType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance) ??
           throw new MissingMemberException(faceType.FullName, name);

    private sealed record Factory(MethodInfo Create);
    private sealed record Validator(
        MethodInfo GetFace,
        PropertyInfo PointSize,
        PropertyInfo UnitsPerEm,
        PropertyInfo LineHeight,
        MethodInfo GetCharacterTable,
        MethodInfo GetAtlasTexture,
        MethodInfo GetMaterial,
        MethodInfo TryAddCharacters);
    private sealed record TextureValidator(MethodInfo GetWidth, MethodInfo GetHeight);
    private sealed record TextBinder(
        MethodInfo GetFont,
        MethodInfo GetMaterial,
        MethodInfo SetFont,
        MethodInfo SetMaterial,
        MethodInfo SetSharedMaterial,
        MethodInfo? SetAllDirty);
    private sealed record MaterialAtlasBinder(MethodInfo SetTexture);
}
