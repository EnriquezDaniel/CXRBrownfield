using UnityEngine;

// Attached by WorldRenderer to every rendered fence segment (panel/post) so EditController can
// identify and clear a fence's geometry by id. Mirrors PathMarker for path ribbons.
public class FenceMarker : MonoBehaviour
{
    public string fenceId;
}
