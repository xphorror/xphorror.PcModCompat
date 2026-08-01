using System.Linq.Expressions;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Xphorror.PcModCompat;
using Xphorror.PcModCompat.Resources;

namespace StArray.ModManager.Android.PcCompat;

/// <summary>
/// Low-frequency UnityMain object reconstruction through generated proxies.
/// </summary>
internal sealed class PcCompatGeneratedUnityResourceApi
{
    private const string Core = "UnityEngine.CoreModule";
    private const string TextCoreText = "UnityEngine.TextCoreTextEngineModule";
    private const int DontUnloadUnusedAsset = 32;
    private static readonly string[] FaceInfoFieldNames =
    [
        "m_FaceIndex", "m_FamilyName", "m_StyleName", "m_PointSize", "m_Scale",
        "m_UnitsPerEM", "m_LineHeight", "m_AscentLine", "m_CapLine", "m_MeanLine",
        "m_Baseline", "m_DescentLine", "m_SuperscriptOffset", "m_SuperscriptSize",
        "m_SubscriptOffset", "m_SubscriptSize", "m_UnderlineOffset",
        "m_UnderlineThickness", "m_StrikethroughOffset", "m_StrikethroughThickness",
        "m_TabWidth"
    ];
    private readonly Type _objectType;
    private readonly Type _textureType;
    private readonly Type _texture2DType;
    private readonly Type _spriteType;
    private readonly Type _materialType;
    private readonly Type _fontType;
    private readonly Type _gameObjectType;
    private readonly Type _transformType;
    private readonly Type _rectTransformType;
    private readonly Type _canvasRendererType;
    private readonly Type _graphicType;
    private readonly Type _maskableGraphicType;
    private readonly Type _imageType;
    private readonly Type _rawImageType;
    private readonly Type _tmpFontAssetType;
    private readonly Type _tmpAssetType;
    private readonly Type _tmpCharacterType;
    private readonly Type _tmpTextElementType;
    private readonly Type _textCoreFontAssetType;
    private readonly Type _textCoreTextAssetType;
    private readonly Type _textCoreCharacterType;
    private readonly Type _textCoreAtlasPopulationModeType;
    private readonly Type _faceInfoType;
    private readonly Type _glyphType;
    private readonly Type _glyphMetricsType;
    private readonly Type _glyphRectType;
    private readonly Type _atlasPopulationModeType;
    private readonly Type _glyphRenderModeType;
    private readonly Type _colorType;
    private readonly Type _vector2Type;
    private readonly Type _globalIlluminationFlagsType;
    private readonly Type _hideFlagsType;
    private readonly Type _textureFormatType;
    private readonly Type _filterModeType;
    private readonly Type _wrapModeType;
    private readonly Type _spriteMeshType;
    private readonly ConstructorInfo _textureConstructor;
    private readonly ConstructorInfo _rectConstructor;
    private readonly ConstructorInfo _vector2Constructor;
    private readonly ConstructorInfo _vector4Constructor;
    private readonly ConstructorInfo _colorConstructor;
    private readonly ConstructorInfo _materialConstructor;
    private readonly ConstructorInfo _fontConstructor;
    private readonly ConstructorInfo _textCoreFontConstructor;
    private readonly ConstructorInfo _textCoreCharacterConstructor;
    private readonly ConstructorInfo _gameObjectConstructor;
    private readonly ConstructorInfo _vector3Constructor;
    private readonly ConstructorInfo _quaternionConstructor;
    private readonly Func<object> _createFaceInfo;
    private readonly Func<object> _createGlyphMetrics;
    private readonly Func<object> _createGlyphRect;
    private readonly Func<object> _createGlyph;
    private readonly Func<object> _createTmpCharacter;
    private readonly MethodInfo _loadRawTextureData;
    private readonly MethodInfo _applyTexture;
    private readonly MethodInfo _setFilterMode;
    private readonly MethodInfo _setWrapModeU;
    private readonly MethodInfo _setWrapModeV;
    private readonly MethodInfo _createSprite;
    private readonly MethodInfo _materialHasProperty;
    private readonly MethodInfo _materialSetInt;
    private readonly MethodInfo _materialSetFloat;
    private readonly MethodInfo _materialSetColor;
    private readonly MethodInfo _materialSetTexture;
    private readonly MethodInfo _materialSetTextureOffset;
    private readonly MethodInfo _materialSetTextureScale;
    private readonly MethodInfo _materialEnableKeyword;
    private readonly MethodInfo _materialSetRenderQueue;
    private readonly MethodInfo _materialSetGlobalIlluminationFlags;
    private readonly MethodInfo _materialSetDoubleSidedGi;
    private readonly MethodInfo _materialSetEnableInstancing;
    private readonly MethodInfo _fontSetMaterial;
    private readonly MethodInfo _instantiate;
    private readonly MethodInfo _dontDestroyOnLoad;
    private readonly MethodInfo _gameObjectSetLayer;
    private readonly MethodInfo _gameObjectSetActive;
    private readonly MethodInfo _gameObjectGetTransform;
    private readonly MethodInfo _gameObjectAddComponent;
    private readonly MethodInfo _transformSetParent;
    private readonly MethodInfo _transformSetLocalPosition;
    private readonly MethodInfo _transformSetLocalRotation;
    private readonly MethodInfo _transformSetLocalScale;
    private readonly MethodInfo _rectSetAnchorMin;
    private readonly MethodInfo _rectSetAnchorMax;
    private readonly MethodInfo _rectSetAnchoredPosition;
    private readonly MethodInfo _rectSetSizeDelta;
    private readonly MethodInfo _rectSetPivot;
    private readonly MethodInfo _canvasRendererSetCullTransparentMesh;
    private readonly MethodInfo _graphicSetColor;
    private readonly MethodInfo _graphicSetRaycastTarget;
    private readonly MethodInfo _graphicSetMaterial;
    private readonly MethodInfo _maskableGraphicSetMaskable;
    private readonly MethodInfo _imageSetSprite;
    private readonly MethodInfo _imageSetType;
    private readonly MethodInfo _imageSetPreserveAspect;
    private readonly MethodInfo _imageSetFillCenter;
    private readonly MethodInfo _imageSetFillMethod;
    private readonly MethodInfo _imageSetFillAmount;
    private readonly MethodInfo _imageSetFillClockwise;
    private readonly MethodInfo _imageSetFillOrigin;
    private readonly MethodInfo _imageSetUseSpriteMesh;
    private readonly MethodInfo _imageSetPixelsPerUnitMultiplier;
    private readonly MethodInfo _rawImageSetTexture;
    private readonly MethodInfo _rawImageSetUvRect;
    private readonly MethodInfo _tmpAssetSetFaceInfo;
    private readonly MethodInfo _tmpAssetSetMaterial;
    private readonly MethodInfo _tmpAssetSetHashCode;
    private readonly MethodInfo _tmpAssetSetMaterialHashCode;
    private readonly MethodInfo _tmpFontSetAtlasPopulationMode;
    private readonly MethodInfo _tmpFontSetGlyphTable;
    private readonly MethodInfo _tmpFontSetCharacterTable;
    private readonly MethodInfo _tmpFontSetAtlasTextures;
    private readonly MethodInfo _tmpFontSetMultiAtlas;
    private readonly MethodInfo _tmpFontSetAtlasWidth;
    private readonly MethodInfo _tmpFontSetAtlasHeight;
    private readonly MethodInfo _tmpFontSetAtlasPadding;
    private readonly MethodInfo _tmpFontSetAtlasRenderMode;
    private readonly MethodInfo _tmpFontReadDefinition;
    private readonly MethodInfo _textCoreAssetSetMaterial;
    private readonly MethodInfo _textCoreAssetSetHashCode;
    private readonly MethodInfo _textCoreAssetSetMaterialHashCode;
    private readonly MethodInfo _textCoreFontSetFaceInfo;
    private readonly MethodInfo _textCoreFontSetAtlasPopulationMode;
    private readonly MethodInfo _textCoreFontSetGlyphTable;
    private readonly MethodInfo _textCoreFontSetCharacterTable;
    private readonly MethodInfo _textCoreFontSetAtlasTextures;
    private readonly MethodInfo _textCoreFontSetMultiAtlas;
    private readonly MethodInfo _textCoreFontSetAtlasWidth;
    private readonly MethodInfo _textCoreFontSetAtlasHeight;
    private readonly MethodInfo _textCoreFontSetAtlasPadding;
    private readonly MethodInfo _textCoreFontSetAtlasRenderMode;
    private readonly MethodInfo _textCoreFontReadDefinition;
    private readonly PropertyInfo[] _faceInfoFields;
    private readonly Action<object, float> _glyphMetricsSetWidth;
    private readonly Action<object, float> _glyphMetricsSetHeight;
    private readonly Action<object, float> _glyphMetricsSetBearingX;
    private readonly Action<object, float> _glyphMetricsSetBearingY;
    private readonly Action<object, float> _glyphMetricsSetAdvance;
    private readonly Action<object, int> _glyphRectSetX;
    private readonly Action<object, int> _glyphRectSetY;
    private readonly Action<object, int> _glyphRectSetWidth;
    private readonly Action<object, int> _glyphRectSetHeight;
    private readonly Action<object, uint> _glyphSetIndex;
    private readonly Action<object, object> _glyphSetMetrics;
    private readonly Action<object, object> _glyphSetRect;
    private readonly Action<object, float> _glyphSetScale;
    private readonly Action<object, int> _glyphSetAtlasIndex;
    private readonly Action<object, int> _glyphSetClassDefinitionType;
    private readonly Action<object, int> _tmpTextElementSetElementType;
    private readonly Action<object, uint> _tmpTextElementSetUnicode;
    private readonly Action<object, object> _tmpTextElementSetTextAsset;
    private readonly Action<object, object> _tmpTextElementSetGlyph;
    private readonly Action<object, uint> _tmpTextElementSetGlyphIndex;
    private readonly Action<object, float> _tmpTextElementSetScale;
    private readonly PropertyInfo _tmpFontAtlasTexture;
    private readonly PropertyInfo _tmpFontAtlasTextureIndex;
    private readonly PropertyInfo _tmpFontNormalStyle;
    private readonly PropertyInfo _tmpFontNormalSpacingOffset;
    private readonly PropertyInfo _tmpFontBoldStyle;
    private readonly PropertyInfo _tmpFontBoldSpacing;
    private readonly PropertyInfo _tmpFontItalicStyle;
    private readonly PropertyInfo _tmpFontTabSize;
    private readonly PropertyInfo _textCoreFontAtlasTexture;
    private readonly PropertyInfo _textCoreFontAtlasTextureIndex;
    private readonly MethodInfo _textCoreFontRegularStyleWeight;
    private readonly MethodInfo _textCoreFontRegularStyleSpacing;
    private readonly MethodInfo _textCoreFontBoldStyleWeight;
    private readonly MethodInfo _textCoreFontBoldStyleSpacing;
    private readonly MethodInfo _textCoreFontItalicStyleSlant;
    private readonly MethodInfo _textCoreFontTabMultiple;
    private readonly MethodInfo _objectImplicit;
    private readonly MethodInfo _getHideFlags;
    private readonly MethodInfo _setHideFlags;
    private readonly MethodInfo _setName;
    private readonly MethodInfo _destroy;
    private readonly Dictionary<object, object> _prefabHolders =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, object> _imguiTextCoreFonts =
        new(ReferenceEqualityComparer.Instance);

