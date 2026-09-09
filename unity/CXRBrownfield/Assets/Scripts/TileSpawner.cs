using System.Collections.Generic;
using UnityEngine;

// Shared tile instantiation used by TileBuildingEditor (authoring) and WorldRenderer
// (environment rendering) so both produce identical geometry, pivots, and face materials.
// Convention: building-local corner pivot; a tile occupies [(gridX)·cs, (gridX+1)·cs] on X.
public static class TileSpawner
{
    // defaultWallMaterialId: the building's style material (BuildingStyleResolver.WallMaterialId),
    // painted on every wall face the tile has not been hand-painted on. Null = no style.
    public static GameObject Spawn(TileDef tile, Transform parent,
                                   TileShapePalette shapes, MaterialPalette materials,
                                   float cellSize, string defaultWallMaterialId = null)
    {
        if (tile == null || shapes == null) return null;

        // A deformed tile is a skewed/trapezoidal prism that no prefab + TRS can represent, so its
        // mesh is generated procedurally (acute/obtuse corners, sloped edges). Plain tiles keep the
        // fast prefab path untouched.
        GameObject go = tile.deform != null
            ? SpawnDeformedTile(tile, parent, shapes, materials, cellSize)
            : Object.Instantiate(shapes.GetPrefab(tile.shapeId), parent);
        if (go == null) return null;

        if (tile.deform == null)
        {
            PlaceInCell(go, tile, shapes, cellSize);
        }
        else
        {
            // Deformed tiles bake their fit AND rotation directly into warped vertices authored in
            // grid-aligned cell-local space (see SpawnDeformedTile), so the GameObject carries no
            // rotation: the deform cage is a grid-space field, and re-rotating it here would tear the
            // shared corner posts that keep skewed neighbours gap-free.
            go.transform.localPosition = CellCenter(tile, cellSize);
            go.transform.localRotation = Quaternion.identity;
        }
        go.name = $"Tile_{tile.gridX}_{tile.gridZ}_F{tile.floor}";

        if (tile.faceMaterials != null)
            foreach (var kv in tile.faceMaterials)
                ApplyFaceMaterial(go, tile, kv.Key, kv.Value, shapes, materials);

        // The style fills in behind the hand paint. Applied here, inside Spawn, so anything the
        // caller does to the renderer afterwards (the editor's translucent floor ghosting clones
        // sharedMaterials) sees the styled slots.
        if (!string.IsNullOrEmpty(defaultWallMaterialId))
        {
            var entry = shapes.GetEntry(tile.shapeId);
            if (entry != null)
                foreach (var face in BuildingStyles.UnpaintedWallFaces(entry.faceNames, tile))
                    ApplyFaceMaterial(go, tile, face, defaultWallMaterialId, shapes, materials);
        }

        return go;
    }

    // Center of a tile's cell in building-local meters. Cells are true cubes of edge cellSize, so
    // floors stack by the same pitch (cube edge) on Y — a tile sits exactly one cube above the one
    // below it, seamless in all three dimensions. Placement re-anchors on the geometry CENTER, so the
    // cell center (not its floor surface) is the placement point on every axis: X/Z use (grid+0.5)·cs
    // and Y uses (floor+0.5)·cs. This seats floor 0 with its base on the building origin (the terrain)
    // instead of sinking half a cell below it, and matches the cell-center convention the decor placer
    // uses (TileFaceGeometry / TileBuildingEditor.PlaceFaceDecor: cellCenter.y = (floor+0.5)·cs).
    public static Vector3 CellCenter(TileDef tile, float cellSize) => new Vector3(
        (tile.gridX + 0.5f) * cellSize,
        (tile.floor + 0.5f) * cellSize,
        (tile.gridZ + 0.5f) * cellSize);

