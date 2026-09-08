// Tooltip copy for every clickable control in the runtime tool panels, in one place so it can be
// read and edited as a whole. Call sites pass these to the UITheme control helpers
// (UITheme.Button("Refresh", UITips.RefreshPlaces)); the hover card itself is drawn by
// UITheme.DrawTooltipOverlay from UIShell.OnGUI.
//
// House style for a tip: say what the click does and anything you'd otherwise have to discover
// by trying it (a key that does the same, why the button is off, what gets kept). Plain words,
// one to three short sentences, no em dashes, no bold, no sign-off sentence.
public static class UITips
{
    // ---- command bar (UIShell) ----
    public static readonly string[] CommandBar =
    {
        "Select things in the scene, then move, rotate, or scale them.",
        "Pick an object or building from the catalog and click the ground to drop it.",
        "Lot size, paths, fences, scattered objects, ground surfaces, and the measure tool.",
        "Edit a building tile by tile. Double-click a building in the scene or open one from the library.",
        "Upload a sketch and have the server turn it into a place.",
    };
    public const string Walk        = "Drops a person marker where you click the ground and walks from there at eye height. WASD moves, Shift runs, Esc comes back to the editor view.";
    public const string PlacingHint = "Click the ground to start walking  \u00b7  Esc to cancel";
    public const string WalkingHint = "Walking  \u00b7  WASD move  \u00b7  Shift run  \u00b7  Esc to leave";

    // ---- Library rail (LibraryBrowser) ----
    public const string LiveShare      = "Publishes the loaded places so a headset or a second PC can mirror them. While on, edits auto-save about a second after you make them.";
    public const string Admin          = "Shows the admin actions under each loaded place and the archive buttons in the lists.";
    public const string NewPlace       = "Shows a name field for a new, empty place.";
    public static readonly string[] LibraryTabs =
    {
        "Places you can load into the scene and edit.",
        "Reusable building definitions. Open one to edit its tiles.",
    };
    public const string CreatePlace    = "Creates the place on the server and loads it.";
    public const string RefreshPlaces  = "Reloads the list from the server.";
    public const string LoadPlace      = "Loads this place into the scene next to whatever is already loaded.";
    public const string FocusPlace     = "Already loaded. Makes it the active place, the one you edit.";
    public const string Favorite       = "Stars this so it sorts to the top. Click again to unstar.";
    public const string ArchivePlace   = "Moves this place to the archive folder on the server. It leaves the list.";
    public const string LoadedRow      = "Click to make this the active place. Only the active place can be edited and saved.";
    public const string Lock           = "Marks this place read-only, for a digital twin you design on top of. Unlock from this row later.";
    public const string UnlockAsk      = "Asks before removing the read-only lock.";
    public const string UnlockConfirm  = "Removes the lock. The place becomes editable again.";
    public const string Cancel         = "Closes this without changing anything.";
    public const string EditPlace      = "Makes this the active place so its controls unlock.";
    public const string ViewPlace      = "Makes this locked place active. It paints the terrain, and it stays read-only.";
    public const string ClosePlace     = "Removes this place from the scene. Nothing is deleted on the server.";
    public const string Save           = "Writes the active place to the server. Off until something has changed.";
    public const string SaveAs         = "Saves a copy under a new name. The copy is unlocked, which is how you design on top of a locked twin.";
    public const string SaveCopy       = "Creates the copy with the name typed here.";
    public const string Rerender       = "Rebuilds this place's 3D objects from its data. Use it if the scene looks stale.";
    public const string Duplicate      = "Makes an unlocked copy on the server and loads it.";
    public const string DeleteAsk      = "Asks before removing this place. Deleting archives it, so an admin can still get it back.";
    public const string DeleteConfirm  = "Archives the place now.";
    public const string InstanceName   = "Click to select this in the scene. Shift or Ctrl adds it to the selection.";
    public const string IncludeToggle  = "On means this item renders and counts as part of the place. Off hides it without deleting it.";
    public const string DeleteInstance = "Removes this from the place. Ctrl+Z brings it back.";
    public const string LowDetail      = "Hides everything marked optional, which is what the VR viewer shows. Editing stays on and hidden items keep their rows in the lists.";
    public const string OptionalToggle = "Marks this item optional. The VR viewer skips optional items to run faster. The desktop editor still shows them unless Low detail is on.";
    public const string MarkOptional   = "Marks every selected item optional so the VR viewer skips it. Ctrl+Z reverts.";
    public const string MarkRequired   = "Marks every selected item required so the VR viewer renders it again. Ctrl+Z reverts.";
    public const string TypeOptional   = "Marks every object of the selected object's type optional, across the whole place. Ctrl+Z reverts.";
    public const string TypeRequired   = "Marks every object of the selected object's type required, across the whole place. Ctrl+Z reverts.";
    public const string NewBuilding    = "Creates an empty 3 by 3 building with this name and opens the tile editor.";
    public const string RefreshBuildings = "Reloads the building list from the server.";
    public const string EditBuilding   = "Opens this building in the tile editor.";
    public const string ArchiveBuilding = "Moves this building to the archive folder on the server.";