    public PcCompatGeneratedUnityResourceApi()
    {
        PcCompatIl2CppInteropBootstrap.RequireReady();
        _objectType = RequiredType(Core, "UnityEngine.Object");
        _textureType = RequiredType(Core, "UnityEngine.Texture");
        _texture2DType = RequiredType(Core, "UnityEngine.Texture2D");
        _spriteType = RequiredType(Core, "UnityEngine.Sprite");
        _materialType = RequiredType(Core, "UnityEngine.Material");
        _fontType = RequiredType("UnityEngine.TextRenderingModule", "UnityEngine.Font");
        _gameObjectType = RequiredType(Core, "UnityEngine.GameObject");
        _transformType = RequiredType(Core, "UnityEngine.Transform");
        _rectTransformType = RequiredType(Core, "UnityEngine.RectTransform");
        _canvasRendererType = RequiredType("UnityEngine.UIModule", "UnityEngine.CanvasRenderer");
        _graphicType = RequiredType("UnityEngine.UI", "UnityEngine.UI.Graphic");
        _maskableGraphicType = RequiredType("UnityEngine.UI", "UnityEngine.UI.MaskableGraphic");
        _imageType = RequiredType("UnityEngine.UI", "UnityEngine.UI.Image");
        _rawImageType = RequiredType("UnityEngine.UI", "UnityEngine.UI.RawImage");
        _tmpFontAssetType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_FontAsset");
        _tmpAssetType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_Asset");
        _tmpCharacterType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_Character");
        _tmpTextElementType = RequiredType("Unity.TextMeshPro", "TMPro.TMP_TextElement");
        _textCoreFontAssetType = RequiredType(TextCoreText, "UnityEngine.TextCore.Text.FontAsset");
        _textCoreTextAssetType = RequiredType(TextCoreText, "UnityEngine.TextCore.Text.TextAsset");
        _textCoreCharacterType = RequiredType(TextCoreText, "UnityEngine.TextCore.Text.Character");
        _textCoreAtlasPopulationModeType = RequiredType(
            TextCoreText,
            "UnityEngine.TextCore.Text.AtlasPopulationMode");
        _faceInfoType = RequiredType(
            "UnityEngine.TextCoreFontEngineModule",
            "UnityEngine.TextCore.FaceInfo");
        _glyphType = RequiredType(
            "UnityEngine.TextCoreFontEngineModule",
            "UnityEngine.TextCore.Glyph");
        _glyphMetricsType = RequiredType(
            "UnityEngine.TextCoreFontEngineModule",
            "UnityEngine.TextCore.GlyphMetrics");
        _glyphRectType = RequiredType(
            "UnityEngine.TextCoreFontEngineModule",
            "UnityEngine.TextCore.GlyphRect");
        _atlasPopulationModeType = RequiredType("Unity.TextMeshPro", "TMPro.AtlasPopulationMode");
        _glyphRenderModeType = RequiredType(
            "UnityEngine.TextCoreFontEngineModule",
            "UnityEngine.TextCore.LowLevel.GlyphRenderMode");
        _colorType = RequiredType(Core, "UnityEngine.Color");
        _vector2Type = RequiredType(Core, "UnityEngine.Vector2");
        _globalIlluminationFlagsType = RequiredType(
            Core,
            "UnityEngine.MaterialGlobalIlluminationFlags");
        _hideFlagsType = RequiredType(Core, "UnityEngine.HideFlags");
        _textureFormatType = RequiredType(Core, "UnityEngine.TextureFormat");
        _filterModeType = RequiredType(Core, "UnityEngine.FilterMode");
        _wrapModeType = RequiredType(Core, "UnityEngine.TextureWrapMode");
        _spriteMeshType = RequiredType(Core, "UnityEngine.SpriteMeshType");
        var rectType = RequiredType(Core, "UnityEngine.Rect");
        var vector2Type = _vector2Type;
        var vector4Type = RequiredType(Core, "UnityEngine.Vector4");
        var vector3Type = RequiredType(Core, "UnityEngine.Vector3");
        var quaternionType = RequiredType(Core, "UnityEngine.Quaternion");

        _textureConstructor = RequiredConstructor(
            _texture2DType,
            typeof(int),
            typeof(int),
            _textureFormatType,
            typeof(bool),
            typeof(bool));
        _rectConstructor = RequiredConstructor(rectType, typeof(float), typeof(float), typeof(float), typeof(float));
        _vector2Constructor = RequiredConstructor(vector2Type, typeof(float), typeof(float));
        _vector4Constructor = RequiredConstructor(vector4Type, typeof(float), typeof(float), typeof(float), typeof(float));
        _colorConstructor = RequiredConstructor(
            _colorType,
            typeof(float),
            typeof(float),
            typeof(float),
            typeof(float));
        _materialConstructor = RequiredConstructor(_materialType, _materialType);
        _fontConstructor = RequiredConstructor(_fontType);
        _textCoreFontConstructor = RequiredConstructor(_textCoreFontAssetType);
        _textCoreCharacterConstructor = RequiredConstructor(
            _textCoreCharacterType,
            typeof(uint),
            _textCoreFontAssetType,
            _glyphType);
        _gameObjectConstructor = RequiredConstructor(
            _gameObjectType,
            typeof(string),
            typeof(Il2CppSystem.Type[]));
        _vector3Constructor = RequiredConstructor(vector3Type, typeof(float), typeof(float), typeof(float));
        _quaternionConstructor = RequiredConstructor(
            quaternionType,
            typeof(float),
            typeof(float),
            typeof(float),
            typeof(float));
        _createFaceInfo = CompileObjectConstructor(RequiredConstructor(_faceInfoType));
        _createGlyphMetrics = CompileDefaultValue(_glyphMetricsType);
        _createGlyphRect = CompileDefaultValue(_glyphRectType);
        _createGlyph = CompileIl2CppObjectAllocator(_glyphType);
        _createTmpCharacter = CompileIl2CppObjectAllocator(_tmpCharacterType);
        _loadRawTextureData = RequiredMethod(
            _texture2DType,
            "LoadRawTextureData",
            isStatic: false,
            typeof(Il2CppStructArray<byte>));
        _applyTexture = RequiredMethod(
            _texture2DType,
            "Apply",
            isStatic: false,
            typeof(bool),
            typeof(bool));
        _setFilterMode = RequiredMethod(_textureType, "set_filterMode", false, _filterModeType);
        _setWrapModeU = RequiredMethod(_textureType, "set_wrapModeU", false, _wrapModeType);
        _setWrapModeV = RequiredMethod(_textureType, "set_wrapModeV", false, _wrapModeType);
        _createSprite = RequiredMethod(
            _spriteType,
            "Create",
            true,
            _texture2DType,
            rectType,
            vector2Type,
            typeof(float),
            typeof(uint),
            _spriteMeshType,
            vector4Type);
        _materialHasProperty = RequiredMethod(_materialType, "HasProperty", false, typeof(string));
        _materialSetInt = RequiredMethod(_materialType, "SetInt", false, typeof(string), typeof(int));
        _materialSetFloat = RequiredMethod(_materialType, "SetFloat", false, typeof(string), typeof(float));
        _materialSetColor = RequiredMethod(_materialType, "SetColor", false, typeof(string), _colorType);
        _materialSetTexture = RequiredMethod(_materialType, "SetTexture", false, typeof(string), _textureType);
        _materialSetTextureOffset = RequiredMethod(
            _materialType,
            "SetTextureOffset",
            false,
            typeof(string),
            _vector2Type);
        _materialSetTextureScale = RequiredMethod(
            _materialType,
            "SetTextureScale",
            false,
            typeof(string),
            _vector2Type);
        _materialEnableKeyword = RequiredMethod(_materialType, "EnableKeyword", false, typeof(string));
        _materialSetRenderQueue = RequiredMethod(_materialType, "set_renderQueue", false, typeof(int));
        _materialSetGlobalIlluminationFlags = RequiredMethod(
            _materialType,
            "set_globalIlluminationFlags",
            false,
            _globalIlluminationFlagsType);
        _materialSetDoubleSidedGi = RequiredMethod(
            _materialType,
            "set_doubleSidedGI",
            false,
            typeof(bool));
        _materialSetEnableInstancing = RequiredMethod(
            _materialType,
            "set_enableInstancing",
            false,
            typeof(bool));
        _fontSetMaterial = RequiredMethod(_fontType, "set_material", false, _materialType);
        _instantiate = RequiredMethod(_objectType, "Instantiate", true, _objectType);
        _dontDestroyOnLoad = RequiredMethod(_objectType, "DontDestroyOnLoad", true, _objectType);
        _gameObjectSetLayer = RequiredMethod(_gameObjectType, "set_layer", false, typeof(int));
        _gameObjectSetActive = RequiredMethod(_gameObjectType, "SetActive", false, typeof(bool));
        _gameObjectGetTransform = RequiredMethod(_gameObjectType, "get_transform", false);
        _gameObjectAddComponent = RequiredMethod(
            _gameObjectType,
            "AddComponent",
            false,
            typeof(Il2CppSystem.Type));
        _transformSetParent = RequiredMethod(
            _transformType,
            "SetParent",
            false,
            _transformType,
            typeof(bool));
        _transformSetLocalPosition = RequiredMethod(
            _transformType,
            "set_localPosition",
            false,
            vector3Type);
        _transformSetLocalRotation = RequiredMethod(
            _transformType,
            "set_localRotation",
            false,
            quaternionType);
        _transformSetLocalScale = RequiredMethod(
            _transformType,
            "set_localScale",
            false,
            vector3Type);
        _rectSetAnchorMin = RequiredMethod(_rectTransformType, "set_anchorMin", false, vector2Type);
        _rectSetAnchorMax = RequiredMethod(_rectTransformType, "set_anchorMax", false, vector2Type);
        _rectSetAnchoredPosition = RequiredMethod(
            _rectTransformType,
            "set_anchoredPosition",
            false,
            vector2Type);
        _rectSetSizeDelta = RequiredMethod(_rectTransformType, "set_sizeDelta", false, vector2Type);
        _rectSetPivot = RequiredMethod(_rectTransformType, "set_pivot", false, vector2Type);
        _canvasRendererSetCullTransparentMesh = RequiredMethod(
            _canvasRendererType,
            "set_cullTransparentMesh",
            false,
            typeof(bool));
        _graphicSetColor = RequiredMethod(_graphicType, "set_color", false, _colorType);
        _graphicSetRaycastTarget = RequiredMethod(
            _graphicType,
            "set_raycastTarget",
            false,
            typeof(bool));
        _graphicSetMaterial = RequiredMethod(_graphicType, "set_material", false, _materialType);
        _maskableGraphicSetMaskable = RequiredMethod(
            _maskableGraphicType,
            "set_maskable",
            false,
            typeof(bool));
        _imageSetSprite = RequiredMethod(_imageType, "set_sprite", false, _spriteType);
        _imageSetType = RequiredSingleParameterMethod(_imageType, "set_type");
        _imageSetPreserveAspect = RequiredMethod(
            _imageType,
            "set_preserveAspect",
            false,
            typeof(bool));
        _imageSetFillCenter = RequiredMethod(_imageType, "set_fillCenter", false, typeof(bool));
        _imageSetFillMethod = RequiredSingleParameterMethod(_imageType, "set_fillMethod");
        _imageSetFillAmount = RequiredMethod(_imageType, "set_fillAmount", false, typeof(float));
        _imageSetFillClockwise = RequiredMethod(
            _imageType,
            "set_fillClockwise",
            false,
            typeof(bool));
        _imageSetFillOrigin = RequiredMethod(_imageType, "set_fillOrigin", false, typeof(int));
        _imageSetUseSpriteMesh = RequiredMethod(
            _imageType,
            "set_useSpriteMesh",
            false,
            typeof(bool));
        _imageSetPixelsPerUnitMultiplier = RequiredMethod(
            _imageType,
            "set_pixelsPerUnitMultiplier",
            false,
            typeof(float));
        _rawImageSetTexture = RequiredMethod(_rawImageType, "set_texture", false, _textureType);
        _rawImageSetUvRect = RequiredMethod(_rawImageType, "set_uvRect", false, rectType);
        _tmpAssetSetFaceInfo = RequiredMethod(_tmpAssetType, "set_faceInfo", false, _faceInfoType);
        _tmpAssetSetMaterial = RequiredMethod(_tmpAssetType, "set_material", false, _materialType);
        _tmpAssetSetHashCode = RequiredMethod(_tmpAssetType, "set_hashCode", false, typeof(int));
        _tmpAssetSetMaterialHashCode = RequiredMethod(
            _tmpAssetType,
            "set_materialHashCode",
            false,
            typeof(int));
        _tmpFontSetAtlasPopulationMode = RequiredMethod(
            _tmpFontAssetType,
            "set_atlasPopulationMode",
            false,
            _atlasPopulationModeType);
        _tmpFontSetGlyphTable = RequiredSingleParameterMethod(_tmpFontAssetType, "set_glyphTable");
        _tmpFontSetCharacterTable = RequiredSingleParameterMethod(
            _tmpFontAssetType,
            "set_characterTable");
        _tmpFontSetAtlasTextures = RequiredSingleParameterMethod(
            _tmpFontAssetType,
            "set_atlasTextures");
        _tmpFontSetMultiAtlas = RequiredMethod(
            _tmpFontAssetType,
            "set_isMultiAtlasTexturesEnabled",
            false,
            typeof(bool));
        _tmpFontSetAtlasWidth = RequiredMethod(_tmpFontAssetType, "set_atlasWidth", false, typeof(int));
        _tmpFontSetAtlasHeight = RequiredMethod(_tmpFontAssetType, "set_atlasHeight", false, typeof(int));
        _tmpFontSetAtlasPadding = RequiredMethod(_tmpFontAssetType, "set_atlasPadding", false, typeof(int));
        _tmpFontSetAtlasRenderMode = RequiredMethod(
            _tmpFontAssetType,
            "set_atlasRenderMode",
            false,
            _glyphRenderModeType);
        _tmpFontReadDefinition = RequiredMethod(
            _tmpFontAssetType,
            "ReadFontAssetDefinition",
            false);
        _textCoreAssetSetMaterial = RequiredMethod(
            _textCoreTextAssetType,
            "set_material",
            false,
            _materialType);
        _textCoreAssetSetHashCode = RequiredMethod(
            _textCoreTextAssetType,
            "set_hashCode",
            false,
            typeof(int));
        _textCoreAssetSetMaterialHashCode = RequiredMethod(
            _textCoreTextAssetType,
            "set_materialHashCode",
            false,
            typeof(int));
        _textCoreFontSetFaceInfo = RequiredMethod(
            _textCoreFontAssetType,
            "set_faceInfo",
            false,
            _faceInfoType);
        _textCoreFontSetAtlasPopulationMode = RequiredMethod(
            _textCoreFontAssetType,
            "set_atlasPopulationMode",
            false,
            _textCoreAtlasPopulationModeType);
        _textCoreFontSetGlyphTable = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_glyphTable");
        _textCoreFontSetCharacterTable = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_characterTable");
        _textCoreFontSetAtlasTextures = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_atlasTextures");
        _textCoreFontSetMultiAtlas = RequiredMethod(
            _textCoreFontAssetType,
            "set_isMultiAtlasTexturesEnabled",
            false,
            typeof(bool));
        _textCoreFontSetAtlasWidth = RequiredMethod(
            _textCoreFontAssetType,
            "set_atlasWidth",
            false,
            typeof(int));
        _textCoreFontSetAtlasHeight = RequiredMethod(
            _textCoreFontAssetType,
            "set_atlasHeight",
            false,
            typeof(int));
        _textCoreFontSetAtlasPadding = RequiredMethod(
            _textCoreFontAssetType,
            "set_atlasPadding",
            false,
            typeof(int));
        _textCoreFontSetAtlasRenderMode = RequiredMethod(
            _textCoreFontAssetType,
            "set_atlasRenderMode",
            false,
            _glyphRenderModeType);
        _textCoreFontReadDefinition = RequiredMethod(
            _textCoreFontAssetType,
            "ReadFontAssetDefinition",
            false);
        _faceInfoFields = FaceInfoFieldNames
            .Select(name => RequiredWritableProperty(_faceInfoType, name))
            .ToArray();
        _glyphMetricsSetWidth = CompileFieldSetter<float>(
            RequiredWritableField(_glyphMetricsType, "m_Width"));
        _glyphMetricsSetHeight = CompileFieldSetter<float>(
            RequiredWritableField(_glyphMetricsType, "m_Height"));
        _glyphMetricsSetBearingX = CompileFieldSetter<float>(
            RequiredWritableField(_glyphMetricsType, "m_HorizontalBearingX"));
        _glyphMetricsSetBearingY = CompileFieldSetter<float>(
            RequiredWritableField(_glyphMetricsType, "m_HorizontalBearingY"));
        _glyphMetricsSetAdvance = CompileFieldSetter<float>(
            RequiredWritableField(_glyphMetricsType, "m_HorizontalAdvance"));
        _glyphRectSetX = CompileFieldSetter<int>(RequiredWritableField(_glyphRectType, "m_X"));
        _glyphRectSetY = CompileFieldSetter<int>(RequiredWritableField(_glyphRectType, "m_Y"));
        _glyphRectSetWidth = CompileFieldSetter<int>(
            RequiredWritableField(_glyphRectType, "m_Width"));
        _glyphRectSetHeight = CompileFieldSetter<int>(
            RequiredWritableField(_glyphRectType, "m_Height"));
        _glyphSetIndex = CompilePropertySetter<uint>(
            RequiredWritableProperty(_glyphType, "m_Index"));
        _glyphSetMetrics = CompileObjectPropertySetter(
            RequiredWritableProperty(_glyphType, "m_Metrics"));
        _glyphSetRect = CompileObjectPropertySetter(
            RequiredWritableProperty(_glyphType, "m_GlyphRect"));
        _glyphSetScale = CompilePropertySetter<float>(
            RequiredWritableProperty(_glyphType, "m_Scale"));
        _glyphSetAtlasIndex = CompilePropertySetter<int>(
            RequiredWritableProperty(_glyphType, "m_AtlasIndex"));
        _glyphSetClassDefinitionType = CompilePropertySetter<int>(
            RequiredWritableProperty(_glyphType, "m_ClassDefinitionType"));
        _tmpTextElementSetElementType = CompilePropertySetter<int>(
            RequiredWritableProperty(_tmpTextElementType, "m_ElementType"));
        _tmpTextElementSetUnicode = CompilePropertySetter<uint>(
            RequiredWritableProperty(_tmpTextElementType, "m_Unicode"));
        _tmpTextElementSetTextAsset = CompileObjectPropertySetter(
            RequiredWritableProperty(_tmpTextElementType, "m_TextAsset"));
        _tmpTextElementSetGlyph = CompileObjectPropertySetter(
            RequiredWritableProperty(_tmpTextElementType, "m_Glyph"));
        _tmpTextElementSetGlyphIndex = CompilePropertySetter<uint>(
            RequiredWritableProperty(_tmpTextElementType, "m_GlyphIndex"));
        _tmpTextElementSetScale = CompilePropertySetter<float>(
            RequiredWritableProperty(_tmpTextElementType, "m_Scale"));
        _tmpFontAtlasTexture = RequiredWritableProperty(_tmpFontAssetType, "m_AtlasTexture");
        _tmpFontAtlasTextureIndex = RequiredWritableProperty(
            _tmpFontAssetType,
            "m_AtlasTextureIndex");
        _tmpFontNormalStyle = RequiredWritableProperty(_tmpFontAssetType, "normalStyle");
        _tmpFontNormalSpacingOffset = RequiredWritableProperty(
            _tmpFontAssetType,
            "normalSpacingOffset");
        _tmpFontBoldStyle = RequiredWritableProperty(_tmpFontAssetType, "boldStyle");
        _tmpFontBoldSpacing = RequiredWritableProperty(_tmpFontAssetType, "boldSpacing");
        _tmpFontItalicStyle = RequiredWritableProperty(_tmpFontAssetType, "italicStyle");
        _tmpFontTabSize = RequiredWritableProperty(_tmpFontAssetType, "tabSize");
        _textCoreFontAtlasTexture = RequiredWritableProperty(
            _textCoreFontAssetType,
            "m_AtlasTexture");
        _textCoreFontAtlasTextureIndex = RequiredWritableProperty(
            _textCoreFontAssetType,
            "m_AtlasTextureIndex");
        _textCoreFontRegularStyleWeight = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_regularStyleWeight");
        _textCoreFontRegularStyleSpacing = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_regularStyleSpacing");
        _textCoreFontBoldStyleWeight = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_boldStyleWeight");
        _textCoreFontBoldStyleSpacing = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_boldStyleSpacing");
        _textCoreFontItalicStyleSlant = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_italicStyleSlant");
        _textCoreFontTabMultiple = RequiredSingleParameterMethod(
            _textCoreFontAssetType,
            "set_tabMultiple");
        _objectImplicit = RequiredMethod(_objectType, "op_Implicit", true, _objectType);
        _getHideFlags = RequiredMethod(_objectType, "get_hideFlags", false);
        _setHideFlags = RequiredMethod(_objectType, "set_hideFlags", false, _hideFlagsType);
        _setName = RequiredMethod(_objectType, "set_name", false, typeof(string));
        _destroy = RequiredMethod(_objectType, "Destroy", true, _objectType);
    }

