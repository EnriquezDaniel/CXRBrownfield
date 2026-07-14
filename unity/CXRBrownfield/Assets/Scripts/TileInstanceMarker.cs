using UnityEngine;

// Attached to each tile GameObject by TileBuildingEditor so face-paint raycasts
// can identify which grid cell was hit.
public class TileInstanceMarker : MonoBehaviour
{
    public int gridX, gridZ, floor;
}
