using UnityEngine;

// Attached by WorldRenderer to every rendered water body mesh so EditController can identify it
// via Physics.Raycast (click to edit). Mirrors PathMarker / FenceMarker. The mesh sits on the Water
// layer, which the walkthrough walker ignores, so a walker wades down into the carved bed instead
// of standing on the surface (WorldRenderer.RenderWater).
public class WaterMarker : MonoBehaviour
{
    public string waterId;
}
