using UnityEngine;

// Attached by WorldRenderer to every rendered path ribbon so EditController can identify it via
// Physics.Raycast (for selection / deletion). Mirrors InstanceMarker for object/building instances.
public class PathMarker : MonoBehaviour
{
    public string pathId;
}
