using System.Runtime.InteropServices;
using ImGuiNET;

namespace StArray.ModManager.UI;

/// <summary>
/// ImGui 渲染器接口 —— 抽象渲染管线，允许替换不同的渲染后端
/// </summary>
public interface IImGuiRenderer
{
    /// <summary>
    /// 是否已完成初始化
    /// </summary>
    bool IsInitialized { get; }

    /// <summary>
    /// 安装 Hook 并准备渲染管线
    /// </summary>
    bool Install();

    /// <summary>
    /// 初始化 ImGui 上下文并加载 ModManager 的内嵌字体。
    /// </summary>
    void InitImGui()
    {
        ImGui.SetCurrentContext(ImGui.CreateContext());
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.DockingEnable;
        LoadIconFont(io);
        LoadEmbeddedFont(io);
        try
        {
            io.Fonts.Build();
        }
        finally
        {
            FreeFontMemory();
        }
    }

    private static nint _fontPtr1;
    private static nint _fontPtr2;
    private static nint _textGlyphRanges;

    private static void FreeFontMemory()
    {
        if (_fontPtr1 != 0)
        {
            Marshal.FreeHGlobal(_fontPtr1);
            _fontPtr1 = 0;
        }

        if (_fontPtr2 != 0)
        {
            Marshal.FreeHGlobal(_fontPtr2);
            _fontPtr2 = 0;
        }

        if (_textGlyphRanges != 0)
        {
            Marshal.FreeHGlobal(_textGlyphRanges);
            _textGlyphRanges = 0;
        }
    }

    private static unsafe void LoadEmbeddedFont(ImGuiIOPtr io)
    {
        try
        {
            using var stream = typeof(IImGuiRenderer).Assembly.GetManifestResourceStream(
                "StArray.ModManager.Resources.NotoSansCJK-Regular.otf");
            if (stream == null)
                return;

            var font = new byte[stream.Length];
            stream.ReadExactly(font);
            _fontPtr1 = Marshal.AllocHGlobal(font.Length);
            Marshal.Copy(font, 0, _fontPtr1, font.Length);
            _textGlyphRanges = ImGuiTextGlyphRanges.Create(io);

            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            config->MergeMode = 1;
            config->FontDataOwnedByAtlas = 0;
            try
            {
                io.Fonts.AddFontFromMemoryTTF(
                    _fontPtr1,
                    font.Length,
                    16f,
                    config,
                    _textGlyphRanges);
            }
            finally
            {
                ImGuiNative.ImFontConfig_destroy(config);
            }
        }
        catch
        {
            // Preserve the upstream best-effort fallback to ImGui's default font.
        }
    }

    private static unsafe void LoadIconFont(ImGuiIOPtr io)
    {
        try
        {
            using var stream = typeof(IImGuiRenderer).Assembly.GetManifestResourceStream(
                "StArray.ModManager.Resources.fa-solid-900.ttf");
            if (stream == null)
                return;

            var font = new byte[stream.Length];
            stream.ReadExactly(font);
            _fontPtr2 = Marshal.AllocHGlobal(font.Length);
            Marshal.Copy(font, 0, _fontPtr2, font.Length);

            var config = ImGuiNative.ImFontConfig_ImFontConfig();
            config->FontDataOwnedByAtlas = 0;
            try
            {
                ushort[] iconRange = [0xe005, 0xf8ff, 0];
                fixed (ushort* range = iconRange)
                {
                    io.Fonts.AddFontFromMemoryTTF(
                        _fontPtr2,
                        font.Length,
                        16f,
                        config,
                        (IntPtr)range);
                }
            }
            finally
            {
                ImGuiNative.ImFontConfig_destroy(config);
            }
        }
        catch
        {
            // Text controls remain usable when the icon font cannot be loaded.
        }
    }

    /// <summary>
    /// 每帧 UI 构建回调（由渲染循环驱动）
    /// </summary>
    event Action OnRender;
}
