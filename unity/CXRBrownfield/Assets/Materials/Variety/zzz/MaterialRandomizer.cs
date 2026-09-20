// Inspector for a material pack demo. Editor only: unguarded it fails every player build.
#if UNITY_EDITOR
using UnityEngine;
using System.Collections;
using UnityEditor;

[CustomEditor(typeof(MaterialRandomizerScript))]
public class MaterialRandomizer : Editor 
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        MaterialRandomizerScript myScript = (MaterialRandomizerScript)target;
        if(GUILayout.Button("Find Materials"))
        {
            myScript.findMaterials();
        }
        if(GUILayout.Button("Find Spheres"))
        {
            myScript.findMaterialSpheres();
        }
        if(GUILayout.Button("Randomize Materials"))
        {
            myScript.randomizeMaterials();
        }
    }
}
#endif