    // Poses an (undeformed) tile GameObject in its cell: position, rotation, fit and anchor. The
    // single owner of that math for the renderer, the editor's live tiles, its placement ghost and
    // its rotate tools, so all of them agree by construction. Safe to re-run on a GameObject that was
    // already placed (position and scale are recomputed from the mesh bounds each time).
    //   1. Start at the cell center.
    //   2. The tile's own rotation is composed on top of the shape's baseline orientation correction,
    //      so prefabs authored facing the wrong way (e.g. the curved corner) still end up correct.
    //   3. FitToBox scales the geometry to the shape's box (cellSize on every axis for a full cube,
    //      the palette's cellExtents otherwise) and re-anchors on the geometry center, so tiles tile
    //      seamlessly and turn about their own center regardless of the prefab's authored size or
    //      pivot (shapes vary wildly: square/wedge authored at 3.5, curvedcorner at 175 with an
    //      off-center pivot).
    //   4. A sub-cell shape then slides to its anchor (TileFit.CenterOffset): a floor slab rests on
    //      the floor even when tipped on its side, a pillar stays centered. Zero for full cubes.
    public static void PlaceInCell(GameObject go, TileDef tile, TileShapePalette shapes, float cellSize)
        => Pose(go, tile, shapes, cellSize, CellCenter(tile, cellSize));

    private static void Pose(GameObject go, TileDef tile, TileShapePalette shapes, float cellSize, Vector3 origin)
    {
        Quaternion tileRot = Quaternion.Euler(tile.rotationX, tile.rotation, tile.rotationZ);
        Quaternion defRot  = shapes != null ? shapes.GetDefaultRotation(tile.shapeId) : Quaternion.identity;
        TileFit    fit     = shapes != null ? shapes.GetFit(tile.shapeId) : TileFit.Full;

        go.transform.localPosition = origin;
        go.transform.localRotation = tileRot * defRot;
        FitToBox(go, fit.PrefabLocalSize(defRot, cellSize));
        go.transform.localPosition += fit.CenterOffset(tileRot, cellSize);
    }

    // Fits a full-cube shape: see FitToBox. Fitting to a cube — rather than a cellSize×floorHeight×
    // cellSize box — is what keeps a tile correctly sized at ANY rotation: a non-cubic target box stays
    // cell-sized only while its local Y points up, so the instant a tile is tipped (any rotationX/
    // rotationZ) the box's unequal axes swap into the footprint and height and the tile reads as
    // "wider when tall". A cube is rotation-invariant, so all axes stay cellSize. (Sub-cell shapes
    // are deliberately non-cubic and rotate as rigid boxes — a tipped pillar IS a beam.)
    public static void FitToCell(GameObject go, float cellSize) => FitToBox(go, Vector3.one * cellSize);

    // Scales and re-anchors the prefab so its geometry spans exactly targetSize (in the GameObject's
    // own local axes), with its geometry CENTER sitting on the current localPosition. Both values
    // come from the combined mesh bounds measured in the GameObject's own local space (independent of
    // the prefab's authored scale and pivot):
    //   - localScale solves measuredSize · scale = targetSize, per-axis, so any shape fills the box.
    //   - localPosition is shifted by the (scaled, rotated) bounds-center so rotation happens about
    //     the geometry center: the tile stays centered at every rotation, even for imported prefabs
    //     whose pivot isn't centered (the built-in cube already is, so it's a no-op).
    // Must run after localPosition and localRotation are set, since it composes onto them.
    public static void FitToBox(GameObject go, Vector3 targetSize)
    {
        if (!LocalGeometryBounds(go, out Bounds b)) return;  // no measurable mesh — leave as authored
        Vector3 size = b.size;
        if (size.x <= 0f || size.y <= 0f || size.z <= 0f) return;

        var scale = new Vector3(targetSize.x / size.x, targetSize.y / size.y, targetSize.z / size.z);
        go.transform.localScale = scale;
        // Geometry center currently lands at localPosition + localRotation·(scale∘center); cancel that
        // offset so it lands on localPosition (the cell/floor center) for any rotation.
        go.transform.localPosition -= go.transform.localRotation * Vector3.Scale(scale, b.center);
    }