    public object CreateTexture(PcCompatResourceIrAsset asset, byte[] pixels)
    {
        var texture = asset.Texture
                      ?? throw new InvalidDataException("Texture IR metadata is missing.");
        var alpha8 = asset.MaterializationKind == PcCompatResourceIrMaterializationKind.TextureAlpha8;
        var expectedLength = checked(texture.Width * texture.Height * (alpha8 ? 1 : 4));
        if (pixels.Length != expectedLength || (alpha8 && texture.SourceFormat != 1))
            throw new InvalidDataException("Texture payload format or length does not match Resource IR.");
        var proxy = _textureConstructor.Invoke(
            [
                texture.Width,
                texture.Height,
                Enum.ToObject(_textureFormatType, alpha8 ? 1 : 4),
                false,
                texture.Linear
            ]) ?? throw new InvalidOperationException("Texture2D constructor returned null.");
        _loadRawTextureData.Invoke(proxy, [new Il2CppStructArray<byte>(pixels)]);
        _setFilterMode.Invoke(proxy, [Enum.ToObject(_filterModeType, texture.FilterMode)]);
        _setWrapModeU.Invoke(proxy, [Enum.ToObject(_wrapModeType, texture.WrapU)]);
        _setWrapModeV.Invoke(proxy, [Enum.ToObject(_wrapModeType, texture.WrapV)]);
        _setName.Invoke(proxy, [asset.Name]);
        // Sprite.Create may consume this texture later in the VirtualBundle graph.
        _applyTexture.Invoke(proxy, [false, false]);
        return ProtectFromUnload(proxy);
    }

