using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using StArray.ModManager.Manager;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Android.PcCompat;

public static unsafe class PcCompatUnityHudBridge
{
    private const string LogTag = "PcCompatUnityHud";
    private static readonly Dictionary<string, HudSurface> Surfaces =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool s_installed;
    private static bool s_failed;
    private static int s_callbackActive;
    private static int s_sourceRefreshQueued;

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
            Logger.Info(LogTag, "owner-scoped Unity Canvas HUD callback registered");
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
            ApplySourceSnapshot(forceResourceRefresh: false);
        }
        catch (Exception ex)
        {
            FailRenderer($"Unity HUD registry update failed at generation {generation}", ex);
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
            ApplySourceSnapshot(forceResourceRefresh: true);
        }
        catch (Exception ex)
        {
            FailRenderer("Unity HUD resource registry refresh failed", ex);
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

        if (PcCompatResourceBundleLoader.TryScheduleUnityMainWork(RefreshSourcesOnUnityMain))
            return;

        Volatile.Write(ref s_sourceRefreshQueued, 0);
        Logger.Warn(LogTag, "Unity HUD source refresh could not be scheduled on UnityMain");
    }

    private static void RefreshSourcesOnUnityMain()
    {
        Volatile.Write(ref s_sourceRefreshQueued, 0);
        RefreshResourcesOnUnityMain();
    }

    private static void ApplySourceSnapshot(bool forceResourceRefresh)
    {
        var snapshots = PcCompatUnityHudRuntime.SnapshotSources()
            .OrderBy(snapshot => snapshot.OwnerId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var registeredOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var occupiedSlots = new Dictionary<(int X, int Y), int>();

        foreach (var snapshot in snapshots)
        {
            if (!registeredOwners.Add(snapshot.OwnerId))
            {
                FailSource(
                    snapshot.OwnerId,
                    new InvalidOperationException("duplicate HUD owner in source snapshot"));
                continue;
            }

            if (snapshot.Error != null)
            {
                FailSource(snapshot.OwnerId, snapshot.Error);
                continue;
            }

            var frame = snapshot.Frame;
            if (frame == null || !frame.Visible)
            {
                if (Surfaces.TryGetValue(snapshot.OwnerId, out var hidden))
                {
                    try
                    {
                        hidden.SetVisible(false);
                    }
                    catch (Exception ex)
                    {
                        hidden.Fail(ex);
                    }
                }
                continue;
            }

            if (!string.IsNullOrWhiteSpace(frame.ModId) &&
                !frame.ModId.Equals(snapshot.OwnerId, StringComparison.OrdinalIgnoreCase))
            {
                FailSource(
                    snapshot.OwnerId,
                    new InvalidOperationException(
                        $"HUD frame owner mismatch: registered={snapshot.OwnerId}, frame={frame.ModId}"));
                continue;
            }

            Surfaces.TryGetValue(snapshot.OwnerId, out var surface);
            if (surface != null &&
                surface.SessionGeneration != 0 &&
                snapshot.SessionGeneration != 0 &&
                surface.SessionGeneration != snapshot.SessionGeneration)
            {
                try
                {
                    surface.Destroy();
                }
                catch (Exception ex)
                {
                    Logger.Warn(
                        LogTag,
                        $"HUD session root destroy failed owner={snapshot.OwnerId}: {ex.Message}");
                }
                Surfaces.Remove(snapshot.OwnerId);
                surface = null;
            }
            if (surface?.Failed == true &&
                !PcCompatUnityHudRuntime.RendererAvailableFor(snapshot.OwnerId))
                continue;

            var slot = (
                BitConverter.SingleToInt32Bits(frame.PositionX),
                BitConverter.SingleToInt32Bits(frame.PositionY));
            occupiedSlots.TryGetValue(slot, out var stackIndex);
            occupiedSlots[slot] = stackIndex + 1;
            var scale = Math.Clamp(frame.Scale, 0.5f, 2.5f);
            var effectiveY = frame.PositionY + stackIndex * (frame.Height * scale + 8f);

            try
            {
                if (surface?.Failed == true)
                {
                    surface.Destroy();
                    Surfaces.Remove(snapshot.OwnerId);
                    surface = null;
                }
                if (surface == null)
                {
                    surface = new HudSurface(snapshot.OwnerId, snapshot.SessionGeneration);
                    Surfaces.Add(snapshot.OwnerId, surface);
                }
                if (forceResourceRefresh)
                    surface.InvalidateResources();
                surface.ApplyFrame(frame, effectiveY);
                PcCompatUnityHudRuntime.ClearSourceRendererFailure(snapshot.OwnerId);
            }
            catch (Exception ex)
            {
                if (surface != null)
                    surface.Fail(ex);
                else
                    FailSource(snapshot.OwnerId, ex);
            }
        }

        foreach (var owner in Surfaces.Keys
                     .Where(owner => !registeredOwners.Contains(owner))
                     .ToArray())
        {
            try
            {
                Surfaces[owner].Destroy();
            }
            catch (Exception ex)
            {
                Logger.Warn(LogTag, $"HUD surface destroy failed owner={owner}: {ex.Message}");
            }
            Surfaces.Remove(owner);
        }
    }

    private static void FailSource(string ownerId, Exception exception)
    {
        PcCompatUnityHudRuntime.MarkSourceRendererFailed(ownerId);
        if (Surfaces.TryGetValue(ownerId, out var surface))
        {
            surface.Fail(exception);
            return;
        }
        Logger.Error(LogTag, $"HUD source quarantined owner={ownerId}: {exception}");
    }

    internal static void ReleaseResourcesOnUnityMain(
        string modId,
        string candidateSha256Hex,
        long sessionGeneration)
    {
        if (!Surfaces.TryGetValue(modId, out var surface))
            return;
        surface.ReleaseResources(candidateSha256Hex, sessionGeneration);
    }

    private static void FailRenderer(string message, Exception ex)
    {
        if (s_failed)
            return;

        s_failed = true;
        PcCompatUnityHudRuntime.MarkRendererFailed();
        try { PcCompatNativeHookRules.SetOverlayChangedCallback(nint.Zero); } catch { }
        foreach (var surface in Surfaces.Values)
            surface.Destroy();
        Surfaces.Clear();
        Logger.Error(LogTag, $"{message}; falling back to per-MOD ImGui HUD: {ex}");
    }

    private sealed class HudSurface
    {
        private readonly string _ownerId;
        private readonly string _ownershipIdentity;
        public long SessionGeneration { get; }
        private readonly PcCompatGeneratedUnityHudApi _api = new();
        private readonly List<object> _gcRoots = new(16);
        private readonly List<object> _resourceGcRoots = new(8);
        private nint _root;
        private nint _rootTransform;
        private nint _panel;
        private nint _panelRect;
        private nint _progressBarBackgroundObject;
        private nint _progressBarBackgroundRect;
        private nint _progressBarFillObject;
        private nint _progressBarFillRect;
        private nint _mainText;
        private nint _mainRect;
        private nint _gameFont;
        private nint _font;
        private nint _resourceFontObject;
        private PcCompatResolvedResourceBinding? _resourceFontBinding;
        private PcCompatResolvedResourceBinding? _resourceProgressBinding;
        private nint _resourceProgressObject;
        private nint _resourceProgressRect;
        private nint _resourceProgressLineRect;
        private string _resourceProgressFailureKey = string.Empty;
        private bool _visible;
        private string _richText = string.Empty;
        private int _styleGeneration = int.MinValue;
        private float _layoutWidth = float.NaN;
        private float _layoutHeight = float.NaN;
        private float _layoutScale = float.NaN;
        private float _layoutX = float.NaN;
        private float _layoutY = float.NaN;
        private float _backgroundOpacity = float.NaN;
        private bool _progressBarVisible;
        private float _progressBarValue = float.NaN;

        public HudSurface(string ownerId, long sessionGeneration)
        {
            _ownerId = ownerId;
            SessionGeneration = sessionGeneration;
            _ownershipIdentity = "unity-hud-surface;";
            if (!PcCompatRuntime.TryRegisterOwnedResource(
                    ownerId,
                    sessionGeneration,
                    ModOwnedResourceKind.UnityObject,
                    _ownershipIdentity))
            {
                throw new InvalidOperationException(
                    $"Unity HUD surface ownership registration failed owner={ownerId} " +
                    $"generation={sessionGeneration}.");
            }
            try
            {
                EnsureCreated();
            }
            catch
            {
                RetireOwnership();
                throw;
            }
        }

        public bool Failed { get; private set; }

        public void InvalidateResources()
        {
            _styleGeneration = int.MinValue;
            _progressBarValue = float.NaN;
        }

        public void ApplyFrame(PcCompatUnityHudFrame frame, float effectiveY)
        {
            if (Failed)
                return;
            EnsureCreated();

            var styleChanged = _styleGeneration != frame.StyleGeneration ||
                               _layoutWidth != frame.Width ||
                               _layoutHeight != frame.Height ||
                               _layoutScale != frame.Scale ||
                               _layoutX != frame.PositionX ||
                               _layoutY != effectiveY ||
                               _backgroundOpacity != frame.BackgroundOpacity ||
                               _progressBarVisible != frame.ProgressBarVisible;
            if (styleChanged)
            {
                var scale = Math.Clamp(frame.Scale, 0.5f, 2.5f);
                var x = frame.PositionX;
                var width = frame.Width * scale;
                var height = frame.Height * scale;
                var paddingX = 14f * scale;
                var paddingY = 8f * scale;
                if (_resourceFontBinding == null)
                {
                    var gameFont = _api.ApplyLocalizedFont(_mainText);
                    if (gameFont != nint.Zero)
                    {
                        _gameFont = gameFont;
                        _font = gameFont;
                    }
                }

                _api.SetTopLeftRect(_panelRect, x, effectiveY, width, height);
                _api.SetTopLeftRect(
                    _mainRect,
                    x + paddingX,
                    effectiveY + paddingY,
                    Math.Max(1f, width - paddingX * 2f),
                    Math.Max(1f, height - paddingY * 2f));
                _api.SetFontSize(_mainText, 25f * scale);
                _api.SetGraphicColor(
                    _panel,
                    0.125f,
                    0.125f,
                    0.125f,
                    Math.Clamp(frame.BackgroundOpacity, 0f, 1f));
                ApplyProgressBarFrame(frame, x, effectiveY, width, height, scale);
                _styleGeneration = frame.StyleGeneration;
                _layoutWidth = frame.Width;
                _layoutHeight = frame.Height;
                _layoutScale = frame.Scale;
                _layoutX = frame.PositionX;
                _layoutY = effectiveY;
                _backgroundOpacity = frame.BackgroundOpacity;
            }
            else if (_progressBarVisible != frame.ProgressBarVisible ||
                     _progressBarValue != frame.ProgressBarValue)
            {
                var scale = Math.Clamp(frame.Scale, 0.5f, 2.5f);
                ApplyProgressBarFrame(
                    frame,
                    frame.PositionX,
                    effectiveY,
                    frame.Width * scale,
                    frame.Height * scale,
                    scale);
            }

            ApplyResourceFont(frame);
            if (!string.Equals(_richText, frame.RichText, StringComparison.Ordinal))
            {
                _api.SetText(_mainText, frame.RichText);
                _richText = frame.RichText;
            }
            SetVisible(true);
        }

        public void SetVisible(bool visible)
        {
            if (_root == nint.Zero || _visible == visible)
                return;
            _api.SetActive(_root, visible);
            _visible = visible;
        }

        public void ReleaseResources(string candidateSha256Hex, long sessionGeneration)
        {
            try
            {
                if (SessionGeneration != 0 &&
                    sessionGeneration != 0 &&
                    SessionGeneration != sessionGeneration)
                {
                    Logger.Info(
                        LogTag,
                        $"ignored stale HUD resource release owner={_ownerId} " +
                        $"surfaceGeneration={SessionGeneration} releaseGeneration={sessionGeneration}");
                    return;
                }
                if (BindingMatches(
                        _resourceFontBinding,
                        _ownerId,
                        candidateSha256Hex,
                        sessionGeneration))
                    RestoreGameFont();
                if (BindingMatches(
                        _resourceProgressBinding,
                        _ownerId,
                        candidateSha256Hex,
                        sessionGeneration))
                    ReleaseResourceProgressBar(destroyObject: true);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    LogTag,
                    $"resource visual release failed mod={_ownerId} generation={sessionGeneration}: {ex.Message}");
            }
        }

        public void Fail(Exception exception)
        {
            if (Failed)
                return;
            Failed = true;
            PcCompatUnityHudRuntime.MarkSourceRendererFailed(_ownerId);
            try { SetVisible(false); } catch { }
            Logger.Error(LogTag, $"HUD surface quarantined owner={_ownerId}: {exception}");
        }

        public void Destroy()
        {
            try
            {
                try { SetVisible(false); } catch { }
                try { ReleaseResourceProgressBar(destroyObject: true); } catch { }
                if (_root != nint.Zero)
                {
                    try { _api.Destroy(_root); } catch { }
                }
                _root = nint.Zero;
                _gcRoots.Clear();
                _resourceGcRoots.Clear();
                _api.Clear();
            }
            finally
            {
                RetireOwnership();
            }
        }

        private void RetireOwnership()
            => PcCompatRuntime.RetireOwnedResource(
                _ownerId,
                SessionGeneration,
                ModOwnedResourceKind.UnityObject,
                _ownershipIdentity);

        private void EnsureCreated()
        {
            if (_root != nint.Zero)
                return;

            var root = _api.CreateGameObject($"xphorror.PcModCompat HUD [{_ownerId}]");
            _api.SetActive(root, false);
            var canvas = _api.AddComponent(root, _api.CanvasType);
            var scaler = _api.AddComponent(root, _api.CanvasScalerType);
            if (canvas == nint.Zero || scaler == nint.Zero)
                throw new InvalidOperationException("Canvas or CanvasScaler creation failed");

            _api.SetCanvasRenderMode(canvas, 0);
            _api.SetCanvasSortingOrder(canvas, 0);
            _api.SetCanvasScaleMode(scaler, 1);
            _api.SetCanvasReferenceResolution(scaler, 1920f, 1080f);
            _api.SetCanvasMatch(scaler, 0.5f);

            var rootTransform = RequireObject(_api.GetTransform(root), "root RectTransform");
            RootObject(rootTransform);
            var panelObject = _api.CreateGameObject("Background");
            var panel = RequireObject(_api.AddComponent(panelObject, _api.ImageType), "background Image");
            RootObject(panel);
            var panelRect = RequireObject(_api.GetTransform(panelObject), "background RectTransform");
            RootObject(panelRect);
            _api.SetParent(panelRect, rootTransform);
            _api.SetRaycastTarget(panel, false);

            var mainObject = _api.CreateGameObject("Text");
            var mainText = RequireObject(_api.AddComponent(mainObject, _api.TextMeshProType), "main TextMeshProUGUI");
            RootObject(mainText);
            var mainRect = RequireObject(_api.GetRectTransform(mainText, mainObject), "main RectTransform");
            RootObject(mainRect);
            _api.SetParent(mainRect, rootTransform);
            _api.ConfigureText(mainText, richText: true);

            var progressBackgroundObject = _api.CreateGameObject("ProgressBar.Background");
            var progressBackground = RequireObject(
                _api.AddComponent(progressBackgroundObject, _api.ImageType),
                "progress background Image");
            RootObject(progressBackground);
            var progressBackgroundRect = RequireObject(
                _api.GetTransform(progressBackgroundObject),
                "progress background RectTransform");
            RootObject(progressBackgroundRect);
            _api.SetParent(progressBackgroundRect, rootTransform);
            _api.SetRaycastTarget(progressBackground, false);

            var progressFillObject = _api.CreateGameObject("ProgressBar.Fill");
            var progressFill = RequireObject(
                _api.AddComponent(progressFillObject, _api.ImageType),
                "progress fill Image");
            RootObject(progressFill);
            var progressFillRect = RequireObject(
                _api.GetTransform(progressFillObject),
                "progress fill RectTransform");
            RootObject(progressFillRect);
            _api.SetParent(progressFillRect, rootTransform);
            _api.SetRaycastTarget(progressFill, false);

            var font = _api.ApplyLocalizedFont(mainText);
            if (font == nint.Zero)
                font = _api.GetFont(mainText);
            _gameFont = font;
            _font = font;

            _api.SetGraphicColor(panel, 0.125f, 0.125f, 0.125f, 0f);
            _api.SetGraphicColor(progressBackground, 0.18f, 0.18f, 0.18f, 0.42f);
            _api.SetGraphicColor(progressFill, 0.88f, 0.77f, 1f, 1f);
            _api.SetGraphicColor(mainText, 1f, 1f, 1f, 1f);
            _api.SetActive(progressBackgroundObject, false);
            _api.SetActive(progressFillObject, false);
            _api.DontDestroyOnLoad(root);

            _root = root;
            _rootTransform = rootTransform;
            _panel = panel;
            _panelRect = panelRect;
            _progressBarBackgroundObject = progressBackgroundObject;
            _progressBarBackgroundRect = progressBackgroundRect;
            _progressBarFillObject = progressFillObject;
            _progressBarFillRect = progressFillRect;
            _mainText = mainText;
            _mainRect = mainRect;
            Logger.Info(
                LogTag,
                $"Unity Canvas HUD created owner={_ownerId} root=0x{root.ToInt64():X} font=0x{font.ToInt64():X}");
        }

        private void ApplyProgressBarFrame(
            PcCompatUnityHudFrame frame,
            float x,
            float y,
            float width,
            float height,
            float scale)
        {
            _progressBarVisible = frame.ProgressBarVisible;
            _progressBarValue = frame.ProgressBarValue;
            if (!frame.ProgressBarVisible)
            {
                _api.SetActive(_progressBarBackgroundObject, false);
                _api.SetActive(_progressBarFillObject, false);
                if (_resourceProgressObject != nint.Zero)
                    _api.SetActive(_resourceProgressObject, false);
                return;
            }

            var barX = x + 14f * scale;
            var barWidth = Math.Max(1f, width - 28f * scale);
            var resourceBarHeight = Math.Max(2f, 18f * scale);
            var resourceBarY = y + height - 18f * scale;
            if (TryApplyResourceProgressBar(
                    frame,
                    barX,
                    resourceBarY,
                    barWidth,
                    resourceBarHeight,
                    scale))
            {
                _api.SetActive(_progressBarBackgroundObject, false);
                _api.SetActive(_progressBarFillObject, false);
                return;
            }

            if (_resourceProgressObject != nint.Zero)
                _api.SetActive(_resourceProgressObject, false);
            var barHeight = Math.Max(2f, 6f * scale);
            var barY = y + height - 12f * scale;
            var fillWidth = Math.Max(1f, barWidth * Math.Clamp(frame.ProgressBarValue, 0f, 1f));
            _api.SetActive(_progressBarBackgroundObject, true);
            _api.SetActive(_progressBarFillObject, true);
            _api.SetTopLeftRect(_progressBarBackgroundRect, barX, barY, barWidth, barHeight);
            _api.SetTopLeftRect(_progressBarFillRect, barX, barY, fillWidth, barHeight);
        }

        private void ApplyResourceFont(PcCompatUnityHudFrame frame)
        {
            if (frame.PlainText.Any(character => character > 0x7f))
            {
                RestoreGameFont();
                return;
            }

            var status = PcCompatResourceBundleLoader.TryGetOrRequestAsset(
                _ownerId,
                "overlay.font",
                "TMP_FontAsset",
                out var font,
                out var binding);
            if (status == PcCompatResourceAssetStatus.Ready && font != nint.Zero)
            {
                if (_resourceFontObject != nint.Zero && _resourceFontObject != font)
                    _api.Forget(_resourceFontObject);
                if (font != _font)
                {
                    _api.SetFont(_mainText, font);
                    _font = font;
                }
                _resourceFontObject = font;
                _resourceFontBinding = binding;
                return;
            }

            if (_resourceFontBinding != null &&
                (!PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                     _ownerId,
                     "overlay.font",
                     "TMP_FontAsset",
                     out var currentBinding) ||
                 !SameBinding(_resourceFontBinding, currentBinding)))
                RestoreGameFont();
        }

        private bool TryApplyResourceProgressBar(
            PcCompatUnityHudFrame frame,
            float x,
            float y,
            float width,
            float height,
            float scale)
        {
            var status = PcCompatResourceBundleLoader.TryGetOrRequestAsset(
                _ownerId,
                "overlay.progress_bar",
                "GameObject",
                out var prefab,
                out var binding);
            if (status != PcCompatResourceAssetStatus.Ready || prefab == nint.Zero)
            {
                if (_resourceProgressBinding != null &&
                    (!PcCompatResourceRecipeRuntime.TryResolveLoadedBinding(
                         _ownerId,
                         "overlay.progress_bar",
                         "GameObject",
                         out var currentBinding) ||
                     !SameBinding(_resourceProgressBinding, currentBinding)))
                    ReleaseResourceProgressBar(destroyObject: true);
                return false;
            }

            var bindingKey = BindingKey(binding);
            if (_resourceProgressObject == nint.Zero ||
                _resourceProgressBinding == null ||
                !SameBinding(_resourceProgressBinding, binding))
            {
                if (_resourceProgressFailureKey.Equals(bindingKey, StringComparison.Ordinal))
                    return false;
                try
                {
                    CreateResourceProgressBar(prefab, binding);
                }
                catch (Exception ex)
                {
                    _resourceProgressFailureKey = bindingKey;
                    ReleaseResourceProgressBar(destroyObject: true);
                    Logger.Warn(
                        LogTag,
                        $"progress prefab adapter rejected mod={binding.ModId} asset={binding.AssetName}: {ex.Message}");
                    return false;
                }
            }

            _api.SetTopLeftRect(_resourceProgressRect, x, y, width, height);
            _api.SetSizeDeltaX(
                _resourceProgressLineRect,
                Math.Max(0f, (width - 4f * scale) * Math.Clamp(frame.ProgressBarValue, 0f, 1f)));
            _api.SetActive(_resourceProgressObject, true);
            return true;
        }

        private void CreateResourceProgressBar(
            nint prefab,
            PcCompatResolvedResourceBinding binding)
        {
            ReleaseResourceProgressBar(destroyObject: true);
            var instance = RequireObject(_api.Instantiate(prefab), "resource progress prefab instance");
            _resourceProgressObject = instance;
            RootResourceObject(instance);
            var transform = RequireObject(_api.GetTransform(instance), "resource progress Transform");
            var rect = RequireObject(
                _api.GetComponent(transform, _api.RectTransformType),
                "resource progress RectTransform");
            _resourceProgressRect = rect;
            RootResourceObject(rect);
            _api.SetParent(rect, _rootTransform);

            var lineTransform = RequireObject(_api.FindChild(rect, "line"), "resource progress child 'line'");
            var border = RequireObject(_api.FindChild(rect, "borderLine"), "resource progress child 'borderLine'");
            var background = RequireObject(_api.FindChild(rect, "background"), "resource progress child 'background'");
            var line = RequireObject(
                _api.GetComponent(lineTransform, _api.RectTransformType),
                "resource progress line RectTransform");
            _resourceProgressLineRect = line;
            RootResourceObject(line);
            var lineImage = RequireObject(_api.GetComponent(line, _api.ImageType), "resource progress line Image");
            var borderImage = RequireObject(_api.GetComponent(border, _api.ImageType), "resource progress border Image");
            var backgroundImage = RequireObject(
                _api.GetComponent(background, _api.ImageType),
                "resource progress background Image");
            _api.SetRaycastTarget(lineImage, false);
            _api.SetRaycastTarget(borderImage, false);
            _api.SetRaycastTarget(backgroundImage, false);
            _api.SetActive(instance, false);
            _resourceProgressBinding = binding;
            _resourceProgressFailureKey = string.Empty;
            Logger.Info(
                LogTag,
                $"progress prefab instantiated mod={binding.ModId} asset={binding.AssetName} " +
                $"generation={binding.SessionGeneration}");
        }

        private void RestoreGameFont()
        {
            if (_resourceFontBinding == null && _resourceFontObject == nint.Zero)
                return;
            var resourceFont = _resourceFontObject;
            _resourceFontObject = nint.Zero;
            _resourceFontBinding = null;
            _api.Forget(resourceFont);
            var localizedFont = _api.ApplyLocalizedFont(_mainText);
            if (localizedFont != nint.Zero)
            {
                _gameFont = localizedFont;
                _font = localizedFont;
            }
            else if (_gameFont != nint.Zero && _gameFont != _font)
            {
                _api.SetFont(_mainText, _gameFont);
                _font = _gameFont;
            }
        }

        private void ReleaseResourceProgressBar(bool destroyObject)
        {
            var instance = _resourceProgressObject;
            var rect = _resourceProgressRect;
            var lineRect = _resourceProgressLineRect;
            _resourceProgressObject = nint.Zero;
            _resourceProgressRect = nint.Zero;
            _resourceProgressLineRect = nint.Zero;
            _resourceProgressBinding = null;
            if (instance != nint.Zero)
            {
                try { _api.SetActive(instance, false); } catch { }
                if (destroyObject)
                {
                    try { _api.Destroy(instance); } catch { }
                }
            }
            _api.Forget(instance);
            _api.Forget(rect);
            _api.Forget(lineRect);
            _resourceGcRoots.Clear();
        }

        private void RootObject(nint obj)
            => _gcRoots.Add(new Il2CppSystem.Object(obj));

        private void RootResourceObject(nint obj)
            => _resourceGcRoots.Add(new Il2CppSystem.Object(obj));

        private static nint RequireObject(nint obj, string label)
            => obj != nint.Zero
                ? obj
                : throw new InvalidOperationException($"Unity object creation failed: {label}");
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
}