    // Axis-aligned bounds of the prefab's combined renderable geometry, expressed in the root
    // GameObject's local units (i.e. as if its localScale were 1). Covers both MeshRenderer (via
    // MeshFilter) and SkinnedMeshRenderer — rigged FBX tiles (e.g. the curved corner) render through
    // a skinned mesh with no MeshFilter, so measuring only MeshFilters under-counts and over-scales
    // them. Each mesh's bounds are mapped through its transform relative to the root, so the root's
    // own scale and rotation cancel out.
    private static bool LocalGeometryBounds(GameObject go, out Bounds bounds)
    {
        Matrix4x4 toLocal = go.transform.worldToLocalMatrix;
        bool any = false;
        Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;

        // Maps the 8 corners of a local-space AABB (in transform t's local space) into the root's
        // local space and grows the running min/max.
        void Accumulate(Bounds local, Transform t)
        {
            Matrix4x4 m = toLocal * t.localToWorldMatrix;
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = c + new Vector3(
                    (i & 1) == 0 ? -e.x : e.x,
                    (i & 2) == 0 ? -e.y : e.y,
                    (i & 4) == 0 ? -e.z : e.z);
                Vector3 p = m.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
                any = true;
            }
        }

        foreach (var mf in go.GetComponentsInChildren<MeshFilter>(true))
            if (mf.sharedMesh != null) Accumulate(mf.sharedMesh.bounds, mf.transform);
        // localBounds is the skinned mesh's authoritative render-space AABB (accounts for the bind
        // pose / root bone), so it's more accurate than sharedMesh.bounds for the fit.
        foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            if (smr.sharedMesh != null) Accumulate(smr.localBounds, smr.transform);

