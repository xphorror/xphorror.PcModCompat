using UnityEngine.UI;
using Xphorror.PcModCompat;

namespace JipperKeyViewer.KeyViewer;

/// <summary>
/// Stands in for a MOD-owned render component using JipperKeyViewer's released type name. The
/// catalog matches the verified base/callback shape rather than this name.
/// </summary>
/// <remarks>
/// The shape that matters is copied from the release source: derives <c>MaskableGraphic</c> (a proxy
/// type in production, which is the whole reason a registration is needed), and declares exactly one
/// <c>protected override void OnPopulateMesh(VertexHelper)</c>. Protected is the point - the bridge
/// has to bind it without changing its accessibility.
/// </remarks>
public class RainGraphic : MaskableGraphic
{
    public bool renderMain = true;
    public int PopulateCount { get; private set; }
    public object? LastArgument { get; private set; }
    public bool ThrowOnPopulate { get; set; }

    protected override void OnPopulateMesh(VertexHelper vh)
    {
        if (ThrowOnPopulate)
            throw new InvalidOperationException("injected OnPopulateMesh failure");
        PopulateCount++;
        LastArgument = vh;
    }
}