    // ---- Browse rail: selection / transform (EditController) ----
    public const string EditTiles      = "Opens the tile editor for this building. Double-clicking it in the scene does the same.";
    public const string SetSize        = "Applies the typed width, depth, and height in meters.";
    public static readonly string[] TransformTools =
    {
        "Move tool. Drag the gizmo arrows, press G, or drag the axis handles below.",
        "Rotate tool. Drag the ring, press R, or use the sliders below. Hold Ctrl to snap to 15 degrees.",
        "Scale tool. Drag the top cube, press T, or use the slider below.",
    };
    public static string MoveDrag(string axis) =>
        $"Drag left or right to slide the selection along {axis}. Hold Ctrl to move in whole 1 m steps. Arrow keys nudge too.";
    public const string PivotToggle    = "Switches how a group turns. Each object spins in place, or the whole group swings around its shared center.";
    public const string SkewFoldout    = "Shows controls that bend a footprint corner or slope a roof edge. Affects the whole building.";
    public const string SkewBend       = "Slant the footprint so one corner becomes acute or obtuse. The slant runs at a constant angle to the far end. Minus leans acute, plus obtuse.";
    public const string SkewSlope      = "Tilt one roof edge into a shed roof.";
    public static string SkewCorner(string corner) =>
        $"Bends the {corner} footprint corner. The marker in the scene shows which corner this is.";
    public const string SkewEdge       = "Which roof edge rises or falls.";
    public const string SkewSmooth     = "Eases the slope in with a curve.";
    public const string SkewLinear     = "Slopes at a constant rate.";
    public const string SkewApply      = "Adds this deform on top of the building's current shape and saves it. Ctrl+Z reverts it.";
    public const string SkewReset      = "Clears every deform on this building and saves it.";
    public const string DeleteSelected = "Removes the selected items from the place. Ctrl+Z brings them back.";

    // ---- Place rail ----
    public const string StopPlacing    = "Leaves placement mode. Esc does the same.";
    public static readonly string[] PlaceFilters =
    {
        "Show everything in the catalog.",
        "Only prefab objects, like trees and props.",
        "Only buildings from the library.",
    };
    public static string PlaceObject(string key) =>
        $"Click, then click the ground to place {key}. Scroll to turn the ghost before you drop it.";
    public static string PlaceBuilding(string name) =>
        $"Click, then click the ground to place {name}. Scroll to turn the ghost before you drop it.";
    public const string RefreshBuildingThumbs = RefreshBuildings;

    // ---- Terrain rail: site / lot ----
    public const string SiteApply      = "Resizes the ground to the typed width and length in meters. Content stays where it is.";
    public const string EditLotHandles = "Shows drag handles in the scene for the ground rectangle or the parcel outline.";
    public const string DoneLot        = "Hides the handles. The shape is kept.";
    public const string LotRectangle   = "Drag the far corner or edges to resize the ground from its origin corner.";
    public const string LotParcel      = "Drag the outline's points. Click an edge to add a point. Delete removes the selected one.";
    public const string ResetParcel    = "Drops the custom outline so the lot is the plain rectangle again.";
    public const string FitTerrainToLot = "Shrinks or grows the ground rectangle to hug the parcel outline, plus a 2 m margin.";
    public const string FitLotToContent = "Grows or shrinks the ground so every placed item fits, with a 5 m margin.";

