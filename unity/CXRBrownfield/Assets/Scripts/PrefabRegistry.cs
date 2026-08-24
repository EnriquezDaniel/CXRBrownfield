using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "NewPrefabRegistry", menuName = "Prefab Registry")]
public class PrefabRegistry : ScriptableObject
{
    [System.Serializable]
    public class Entry
    {
        public string key; 
        public GameObject prefab;
    }

    public List<Entry> entries = new();

    // Null-tolerant on purpose: a blank key is a real state (a freshly added Inspector row starts
    // empty), and lookup keys come from data (prefabType / prefab_type) that can be null. Skips bad
    // entries instead of throwing. OrdinalIgnoreCase matches the other palettes.
    public GameObject GetPrefab(string key)
    {
        if (entries == null || string.IsNullOrEmpty(key)) return null;
        foreach (var e in entries)
            if (e != null && string.Equals(e.key, key, System.StringComparison.OrdinalIgnoreCase))
                return e.prefab;
        return null;
    }
}