    public bool IsAlive(object proxy)
    {
        if (!_objectType.IsInstanceOfType(proxy))
            return false;
        return _objectImplicit.Invoke(null, [proxy]) is true;
    }

    public object CreateSprite(PcCompatResourceIrAsset asset, object textureProxy)
    {
        var sprite = asset.Sprite
                     ?? throw new InvalidDataException("Sprite IR metadata is missing.");
        if (!_texture2DType.IsInstanceOfType(textureProxy))
            throw new InvalidCastException("Sprite dependency is not a generated Texture2D proxy.");
        var rect = _rectConstructor.Invoke([sprite.X, sprite.Y, sprite.Width, sprite.Height]);
        var pivot = _vector2Constructor.Invoke([sprite.PivotX, sprite.PivotY]);
        var border = _vector4Constructor.Invoke(
            [sprite.BorderLeft, sprite.BorderBottom, sprite.BorderRight, sprite.BorderTop]);
        var proxy = _createSprite.Invoke(
            null,
            [
                textureProxy,
                rect,
                pivot,
                sprite.PixelsPerUnit,
                sprite.Extrude,
                Enum.ToObject(_spriteMeshType, 0),
                border
            ]) ?? throw new InvalidOperationException("Sprite.Create returned null.");
        _setName.Invoke(proxy, [asset.Name]);
        return ProtectFromUnload(proxy);
    }