    // ---- Terrain rail: paths ----
    public const string DrawPaths      = "Draw a surfaced path on the ground. Click to drop points, or drag in freehand mode.";
    public const string DoneDrawPath   = "Stops drawing. Finished paths stay.";
    public const string PathFreehand   = "Freehand traces the path while you drag. Straight drops a point per click, and Enter or a double-click ends it.";
    public const string DoneEditPath   = "Finishes editing this path.";
    public const string PathMaterial   = "Surface for new paths, and for the path being edited.";
    public const string InsertPoint    = "Splits the longest segment with a new point you can drag.";
    public const string PathsFold      = "Lists every path in this place.";
    public const string EditPath       = "Shows drag handles on this path.";
    public const string DeletePath     = "Removes this path from the place.";

    // ---- Terrain rail: fences ----
    public const string DrawFences     = "Draw a fence run. Drag from start to end, or switch to freehand.";
    public const string DoneDrawFence  = "Stops drawing. Finished fences stay.";
    public const string FenceFreehand  = "Freehand follows your drag. Straight makes one run from where you press to where you release.";
    public const string DoneEditFence  = "Finishes editing this fence.";
    public const string FenceType      = "Panel style for new fences, and for the fence being edited.";
    public const string FencesFold     = "Lists every fence in this place. Clicking a fence in the scene opens it here too.";
    public const string EditFence      = "Shows drag handles on this fence.";
    public const string DeleteFence    = "Removes this fence from the place.";
    public const string FenceRow       = "Highlights this fence in the scene so you can find it. Click again to clear the highlight.";

    // ---- Terrain rail: water ----
    public const string DrawWater      = "Draw a pond or a river on the ground. The ground under it is dug out to hold the water, and fills back in if you delete it. Esc drops a half-drawn shape, then leaves the tool.";
    public const string DoneDrawWater  = "Stops drawing. Finished water stays.";
    public static readonly string[] WaterKindLabels = { "Pond", "River" };
    public static readonly string[] WaterKindTips =
    {
        "A closed outline for ponds, lakes and puddles. Click each corner, then Enter or a double-click closes it. Backspace removes the last corner. Needs at least three corners.",
        "A ribbon along a line for rivers, streams and canals. Click to drop points, or switch to freehand and drag. Enter or a double-click ends it. Backspace removes the last point.",
    };
    public static readonly string[] WaterPresetLabels = { "Puddle", "Pond", "River", "Lake" };
    public static readonly string[] WaterPresetTips =
    {
        "Pond outline, 5 cm deep, 0.3 m bank, clear water. Good for wet spots on paving.",
        "Pond outline, 1.5 m deep, 2 m bank, lake blue.",
        "River ribbon 6 m wide, 1.2 m deep, 2 m bank, lake blue.",
        "Pond outline, 4 m deep, 6 m bank, dark water. Wide banks read well from a distance.",
    };
    public const string WaterFreehand  = "Freehand traces the river while you drag and smooths the result. Straight drops a point per click, and Enter or a double-click ends it.";
    public const string WaterDepth     = "How deep the bed is dug below the flat ground height, in meters. On flat ground that is the depth of the hole. The water level below cannot sink beneath this bed.";
    public const string WaterLevel     = "Height of the water surface, in meters from the flat ground height. 0 is level with flat ground, negative sinks it into the hole, positive lifts it above. It stops just above the bed, so lowering Depth pushes it up. Ground above the surface around the edge is cut down to meet it.";
    public const string WaterBank      = "How far the ground slopes on each side of the water's edge, in meters. Inside the outline it slopes from the edge down to the bed. Outside it slopes from the surrounding ground down to the edge. 0 makes a sheer step.";
    public const string WaterWidth     = "Width of the river ribbon in meters, measured across the water.";
    public const string WaterSmoothing = "0 keeps the corners you clicked. 1 bends the river into a flowing curve. Corners are always rounded to at least half the width.";
    public const string WaterClipToLot = "Digs the bed only inside the parcel outline. Ground outside it stays as it is, so water crossing the parcel edge stops there.";
    public const string WaterMaterial  = "Surface color for new water, and for the water being edited. Flat and see-through, no waves or reflections. Add more in the WaterPalette asset.";
    public const string WaterMaterialLabel = "The color in use. Pick another from the list below.";
    public const string DoneEditWater  = "Finishes editing this water body and shows the finished mesh. Esc does the same.";
    public const string WaterEditHint  = "Drag a dot to move that corner. Click the outline to add a dot there. Delete removes the selected dot, or the whole body when no dot is selected. Slider changes apply when you release the mouse.";
    public const string WaterFold      = "Lists every pond and river in this place. Clicking water in the scene opens it here too.";
    public const string EditWater      = "Shows drag handles on this water body. You can also click the water in the scene.";
    public const string DeleteWater    = "Removes this water body. The ground under it fills back in. Ctrl+Z brings it back.";

