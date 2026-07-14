using UnityEngine;
using System.Collections.Generic;

public class WorldGenerator : MonoBehaviour 
{
    [Header("References")]
    [SerializeField] private Terrain targetTerrain;
    [SerializeField] private PrefabRegistry prefabRegistry;
    [SerializeField] private TerrainRegistry terrainRegistry;
    [SerializeField] private BuildingGenerator buildingGenerator;

    [Header("Input")] 
    [SerializeField] private float prefabScaleFactor = 1;
    
    [Header("Test")] 
    [SerializeField] private bool testGeneration;
    [TextArea(15, 30)]
    [SerializeField] private string jsonInput;
    private float _canvasHeight;
    private float _canvasWidth;
    private float defaultYRotation = 90f;
    
    public void Start() {

        if (!testGeneration)
        {
            return;
        }
        
        if (string.IsNullOrWhiteSpace(jsonInput)) {
            Debug.Log("JSON input is null or whitespace");
            return;
        }

        Debug.Log("starting world generator!");

        FullTerrainData data = JsonUtility.FromJson<FullTerrainData>(jsonInput);
        ApplyLayoutData(data);
    }

    public void ApplyLayoutData(FullTerrainData data) {
        if (data == null) {
            Debug.LogWarning("WorldGenerator.ApplyLayoutData received null data.");
            return;
        }

        if (data.site_scale == null || data.site_scale.normalized_canvas == null || data.site_scale.normalized_canvas.Length < 4) {
            Debug.LogWarning("WorldGenerator.ApplyLayoutData received invalid site scale data.");
            return;
        }

        _canvasWidth = data.site_scale.normalized_canvas[2];
        _canvasHeight = data.site_scale.normalized_canvas[3];

        Debug.Log("Applying generated layout to world...");
        PaintTerrain(data);
        PlacePrefabs(data);
        GenerateModularBuildings(data);
        CreateBoxBuildings(data);
    }
    
