using UnityEngine;

// Direction B — "Docked Rails" shell.
//
// The redesign (Assets/Redesign.html) docks the tool to the screen edges: a Library rail on the
// left, a single context Inspector rail on the right whose content swaps by the active mode, and a
// command bar across the top. This file holds that global mode (`UIMode`) and the MonoBehaviour
// that draws the top command bar (`UIShell`).
//
// Each existing panel keeps its own OnGUI but now consults UIMode so exactly one inspector renders
// in the right rail at a time:
//   Browse   → EditController (selection / transform)
//   Place    → EditController (asset thumbnail grid)
//   Terrain  → EditController (paths / scatter / ground)
//   Build    → TileBuildingEditor (tile editor)   — EditController shows an empty-state until a
//                                                    building is opened
//   Generate → ModelRequesterUI (Sketch → 3D)
//
// (Admin re-render / duplicate / archive / delete moved to the Library rail's Loaded rows,
// revealed by the Admin toggle — the old Manage command is gone.)

public enum AppMode { Browse, Place, Terrain, Build, Generate }

// Single source of truth for the active command. Panels read UIMode.Current in OnGUI; the command
// bar (and a few flows like double-click-to-edit) write it through Set().
public static class UIMode
{
    public static readonly string[] Labels = { "Browse", "Place", "Terrain", "Build", "Generate" };

    static AppMode _current = AppMode.Browse;
    public static AppMode Current => _current;

    // Fired with (previous, next) whenever the mode actually changes.
    public static event System.Action<AppMode, AppMode> Changed;

    public static void Set(AppMode mode)
    {
        if (mode == _current) return;
        var prev = _current;
        _current = mode;
        Changed?.Invoke(prev, mode);
    }
}

// Draws the top command bar and routes mode changes to the panels. Lives on the same UI/Manager
// GameObject as the other tool components; references auto-resolve if left unassigned.
public class UIShell : MonoBehaviour
{
    // USER WIRES THIS IN INSPECTOR (optional — falls back to scene lookup):
    [SerializeField] private EditController    editController;
    [SerializeField] private TileBuildingEditor tileBuildingEditor;
    [SerializeField] private ModelRequesterUI  modelRequester;
    [SerializeField] private LibraryBrowser    libraryBrowser;
    [SerializeField] private WalkthroughController walkthrough;

    // Toolbar tabs plus the Walk button at the right end.
    [SerializeField] private float barWidth = 640f;
    private const float WALK_BUTTON_W = 72f;

    private void Awake()
    {
        if (editController      == null) editController      = FindObjectOfType<EditController>();
        if (tileBuildingEditor  == null) tileBuildingEditor  = FindObjectOfType<TileBuildingEditor>();
        if (modelRequester      == null) modelRequester      = FindObjectOfType<ModelRequesterUI>();
        if (libraryBrowser      == null) libraryBrowser      = FindObjectOfType<LibraryBrowser>();
        if (walkthrough         == null) walkthrough         = FindObjectOfType<WalkthroughController>();
    }

    // The active shell, so the scene-picking code can ask where the bar is. Null when no shell is
    // in the scene (VRViewer) or it's disabled — then nothing is blocked.
    private static UIShell _active;

    private void OnEnable()  { _active = this; UIMode.Changed += OnModeChanged; }
    private void OnDisable() { if (_active == this) _active = null; UIMode.Changed -= OnModeChanged; }

    // Screen rect of the command bar in GUI space (origin top-left), or an empty rect when no shell
    // is active. Single definition — OnGUI draws to it and the hit test below reads it, so the two
    // can't drift as barWidth or the window size changes.
    public static Rect BarRect => _active == null
        ? Rect.zero
        : new Rect((Screen.width - _active.barWidth) * 0.5f, UITheme.Margin,
                   _active.barWidth, UITheme.PrimaryH + UITheme.Pad * 2f);

    // True when `screenPos` — Input System screen coords, origin BOTTOM-left — lands on the command
    // bar. Every tool's "is the pointer over UI?" test only checks x against the left/right rails,
    // but the bar is centered, so it falls in the gap between them: without this a click on the bar
    // also reaches the scene and selects (or paints) whatever sits behind it.
    public static bool BlocksScreenPoint(Vector2 screenPos) =>
        BarRect.Contains(new Vector2(screenPos.x, Screen.height - screenPos.y));

    // When the operator leaves a mode, drop any in-progress placement / terrain tool / tile edit so
    // nothing leaks across modes. EditController.ExitForModeSwitch() centralises that teardown.
    private void OnModeChanged(AppMode prev, AppMode next)
    {
        if (editController != null) editController.ExitForModeSwitch();
    }

    private void OnGUI()
    {
        // Run after the rails (a lower GUI.depth draws later, i.e. on top) so the tooltip overlay
        // below lands above every panel and shows the tooltip the rails captured this same frame.
        GUI.depth = -10;

        var rect = BarRect;
        UITheme.PanelBackground(rect);
        GUILayout.BeginArea(UITheme.Inset(rect));
        if (WalkthroughController.IsEngaged)
        {
            // Walkthrough owns the screen: the bar keeps its rect (so clicks on it stay UI) and
            // shows what the keys do instead of the mode tabs.
            UITheme.HintStrip(WalkthroughController.IsWalking ? UITips.WalkingHint : UITips.PlacingHint);
            UITheme.CaptureTooltip();
            GUILayout.EndArea();
            return;   // no hover card either: the cursor is locked or about to be
        }

        GUILayout.BeginHorizontal();
        int sel = UITheme.CommandBar((int)UIMode.Current, UIMode.Labels, UITips.CommandBar);
        GUILayout.Space(UITheme.Pad);
        if (UITheme.Button("Walk", UITips.Walk, GUILayout.Width(WALK_BUTTON_W), GUILayout.Height(UITheme.PrimaryH)))
            walkthrough?.BeginPlacing();
        GUILayout.EndHorizontal();
        UITheme.CaptureTooltip();
        GUILayout.EndArea();

        if (sel != (int)UIMode.Current) UIMode.Set((AppMode)sel);

        // One hover card for the whole UI (see UITheme.CaptureTooltip / UITips).
        UITheme.DrawTooltipOverlay();
    }
}