    public object CreateMaterial(
        PcCompatResourceIrAsset asset,
        object baseMaterialProxy,
        IReadOnlyDictionary<string, object> textureDependencies)
    {
        var material = asset.Material
                       ?? throw new InvalidDataException("Material IR metadata is missing.");
        if (!_materialType.IsInstanceOfType(baseMaterialProxy))
            throw new InvalidCastException("Material capability is not a generated Material proxy.");
        var proxy = _materialConstructor.Invoke([baseMaterialProxy])
                    ?? throw new InvalidOperationException("Material copy constructor returned null.");
        try
        {
            var propertyNames = material.Ints.Select(value => value.PropertyName)
                .Concat(material.Floats.Select(value => value.PropertyName))
                .Concat(material.Colors.Select(value => value.PropertyName))
                .Concat(material.Textures.Select(value => value.PropertyName));
            foreach (var propertyName in propertyNames)
            {
                if (_materialHasProperty.Invoke(proxy, [propertyName]) is not true)
                    throw new InvalidOperationException(
                        $"Material capability lacks required property: {propertyName}");
            }
            foreach (var value in material.Ints)
                _materialSetInt.Invoke(proxy, [value.PropertyName, value.Value]);
            foreach (var value in material.Floats)
                _materialSetFloat.Invoke(proxy, [value.PropertyName, value.Value]);
            foreach (var value in material.Colors)
            {
                var color = _colorConstructor.Invoke([value.R, value.G, value.B, value.A]);
                _materialSetColor.Invoke(proxy, [value.PropertyName, color]);
            }
            foreach (var value in material.Textures)
            {
                object? texture = null;
                if (value.TextureAssetId.Length != 0)
                {
                    if (!textureDependencies.TryGetValue(value.TextureAssetId, out texture) ||
                        !_textureType.IsInstanceOfType(texture))
                        throw new InvalidOperationException(
                            $"Material Texture dependency is unavailable: {value.TextureAssetId}");
                }
                _materialSetTexture.Invoke(proxy, [value.PropertyName, texture]);
                var offset = _vector2Constructor.Invoke([value.OffsetX, value.OffsetY]);
                var scale = _vector2Constructor.Invoke([value.ScaleX, value.ScaleY]);
                _materialSetTextureOffset.Invoke(proxy, [value.PropertyName, offset]);
                _materialSetTextureScale.Invoke(proxy, [value.PropertyName, scale]);
            }
            foreach (var keyword in material.Keywords)
                _materialEnableKeyword.Invoke(proxy, [keyword]);
            _materialSetRenderQueue.Invoke(proxy, [material.CustomRenderQueue]);
            _materialSetGlobalIlluminationFlags.Invoke(
                proxy,
                [Enum.ToObject(_globalIlluminationFlagsType, material.GlobalIlluminationFlags)]);
            _materialSetDoubleSidedGi.Invoke(proxy, [material.DoubleSidedGi]);
            _materialSetEnableInstancing.Invoke(proxy, [material.EnableInstancing]);
            _setName.Invoke(proxy, [asset.Name]);
            return ProtectFromUnload(proxy);
        }
        catch
        {
            Destroy(proxy);
            throw;
        }
    }

    public object CreateTmpFont(
        PcCompatResourceIrAsset asset,
        object shellFontProxy,
        object materialProxy,
        IReadOnlyList<object> atlasTextureProxies,
        ResourceIrTmpFontPayload payload)
    {
        var font = asset.TmpFont
                   ?? throw new InvalidDataException("TMP font IR metadata is missing.");
        if (!_tmpFontAssetType.IsInstanceOfType(shellFontProxy))
            throw new InvalidCastException("TMP font shell is not a generated TMP_FontAsset proxy.");
        if (!_materialType.IsInstanceOfType(materialProxy))
            throw new InvalidCastException("TMP font material is not a generated Material proxy.");
        if (payload.Glyphs.Count != font.GlyphCount ||
            payload.Characters.Count != font.CharacterCount)
            throw new InvalidDataException("TMP font payload count disagrees with Resource IR.");
        if (atlasTextureProxies.Count != font.AtlasTextureAssetIds.Count ||
            atlasTextureProxies.Any(value => !_texture2DType.IsInstanceOfType(value)))
            throw new InvalidDataException("TMP font atlas dependency count or type is invalid.");
        if (payload.Glyphs.Any(glyph =>
                glyph.AtlasIndex >= atlasTextureProxies.Count ||
                (long)glyph.RectX + glyph.RectWidth > font.AtlasWidth ||
                (long)glyph.RectY + glyph.RectHeight > font.AtlasHeight))
            throw new InvalidDataException("TMP glyph lies outside its declared atlas.");

        var face = font.Face;
        var faceInfo = _createFaceInfo();
        SetProperties(
            _faceInfoFields,
            faceInfo,
            [
                face.FaceIndex,
            face.FamilyName,
            face.StyleName,
            face.PointSize,
            face.Scale,
            face.UnitsPerEm,
            face.LineHeight,
            face.AscentLine,
            face.CapLine,
            face.MeanLine,
            face.Baseline,
            face.DescentLine,
            face.SuperscriptOffset,
            face.SuperscriptSize,
            face.SubscriptOffset,
            face.SubscriptSize,
            face.UnderlineOffset,
            face.UnderlineThickness,
            face.StrikethroughOffset,
            face.StrikethroughThickness,
            face.TabWidth
            ]);
        var glyphsByIndex = new Dictionary<uint, object>(payload.Glyphs.Count);
        var glyphList = CreateIl2CppList(_glyphType, payload.Glyphs.Count, glyph =>
        {
            var metrics = _createGlyphMetrics();
            _glyphMetricsSetWidth(metrics, glyph.Width);
            _glyphMetricsSetHeight(metrics, glyph.Height);
            _glyphMetricsSetBearingX(metrics, glyph.HorizontalBearingX);
            _glyphMetricsSetBearingY(metrics, glyph.HorizontalBearingY);
            _glyphMetricsSetAdvance(metrics, glyph.HorizontalAdvance);
            var rect = _createGlyphRect();
            _glyphRectSetX(rect, glyph.RectX);
            _glyphRectSetY(rect, glyph.RectY);
            _glyphRectSetWidth(rect, glyph.RectWidth);
            _glyphRectSetHeight(rect, glyph.RectHeight);
            var proxy = _createGlyph();
            _glyphSetIndex(proxy, glyph.Index);
            _glyphSetMetrics(proxy, metrics);
            _glyphSetRect(proxy, rect);
            _glyphSetScale(proxy, glyph.Scale);
            _glyphSetAtlasIndex(proxy, glyph.AtlasIndex);
            _glyphSetClassDefinitionType(proxy, glyph.ClassDefinitionType);
            if (!glyphsByIndex.TryAdd(glyph.Index, proxy))
                throw new InvalidDataException($"Duplicate TMP glyph index: {glyph.Index}");
            return proxy;
        }, payload.Glyphs);
        var characterList = CreateIl2CppList(_tmpCharacterType, payload.Characters.Count, character =>
        {
            if (!glyphsByIndex.TryGetValue(character.GlyphIndex, out var glyph))
                throw new InvalidDataException(
                    $"TMP character references missing glyph index: {character.GlyphIndex}");
            var proxy = _createTmpCharacter();
            _tmpTextElementSetUnicode(proxy, character.Unicode);
            _tmpTextElementSetTextAsset(proxy, shellFontProxy);
            _tmpTextElementSetGlyph(proxy, glyph);
            _tmpTextElementSetGlyphIndex(proxy, character.GlyphIndex);
            _tmpTextElementSetScale(proxy, character.Scale);
            _tmpTextElementSetElementType(proxy, character.ElementType);
            return proxy;
        }, payload.Characters);
        var atlasArray = CreateIl2CppReferenceArray(
            _tmpFontSetAtlasTextures.GetParameters()[0].ParameterType,
            atlasTextureProxies);
        _tmpAssetSetFaceInfo.Invoke(shellFontProxy, [faceInfo]);
        _tmpAssetSetMaterial.Invoke(shellFontProxy, [materialProxy]);
        _tmpAssetSetHashCode.Invoke(shellFontProxy, [0]);
        _tmpAssetSetMaterialHashCode.Invoke(shellFontProxy, [0]);
        _tmpFontSetAtlasPopulationMode.Invoke(
            shellFontProxy,
            [Enum.ToObject(_atlasPopulationModeType, font.AtlasPopulationMode)]);
        _tmpFontSetGlyphTable.Invoke(shellFontProxy, [glyphList]);
        _tmpFontSetCharacterTable.Invoke(shellFontProxy, [characterList]);
        _tmpFontSetAtlasTextures.Invoke(shellFontProxy, [atlasArray]);
        _tmpFontAtlasTexture.SetValue(shellFontProxy, atlasTextureProxies[font.AtlasTextureIndex]);
        _tmpFontAtlasTextureIndex.SetValue(shellFontProxy, font.AtlasTextureIndex);
        _tmpFontSetMultiAtlas.Invoke(shellFontProxy, [font.MultiAtlasTexturesEnabled]);
        _tmpFontSetAtlasWidth.Invoke(shellFontProxy, [font.AtlasWidth]);
        _tmpFontSetAtlasHeight.Invoke(shellFontProxy, [font.AtlasHeight]);
        _tmpFontSetAtlasPadding.Invoke(shellFontProxy, [font.AtlasPadding]);
        _tmpFontSetAtlasRenderMode.Invoke(
            shellFontProxy,
            [Enum.ToObject(_glyphRenderModeType, font.AtlasRenderMode)]);
        SetNumericProperty(_tmpFontNormalStyle, shellFontProxy, font.NormalStyle);
        SetNumericProperty(_tmpFontNormalSpacingOffset, shellFontProxy, font.NormalSpacingOffset);
        SetNumericProperty(_tmpFontBoldStyle, shellFontProxy, font.BoldStyle);
        SetNumericProperty(_tmpFontBoldSpacing, shellFontProxy, font.BoldSpacing);
        SetNumericProperty(_tmpFontItalicStyle, shellFontProxy, font.ItalicStyle);
        SetNumericProperty(_tmpFontTabSize, shellFontProxy, font.TabSize);
        _setName.Invoke(shellFontProxy, [asset.Name]);
        _tmpFontReadDefinition.Invoke(shellFontProxy, null);
        return ProtectFromUnload(shellFontProxy);
    }

