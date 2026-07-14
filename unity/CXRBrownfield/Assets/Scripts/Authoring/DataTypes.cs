using UnityEngine;
using System.Collections.Generic;

[System.Serializable]
public class TargetDimensionsFt
{
    public float width_ft;
    public float depth_ft;
    public float height_ft;
}

[System.Serializable]
public class GeneratedObject
{
    public string area_name;
    public string semantic_tag;
    public string object_type;
    public int[] bounding_box;
    public int[] center_point;
    public float rotation_y_deg;
    public float approx_sq_ft;
    public TargetDimensionsFt target_dimensions_ft;
    public string unity_strategy;
}

[System.Serializable]
public class GeneratedBuilding
{
    public string area_name;
    public string semantic_tag;
    public int[] bounding_box;
    public int[] center_point;
    public float rotation_y_deg;
    public int floors;
    public float approx_sq_ft;
    public string unity_strategy;
    // Optional non-90° footprint corners. Length-4 tilt angle (degrees) per corner, in the
    // building's local grid frame, order [SW, SE, NE, NW]; 0 (or null/empty) = square corner.
    // The wall at that corner tilts at this constant angle all the way to the far end, shearing the
    // footprint into a trapezoid (one face stays straight, the opposite leaves the corner as a
    // single straight slant). Negative = acute, positive = obtuse. Only emit when the sketch clearly
    // shows an angled corner.
    public float[] corner_angles;
}

[System.Serializable]
public class TerrainZone
{
    public string area_name;
    public string semantic_tag;
    public string terrain_type;
    public int[] bounding_box;
    public float approx_sq_ft;
    public string unity_strategy;
}

[System.Serializable]
public class SiteScale {
    public int[] normalized_canvas;
    public float site_width_ft;
    public float site_height_ft;
    public float[][] lot_boundary;
    public string scale_note;
}

[System.Serializable]
public class PrefabInstance {
    public string area_name;
    public string semantic_tag;
    public string prefab_type;
    public int[] center_point;
    public int[] footprint_box;
    public float rotation_deg;
    public float scale_multiplier;
    public string unity_strategy;
}

[System.Serializable]
public class GeneratedPath {
    public string area_name;
    public string semantic_tag;
    public string path_material;   // canonical id: pavement_dark|pavement_light|brick|dirt|asphalt
    public float width_ft;         // path width in feet (consistent with target_dimensions_ft)
    public int[][] points;         // [[y, x], ...] canvas coords (same convention as other elements)
    public float smoothing = -1f;  // 0=crisp..1=curvy; -1 (omitted) = derive from path_material
}

[System.Serializable]
public class GeneratedFence {
    public string area_name;
    public string semantic_tag;
    public string fence_type;      // canonical id: picket|lattice|chain_link|wood_privacy|wrought_iron
    public int[][] points;         // [[y, x], ...] canvas coords (same convention as other elements)
    public float height_ft = -1f;  // fence height in feet; -1 (omitted) = use FencePalette default
    public float smoothing = -1f;  // 0=crisp..1=curvy; -1 (omitted) = crisp (fences are usually straight)
}

[System.Serializable]
public class FullTerrainData {
    public SiteScale site_scale;
    public List<TerrainZone> terrain_zones;
    public List<GeneratedBuilding> generated_buildings;
    public List<GeneratedObject> generated_objects;
    public List<PrefabInstance> prefab_instances;
    public List<GeneratedPath> paths;
    public List<GeneratedFence> fences;
}
