using System.Reflection;
using JALib.Core.Patch;

namespace JALib.Core;

public abstract class MultiFeature
{
    private readonly HashSet<Feature> _enabledFeatures = [];

    protected MultiFeature(JAMod mod)
    {
        Mod = mod ?? throw new ArgumentNullException(nameof(mod));
        Patcher = new JAPatcher(mod);
        Patcher.OnFailPatch += OnFailPatch;
    }

    public readonly JAPatcher Patcher;
    public readonly JAMod Mod;

    internal static MultiFeature GetMultiFeaturePatch(JAMod mod, Type type)
    {
        if (mod.MultiFeatures.TryGetValue(type, out var patch))
            return patch;
        if (!typeof(MultiFeature).IsAssignableFrom(type))
        {
            patch = new DefaultTypeMultiPatch(mod, type);
        }
        else
        {
            patch = CreateCustom(mod, type);
        }
        mod.MultiFeatures[type] = patch;
        return patch;
    }

    public void ActiveFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (!_enabledFeatures.Add(feature) || _enabledFeatures.Count != 1)
            return;
        Patcher.Patch();
        OnEnable();
    }

    public void InactiveFeature(Feature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        if (!_enabledFeatures.Remove(feature) || _enabledFeatures.Count != 0)
            return;
        Patcher.Unpatch();
        OnDisable();
    }

    protected virtual void OnEnable() { }
    protected virtual void OnDisable() { }

    private void OnFailPatch(string patchId, bool disabled)
    {
        if (!disabled)
            return;
        foreach (var feature in _enabledFeatures.ToArray())
        {
            try
            {
                feature.Disable();
            }
            catch (Exception exception)
            {
                Mod.LogReportException(
                    $"OnFailPatch Error for feature '{feature.Name}' in patch '{patchId}'",
                    exception);
            }
        }
    }

    private static MultiFeature CreateCustom(JAMod mod, Type type)
    {
        var constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: [typeof(JAMod)],
            modifiers: null);
        if (constructor != null)
            return (MultiFeature)constructor.Invoke([mod]);

        constructor = type.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (constructor != null)
            return (MultiFeature)constructor.Invoke(null);
        throw new MissingMethodException(
            type.FullName,
            ".ctor(JAMod) or .ctor()");
    }

    private sealed class DefaultTypeMultiPatch : MultiFeature
    {
        public DefaultTypeMultiPatch(JAMod mod, Type type)
            : base(mod)
            => Patcher.AddPatch(type);
    }
}
