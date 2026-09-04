using UnityEngine.UI;

namespace JipperKeyViewer.KeyViewer;

public class KeyShapeLayer : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}

public class RainLayer : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}

public class GhostRainLayer : MaskableGraphic
{
    protected override void OnPopulateMesh(VertexHelper vh) => vh.Clear();
}
