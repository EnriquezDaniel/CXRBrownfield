using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Builds a building's signs (BuildingSigns.LayoutFor(instance, def)) as dark plates with white
// TextMeshPro text, one "Sign N" child per drawn sign under a single "Signs" root. Shared by
// WorldRenderer (environment rendering) and TileBuildingEditor (live editing) so both show the same
// signs, the same way TileSpawner is the one owner of tile geometry. No colliders: a sign must never
// catch the Paint / Decorate raycasts meant for the wall behind it, and the selection panel's Move
// sign drag works on the wall planes, so it needs none either.
public static class BuildingSignSpawner
{
    public const string RootName = "Signs";

    private const float PlateLift  = 0.02f;   // metres off the wall, clear of z-fighting
    private const float TextLift   = 0.02f;   // metres off the plate
    private const float FontPerMetre = 10f;   // TextMeshPro (3D) font size that gives a 1 m em
    private static readonly Color PlateColor = new(0.09f, 0.09f, 0.10f);
    private static readonly Color TextColor  = Color.white;

    private static Material _plateMaterial;   // one shared plate look; nulled by a domain reload
    private static readonly HashSet<string> _warned = new();

    // The "Signs" GameObject under `parent` (the building root, corner pivot), or null when nothing
    // is drawn. fitFor resolves each shape's TileFit (null = full cells). warnKey names the building
    // in the one-time "no room" warning (the instance id, so two copies of one def each get their own).
    public static GameObject Spawn(BuildingDef def, BuildingInstance inst, Transform parent, float cellSize,
                                   Func<string, TileFit> fitFor, string warnKey = null)
    {
        if (def == null || parent == null) return null;
        warnKey ??= def.id ?? "";
        var entries = BuildingSigns.EntriesFor(inst, def);
        var layout  = BuildingSigns.Layout(def, entries, BuildingSigns.StartFace(inst, def), cellSize, fitFor);

        int hidden = 0;
        foreach (var r in layout)
            if (r.skip == BuildingSigns.Skip.NoRoom || r.skip == BuildingSigns.Skip.TooSmall) hidden++;
        if (hidden == 0) _warned.Remove(warnKey);
        else if (warnKey.Length > 0 && _warned.Add(warnKey))
            Debug.LogWarning($"[BuildingSign] '{def.name}' has {hidden} sign(s) with no open wall to hang on, so they are not shown.");

        GameObject root = null;
        for (int i = 0; i < layout.Count; i++)
        {
            if (layout[i].skip != BuildingSigns.Skip.None) continue;
            if (root == null)
            {
                root = new GameObject(RootName);
                root.layer = parent.gameObject.layer;
                root.transform.SetParent(parent, false);
            }
            SpawnOne(root.transform, $"Sign {i + 1}", BuildingSigns.NormalizeText(entries[i].text), layout[i].p, cellSize);
        }
        return root;
    }

    private static void SpawnOne(Transform parent, string name, string text, BuildingSigns.Placement p, float cellSize)
    {
        int layer = parent.gameObject.layer;

        // Local -Z faces out of the wall (Quad and TMP both present their front on -Z).
        var sign = new GameObject(name);
        sign.layer = layer;
        sign.transform.SetParent(parent, false);
        sign.transform.localPosition = p.center + p.normal * PlateLift;
        sign.transform.localRotation = Quaternion.LookRotation(-p.normal, p.up);

        var plate = GameObject.CreatePrimitive(PrimitiveType.Quad);
        plate.name  = "Plate";
        plate.layer = layer;
        var col = plate.GetComponent<Collider>();
        if (col != null)
        {
            // Immediate outside play mode (edit-time previews, tests); deferred Destroy is play only.
            if (Application.isPlaying) UnityEngine.Object.Destroy(col);
            else                       UnityEngine.Object.DestroyImmediate(col);
        }
        plate.transform.SetParent(sign.transform, false);
        plate.transform.localPosition = Vector3.zero;
        plate.transform.localRotation = Quaternion.identity;
        plate.transform.localScale    = new Vector3(p.width, p.height, 1f);
        var rend = plate.GetComponent<MeshRenderer>();
        if (rend != null) rend.sharedMaterial = PlateMaterial();

        var textGO = new GameObject("Text");
        textGO.layer = layer;
        textGO.transform.SetParent(sign.transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -TextLift);
        textGO.transform.localRotation = Quaternion.identity;
        var tmp = textGO.AddComponent<TextMeshPro>();
        float pad = BuildingSigns.PadFrac * cellSize * p.scale;
        tmp.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.1f, p.width - 2f * pad),
                                                  Mathf.Max(0.1f, p.height - 2f * pad));
        tmp.text             = text;
        tmp.color            = TextColor;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode     = TextOverflowModes.Overflow;
        // Every sign shares one text size; autosizing only steps in for a word of wide letters that
        // would otherwise run off its plate.
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = 0.5f;
        tmp.fontSizeMax      = BuildingSigns.TextEmFrac * cellSize * p.scale * FontPerMetre;
        // fontAsset stays unset so TMP Settings supplies the project default (LiberationSans SDF).
    }

    private static Material PlateMaterial()
    {
        if (_plateMaterial != null) return _plateMaterial;
        Shader shader = Shader.Find("Universal Render Pipeline/Lit")
                        ?? Shader.Find("Standard")
                        ?? Shader.Find("Sprites/Default");
        _plateMaterial = new Material(shader) { name = "BuildingSignPlate" };
        if (_plateMaterial.HasProperty("_BaseColor")) _plateMaterial.SetColor("_BaseColor", PlateColor);
        if (_plateMaterial.HasProperty("_Color"))     _plateMaterial.SetColor("_Color", PlateColor);
        if (_plateMaterial.HasProperty("_Smoothness")) _plateMaterial.SetFloat("_Smoothness", 0.25f);
        return _plateMaterial;
    }
}