    public object CreateImGuiFontFromTmpAtlas(
        PcCompatResourceIrAsset asset,
        object materialProxy,
        IReadOnlyList<object> atlasTextureProxies,
        ResourceIrTmpFontPayload payload)
    {
        var fontInfo = asset.TmpFont
                       ?? throw new InvalidDataException("TMP font projection metadata is missing.");
        if (!_materialType.IsInstanceOfType(materialProxy))
            throw new InvalidCastException("TMP font projection material is not a generated Material proxy.");
        if (atlasTextureProxies.Count != fontInfo.AtlasTextureAssetIds.Count ||
            atlasTextureProxies.Any(value => !_texture2DType.IsInstanceOfType(value)))
            throw new InvalidDataException("TMP font projection atlas dependency count or type is invalid.");
        if (payload.Glyphs.Count != fontInfo.GlyphCount ||
            payload.Characters.Count != fontInfo.CharacterCount)
            throw new InvalidDataException("TMP font projection payload count disagrees with Resource IR.");
        if (payload.Glyphs.Any(glyph =>
                glyph.AtlasIndex >= atlasTextureProxies.Count ||
                (long)glyph.RectX + glyph.RectWidth > fontInfo.AtlasWidth ||
                (long)glyph.RectY + glyph.RectHeight > fontInfo.AtlasHeight))
            throw new InvalidDataException("TMP font projection glyph lies outside its declared atlas.");

        object? font = null;
        object? textCoreFont = null;
        var mappingRegistered = false;
        try
        {
            textCoreFont = _textCoreFontConstructor.Invoke([])
                           ?? throw new InvalidOperationException(
                               "Unity TextCore FontAsset constructor returned null.");
            var face = fontInfo.Face;
            var faceInfo = _createFaceInfo();
            SetProperties(
                _faceInfoFields,
                faceInfo,
                [
                    face.FaceIndex,
                    face.FamilyName,
                    face.StyleName,
                    face.PointSize,
                    face.Scale,
                    face.UnitsPerEm,
                    face.LineHeight,
                    face.AscentLine,
                    face.CapLine,
                    face.MeanLine,
                    face.Baseline,
                    face.DescentLine,
                    face.SuperscriptOffset,
                    face.SuperscriptSize,
                    face.SubscriptOffset,
                    face.SubscriptSize,
                    face.UnderlineOffset,
                    face.UnderlineThickness,
                    face.StrikethroughOffset,
                    face.StrikethroughThickness,
                    face.TabWidth
                ]);
            var glyphsByIndex = new Dictionary<uint, object>(payload.Glyphs.Count);
            var glyphList = CreateIl2CppList(_glyphType, payload.Glyphs.Count, glyph =>
            {
                var metrics = _createGlyphMetrics();
                _glyphMetricsSetWidth(metrics, glyph.Width);
                _glyphMetricsSetHeight(metrics, glyph.Height);
                _glyphMetricsSetBearingX(metrics, glyph.HorizontalBearingX);
                _glyphMetricsSetBearingY(metrics, glyph.HorizontalBearingY);
                _glyphMetricsSetAdvance(metrics, glyph.HorizontalAdvance);
                var rect = _createGlyphRect();
                _glyphRectSetX(rect, glyph.RectX);
                _glyphRectSetY(rect, glyph.RectY);
                _glyphRectSetWidth(rect, glyph.RectWidth);
                _glyphRectSetHeight(rect, glyph.RectHeight);
                var proxy = _createGlyph();
                _glyphSetIndex(proxy, glyph.Index);
                _glyphSetMetrics(proxy, metrics);
                _glyphSetRect(proxy, rect);
                _glyphSetScale(proxy, glyph.Scale);
                _glyphSetAtlasIndex(proxy, glyph.AtlasIndex);
                _glyphSetClassDefinitionType(proxy, glyph.ClassDefinitionType);
                if (!glyphsByIndex.TryAdd(glyph.Index, proxy))
                    throw new InvalidDataException($"Duplicate TextCore glyph index: {glyph.Index}");
                return proxy;
            }, payload.Glyphs);
            var characterList = CreateIl2CppList(
                _textCoreCharacterType,
                payload.Characters.Count,
                character =>
                {
                    if (!glyphsByIndex.TryGetValue(character.GlyphIndex, out var glyph))
                    {
                        throw new InvalidDataException(
                            $"TextCore character references missing glyph index: {character.GlyphIndex}");
                    }
                    return _textCoreCharacterConstructor.Invoke(
                               [character.Unicode, textCoreFont, glyph])
                           ?? throw new InvalidOperationException(
                               "Unity TextCore Character constructor returned null.");
                },
                payload.Characters);
            var atlasArray = CreateIl2CppReferenceArray(
                _textCoreFontSetAtlasTextures.GetParameters()[0].ParameterType,
                atlasTextureProxies);

            _textCoreAssetSetMaterial.Invoke(textCoreFont, [materialProxy]);
            _textCoreAssetSetHashCode.Invoke(textCoreFont, [0]);
            _textCoreAssetSetMaterialHashCode.Invoke(textCoreFont, [0]);
            _textCoreFontSetFaceInfo.Invoke(textCoreFont, [faceInfo]);
            _textCoreFontSetAtlasPopulationMode.Invoke(
                textCoreFont,
                [Enum.ToObject(_textCoreAtlasPopulationModeType, 0)]);
            _textCoreFontSetGlyphTable.Invoke(textCoreFont, [glyphList]);
            _textCoreFontSetCharacterTable.Invoke(textCoreFont, [characterList]);
            _textCoreFontSetAtlasTextures.Invoke(textCoreFont, [atlasArray]);
            _textCoreFontAtlasTexture.SetValue(
                textCoreFont,
                atlasTextureProxies[fontInfo.AtlasTextureIndex]);
            _textCoreFontAtlasTextureIndex.SetValue(textCoreFont, fontInfo.AtlasTextureIndex);
            _textCoreFontSetMultiAtlas.Invoke(textCoreFont, [fontInfo.MultiAtlasTexturesEnabled]);
            _textCoreFontSetAtlasWidth.Invoke(textCoreFont, [fontInfo.AtlasWidth]);
            _textCoreFontSetAtlasHeight.Invoke(textCoreFont, [fontInfo.AtlasHeight]);
            _textCoreFontSetAtlasPadding.Invoke(textCoreFont, [fontInfo.AtlasPadding]);
            _textCoreFontSetAtlasRenderMode.Invoke(
                textCoreFont,
                [Enum.ToObject(_glyphRenderModeType, fontInfo.AtlasRenderMode)]);
            InvokeNumericSetter(_textCoreFontRegularStyleWeight, textCoreFont, fontInfo.NormalStyle);
            InvokeNumericSetter(
                _textCoreFontRegularStyleSpacing,
                textCoreFont,
                fontInfo.NormalSpacingOffset);
            InvokeNumericSetter(_textCoreFontBoldStyleWeight, textCoreFont, fontInfo.BoldStyle);
            InvokeNumericSetter(_textCoreFontBoldStyleSpacing, textCoreFont, fontInfo.BoldSpacing);
            InvokeNumericSetter(_textCoreFontItalicStyleSlant, textCoreFont, fontInfo.ItalicStyle);
            InvokeNumericSetter(_textCoreFontTabMultiple, textCoreFont, fontInfo.TabSize);
            _setName.Invoke(textCoreFont, [asset.Name + " [PcCompat TextCore]"]);
            _textCoreFontReadDefinition.Invoke(textCoreFont, null);
            ProtectFromUnload(textCoreFont);

            font = _fontConstructor.Invoke([])
                   ?? throw new InvalidOperationException("Unity Font identity constructor returned null.");
            _fontSetMaterial.Invoke(font, [materialProxy]);
            _setName.Invoke(font, [asset.Name + " [PcCompat IMGUI]"]);
            ProtectFromUnload(font);
            PcCompatNativeHookRules.RegisterImGuiFontMapping(
                RequiredPointer(font, "Unity Font identity"),
                RequiredPointer(textCoreFont, "Unity TextCore FontAsset"));
            mappingRegistered = true;
            _imguiTextCoreFonts.Add(font, textCoreFont);
            return font;
        }
        catch
        {
            if (mappingRegistered && font != null)
            {
                PcCompatNativeHookRules.UnregisterImGuiFontMapping(
                    RequiredPointer(font, "Unity Font identity"));
            }
            if (font != null)
                _destroy.Invoke(null, [font]);
            if (textCoreFont != null)
                _destroy.Invoke(null, [textCoreFont]);
            throw;
        }
    }

