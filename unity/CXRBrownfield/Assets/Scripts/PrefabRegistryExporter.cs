// Trigger via: Tools > Export Prefab Registry to Clipboard
// Must stay inside #if UNITY_EDITOR (or be moved to an Editor/ folder) because
// it references UnityEditor APIs that don't exist in player builds.
#if UNITY_EDITOR
using System.Text;
using UnityEditor;
using UnityEngine;

public static class PrefabRegistryExporter
{
    [MenuItem("Tools/Export Prefab Registry to Clipboard")]
    public static void ExportToClipboard()
    {
        // Find the PrefabRegistry asset anywhere in the project
        string[] guids = AssetDatabase.FindAssets("t:PrefabRegistry");

        if (guids.Length == 0)
        {
            EditorUtility.DisplayDialog(
                "Prefab Registry Exporter",
                "No PrefabRegistry asset found in the project.",
                "OK"
            );
            return;
        }

        string path = AssetDatabase.GUIDToAssetPath(guids[0]);
        PrefabRegistry registry = AssetDatabase.LoadAssetAtPath<PrefabRegistry>(path);

        if (registry == null || registry.entries == null || registry.entries.Count == 0)
        {
            EditorUtility.DisplayDialog(
                "Prefab Registry Exporter",
                "PrefabRegistry found but has no entries.",
                "OK"
            );
            return;
        }

        var sb = new StringBuilder();

        sb.AppendLine("| prefab_type | unity_prefab_name |");
        sb.AppendLine("|---|---|");

        foreach (var entry in registry.entries)
        {
            string prefabName = entry.prefab != null ? entry.prefab.name : "(missing)";
            sb.AppendLine($"| \"{entry.key}\" | {prefabName} |");
        }

        EditorGUIUtility.systemCopyBuffer = sb.ToString();

        EditorUtility.DisplayDialog(
            "Prefab Registry Exporter",
            $"Copied {registry.entries.Count} entries to clipboard.\n\nPaste under the '🌳 Prefab Instances' section in your prompt markdown.",
            "OK"
        );

        Debug.Log("[PrefabRegistryExporter] Exported:\n" + sb);
    }
}
#endif