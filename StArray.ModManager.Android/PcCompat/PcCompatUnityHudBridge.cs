using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public static unsafe class PcCompatUnityHudBridge
{
    private const string LogTag = "PcCompatUnityHud";
    private static bool s_installed;
    private static bool s_failed;
    private static int s_callbackActive;
    private static PcCompatGeneratedUnityHudApi? s_api;
    private static nint s_root;
    private static nint s_rootTransform;
    private static nint s_panel;
    private static nint s_panelRect;
    private static nint s_progressBarBackgroundObject;
    private static nint s_progressBarBackgroundRect;
    private static nint s_progressBarFillObject;
    private static nint s_progressBarFillRect;
    private static nint s_mainText;
    private static nint s_mainRect;
    private static nint s_gameFont;
    private static nint s_font;
    private static nint s_resourceFontObject;
    private static PcCompatResolvedResourceBinding? s_resourceFontBinding;
    private static PcCompatResolvedResourceBinding? s_resourceProgressBinding;
    private static nint s_resourceProgressObject;
    private static nint s_resourceProgressRect;
    private static nint s_resourceProgressLineRect;
    private static string s_resourceProgressFailureKey = string.Empty;
    private static bool s_visible;
    private static string s_richText = string.Empty;
    private static int s_styleGeneration = int.MinValue;
    private static float s_layoutWidth = float.NaN;
    private static float s_layoutHeight = float.NaN;
    private static float s_layoutScale = float.NaN;
    private static float s_layoutX = float.NaN;
    private static float s_layoutY = float.NaN;
    private static float s_backgroundOpacity = float.NaN;
    private static bool s_progressBarVisible;
    private static float s_progressBarValue = float.NaN;
    private static int s_sourceRefreshQueued;
    private static readonly object?[] GcRoots = new object?[16];
    private static int s_gcHandleCount;
    private static readonly object?[] ResourceGcRoots = new object?[8];
    private static int s_resourceGcHandleCount;

    public static void Install()
    {
        if (s_installed)
            return;

        try
        {
            var callback = (nint)(delegate* unmanaged[Cdecl]<uint, void>)&OnOverlayChanged;
            PcCompatNativeHookRules.SetOverlayChangedCallback(callback);
            PcCompatUnityHudRuntime.RegisterRenderer();
            s_installed = true;
            PcCompatUnityHudRuntime.RegisterSourcesChangedSink(OnSourcesChanged);
            Logger.Info(LogTag, "Unity Canvas HUD callback registered");
        }
        catch (Exception ex)
        {
            FailRenderer("callback registration failed", ex);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnOverlayChanged(uint generation)
    {
        if (s_failed || Interlocked.Exchange(ref s_callbackActive, 1) != 0)
            return;

        try
        {
            if (!PcCompatUnityHudRuntime.TryGetFrame(out var frame))
            {
                SetVisible(false);
                return;
            }

            if (!frame.Visible)
            {
                SetVisible(false);
                return;
            }

            EnsureCreated();
            ApplyFrame(frame);
        }
        catch (Exception ex)
        {
            FailRenderer($"Unity HUD update failed at generation {generation}", ex);
        }
        finally
        {
            Volatile.Write(ref s_callbackActive, 0);
        }
    }

    internal static void RefreshResourcesOnUnityMain()
    {
        if (s_failed || Interlocked.Exchange(ref s_callbackActive, 1) != 0)
            return;

        try
        {
            s_styleGeneration = int.MinValue;
            s_progressBarValue = float.NaN;
            if (!PcCompatUnityHudRuntime.TryGetFrame(out var frame) || !frame.Visible)
            {
                SetVisible(false);
                return;
            }
            EnsureCreated();
            ApplyFrame(frame);
        }
        catch (Exception ex)
        {
            FailRenderer("Unity HUD resource refresh failed", ex);
        }
        finally
        {
            Volatile.Write(ref s_callbackActive, 0);
        }
    }

    private static void OnSourcesChanged()
    {
        if (!s_installed || s_failed ||
            Interlocked.Exchange(ref s_sourceRefreshQueued, 1) != 0)
            return;

        if (PcCompatUnityMainExecutionContext.IsActive &&
            Volatile.Read(ref s_callbackActive) == 0)
        {
            Volatile.Write(ref s_sourceRefreshQueued, 0);
            RefreshResourcesOnUnityMain();
            return;
        }

        if (PcCompatResourceBundleLoader.TryScheduleUnityMainWork(
                RefreshSourcesOnUnityMain))
            return;

        Volatile.Write(ref s_sourceRefreshQueued, 0);
        Logger.Warn(LogTag, "Unity HUD source refresh could not be scheduled on UnityMain");
    }

    private static void RefreshSourcesOnUnityMain()
    {
        Volatile.Write(ref s_sourceRefreshQueued, 0);
        RefreshResourcesOnUnityMain();
    }

    internal static void ReleaseResourcesOnUnityMain(
        string modId,
        string candidateSha256Hex,
        long sessionGeneration)
    {
        if (s_root == nint.Zero || s_api == null)
            return;

        try
        {
            if (BindingMatches(
                    s_resourceFontBinding,
                    modId,
                    candidateSha256Hex,
                    sessionGeneration))
                RestoreGameFont(s_api);
            if (BindingMatches(
                    s_resourceProgressBinding,
                    modId,
                    candidateSha256Hex,
                    sessionGeneration))
                ReleaseResourceProgressBar(s_api, destroyObject: true);
        }
        catch (Exception ex)
        {
            Logger.Warn(
                LogTag,
                $"resource visual release failed mod={modId} generation={sessionGeneration}: {ex.Message}");
        }
    }

    private static void EnsureCreated()
    {
        if (s_root != nint.Zero)
            return;

        var api = new PcCompatGeneratedUnityHudApi();
        var root = api.CreateGameObject("xphorror.PcModCompat HUD");
        api.SetActive(root, false);

        var canvas = api.AddComponent(root, api.CanvasType);
        var scaler = api.AddComponent(root, api.CanvasScalerType);
        if (canvas == nint.Zero || scaler == nint.Zero)
            throw new InvalidOperationException("Canvas or CanvasScaler creation failed");

        api.SetCanvasRenderMode(canvas, 0);
        api.SetCanvasSortingOrder(canvas, 0);
        api.SetCanvasScaleMode(scaler, 1);
        api.SetCanvasReferenceResolution(scaler, 1920f, 1080f);
        api.SetCanvasMatch(scaler, 0.5f);

        var rootTransform = RequireObject(api.GetTransform(root), "root RectTransform");
        RootObject(rootTransform);
        var panelObject = api.CreateGameObject("Background");
        var panel = RequireObject(api.AddComponent(panelObject, api.ImageType), "background Image");
        RootObject(panel);
        var panelRect = RequireObject(api.GetTransform(panelObject), "background RectTransform");
        RootObject(panelRect);
        api.SetParent(panelRect, rootTransform);
        api.SetRaycastTarget(panel, false);

        var mainObject = api.CreateGameObject("Text");
        var mainText = RequireObject(api.AddComponent(mainObject, api.TextMeshProType), "main TextMeshProUGUI");
        RootObject(mainText);
        var mainRect = RequireObject(api.GetRectTransform(mainText, mainObject), "main RectTransform");
        RootObject(mainRect);
        api.SetParent(mainRect, rootTransform);
        api.ConfigureText(mainText, richText: true);

        var progressBackgroundObject = api.CreateGameObject("ProgressBar.Background");
        var progressBackground = RequireObject(api.AddComponent(progressBackgroundObject, api.ImageType), "progress background Image");
        RootObject(progressBackground);
        var progressBackgroundRect = RequireObject(api.GetTransform(progressBackgroundObject), "progress background RectTransform");
        RootObject(progressBackgroundRect);
        api.SetParent(progressBackgroundRect, rootTransform);
        api.SetRaycastTarget(progressBackground, false);

        var progressFillObject = api.CreateGameObject("ProgressBar.Fill");
        var progressFill = RequireObject(api.AddComponent(progressFillObject, api.ImageType), "progress fill Image");
        RootObject(progressFill);
        var progressFillRect = RequireObject(api.GetTransform(progressFillObject), "progress fill RectTransform");
        RootObject(progressFillRect);
        api.SetParent(progressFillRect, rootTransform);
        api.SetRaycastTarget(progressFill, false);

        var font = api.ApplyLocalizedFont(mainText);
        if (font == nint.Zero)
            font = api.GetFont(mainText);
        s_gameFont = font;
        s_font = font;

        api.SetGraphicColor(panel, 0.125f, 0.125f, 0.125f, 0f);
        api.SetGraphicColor(progressBackground, 0.18f, 0.18f, 0.18f, 0.42f);
        api.SetGraphicColor(progressFill, 0.88f, 0.77f, 1f, 1f);
        api.SetGraphicColor(mainText, 1f, 1f, 1f, 1f);
        api.SetActive(progressBackgroundObject, false);
        api.SetActive(progressFillObject, false);
        api.DontDestroyOnLoad(root);

        s_api = api;
        s_root = root;
        s_rootTransform = rootTransform;
        s_panel = panel;
        s_panelRect = panelRect;
        s_progressBarBackgroundObject = progressBackgroundObject;
        s_progressBarBackgroundRect = progressBackgroundRect;
        s_progressBarFillObject = progressFillObject;
        s_progressBarFillRect = progressFillRect;
        s_mainText = mainText;
        s_mainRect = mainRect;
        Logger.Info(LogTag, $"Unity Canvas HUD created root=0x{root.ToInt64():X} font=0x{font.ToInt64():X}");
    }

    private static void ApplyFrame(PcCompatUnityHudFrame frame)
    {
        var api = s_api!;
        ReleaseForeignResourceVisuals(api, frame.ModId);
        if (s_styleGeneration != frame.StyleGeneration ||
            s_layoutWidth != frame.Width ||
            s_layoutHeight != frame.Height ||
            s_layoutScale != frame.Scale ||
            s_layoutX != frame.PositionX ||
            s_layoutY != frame.PositionY ||
            s_backgroundOpacity != frame.BackgroundOpacity ||
            s_progressBarVisible != frame.ProgressBarVisible)
        {
            var scale = Math.Clamp(frame.Scale, 0.5f, 2.5f);
            var x = frame.PositionX;
            var y = frame.PositionY;
            var width = frame.Width * scale;
            var height = frame.Height * scale;
            var paddingX = 14f * scale;
            var paddingY = 8f * scale;
            if (s_resourceFontBinding == null)
            {
                var gameFont = api.ApplyLocalizedFont(s_mainText);
                if (gameFont != nint.Zero)
                {
                    s_gameFont = gameFont;
                    s_font = gameFont;
                }
            }

            api.SetTopLeftRect(s_panelRect, x, y, width, height);
            api.SetTopLeftRect(
                s_mainRect,
                x + paddingX,
                y + paddingY,
                Math.Max(1f, width - paddingX * 2f),
                Math.Max(1f, height - paddingY * 2f));
            api.SetFontSize(s_mainText, 25f * scale);
            api.SetGraphicColor(
                s_panel,
                0.125f,
                0.125f,
                0.125f,
                Math.Clamp(frame.BackgroundOpacity, 0f, 1f));
            ApplyProgressBarFrame(api, frame, x, y, width, height, scale);
            s_styleGeneration = frame.StyleGeneration;
            s_layoutWidth = frame.Width;
            s_layoutHeight = frame.Height;
            s_layoutScale = frame.Scale;
            s_layoutX = frame.PositionX;
            s_layoutY = frame.PositionY;
            s_backgroundOpacity = frame.BackgroundOpacity;
        }
        else if (s_progressBarVisible != frame.ProgressBarVisible ||
                 s_progressBarValue != frame.ProgressBarValue)
        {
            var scale = Math.Clamp(frame.Scale, 0.5f, 2.5f);
            ApplyProgressBarFrame(
                api,
                frame,
                frame.PositionX,
                frame.PositionY,
                frame.Width * scale,
                frame.Height * scale,
                scale);
        }

        ApplyResourceFont(api, frame);

        if (!string.Equals(s_richText, frame.RichText, StringComparison.Ordinal))
        {
            api.SetText(s_mainText, frame.RichText);
            s_richText = frame.RichText;
        }

        SetVisible(true);
    }

    private static void SetVisible(bool visible)
    {
        if (s_root == nint.Zero || s_visible == visible)
            return;

        s_api!.SetActive(s_root, visible);
        s_visible = visible;
    }

    private static void ApplyProgressBarFrame(
        PcCompatGeneratedUnityHudApi api,
        PcCompatUnityHudFrame frame,
        float x,
        float y,
        float width,
        float height,
        float scale)
    {
        s_progressBarVisible = frame.ProgressBarVisible;
        s_progressBarValue = frame.ProgressBarValue;

        var visible = frame.ProgressBarVisible;
        if (!visible)
        {
            api.SetActive(s_progressBarBackgroundObject, false);
            api.SetActive(s_progressBarFillObject, false);
            if (s_resourceProgressObject != nint.Zero)
                api.SetActive(s_resourceProgressObject, false);
            return;
        }

        var barX = x + 14f * scale;
        var barWidth = Math.Max(1f, width - 28f * scale);
        var resourceBarHeight = Math.Max(2f, 18f * scale);
        var resourceBarY = y + height - 18f * scale;
        if (TryApplyResourceProgressBar(api, frame, barX, resourceBarY, barWidth, resourceBarHeight, scale))
        {
            api.SetActive(s_progressBarBackgroundObject, false);
            api.SetActive(s_progressBarFillObject, false);
            return;
        }

        if (s_resourceProgressObject != nint.Zero)
            api.SetActive(s_resourceProgressObject, false);
        if (s_progressBarBackgroundRect == nint.Zero || s_progressBarFillRect == nint.Zero)
            return;

        api.SetActive(s_progressBarBackgroundObject, visible);
        api.SetActive(s_progressBarFillObject, visible);
        var barHeight = Math.Max(2f, 6f * scale);
        var barY = y + height - 12f * scale;
        var fillWidth = Math.Max(1f, barWidth * Math.Clamp(frame.ProgressBarValue, 0f, 1f));

        api.SetTopLeftRect(s_progressBarBackgroundRect, barX, barY, barWidth, barHeight);
        api.SetTopLeftRect(s_progressBarFillRect, barX, barY, fillWidth, barHeight);
    }

    private static void ApplyResourceFont(PcCompatGeneratedUnityHudApi api, PcCompatUnityHudFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.ModId))
        {
            RestoreGameFont(api);
            return;
        }
        if (frame.PlainText.Any(character => character > 0x7f))
        {
            // The imported font may not carry the game's CJK fallback table.
            // Keep localized HUD text readable until generic TMP fallback-list
            // mutation is part of the resource binding contract.
            RestoreGameFont(api);
            return;
        }

        var status = PcCompatResourceBundleLoader.TryGetOrRequestAsset(
            frame.ModId,
            "overlay.font",
            "TMP_FontAsset",
            out var font,
            out var binding);
        if (status == PcCompatResourceAssetStatus.Ready && font != nint.Zero)
        {
            if (s_resourceFontObject != nint.Zero && s_resourceFontObject != font)
                api.Forget(s_resourceFontObject);
            if (font != s_font)
            {
                api.SetFont(s_mainText, font);
                s_font = font;
            }
            s_resourceFontObject = font;
            s_resourceFontBinding = binding;
            return;
        }

        if (s_resourceFontBinding != null &&
            (!PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                 frame.ModId,
                 "overlay.font",
                 "TMP_FontAsset",
                 out var currentBinding) ||
             !SameBinding(s_resourceFontBinding, currentBinding)))
            RestoreGameFont(api);
    }

    private static bool TryApplyResourceProgressBar(
        PcCompatGeneratedUnityHudApi api,
        PcCompatUnityHudFrame frame,
        float x,
        float y,
        float width,
        float height,
        float scale)
    {
        if (string.IsNullOrWhiteSpace(frame.ModId))
            return false;

        var status = PcCompatResourceBundleLoader.TryGetOrRequestAsset(
            frame.ModId,
            "overlay.progress_bar",
            "GameObject",
            out var prefab,
            out var binding);
        if (status != PcCompatResourceAssetStatus.Ready || prefab == nint.Zero)
        {
            if (s_resourceProgressBinding != null &&
                (!PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                     frame.ModId,
                     "overlay.progress_bar",
                     "GameObject",
                     out var currentBinding) ||
                 !SameBinding(s_resourceProgressBinding, currentBinding)))
                ReleaseResourceProgressBar(api, destroyObject: true);
            return false;
        }

        var bindingKey = BindingKey(binding);
        if (s_resourceProgressObject == nint.Zero ||
            s_resourceProgressBinding == null ||
            !SameBinding(s_resourceProgressBinding, binding))
        {
            if (s_resourceProgressFailureKey.Equals(bindingKey, StringComparison.Ordinal))
                return false;
            try
            {
                CreateResourceProgressBar(api, prefab, binding);
            }
            catch (Exception ex)
            {
                s_resourceProgressFailureKey = bindingKey;
                ReleaseResourceProgressBar(api, destroyObject: true);
                Logger.Warn(
                    LogTag,
                    $"progress prefab adapter rejected mod={binding.ModId} asset={binding.AssetName}: {ex.Message}");
                return false;
            }
        }

        api.SetTopLeftRect(s_resourceProgressRect, x, y, width, height);
        api.SetSizeDeltaX(
            s_resourceProgressLineRect,
            Math.Max(0f, (width - 4f * scale) * Math.Clamp(frame.ProgressBarValue, 0f, 1f)));
        api.SetActive(s_resourceProgressObject, true);
        return true;
    }

    private static void CreateResourceProgressBar(
        PcCompatGeneratedUnityHudApi api,
        nint prefab,
        PcCompatResolvedResourceBinding binding)
    {
        ReleaseResourceProgressBar(api, destroyObject: true);
        var instance = RequireObject(api.Instantiate(prefab), "resource progress prefab instance");
        s_resourceProgressObject = instance;
        RootResourceObject(instance);

        var transform = RequireObject(api.GetTransform(instance), "resource progress Transform");
        var rect = RequireObject(
            api.GetComponent(transform, api.RectTransformType),
            "resource progress RectTransform");
        s_resourceProgressRect = rect;
        RootResourceObject(rect);
        api.SetParent(rect, s_rootTransform);

        var lineTransform = RequireObject(api.FindChild(rect, "line"), "resource progress child 'line'");
        var border = RequireObject(api.FindChild(rect, "borderLine"), "resource progress child 'borderLine'");
        var background = RequireObject(api.FindChild(rect, "background"), "resource progress child 'background'");
        var line = RequireObject(
            api.GetComponent(lineTransform, api.RectTransformType),
            "resource progress line RectTransform");
        s_resourceProgressLineRect = line;
        RootResourceObject(line);

        var lineImage = RequireObject(api.GetComponent(line, api.ImageType), "resource progress line Image");
        var borderImage = RequireObject(api.GetComponent(border, api.ImageType), "resource progress border Image");
        var backgroundImage = RequireObject(api.GetComponent(background, api.ImageType), "resource progress background Image");
        api.SetRaycastTarget(lineImage, false);
        api.SetRaycastTarget(borderImage, false);
        api.SetRaycastTarget(backgroundImage, false);
        api.SetActive(instance, false);

        s_resourceProgressBinding = binding;
        s_resourceProgressFailureKey = string.Empty;
        Logger.Info(
            LogTag,
            $"progress prefab instantiated mod={binding.ModId} asset={binding.AssetName} " +
            $"generation={binding.SessionGeneration}");
    }

    private static void ReleaseForeignResourceVisuals(PcCompatGeneratedUnityHudApi api, string modId)
    {
        if (s_resourceFontBinding != null &&
            !s_resourceFontBinding.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            RestoreGameFont(api);
        if (s_resourceProgressBinding != null &&
            !s_resourceProgressBinding.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase))
            ReleaseResourceProgressBar(api, destroyObject: true);
    }

    private static void RestoreGameFont(PcCompatGeneratedUnityHudApi api)
    {
        if (s_resourceFontBinding == null && s_resourceFontObject == nint.Zero)
            return;

        var resourceFont = s_resourceFontObject;
        s_resourceFontObject = nint.Zero;
        s_resourceFontBinding = null;
        api.Forget(resourceFont);

        var localizedFont = api.ApplyLocalizedFont(s_mainText);
        if (localizedFont != nint.Zero)
        {
            s_gameFont = localizedFont;
            s_font = localizedFont;
        }
        else if (s_gameFont != nint.Zero && s_gameFont != s_font)
        {
            api.SetFont(s_mainText, s_gameFont);
            s_font = s_gameFont;
        }
    }

    private static void ReleaseResourceProgressBar(PcCompatGeneratedUnityHudApi api, bool destroyObject)
    {
        var instance = s_resourceProgressObject;
        var rect = s_resourceProgressRect;
        var lineRect = s_resourceProgressLineRect;
        s_resourceProgressObject = nint.Zero;
        s_resourceProgressRect = nint.Zero;
        s_resourceProgressLineRect = nint.Zero;
        s_resourceProgressBinding = null;
        if (instance != nint.Zero)
        {
            try { api.SetActive(instance, false); } catch { }
            if (destroyObject)
            {
                try { api.Destroy(instance); } catch { }
            }
        }
        api.Forget(instance);
        api.Forget(rect);
        api.Forget(lineRect);
        FreeResourceHandles();
    }

    private static bool BindingMatches(
        PcCompatResolvedResourceBinding? binding,
        string modId,
        string candidateSha256Hex,
        long sessionGeneration)
        => binding != null &&
           binding.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase) &&
           binding.CandidateSha256Hex.Equals(candidateSha256Hex, StringComparison.OrdinalIgnoreCase) &&
           binding.SessionGeneration == sessionGeneration;

    private static bool SameBinding(
        PcCompatResolvedResourceBinding left,
        PcCompatResolvedResourceBinding right)
        => left.ModId.Equals(right.ModId, StringComparison.OrdinalIgnoreCase) &&
           left.FeatureGroupId.Equals(right.FeatureGroupId, StringComparison.OrdinalIgnoreCase) &&
           left.CandidateSha256Hex.Equals(right.CandidateSha256Hex, StringComparison.OrdinalIgnoreCase) &&
           left.AssetName.Equals(right.AssetName, StringComparison.Ordinal) &&
           left.ExpectedType.Equals(right.ExpectedType, StringComparison.OrdinalIgnoreCase) &&
           left.SessionGeneration == right.SessionGeneration;

    private static string BindingKey(PcCompatResolvedResourceBinding binding)
        => binding.ModId + "\0" + binding.CandidateSha256Hex + "\0" +
           binding.AssetName + "\0" + binding.SessionGeneration;

    private static void FailRenderer(string message, Exception ex)
    {
        if (s_failed)
            return;

        s_failed = true;
        PcCompatUnityHudRuntime.MarkRendererFailed();
        try
        {
            PcCompatNativeHookRules.SetOverlayChangedCallback(nint.Zero);
        }
        catch
        {
        }

        try
        {
            if (s_root != nint.Zero)
                s_api?.SetActive(s_root, false);
        }
        catch
        {
        }

        FreeRootHandles();
        FreeResourceHandles();
        s_api?.Clear();

        Logger.Error(LogTag, $"{message}; falling back to ImGui HUD: {ex}");
    }

    private static void RootObject(nint obj)
    {
        if (s_gcHandleCount >= GcRoots.Length)
            throw new InvalidOperationException("Unity HUD GCHandle capacity exceeded");
        GcRoots[s_gcHandleCount++] = new Il2CppSystem.Object(obj);
    }

    private static void RootResourceObject(nint obj)
    {
        if (s_resourceGcHandleCount >= ResourceGcRoots.Length)
            throw new InvalidOperationException("Unity HUD resource GCHandle capacity exceeded");
        ResourceGcRoots[s_resourceGcHandleCount++] = new Il2CppSystem.Object(obj);
    }

    private static nint RequireObject(nint obj, string label)
        => obj != nint.Zero
            ? obj
            : throw new InvalidOperationException($"Unity object creation failed: {label}");

    private static void FreeRootHandles()
    {
        while (s_gcHandleCount > 0)
        {
            var index = --s_gcHandleCount;
            GcRoots[index] = null;
        }
    }

    private static void FreeResourceHandles()
    {
        while (s_resourceGcHandleCount > 0)
        {
            var index = --s_resourceGcHandleCount;
            ResourceGcRoots[index] = null;
        }
    }

}
