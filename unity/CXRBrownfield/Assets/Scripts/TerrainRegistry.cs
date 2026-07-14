using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "TerrainRegistry", menuName = "Terrain/TerrainRegistry")]
public class TerrainRegistry : ScriptableObject
{
    [System.Serializable]
    public class Entry
    {
        public string key; 
        public TerrainLayer terrainLayer;
    }

    public List<Entry> entries;

    public TerrainLayer GetTerrainLayer(string key)
    {
        var entry = entries.Find(e => e.key.ToLower() == key.ToLower());
        return entry?.terrainLayer;
    }
}