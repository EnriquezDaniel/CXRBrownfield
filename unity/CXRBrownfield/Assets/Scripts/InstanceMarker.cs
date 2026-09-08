using UnityEngine;

// Attached by WorldRenderer to every instantiated environment object/building root
// so EditController can identify it via Physics.Raycast.
public class InstanceMarker : MonoBehaviour
{
    public string instanceId;
    public bool   isBuilding;   // true = BuildingInstance, false = ObjectInstance
    // True for a decoration painted onto a building face (BuildingDef.embeddedObjects). Such an id
    // lives on the BuildingDef, NOT in env.objectInstances, so it must never become a scene
    // selection — EditController resolves the hit to the host building instead (see UpdateBrowse).
    public bool   isEmbedded;
    // Mirrors the def's `optional` flag. WorldRenderer.SetOptionalHidden toggles these GOs for the
    // editor's Low detail preview; the VR viewer never spawns them at all (skipOptional).
    public bool   isOptional;
}