    public void GenerateModularBuildings(FullTerrainData data)
    {
        if (targetTerrain == null || buildingGenerator == null)
        {
            Debug.LogError("WorldGenerator: Missing dependencies for modular generation.");
            return;
        }

        Transform buildingRoot = GetOrCreateBuildingRoot();
        Vector3 terrainSize = targetTerrain.terrainData.size;

        foreach (var bldg in data.generated_buildings)
        {
            float normX = bldg.center_point[0] / _canvasWidth;
            float normZ = bldg.center_point[1] / _canvasHeight;
        
            float worldX = normX * terrainSize.x;
            float worldZ = normZ * terrainSize.z;
            float terrainHeight = targetTerrain.SampleHeight(new Vector3(worldX, 0, worldZ));
            Vector3 spawnPos = targetTerrain.transform.position + new Vector3(worldX, terrainHeight, worldZ);

            // Calculate Footprint Dimensions in Unity Units
            float worldWidth = ((bldg.bounding_box[2] - bldg.bounding_box[0]) / _canvasWidth) * terrainSize.x;
            float worldDepth = ((bldg.bounding_box[3] - bldg.bounding_box[1]) / _canvasHeight) * terrainSize.z;
            
            Debug.Log($"World width is {worldWidth} and world depth {worldDepth}");

            // 3. Determine Bay Counts based on Ground Prefab Collider
            Renderer renderer = buildingGenerator.GetRenderer();
            if (renderer == null)
            {
                Debug.LogError("WorldGenerator: Building renderer is null.");
                continue;
            }
            
            // We divide the target area by the collider size to get bay counts
            Debug.Log($"bounds of prefab -> x: {renderer.bounds.size.x}, z: {renderer.bounds.size.z}" );
            int baysWide = Mathf.Max(1, Mathf.RoundToInt(worldWidth / renderer.bounds.size.x));
            int baysDeep = Mathf.Max(1, Mathf.RoundToInt(worldDepth / renderer.bounds.size.z));
        
            // You can determine floor count here (defaulting to 1 for now)
            int floors = 2; 

            // 4. Trigger Generation
            buildingGenerator.Generate(
                buildingRoot, 
                spawnPos, 
                baysWide, 
                baysDeep, 
                floors, 
                bldg.rotation_y_deg + defaultYRotation, 
                bldg.area_name 
            );
            
            Debug.Log($"Generating {bldg.area_name} with {floors} floors, {baysWide} bays wide, and {baysDeep} bays deep and rotation {bldg.rotation_y_deg + defaultYRotation}");
        }
    }
    public void CreateBoxBuildings(FullTerrainData data) {
        if (data.generated_objects == null || data.generated_objects.Count == 0) {
            Debug.Log("No generated objects found in layout data.");
            return;
        }

        if (targetTerrain == null) {
            Debug.LogWarning("WorldGenerator.CreateBuildings requires a targetTerrain.");
            return;
        }

        if (data.site_scale == null || data.site_scale.normalized_canvas == null || data.site_scale.normalized_canvas.Length < 4) {
            Debug.LogWarning("WorldGenerator.CreateBuildings received invalid site scale data.");
            return;
        }

        float canvasWidth = data.site_scale.normalized_canvas[2];
        float canvasHeight = data.site_scale.normalized_canvas[3];
        float siteWidthFt = data.site_scale.site_width_ft > 0f ? data.site_scale.site_width_ft : canvasWidth;
        float siteHeightFt = data.site_scale.site_height_ft > 0f ? data.site_scale.site_height_ft : canvasHeight;
        Vector3 terrainSize = targetTerrain.terrainData.size;

        Transform buildingRoot = GetOrCreateBuildingRoot();

        foreach (var building in data.generated_objects) {
            if (building == null || building.target_dimensions_ft == null || building.center_point == null || building.center_point.Length < 2) {
                continue;
            }

            float centerX = (building.center_point[0] / canvasWidth) * terrainSize.x;
            float centerZ = (building.center_point[1] / canvasHeight) * terrainSize.z;

            float terrainHeight = targetTerrain.SampleHeight(new Vector3(centerX, 0f, centerZ));
            float worldX = targetTerrain.transform.position.x + centerX;
            float worldY = targetTerrain.transform.position.y + terrainHeight;
            float worldZ = targetTerrain.transform.position.z + centerZ;

            float buildingWidth = (building.target_dimensions_ft.width_ft / siteWidthFt) * terrainSize.x;
            float buildingDepth = (building.target_dimensions_ft.depth_ft / siteHeightFt) * terrainSize.z;
            float buildingHeight = (building.target_dimensions_ft.height_ft / siteHeightFt) * terrainSize.x;

            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = string.IsNullOrWhiteSpace(building.area_name) ? "GeneratedBuilding" : building.area_name;
            cube.transform.SetParent(buildingRoot, true);
            cube.transform.position = new Vector3(worldX, worldY + (buildingHeight * 0.5f), worldZ);
            cube.transform.localScale = new Vector3(buildingWidth, buildingHeight, buildingDepth);
            cube.transform.localRotation = Quaternion.Euler(0, defaultYRotation, 0);

            if (building.object_type != null)
            {
                Debug.Log($"Created building cube: {cube.name} ({building.object_type})");
            }
        }
    }

    private Transform GetOrCreateBuildingRoot() {
        Transform existing = transform.Find("GeneratedBuildings");
        if (existing != null) {
            return existing;
        }

        GameObject root = new GameObject("GeneratedBuildings");
        root.transform.SetParent(transform, false);
        return root.transform;
    }

    private void ClearChildren(Transform root) {
        for (int i = root.childCount - 1; i >= 0; i--) {
            Transform child = root.GetChild(i);
            if (Application.isPlaying) {
                Destroy(child.gameObject);
            } else {
                DestroyImmediate(child.gameObject);
            }
        }
    }

