using System.Numerics;
using StArray.ModManager.Manager;

namespace StArray.ModManager.RuntimeAbstractions;

/// <summary>把 Unity 世界坐标投影到 ImGui 屏幕坐标。</summary>
public static unsafe class UnityScreen
{
    private const string CoreModule = "UnityEngine.CoreModule.dll";

    private static IRuntimeClass? s_cameraClass;
    private static IRuntimeMethod? s_getMain;
    private static IRuntimeMethod? s_worldToScreenPoint;
    private static IRuntimeMethod? s_getPixelWidth;
    private static IRuntimeMethod? s_getPixelHeight;
    private static IRuntimeMethod? s_getTransform;
    private static IRuntimeMethod? s_getPosition;
    private static bool s_resolved;
    private static nint s_cachedCamera;
    private static int s_cameraAge;

    public static int CameraRefreshInterval { get; set; } = 120;

    public static void Invalidate()
    {
        s_resolved = false;
        s_cameraClass = null;
        s_getMain = null;
        s_worldToScreenPoint = null;
        s_getPixelWidth = null;
        s_getPixelHeight = null;
        s_getTransform = null;
        s_getPosition = null;
        s_cachedCamera = 0;
        s_cameraAge = 0;
    }

    private static bool Resolve()
    {
        if (s_resolved)
            return s_cameraClass != null;
        s_resolved = true;

        try
        {
            var core = RuntimeManager.GetDomain()?.OpenAssembly(CoreModule);
            if (core == null)
            {
                Logger.Warn(nameof(UnityScreen), $"{CoreModule} not found");
                return false;
            }

            s_cameraClass = core.GetClass("UnityEngine", "Camera");
            if (s_cameraClass == null)
            {
                Logger.Warn(nameof(UnityScreen), "UnityEngine.Camera not found");
                return false;
            }

            s_getMain = s_cameraClass.GetMethod("get_main", 0);
            s_worldToScreenPoint = s_cameraClass.GetMethod("WorldToScreenPoint", 1);
            s_getPixelWidth = s_cameraClass.GetMethod("get_pixelWidth", 0);
            s_getPixelHeight = s_cameraClass.GetMethod("get_pixelHeight", 0);
            s_getTransform = core.GetClass("UnityEngine", "Component")
                ?.GetMethod("get_transform", 0);
            s_getPosition = core.GetClass("UnityEngine", "Transform")
                ?.GetMethod("get_position", 0);
            return s_worldToScreenPoint != null;
        }
        catch (Exception exception)
        {
            Logger.Error(nameof(UnityScreen), $"Resolve: {exception.Message}");
            s_cameraClass = null;
            return false;
        }
    }

    public static nint MainCamera
    {
        get
        {
            if (!Resolve() || s_getMain == null)
                return 0;
            if (s_cachedCamera != 0 && ++s_cameraAge < CameraRefreshInterval)
                return s_cachedCamera;

            s_cameraAge = 0;
            try
            {
                s_cachedCamera = s_getMain.InvokeStatic();
            }
            catch (Exception exception)
            {
                Logger.Error(nameof(UnityScreen), $"Camera.main: {exception.Message}");
                s_cachedCamera = 0;
            }
            return s_cachedCamera;
        }
    }

    private static Vector2 CameraPixelSize(nint camera)
    {
        try
        {
            if (s_getPixelWidth != null && s_getPixelHeight != null)
            {
                var width = s_getPixelWidth.InvokeUnbox<int>(camera);
                var height = s_getPixelHeight.InvokeUnbox<int>(camera);
                if (width > 0 && height > 0)
                    return new Vector2(width, height);
            }
        }
        catch
        {
        }

        var size = ImGuiNET.ImGui.GetIO().DisplaySize;
        return size.Y > 0 ? size : new Vector2(1920, 1080);
    }

    public static bool TryWorldToScreen(Vector3 world, out Vector2 screen, out float depth)
    {
        screen = default;
        depth = 0;
        var camera = MainCamera;
        if (camera == 0 || s_worldToScreenPoint == null)
            return false;

        Vector3 raw;
        try
        {
            raw = s_worldToScreenPoint.InvokeUnbox<Vector3>(camera, [(nint)(&world)]);
        }
        catch (Exception exception)
        {
            Logger.Error(nameof(UnityScreen), $"WorldToScreenPoint: {exception.Message}");
            return false;
        }

        depth = raw.Z;
        if (depth <= 0)
            return false;

        var cameraSize = CameraPixelSize(camera);
        var displaySize = ImGuiNET.ImGui.GetIO().DisplaySize;
        screen = new Vector2(
            raw.X * displaySize.X / cameraSize.X,
            (cameraSize.Y - raw.Y) * displaySize.Y / cameraSize.Y);
        return true;
    }

    public static bool TryWorldToScreen(Vector3 world, out Vector2 screen)
        => TryWorldToScreen(world, out screen, out _);

    public static bool TryGetWorldPosition(nint component, out Vector3 world)
    {
        world = default;
        if (component == 0 || !Resolve() || s_getTransform == null || s_getPosition == null)
            return false;

        try
        {
            var transform = s_getTransform.Invoke(component);
            if (transform == 0)
                return false;
            world = s_getPosition.InvokeUnbox<Vector3>(transform);
            return true;
        }
        catch (Exception exception)
        {
            Logger.Error(nameof(UnityScreen), $"Transform.position: {exception.Message}");
            return false;
        }
    }

    public static bool TryComponentToScreen(nint component, out Vector2 screen)
    {
        screen = default;
        return TryGetWorldPosition(component, out var world) &&
               TryWorldToScreen(world, out screen);
    }
}
