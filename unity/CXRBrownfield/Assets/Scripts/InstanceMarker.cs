using UnityEngine;

// Attached by WorldRenderer to every instantiated environment object/building root
// so EditController can identify it via Physics.Raycast.
public class InstanceMarker : MonoBehaviour
{
    public string instanceId;
    public bool   isBuilding;   // true = BuildingInstance, false = ObjectInstance
}