   private void PaintTerrain(FullTerrainData data) {
    TerrainData tData = targetTerrain.terrainData;
    int res = tData.alphamapResolution;

    // 1. Prepare the Layers and a Lookup Table
    int layerCount = terrainRegistry.entries.Count;
    TerrainLayer[] layers = new TerrainLayer[layerCount];
    Dictionary<string, int> keyToIndex = new Dictionary<string, int>();

    for (int i = 0; i < layerCount; i++) {
        var entry = terrainRegistry.entries[i];
        layers[i] = entry.terrainLayer;
        // Store the index for fast lookup later
        keyToIndex[entry.key.ToLower()] = i; 
    }

    // Assign the layers to the terrain
    tData.terrainLayers = layers;

    // 2. Initialize the Alphamap (3D array: [y, x, layerIndex])
    float[,,] map = new float[res, res, layerCount];

    for (int y = 0; y < res; y++) {
        for (int x = 0; x < res; x++) {
            // Default to the first layer (Index 0) in your Registry
            map[y, x, 0] = 1f; 
            for (int l = 1; l < layerCount; l++) {
                map[y, x, l] = 0f;
            }
        }
    }

    // 3. Paint Zones from JSON
    foreach (var zone in data.terrain_zones) {
        // Find the index mapped to this string key
        string zoneKey = zone.terrain_type.ToLower();
        if (!keyToIndex.TryGetValue(zoneKey, out int targetIndex)) {
            Debug.LogWarning($"Terrain key '{zone.terrain_type}' not found in Registry.");
            continue;
        }

        // Map 0-1000 JSON coordinates to Alphamap resolution
        int xStart = Mathf.Clamp(Mathf.RoundToInt((zone.bounding_box[0] / 1000f) * res), 0, res);
        int yStart = Mathf.Clamp(Mathf.RoundToInt((zone.bounding_box[1] / 1000f) * res), 0, res);
        int xEnd   = Mathf.Clamp(Mathf.RoundToInt((zone.bounding_box[2] / 1000f) * res), 0, res);
        int yEnd   = Mathf.Clamp(Mathf.RoundToInt((zone.bounding_box[3] / 1000f) * res), 0, res);

        for (int y = yStart; y < yEnd; y++) {
            for (int x = xStart; x < xEnd; x++) {
                // Set all layers to 0 first (opaque override)
                for (int l = 0; l < layerCount; l++) {
                    map[y, x, l] = 0f;
                }
                // Set our specific registry layer to 1
                map[y, x, targetIndex] = 1f;
            }
        }
    }

    // 4. Apply the map to the terrain data
    tData.SetAlphamaps(0, 0, map);
}
    private void PlacePrefabs(FullTerrainData data) {
        Vector3 terrainSize = targetTerrain.terrainData.size; // e.g., 500m
        
        foreach (var p in data.prefab_instances) {
            GameObject prefab = prefabRegistry.GetPrefab(p.prefab_type);
            if (!prefab) {
                Debug.Log($"missing prefab: {p.prefab_type}");
                continue;
            }

            float xPos = (p.center_point[0] / _canvasWidth) * terrainSize.x;
            float zPos = (p.center_point[1] / _canvasHeight) * terrainSize.z;
    
            // 1. Get the raw terrain height at this coordinate
            float terrainHeight = targetTerrain.SampleHeight(new Vector3(xPos, 0, zPos));

            Vector3 spawnPos = targetTerrain.transform.position + new Vector3(xPos, terrainHeight, zPos);
            GameObject instance = Instantiate(prefab, spawnPos, prefab.transform.rotation);

            // 2. Apply scale first (offset depends on scale!)
            instance.transform.localScale *= prefabScaleFactor;

            // 3. Calculate the Y offset based on the Bounding Box
            // We look for a Renderer to find the visual bottom of the object
            Renderer rend = instance.GetComponentInChildren<Renderer>();
            if (rend != null) {
                // Distance from the pivot (transform.position.y) to the bottom of the bounds
                float bottomOffset = instance.transform.position.y - rend.bounds.min.y;
        
                // Push the instance up by that offset
                instance.transform.position += new Vector3(0, bottomOffset, 0);
            }
        }
    }
}