    public object CreatePrefab(
        PcCompatResourceIrAsset asset,
        IReadOnlyDictionary<string, object> dependencies)
    {
        var prefab = asset.Prefab
                     ?? throw new InvalidDataException("Prefab graph IR metadata is missing.");
        if (prefab.Nodes.Count == 0)
            throw new InvalidDataException("Prefab graph has no root node.");

        object? holder = null;
        try
        {
            holder = CreateGameObject("__PcCompatPrefabHolder:" + asset.Id, isRectTransform: false);
            _gameObjectSetActive.Invoke(holder, [false]);
            _dontDestroyOnLoad.Invoke(null, [holder]);
            var holderTransform = RequiredResult(
                _gameObjectGetTransform.Invoke(holder, null),
                "Prefab holder Transform");
            var gameObjects = new object[prefab.Nodes.Count];
            var transforms = new object[prefab.Nodes.Count];
            for (var index = 0; index < prefab.Nodes.Count; index++)
            {
                var node = prefab.Nodes[index];
                var gameObject = CreateGameObject(node.Name, node.Transform.IsRectTransform);
                gameObjects[index] = gameObject;
                _gameObjectSetActive.Invoke(gameObject, [false]);
                _gameObjectSetLayer.Invoke(gameObject, [node.Layer]);
                var transform = RequiredResult(
                    _gameObjectGetTransform.Invoke(gameObject, null),
                    $"Prefab node Transform index={index}");
                transforms[index] = transform;
                var parent = node.ParentIndex < 0 ? holderTransform : transforms[node.ParentIndex];
                _transformSetParent.Invoke(transform, [parent, false]);
                ApplyTransform(node.Transform, transform);

                if (node.CanvasRenderer != null)
                {
                    var canvasRenderer = AddComponent(gameObject, _canvasRendererType);
                    _canvasRendererSetCullTransparentMesh.Invoke(
                        canvasRenderer,
                        [node.CanvasRenderer.CullTransparentMesh]);
                }
                if (node.Image != null)
                    ConfigureImage(AddComponent(gameObject, _imageType), node.Image, dependencies);
                if (node.RawImage != null)
                    ConfigureRawImage(AddComponent(gameObject, _rawImageType), node.RawImage, dependencies);
            }
            for (var index = prefab.Nodes.Count - 1; index >= 0; index--)
                _gameObjectSetActive.Invoke(gameObjects[index], [prefab.Nodes[index].Active]);

            var root = gameObjects[0];
            _prefabHolders.Add(root, holder);
            holder = null;
            return root;
        }
        catch
        {
            if (holder != null)
                _destroy.Invoke(null, [holder]);
            throw;
        }
    }

    private object CreateGameObject(string name, bool isRectTransform)
    {
        Il2CppSystem.Type[] components = isRectTransform
            ? [Il2CppType.From(_rectTransformType)]
            : [];
        return RequiredResult(
            _gameObjectConstructor.Invoke([name, components]),
            "GameObject constructor");
    }

    private object AddComponent(object gameObject, Type componentType)
    {
        var component = RequiredResult(
            _gameObjectAddComponent.Invoke(gameObject, [Il2CppType.From(componentType)]),
            "GameObject.AddComponent(" + componentType.FullName + ")");
        return Rewrap(component, componentType);
    }

    private void ApplyTransform(PcCompatResourceIrPrefabTransform value, object transform)
    {
        _transformSetLocalPosition.Invoke(transform,
        [
            _vector3Constructor.Invoke(
                [value.LocalPositionX, value.LocalPositionY, value.LocalPositionZ])
        ]);
        _transformSetLocalRotation.Invoke(transform,
        [
            _quaternionConstructor.Invoke(
                [value.LocalRotationX, value.LocalRotationY, value.LocalRotationZ, value.LocalRotationW])
        ]);
        _transformSetLocalScale.Invoke(transform,
        [
            _vector3Constructor.Invoke([value.LocalScaleX, value.LocalScaleY, value.LocalScaleZ])
        ]);
        if (!value.IsRectTransform)
            return;
        var rect = Rewrap(transform, _rectTransformType);
        _rectSetAnchorMin.Invoke(rect, [_vector2Constructor.Invoke([value.AnchorMinX, value.AnchorMinY])]);
        _rectSetAnchorMax.Invoke(rect, [_vector2Constructor.Invoke([value.AnchorMaxX, value.AnchorMaxY])]);
        _rectSetAnchoredPosition.Invoke(
            rect,
            [_vector2Constructor.Invoke([value.AnchoredPositionX, value.AnchoredPositionY])]);
        _rectSetSizeDelta.Invoke(rect, [_vector2Constructor.Invoke([value.SizeDeltaX, value.SizeDeltaY])]);
        _rectSetPivot.Invoke(rect, [_vector2Constructor.Invoke([value.PivotX, value.PivotY])]);
    }

    private void ConfigureImage(
        object image,
        PcCompatResourceIrPrefabImage value,
        IReadOnlyDictionary<string, object> dependencies)
    {
        ConfigureGraphic(image, value.Graphic, dependencies);
        if (value.SpriteAssetId.Length != 0)
            _imageSetSprite.Invoke(image, [RequiredDependency(dependencies, value.SpriteAssetId, _spriteType)]);
        _imageSetType.Invoke(
            image,
            [Enum.ToObject(_imageSetType.GetParameters()[0].ParameterType, value.Type)]);
        _imageSetPreserveAspect.Invoke(image, [value.PreserveAspect]);
        _imageSetFillCenter.Invoke(image, [value.FillCenter]);
        _imageSetFillMethod.Invoke(
            image,
            [Enum.ToObject(_imageSetFillMethod.GetParameters()[0].ParameterType, value.FillMethod)]);
        _imageSetFillAmount.Invoke(image, [value.FillAmount]);
        _imageSetFillClockwise.Invoke(image, [value.FillClockwise]);
        _imageSetFillOrigin.Invoke(image, [value.FillOrigin]);
        _imageSetUseSpriteMesh.Invoke(image, [value.UseSpriteMesh]);
        _imageSetPixelsPerUnitMultiplier.Invoke(image, [value.PixelsPerUnitMultiplier]);
    }

    private void ConfigureRawImage(
        object image,
        PcCompatResourceIrPrefabRawImage value,
        IReadOnlyDictionary<string, object> dependencies)
    {
        ConfigureGraphic(image, value.Graphic, dependencies);
        if (value.TextureAssetId.Length != 0)
            _rawImageSetTexture.Invoke(
                image,
                [RequiredDependency(dependencies, value.TextureAssetId, _textureType)]);
        _rawImageSetUvRect.Invoke(
            image,
            [_rectConstructor.Invoke([value.UvX, value.UvY, value.UvWidth, value.UvHeight])]);
    }

    private void ConfigureGraphic(
        object graphic,
        PcCompatResourceIrPrefabGraphic value,
        IReadOnlyDictionary<string, object> dependencies)
    {
        _graphicSetColor.Invoke(
            graphic,
            [_colorConstructor.Invoke([value.ColorR, value.ColorG, value.ColorB, value.ColorA])]);
        _graphicSetRaycastTarget.Invoke(graphic, [value.RaycastTarget]);
        _maskableGraphicSetMaskable.Invoke(graphic, [value.Maskable]);
        if (value.MaterialAssetId.Length != 0)
            _graphicSetMaterial.Invoke(
                graphic,
                [RequiredDependency(dependencies, value.MaterialAssetId, _materialType)]);
    }

    private static object RequiredDependency(
        IReadOnlyDictionary<string, object> dependencies,
        string assetId,
        Type expectedType)
    {
        if (!dependencies.TryGetValue(assetId, out var dependency) ||
            !expectedType.IsInstanceOfType(dependency))
            throw new InvalidOperationException(
                $"Prefab dependency is unavailable or has wrong type: {assetId} expected={expectedType.FullName}");
        return dependency;
    }

    private static object CreateIl2CppList<T>(
        Type elementType,
        int count,
        Func<T, object> createElement,
        IReadOnlyList<T> source)
    {
        var listType = typeof(Il2CppSystem.Collections.Generic.List<>).MakeGenericType(elementType);
        var constructor = listType.GetConstructor([typeof(int)])
                          ?? throw new MissingMethodException(listType.FullName, ".ctor(Int32)");
        var add = listType.GetMethod(
                      "Add",
                      BindingFlags.Public | BindingFlags.Instance,
                      binder: null,
                      [elementType],
                      modifiers: null)
                  ?? throw new MissingMethodException(listType.FullName, "Add(T)");
        var list = constructor.Invoke([count])
                   ?? throw new InvalidOperationException("IL2CPP List constructor returned null.");
        foreach (var value in source)
            add.Invoke(list, [createElement(value)]);
        return list;
    }

