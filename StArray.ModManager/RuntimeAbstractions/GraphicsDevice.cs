namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>通过 UnityEngine.SystemInfo 查询当前图形后端。</summary>
public static class GraphicsDevice
{
    public static int GetGraphicsDeviceType()
    {
        var systemInfo = RuntimeObject.New("UnityEngine", "UnityEngine", "SystemInfo");
        return systemInfo?.InvokeUnbox<int>("get_graphicsDeviceType", 0) ?? -1;
    }

    public static bool IsD3D9 => GetGraphicsDeviceType() == 0;
    public static bool IsD3D11 => GetGraphicsDeviceType() == 2;
    public static bool IsD3D12 => GetGraphicsDeviceType() == 3;
    public static bool IsOpenGL => GetGraphicsDeviceType() == 11;
    public static bool IsVulkan => GetGraphicsDeviceType() == 13;
}