    // ---- Terrain rail: scatter brush ----
    public const string PaintObjects   = "Scatter brush. Hold the mouse and drag to sprinkle the chosen prefab over the ground.";
    public const string DonePaintObjects = "Leaves the brush. Painted objects stay.";
    public const string BrushPaint     = "Dragging adds objects.";
    public const string BrushErase     = "Dragging removes painted objects inside the radius.";
    public const string BrushPrefab    = "Prefab the brush scatters.";
    public const string RandomRotation = "Gives each scattered object a random heading.";

    // ---- Terrain rail: ground brush ----
    public const string PaintGround    = "Surface brush. Paints grass, concrete, sand and so on into the ground texture.";
    public const string DonePaintGround = "Leaves the brush. Painted ground stays.";
    public const string SurfaceType    = "Terrain surface the brush paints.";
    public const string SurfRound      = "Round stamp.";
    public const string SurfSquare     = "Square stamp. Brush angle below sets how it is turned.";
    public const string SurfStraight   = "Straight drags one run from press to release. Freehand paints under the cursor as you drag.";
    public const string SurfAuto       = "Each stamp turns to follow the direction you drag.";
    public const string SurfFixed      = "Every stamp uses the angle on the slider.";
    public const string FromSelection  = "Copies the selected object's heading into the angle slider.";
    public const string FromLotEdge    = "Copies the angle of the parcel edge nearest the cursor.";
    public const string SnapRunAngle   = "Snaps the run to the brush angle plus multiples of the step. Hold Shift while dragging to skip all snapping.";
    public static string SnapIncrement(float deg) => $"Snaps runs every {deg:0} degrees.";

    // ---- Terrain rail: height brush ----
    public const string ShapeGround     = "Height brush. Hold the mouse on the ground to raise, lower, smooth or flatten it. Objects and paths re-seat when you let go.";
    public const string DoneShapeGround = "Leaves the brush. The shaped ground stays.";
    public static readonly string[] SculptBrushLabels = { "Raise/Lower", "Smooth", "Flatten" };
    public static readonly string[] SculptBrushTips =
    {
        "Raises the ground while you hold the mouse. A negative rate digs instead. Holding still keeps going.",
        "Blends each spot toward its neighbors, softening bumps and steps.",
        "Levels the ground toward the height under the cursor where you pressed.",
    };
    public const string SculptClipToLot = "Keeps the brush inside the parcel outline. Ground outside it stays as it is.";
    public const string ResetGround     = "Removes every height stroke so the ground is flat again. Undo brings them back.";

    // ---- Terrain rail: measure ----
    public const string Measure        = "Click points on the ground to read distances and areas. The first two points can also calibrate the scale.";
    public const string DoneMeasure    = "Leaves the measure tool and clears the points.";
    public const string ClearMeasure   = "Removes all measure points.";
    public const string ApplyCalibration = "Rescales the whole place so the span between points 1 and 2 equals the length you typed.";