    private static object CreateIl2CppReferenceArray(
        Type arrayType,
        IReadOnlyList<object> values)
    {
        var constructor = arrayType.GetConstructor([typeof(long)])
                          ?? throw new MissingMethodException(arrayType.FullName, ".ctor(Int64)");
        var item = arrayType.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance)
                   ?? throw new MissingMemberException(arrayType.FullName, "Item");
        var array = constructor.Invoke([(long)values.Count])
                    ?? throw new InvalidOperationException("IL2CPP reference array constructor returned null.");
        for (var index = 0; index < values.Count; index++)
            item.SetValue(array, values[index], [index]);
        return array;
    }

    private static void SetNumericProperty(PropertyInfo property, object instance, object value)
    {
        var converted = Convert.ChangeType(value, property.PropertyType);
        property.SetValue(instance, converted);
    }

    private static void InvokeNumericSetter(MethodInfo setter, object instance, object value)
    {
        var parameterType = setter.GetParameters()[0].ParameterType;
        setter.Invoke(instance, [Convert.ChangeType(value, parameterType)]);
    }

    private static void SetProperties(
        IReadOnlyList<PropertyInfo> properties,
        object instance,
        IReadOnlyList<object?> values)
    {
        if (properties.Count != values.Count)
            throw new InvalidOperationException("Generated proxy field/value count mismatch.");
        for (var index = 0; index < properties.Count; index++)
            properties[index].SetValue(instance, values[index]);
    }

    private static Func<object> CompileObjectConstructor(ConstructorInfo constructor)
        => Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.New(constructor), typeof(object))).Compile();

    private static Func<object> CompileDefaultValue(Type valueType)
    {
        if (!valueType.IsValueType)
            throw new InvalidOperationException("Generated proxy type is not a value type: " + valueType.FullName);
        return Expression.Lambda<Func<object>>(
            Expression.Convert(Expression.Default(valueType), typeof(object))).Compile();
    }

    private static Func<object> CompileIl2CppObjectAllocator(Type proxyType)
    {
        if (!typeof(Il2CppObjectBase).IsAssignableFrom(proxyType))
            throw new InvalidOperationException(
                "Generated proxy type is not an IL2CPP object wrapper: " + proxyType.FullName);
        var classPointer = Il2CppClassPointerStore.GetNativeClassPointer(proxyType);
        var pointerConstructor = RequiredConstructor(proxyType, typeof(IntPtr));
        var pointer = Expression.Parameter(typeof(IntPtr), "pointer");
        var wrap = Expression.Lambda<Func<IntPtr, object>>(
            Expression.Convert(Expression.New(pointerConstructor, pointer), typeof(object)),
            pointer).Compile();
        var context = proxyType.AssemblyQualifiedName ?? proxyType.FullName ?? proxyType.Name;
        return () => wrap(IL2CPP.RequireIl2CppObject(
            IL2CPP.il2cpp_object_new(IL2CPP.RequireIl2CppClass(classPointer, context)),
            context + " allocation"));
    }

    private static Action<object, T> CompilePropertySetter<T>(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(T), "value");
        return Expression.Lambda<Action<object, T>>(
            Expression.Assign(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                Expression.Convert(value, property.PropertyType)),
            instance,
            value).Compile();
    }

    private static Action<object, object> CompileObjectPropertySetter(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        return Expression.Lambda<Action<object, object>>(
            Expression.Assign(
                Expression.Property(Expression.Convert(instance, property.DeclaringType!), property),
                Expression.Convert(value, property.PropertyType)),
            instance,
            value).Compile();
    }

    private static Action<object, T> CompileFieldSetter<T>(FieldInfo field)
    {
        var declaringType = field.DeclaringType
                            ?? throw new InvalidOperationException("Generated proxy field has no owner.");
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(T), "value");
        var target = declaringType.IsValueType
            ? Expression.Unbox(instance, declaringType)
            : Expression.Convert(instance, declaringType);
        return Expression.Lambda<Action<object, T>>(
            Expression.Assign(
                Expression.Field(target, field),
                Expression.Convert(value, field.FieldType)),
            instance,
            value).Compile();
    }

    private static Action<object, object> CompileObjectFieldSetter(FieldInfo field)
    {
        var declaringType = field.DeclaringType
                            ?? throw new InvalidOperationException("Generated proxy field has no owner.");
        var instance = Expression.Parameter(typeof(object), "instance");
        var value = Expression.Parameter(typeof(object), "value");
        var target = declaringType.IsValueType
            ? Expression.Unbox(instance, declaringType)
            : Expression.Convert(instance, declaringType);
        return Expression.Lambda<Action<object, object>>(
            Expression.Assign(
                Expression.Field(target, field),
                Expression.Convert(value, field.FieldType)),
            instance,
            value).Compile();
    }

    private static object Rewrap(object proxy, Type expectedType)
    {
        if (expectedType.IsInstanceOfType(proxy))
            return proxy;
        if (proxy is not Il2CppObjectBase value || value.Pointer == nint.Zero)
            throw new InvalidCastException("Generated component proxy has no IL2CPP pointer.");
        var constructor = expectedType.GetConstructor([typeof(IntPtr)])
                          ?? throw new MissingMethodException(expectedType.FullName, ".ctor(IntPtr)");
        return constructor.Invoke([value.Pointer]);
    }

    private static nint RequiredPointer(object proxy, string operation)
    {
        if (proxy is not Il2CppObjectBase value || value.Pointer == nint.Zero)
            throw new InvalidOperationException(operation + " has no IL2CPP pointer.");
        return value.Pointer;
    }

    internal static nint GetNativePointer(object proxy, string operation)
        => RequiredPointer(proxy, operation);

    private static object RequiredResult(object? value, string operation)
        => value ?? throw new InvalidOperationException(operation + " returned null.");

    private object ProtectFromUnload(object proxy)
    {
        if (!_objectType.IsInstanceOfType(proxy))
            throw new InvalidCastException("Persistent resource is not a UnityEngine.Object proxy.");
        var current = _getHideFlags.Invoke(proxy, null)
                      ?? throw new InvalidOperationException("UnityEngine.Object.get_hideFlags returned null.");
        var flags = Convert.ToInt32(current);
        _setHideFlags.Invoke(
            proxy,
            [Enum.ToObject(_hideFlagsType, flags | DontUnloadUnusedAsset)]);
        return proxy;
    }

    public object Clone(object proxy)
    {
        if (!_objectType.IsInstanceOfType(proxy))
            throw new InvalidCastException("Capability asset is not a generated UnityEngine.Object proxy.");
        var clone = _instantiate.Invoke(null, [proxy])
                    ?? throw new InvalidOperationException("UnityEngine.Object.Instantiate returned null.");
        if (proxy.GetType().IsInstanceOfType(clone))
            return ProtectFromUnload(clone);
        if (clone is not Il2CppObjectBase cloneBase || cloneBase.Pointer == nint.Zero)
            throw new InvalidOperationException("UnityEngine.Object.Instantiate returned an invalid proxy.");
        var constructor = proxy.GetType().GetConstructor([typeof(IntPtr)])
                          ?? throw new MissingMethodException(proxy.GetType().FullName, ".ctor(IntPtr)");
        return ProtectFromUnload(constructor.Invoke([cloneBase.Pointer]));
    }

    public void Destroy(object proxy)
    {
        if (_imguiTextCoreFonts.Remove(proxy, out var textCoreFont))
        {
            Exception? unregisterFailure = null;
            try
            {
                PcCompatNativeHookRules.UnregisterImGuiFontMapping(
                    RequiredPointer(proxy, "Unity Font identity"));
            }
            catch (Exception exception)
            {
                unregisterFailure = exception;
            }
            finally
            {
                _destroy.Invoke(null, [proxy]);
                _destroy.Invoke(null, [textCoreFont]);
            }
            if (unregisterFailure != null)
                throw unregisterFailure;
            return;
        }
        if (_prefabHolders.Remove(proxy, out var holder))
        {
            _destroy.Invoke(null, [holder]);
            return;
        }
        if (_objectType.IsInstanceOfType(proxy))
            _destroy.Invoke(null, [proxy]);
    }

    private static Type RequiredType(string assemblyName, string fullName)
    {
        if (PcCompatIl2CppInteropBootstrap.TryGetProxyType(assemblyName, fullName, out var type))
            return type;
        throw new TypeLoadException($"Generated proxy type is unavailable: {assemblyName}:{fullName}");
    }

    private static ConstructorInfo RequiredConstructor(Type type, params Type[] parameters)
        => type.GetConstructor(parameters)
           ?? throw new MissingMethodException(
               type.FullName,
               $".ctor({string.Join(',', parameters.Select(parameter => parameter.FullName))})");

    private static MethodInfo RequiredMethod(
        Type type,
        string name,
        bool isStatic,
        params Type[] parameters)
    {
        var flags = BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
        return type.GetMethod(name, flags, binder: null, parameters, modifiers: null)
               ?? throw new MissingMethodException(
                   type.FullName,
                   $"{name}({string.Join(',', parameters.Select(parameter => parameter.FullName))})");
    }

    private static MethodInfo RequiredSingleParameterMethod(Type type, string name)
    {
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.Name == name && method.GetParameters().Length == 1)
            .ToArray();
        return methods.Length == 1
            ? methods[0]
            : throw new MissingMethodException(type.FullName, name + "/1");
    }

    private static PropertyInfo RequiredWritableProperty(Type type, string name)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        return property is { CanWrite: true }
            ? property
            : throw new MissingMemberException(type.FullName, name);
    }

    private static FieldInfo RequiredWritableField(Type type, string name)
    {
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
        return field is { IsInitOnly: false }
            ? field
            : throw new MissingMemberException(type.FullName, name);
    }
}
