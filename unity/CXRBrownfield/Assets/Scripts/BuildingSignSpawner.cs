using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

// Builds a building's sign (a BuildingSigns.Spec, normally BuildingSigns.SpecFor(instance, def)) as
// a dark plate with white TextMeshPro text, seated on the two host tiles BuildingSigns.TryPlace
// picks. Shared by WorldRenderer (environment rendering) and TileBuildingEditor (live editing) so
// both show the same sign, the same way TileSpawner is the one owner of tile geometry. No colliders:
// the sign must never catch the Paint / Decorate raycasts meant for the wall behind it, and the
// selection panel's Move sign drag works on the wall plane, so it needs none either.
public static class BuildingSignSpawner
{
    public const string RootName = "Sign";

    private const float PlateLift  = 0.02f;   // metres off the wall, clear of z-fighting
    private const float TextLift   = 0.02f;   // metres off the plate
    private const float TextPad    = 0.2f;    // metres of plate left bare around the text
    private static readonly Color PlateColor = new(0.09f, 0.09f, 0.10f);
    private static readonly Color TextColor  = Color.white;

    private static Material _plateMaterial;   // one shared plate look; nulled by a domain reload
    private static readonly HashSet<string> _warned = new();

    // The sign GameObject under `parent` (the building root, corner pivot), or null when the spec
    // has no sign or no wall has room. fitFor resolves each shape's TileFit (null = full cells).
    // warnKey names the building in the one-time "no room" warning (the instance id, so two copies
    // of one def each get their own).
    public static GameObject Spawn(BuildingDef def, BuildingSigns.Spec spec, Transform parent, float cellSize,
                                   Func<string, TileFit> fitFor, string warnKey = null)
    {
        if (def == null || parent == null) return null;
        warnKey ??= def.id ?? "";
        var skip = BuildingSigns.TryPlace(def, spec, cellSize, fitFor, out var p);
        if (skip != BuildingSigns.Skip.None)
        {
            if (skip == BuildingSigns.Skip.TooNarrow && warnKey.Length > 0 && _warned.Add(warnKey))
                Debug.LogWarning($"[BuildingSign] '{def.name}' has sign text but no wall with {BuildingSigns.SignTiles} " +
                                 "open tiles side by side, so the sign is not shown.");
            return null;
        }
        _warned.Remove(warnKey);

        string text = BuildingSigns.NormalizeText(spec.text);
        int layer = parent.gameObject.layer;

        // Root: local -Z faces out of the wall (Quad and TMP both present their front on -Z).
        var root = new GameObject(RootName);
        root.layer = layer;
        root.transform.SetParent(parent, false);
        root.transform.localPosition = p.center + p.normal * PlateLift;
        root.transform.localRotation = Quaternion.LookRotation(-p.normal, p.up);

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
        plate.transform.SetParent(root.transform, false);
        plate.transform.localPosition = Vector3.zero;
        plate.transform.localRotation = Quaternion.identity;
        plate.transform.localScale    = new Vector3(p.width, p.height, 1f);
        var rend = plate.GetComponent<MeshRenderer>();
        if (rend != null) rend.sharedMaterial = PlateMaterial();

        var textGO = new GameObject("Text");
        textGO.layer = layer;
        textGO.transform.SetParent(root.transform, false);
        textGO.transform.localPosition = new Vector3(0f, 0f, -TextLift);
        textGO.transform.localRotation = Quaternion.identity;
        var tmp = textGO.AddComponent<TextMeshPro>();
        tmp.rectTransform.sizeDelta = new Vector2(Mathf.Max(0.1f, p.width - 2f * TextPad),
                                                  Mathf.Max(0.1f, p.height - 2f * TextPad));
        tmp.text             = text;
        tmp.color            = TextColor;
        tmp.fontStyle        = FontStyles.Bold;
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.overflowMode     = TextOverflowModes.Overflow;
        tmp.enableAutoSizing = true;
        tmp.fontSizeMin      = 0.5f;
        tmp.fontSizeMax      = 500f;
        // fontAsset stays unset so TMP Settings supplies the project default (LiberationSans SDF).

        return root;
    }

    // Legacy entry: the def's own signText / signFace (records saved before signs moved to the
    // placed instance).
    public static GameObject Spawn(BuildingDef def, Transform parent, float cellSize, Func<string, TileFit> fitFor) =>
        Spawn(def, BuildingSigns.SpecFor(null, def), parent, cellSize, fitFor, def?.id);

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