    // ---- Build rail (TileBuildingEditor) ----
    public static readonly string[] BuildTools =
    {
        "Pick tiles to rotate or delete. Drag to select several.",
        "Click a cell to add one tile. Drag to lay a run of them.",
        "Click a tile face to give it a material.",
        "Click a tile face to mount a door, window, or other prop.",
    };
    public const string FloorDown      = "Goes down one floor.";
    public const string FloorUp        = "Goes up one floor. Above the top floor it adds a new one.";
    public const string ClearFloorAsk  = "Asks before deleting every tile on this floor.";
    public const string ClearFloorConfirm = "Deletes every tile on this floor. Ctrl+Z undoes it.";
    public const string DuplicateFloor = "Copies this floor's tiles and props onto the floor above, then moves up to it.";
    public static string TileShape(string name, string shapeId = null)
    {
        string hint = (shapeId ?? "").ToLowerInvariant() switch
        {
            "pillar" => " A 2 by 2 m post, 4 m tall, centered in its cell.",
            "slab"   => " A 4 by 4 m plate, 2 m thick, resting on the floor of its cell.",
            _        => "",
        };
        return $"New tiles use the {name} shape.{hint}";
    }
    public static string TileYaw(int deg) => $"New tiles face {deg} degrees. Q and E turn this by 90.";
    public const string SelectFloor    = "Selects every tile on the current floor.";
    public const string SelectAll      = "Selects every tile in the building.";
    public static string RotateAxis(string axis) => $"Rotate around the {axis} axis. The {axis} key does the same.";
    public static string RotateStep(float deg) => $"Turns the selected tiles {deg:+0;-0} degrees on the active axis.";
    public const string ResetRotation  = "Sets the selected tiles back to no rotation.";
    public const string DeleteTiles    = "Removes the selected tiles. The Delete key does the same.";
    public const string Deselect       = "Clears the selection. Esc does the same.";
    public const string WholeFacePaint = "One click paints every exposed tile on that side of the building.";
    public static string FaceMaterial(string name) => $"Paint faces with {name}.";
    public static string FaceFallback(string face) => $"Use the {face} face when a click can't tell which face you hit.";
    public const string DecorPlace     = "Clicking faces adds the chosen prop.";
    public const string DecorErase     = "Click or drag across props to remove them, one per click.";
    public const string WholeFaceDecor = "One click puts the prop on every exposed tile of that building side.";
    public const string DecorOptional  = "Clicking a prop flips it between optional and required. Optional props show tinted here and the VR viewer skips them.";
    public const string WholeFaceOptional = "One click marks every prop on that building side optional. When they are all optional already it marks them required.";
    public static string Decor(string id) => $"Places {id} on the faces you click.";
    public const string SaveChanges    = "Saves the building to the server and keeps editing. Esc saves and exits.";

    // ---- Generate rail (ModelRequesterUI) ----
    public const string HealthCheck    = "Pings the server to confirm it is reachable.";
    public const string InputImage     = "Sketch the generator will use.";
    public const string UploadImage    = "Opens a file picker and uploads a sketch to the server.";
    public const string RefreshInputs  = "Reloads the list of uploaded sketches.";
    public const string GenerateScene  = "Sends the selected sketch to the server. The layout it returns becomes a new place in the library. Off until a sketch is selected.";
    public const string PickFromDisk   = "Runs the layout generator on a file picked with a dialog on the server machine.";
    public const string LocalSample    = "Loads the bundled Home Longfellow sketch without the server. Pick a site above and it lands inside that plot, ground and all, otherwise it loads at the origin. It arrives editable and unsaved, so press Save to keep it.";
    public const string ServerSample   = "Loads a sample place from the server.";
    public const string ModelSearch    = "Searches for a 3D model by the typed name and loads it.";

    // ---- Sites (Site panel + Generate rail targeting) ----
    public const string SiteRow          = "Selects this site. Its controls appear below.";
    public const string DrawSiteTool     = "Draws a new site plot for a generated scene. Drag on the ground to size it, Shift for a square. The tool stays on so you can drag the next one. Esc leaves it.";
    public const string RenameSite       = "Renames this site. Click, type the new name, then press Enter or click Rename again.";
    public const string EditSiteBoundary = "Shows drag handles for this site's corners. Click again to finish.";
    public const string DoneSiteBoundary = "Hides the handles. The shape is kept.";
    public const string ClearSiteFill    = "Unlinks the generated scene from this site. The scene stays in the library.";
    public const string DeleteSite       = "Removes the site outline. A linked scene stays in the library.";
    public const string ClampToLot       = "Moves items that sit outside the parcel back to just inside its edge.";
    public const string SiteTargetNew    = "No site. The sketch becomes its own new place in the library.";
    public const string SiteTargetRow    = "The sketch scene is generated for this site and laid out inside it.";
}
