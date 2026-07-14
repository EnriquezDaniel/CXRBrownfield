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

    public List<Entry> entries;

    public GameObject GetPrefab(string key)
    {
        var entry = entries.Find(e => e.key.ToLower() == key.ToLower());
        return entry?.prefab;
    }
}