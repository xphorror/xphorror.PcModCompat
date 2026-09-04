using StArray.ModManager.Resources;
using System.Numerics;
using System.Text;
using System.Text.Json;
using IconFonts;
using ImGuiNET;
using StArray.ModManager.Inspector;
using StArray.ModManager.Runtime;
using Xphorror.PcModCompat;

namespace StArray.ModManager.Manager;

partial class ModManagerUI
{
    private static void RenderModState(ModEntry mod)
    {
        var color = mod.LoadState switch
        {
            ModLoadState.Loaded => new Vector4(0.2f, 0.8f, 0.2f, 1f),
            ModLoadState.Loading => new Vector4(0.8f, 0.8f, 0.2f, 1f),
            ModLoadState.Error => new Vector4(0.9f, 0.2f, 0.2f, 1f),
            _ => new Vector4(0.5f, 0.5f, 0.5f, 1f),
        };

        ImGui.PushStyleColor(ImGuiCol.Text, color);
        ImGui.Text(L10n.Get("State_" + mod.LoadState));

        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.Text(L10n.Get("State_" + mod.LoadState));
            if (mod.LoadState == ModLoadState.Error && !string.IsNullOrEmpty(mod.LoadError))
            {
                ImGui.TextColored(new Vector4(1, 0.3f, 0.3f, 1), mod.LoadError);
            }
            ImGui.EndTooltip();
        }
        ImGui.PopStyleColor();
    }

    private void RenderModAction(ModEntry mod)
    {
        string icon;
        string label;
        Action action;

        switch (mod.LoadState)
        {
            case ModLoadState.Loading:
                icon = FontAwesome7.Xmark;
                label = L10n.Get("Btn_CancelLoad");
                action = () => _modManager.UnloadMod(mod);
                break;
            case ModLoadState.Loaded:
                icon = FontAwesome7.Stop;
                label = L10n.Get("Btn_UnloadMod");
                action = () => _modManager.UnloadMod(mod);
                break;
            case ModLoadState.Error:
                icon = FontAwesome7.Rotate;
                label = L10n.Get("Btn_RetryLoad");
                action = () => _modManager.LoadMod(mod);
                break;
            default:
                icon = FontAwesome7.Play;
                label = L10n.Get("Btn_LoadMod");
                action = () => _modManager.LoadMod(mod);
                break;
        }

        bool clicked = ImGui.SmallButton($"{icon} {label}##action_{mod.Id}");
        if (clicked)
        {
            try
            {
                action();
                SaveConfig();
            }
            catch (Exception exception)
            {
                mod.LoadError = exception.Message;
                Logger.Error(
                    nameof(ModManagerUI),
                    $"MOD action failed id={mod.Id} state={mod.LoadState}: {exception}");
            }
        }
    }

    private void RenderModSettingsWindow()
    {
        if (_expandedModId == null) return;

        var mod = _modManager.Mods.FirstOrDefault(m => m.Id == _expandedModId);
        if (mod?.PluginInstance is not IModSettings settings)
        {
            _expandedModId = null;
            return;
        }

        var open = true;
        var title = L10n.Get("Mod.WindowTitle", mod.Name) + $"###ModSettings_{mod.Id}";
        var layout = settings as IModSettingsLayout;
        var preferredSize = layout?.PreferredWindowSize ?? new Vector2(520, 360);
        ImGui.SetNextWindowSize(preferredSize * LayoutScale, ImGuiCond.Once);
        ImGui.SetNextWindowSizeConstraints(
            new Vector2(480 * LayoutScale, 320 * LayoutScale),
            Vector2.One * 100000f);

        if (ImGui.Begin(title, ref open))
        {
            TrackCurrentWindowInputRect();
            if (mod.LoadState == ModLoadState.Loaded &&
                mod.PluginInstance is IModOriginalSettingsSurface)
            {
                if (ImGui.Button(
                        FontAwesome7.ArrowUpRightFromSquare + " " +
                        L10n.Get("Mod.OpenOriginalSettings") +
                        $"##original_settings_{mod.Id}"))
                {
                    TryOpenOriginalSettingsSurface(mod);
                }
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.Spacing();
            }
            ImGui.PushTextWrapPos();
            if (mod.TryEnterRuntimeCallback(out var callbackLease))
            {
                using (callbackLease)
                using (HookHelper.EnterOwnerScope(
                           mod.RuntimeOwnerId,
                           mod.RuntimeSession,
                           mod.RuntimeKey))
                {
                    // Fault boundary inside the Host's Begin/End pair: an exception escaping
                    // here would skip PopTextWrapPos and End and corrupt the whole frame's
                    // window stack, taking the manager's own UI down with the MOD.
                    UiOwnerScope.TryDraw(
                        mod.RuntimeOwnerId,
                        mod.RuntimeKey.Generation,
                        "settings OnGui",
                        settings.OnGui);
                }
            }
            ImGui.PopTextWrapPos();

            if (layout?.ShowSaveButton != false)
            {
                ImGui.Spacing();
                ImGui.Separator();

                if (ImGui.Button(FontAwesome7.FloppyDisk + " " + L10n.Get("Btn.Save")))
                    SaveSettings(mod, settings);
            }
        }
        ImGui.End();

        if (!open)
            _expandedModId = null;
    }

    /// <summary>序列化检查器可持久化成员到 {mod.FolderPath}/settings.json</summary>
    public void SaveSettings(ModEntry mod, IModSettings settings)
    {
        try
        {
            var path = Path.Combine(mod.FolderPath, "settings.json");
            var members = ModInspector.GetSettingMembers(settings.GetType());
            var dict = new Dictionary<string, JsonElement>();
            foreach (var member in members)
            {
                try
                {
                    var value = member.Get(settings);
                    dict[member.Name] = JsonSerializer.SerializeToElement(
                        value,
                        member.ValueType,
                        ModInspector.SettingsJson);
                }
                catch (Exception ex)
                {
                    Logger.Warn(nameof(ModManagerUI),
                        $"SaveSettings skipped mod={mod.Id} member={member.Name} " +
                        $"type={member.ValueType.FullName}: {ex.GetType().Name}: {SingleLine(ex.Message)}");
                }
            }
            var json = JsonSerializer.Serialize(
                dict,
                ModManagerJsonContext.Default.DictionaryStringJsonElement);
            File.WriteAllText(path, json, Encoding.UTF8);
            _toastMessage = L10n.Get("Toast.ModSaved", mod.Name);
            _toastTimer = 2.5f;
        }
        catch (Exception ex)
        {
            _toastMessage = L10n.Get("Toast.SaveFailed", ex.Message);
            _toastTimer = 3f;
            Logger.Error(nameof(ModManagerUI), $"SaveSettings: {ex.Message}");
        }
    }

    /// <summary>从 {mod.FolderPath}/settings.json 逐项恢复检查器可持久化成员</summary>
    public static void LoadSettings(ModEntry mod, IModSettings settings)
    {
        try
        {
            var path = Path.Combine(mod.FolderPath, "settings.json");
            if (!File.Exists(path)) return;

            var json = File.ReadAllText(path, Encoding.UTF8);
            var dict = JsonSerializer.Deserialize(json, ModManagerJsonContext.Default.DictionaryStringJsonElement);
            if (dict == null) return;

            foreach (var member in ModInspector.GetSettingMembers(settings.GetType()))
            {
                if (!dict.TryGetValue(member.Name, out var elem))
                    continue;

                try
                {
                    var value = elem.Deserialize(member.ValueType, ModInspector.SettingsJson);
                    if (value == null && member.ValueType.IsValueType &&
                        Nullable.GetUnderlyingType(member.ValueType) == null)
                    {
                        throw new JsonException("deserialized value is null for a non-nullable value type");
                    }

                    member.Set(settings, value);
                }
                catch (Exception ex)
                {
                    Logger.Warn(nameof(ModManagerUI),
                        $"LoadSettings skipped mod={mod.Id} member={member.Name} " +
                        $"type={member.ValueType.FullName}: {ex.GetType().Name}: {SingleLine(ex.Message)}");
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(nameof(ModManagerUI), $"LoadSettings: {ex.Message}");
        }
    }

    private void RenderToast()
    {
        if (_toastTimer <= 0) return;

        _toastTimer -= ImGui.GetIO().DeltaTime;
        L10n.RegisterDynamicGlyphText(_toastMessage);

        float alpha = Math.Min(_toastTimer / 0.5f, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.Alpha, alpha);

        var vp = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(vp.Pos + new Vector2(vp.Size.X - 440, vp.Size.Y - 80),
            ImGuiCond.Always, new Vector2(1, 1));
        ImGui.SetNextWindowSize(new Vector2(420, 0));

        if (ImGui.Begin("##toast", ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoInputs |
            ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing))
        {
            ImGui.PushTextWrapPos();
            ImGui.TextWrapped(_toastMessage);
            ImGui.PopTextWrapPos();
        }
        ImGui.End();
        ImGui.PopStyleVar();
    }

    private void RenderAddModPopup()
    {
        if (!_showAddModPopup) return;

        ImGui.OpenPopup(L10n.Get("AddMod_Title"));

        var center = ImGui.GetMainViewport().GetCenter();
        ImGui.SetNextWindowPos(center, ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(560 * LayoutScale, 0), ImGuiCond.Appearing);

        if (ImGui.BeginPopupModal(L10n.Get("AddMod_Title"), ref _showAddModPopup,
            ImGuiWindowFlags.AlwaysAutoResize))
        {
            TrackCurrentWindowInputRect();
            if (_platform.SupportsModZipImport)
                _lastImportStatus = _platform.GetModZipImportStatus();

            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * LayoutScale);
            ImGui.TextWrapped(L10n.Get("AddMod_ImportHint"));
            ImGui.PopTextWrapPos();
            ImGui.Spacing();

            if (!_platform.SupportsModZipImport)
            {
                ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * LayoutScale);
                ImGui.TextColored(new Vector4(1f, 0.4f, 0.2f, 1f), L10n.Get("AddMod_Unsupported"));
                ImGui.PopTextWrapPos();
            }
            else
            {
                if (ImGui.Button(FontAwesome7.FileImport + " " + L10n.Get("AddMod_SelectZip")))
                    _platform.RequestModZipImport();

                ImGui.Separator();
                ImGui.Text(L10n.Get("AddMod_Status"));
                ImGui.SameLine();
                ImGui.TextColored(GetImportStatusColor(_lastImportStatus.State),
                    L10n.Get("AddMod_Status_" + _lastImportStatus.State));
                if (!string.IsNullOrWhiteSpace(_lastImportStatus.Message))
                {
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * LayoutScale);
                    ImGui.TextWrapped(_lastImportStatus.Message);
                    ImGui.PopTextWrapPos();
                }
                if (!string.IsNullOrWhiteSpace(_lastImportStatus.Path))
                {
                    ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + 520 * LayoutScale);
                    ImGui.TextWrapped(_lastImportStatus.Path);
                    ImGui.PopTextWrapPos();
                }

                if (_lastImportStatus.State == ModImportState.Imported &&
                    _lastImportStatus.Serial != _handledImportSerial)
                {
                    _handledImportSerial = _lastImportStatus.Serial;
                    _selectedMod = _modManager.RefreshImportedMod(_lastImportStatus.Path);
                    RegisterModGlyphText();
                    if (_selectedMod != null)
                    {
                        _toastMessage = L10n.Get("AddMod_ImportedReady");
                        _toastTimer = 2.5f;
                    }
                    else
                    {
                        _toastMessage = L10n.Get("AddMod_ImportedNotFound");
                        _toastTimer = 3f;
                    }
                }
            }

            ImGui.Spacing();
            if (ImGui.Button(L10n.Get("Btn_Close")))
                _showAddModPopup = false;

            ImGui.EndPopup();
        }
    }

    private static Vector4 GetImportStatusColor(ModImportState state) => state switch
    {
        ModImportState.Imported => new Vector4(0.2f, 0.8f, 0.2f, 1f),
        ModImportState.Error => new Vector4(1f, 0.25f, 0.25f, 1f),
        ModImportState.Cancelled => new Vector4(0.8f, 0.8f, 0.25f, 1f),
        ModImportState.Importing or ModImportState.Selecting => new Vector4(0.25f, 0.65f, 1f, 1f),
        _ => new Vector4(0.7f, 0.7f, 0.7f, 1f)
    };

    private static string SingleLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "<empty>"
            : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

}
