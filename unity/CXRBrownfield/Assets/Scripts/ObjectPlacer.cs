using UnityEngine;

public class ObjectPlacer : MonoBehaviour
{
    public PrefabRegistry registry;

    public void PlaceObjects(FullTerrainData data, Terrain terrain)
    {
        Vector3 terrainSize = terrain.terrainData.size;
        // Based on your JSON normalized_canvas [0,0,1000,1000]
        float canvasWidth = data.site_scale.normalized_canvas[2]; 
        float canvasHeight = data.site_scale.normalized_canvas[3];

        foreach (var instance in data.prefab_instances)
        {
            GameObject prefab = registry.GetPrefab(instance.prefab_type);
            if (prefab == null) continue;

            // 1. Calculate Normalized Position (0 to 1)
            float normX = instance.center_point[0] / canvasWidth;
            float normZ = instance.center_point[1] / canvasHeight;

            // 2. Convert to World Position
            float worldX = normX * terrainSize.x;
            float worldZ = normZ * terrainSize.z;
            
            // 3. Get Terrain Height at that point
            float worldY = terrain.SampleHeight(new Vector3(worldX, 0, worldZ));
            Vector3 finalPos = terrain.transform.position + new Vector3(worldX, worldY, worldZ);

            // 4. Instantiate and Adjust — rotation_deg is a delta on top of the prefab's
            //    authored orientation, so prefabs modeled facing a direction keep that baseline.
            GameObject obj = Instantiate(prefab, finalPos,
                Quaternion.Euler(0, instance.rotation_deg, 0) * prefab.transform.rotation);
            obj.transform.localScale *= instance.scale_multiplier;
            obj.name = instance.area_name;
        }
    }
}