        bounds = any ? new Bounds((min + max) * 0.5f, max - min) : default;
        return any;
    }

    // Assigns the palette material to the named face's submesh slot.
    // Null materialId = keep the prefab's default material (schema: omit/null = default).
    public static void ApplyFaceMaterial(GameObject tileGO, TileDef tile, string faceName, string materialId,
                                         TileShapePalette shapes, MaterialPalette materials)
    {
        if (string.IsNullOrEmpty(materialId) || shapes == null || materials == null) return;
        Material mat = materials.GetMaterial(materialId);
        if (mat == null) return;
        var entry = shapes.GetEntry(tile.shapeId);
        if (entry == null) return;
        int idx = entry.faceNames?.IndexOf(faceName) ?? -1;
        if (idx < 0) return;

        var rend = tileGO.GetComponentInChildren<MeshRenderer>();
        if (rend == null) return;
        var mats = rend.sharedMaterials;
        if (idx < mats.Length)
        {
            mats[idx] = mat;
            rend.sharedMaterials = mats;
        }
        else
        {
            // The face name resolves to a submesh slot the prefab doesn't have — the data is
            // saved correctly, but it can't display until the '{tile.shapeId}' prefab exposes
            // one material slot per named face (see TileShapePalette.faceNames).
            Debug.LogWarning($"[TileSpawner] Tile '{tile.shapeId}' has {mats.Length} material slot(s) " +
                             $"but face '{faceName}' maps to submesh {idx}; material '{materialId}' won't render.");
        }
    }

    // -----------------------------------------------------------------------
    // Procedural deformed (skewed / trapezoidal) tiles
    // -----------------------------------------------------------------------

    // Cache of generated deformed-tile meshes, keyed by everything the geometry depends on (shape,
    // rotation, cell size, deform offsets). Building a deformed tile is expensive (instantiate +
    // BakeMesh + CombineMeshes + per-vertex warp + RecalculateNormals) and used to run per tile on
    // EVERY re-render -- and the generated meshes were never destroyed, so each rebuild also leaked
    // them. Identical tiles now share one mesh (MeshFilter and MeshCollider both take sharedMesh).
    // Content-keyed and never evicted: a session touches a bounded set of distinct deforms, and a
    // play-mode domain reload clears the statics.
    private static readonly Dictionary<string, Mesh> _deformedBoxMeshes = new();
    private static readonly Dictionary<string, (Mesh mesh, Material[] mats)> _warpedShapeCache = new();

    private static string Inv(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    // Box meshes depend only on the deform cage + cell size (rotation is not baked into the box).
    private static string BoxMeshKey(TileDef tile, float cellSize) =>
        Inv(cellSize) + "|" + TileDeformField.DeformKey(tile.deform);

    // Warped prefab meshes bake shape, fit AND rotation into the vertices, so all of it keys.
    private static string WarpedMeshKey(TileDef tile, float cellSize) =>
        tile.shapeId + "|" + Inv(tile.rotationX) + "|" + tile.rotation + "|" + Inv(tile.rotationZ) +
        "|" + Inv(cellSize) + "|" + TileDeformField.DeformKey(tile.deform);

    // Builds the GameObject for a deformed tile. A SQUARE tile becomes a procedural box prism; any
    // other shape (wedge, quarter-curve, …) is rendered by warping the REAL prefab mesh through the
    // same deform cage, so curves/wedges keep their silhouette under skew instead of collapsing into
    // a box. Both paths author vertices in grid-aligned cell-local space (centered on the cell, with
    // fit + rotation already baked in), so the caller places them with identity rotation.
    private static GameObject SpawnDeformedTile(TileDef tile, Transform parent,
                                                TileShapePalette shapes, MaterialPalette materials,
                                                float cellSize)
    {
        var prefab = shapes != null ? shapes.GetPrefab(tile.shapeId) : null;
        bool box = prefab == null
                   || string.Equals(tile.shapeId, "square", System.StringComparison.OrdinalIgnoreCase);
        return box
            ? SpawnDeformedBox(tile, parent, shapes, cellSize)
            : SpawnWarpedShape(tile, parent, shapes, materials, prefab, cellSize);
    }

    // The square/box path: a procedural prism mesh with one submesh per face (north/east/south/west/
    // top/bottom — same order/names as the square shape's faceNames, so the existing per-face
    // material painting keeps working) plus a matching MeshCollider for the editor's select/paint
    // raycasts.
    private static GameObject SpawnDeformedBox(TileDef tile, Transform parent,
                                               TileShapePalette shapes, float cellSize)
    {
        var go   = new GameObject("DeformedTile");
        go.transform.SetParent(parent, false);
        string key = BoxMeshKey(tile, cellSize);
        if (!_deformedBoxMeshes.TryGetValue(key, out var mesh) || mesh == null)
        {
            mesh = TileDeformField.BuildDeformedMesh(tile.deform, cellSize);
            _deformedBoxMeshes[key] = mesh;
        }

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var rend = go.AddComponent<MeshRenderer>();
        // Seed all six slots with the square shape's default look; faceMaterials overrides per face.
        Material baseMat = DefaultTileMaterial(shapes);
        var slots = new Material[TileDeformField.FaceCount];
        for (int i = 0; i < slots.Length; i++) slots[i] = baseMat;
        rend.sharedMaterials = slots;

        go.AddComponent<MeshCollider>().sharedMesh = mesh;
        return go;
    }

    // The general (non-square) path: instantiate the real prefab, fit + rotate it into the cell
    // exactly as a plain tile would be, bake its (possibly skinned) geometry into one cell-local
    // mesh, then push every vertex through the deform cage. Because the warp is a pure function of
    // grid position, the result stays seamless against box and curve neighbours alike, and because
    // the prefab's per-submesh materials are carried over, the look and per-face painting survive.
    private static GameObject SpawnWarpedShape(TileDef tile, Transform parent, TileShapePalette shapes,
                                               MaterialPalette materials, GameObject prefab, float cellSize)
    {
        var go = new GameObject("DeformedTile");
        go.transform.SetParent(parent, false);   // identity local transform == cell-local space

        // Cache hit: reuse the baked mesh, but CLONE the material slot array. ApplyFaceMaterial
        // writes palette materials into the renderer's slots per instance, and two renderers
        // sharing one cached array would leak face paint between buildings.
        string cacheKey = WarpedMeshKey(tile, cellSize);
        if (_warpedShapeCache.TryGetValue(cacheKey, out var cached) && cached.mesh != null)
        {
            go.AddComponent<MeshFilter>().sharedMesh = cached.mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = (Material[])cached.mats.Clone();
            go.AddComponent<MeshCollider>().sharedMesh = cached.mesh;
            return go;
        }

        // Pose a throwaway copy of the prefab in the cell (shape + rotation + fit + anchor, exactly as
        // PlaceInCell would, with the cell center at the origin), bake it, warp it, then discard the
        // copy — only the warped static mesh survives. Because the pose is in cell-local space, a
        // sub-cell shape (pillar, slab) sits inside the cage where it belongs before the warp.
        var temp = Object.Instantiate(prefab, go.transform);
        Pose(temp, tile, shapes, cellSize, Vector3.zero);

        if (BakeWarpedMesh(temp, go.transform, tile.deform, cellSize, out Mesh mesh, out Material[] mats))
        {
            _warpedShapeCache[cacheKey] = (mesh, (Material[])mats.Clone());
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = mats;
            go.AddComponent<MeshCollider>().sharedMesh = mesh;
        }
        DestroyNow(temp);
        return go;
    }

    // Combines every renderer under `src` into one mesh expressed in `toLocal`'s space (the cell-
    // local frame), then warps each vertex through the deform cage. Submeshes are kept un-merged and
    // their source materials collected in parallel, so submesh order still lines up with the shape's
    // faceNames (per-face painting) and the prefab's look is preserved. Skinned meshes (e.g. the
    // curved corner) are snapshotted with BakeMesh, which bakes in renderer-local space WITHOUT the
    // transform scale, so mapping through localToWorldMatrix (which includes scale) is correct.
    private static bool BakeWarpedMesh(GameObject src, Transform toLocal, TileDeform deform,
                                       float cellSize, out Mesh mesh, out Material[] mats)
    {
        mesh = null; mats = null;
        var combine = new List<CombineInstance>();
        var matList = new List<Material>();
        var temps   = new List<Mesh>();   // baked skinned snapshots to free after the combine
        Matrix4x4 root = toLocal.worldToLocalMatrix;

        void Add(Mesh m, Transform t, Renderer r)
        {
            if (m == null) return;
            Matrix4x4 mtx = root * t.localToWorldMatrix;
            var rmats = r != null ? r.sharedMaterials : null;
            for (int s = 0; s < m.subMeshCount; s++)
            {
                combine.Add(new CombineInstance { mesh = m, subMeshIndex = s, transform = mtx });
                matList.Add(rmats != null && s < rmats.Length ? rmats[s] : null);
            }
        }

        foreach (var mf in src.GetComponentsInChildren<MeshFilter>(true))
            Add(mf.sharedMesh, mf.transform, mf.GetComponent<MeshRenderer>());
        foreach (var smr in src.GetComponentsInChildren<SkinnedMeshRenderer>(true))
        {
            if (smr.sharedMesh == null) continue;
            var baked = new Mesh();
            smr.BakeMesh(baked);
            temps.Add(baked);
            Add(baked, smr.transform, smr);
        }
        if (combine.Count == 0) { foreach (var t in temps) DestroyNow(t); return false; }

        mesh = new Mesh { name = "WarpedTile", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.CombineMeshes(combine.ToArray(), mergeSubMeshes: false, useMatrices: true);
        foreach (var t in temps) DestroyNow(t);   // combine has copied the data out

        var verts = mesh.vertices;
        for (int i = 0; i < verts.Length; i++)
            verts[i] = TileDeformField.WarpVertex(deform, verts[i], cellSize);
        mesh.vertices = verts;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();

        mats = matList.ToArray();
        return true;
    }

    // Destroys a transient object immediately in the editor and at runtime, so the throwaway prefab
    // copy never lingers as a child (a deferred Object.Destroy would leave it visible for a frame and
    // could get swept into a BakePass combine) and baked snapshot meshes don't leak.
    private static void DestroyNow(Object obj)
    {
        if (obj == null) return;
        if (Application.isPlaying) Object.Destroy(obj);
        else Object.DestroyImmediate(obj);
    }

    // A sensible default material for procedural tiles: the square prefab's first shared material, so
    // unpainted deformed tiles look like unpainted square tiles. Falls back to a plain lit material.
    private static Material DefaultTileMaterial(TileShapePalette shapes)
    {
        var prefab = shapes != null ? shapes.GetPrefab("square") : null;
        var rend   = prefab != null ? prefab.GetComponentInChildren<MeshRenderer>() : null;
        if (rend != null && rend.sharedMaterial != null) return rend.sharedMaterial;

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        return shader != null ? new Material(shader) : null;
    }
}
