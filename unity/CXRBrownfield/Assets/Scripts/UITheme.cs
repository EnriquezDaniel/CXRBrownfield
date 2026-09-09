using UnityEngine;

// Shared IMGUI theme for every runtime tool panel (LibraryBrowser, EditController,
// TileBuildingEditor, ModelRequesterUI, UIShell).
//
// This is the Unity port of Assets/Redesign.html — the "calmer, clearer interface" visual
// target (Direction B: docked rails, light frosted-paper panels). Every panel renders through
// this one class, so reskinning here reskins the whole tool. The color/size tokens below are the
// literal values from the redesign's design system, so they line up 1:1 with its CSS variables
// (button fills/edges were since darkened a step so controls separate from the paper panel).
//
// Usage inside OnGUI:
//   UITheme.PanelBackground(rect);                 // rounded frosted-paper card behind the panel
//   GUILayout.BeginArea(UITheme.Inset(rect));      // content inset with padding
//   UITheme.Title("…"); UITheme.Header("…"); …
//   UITheme.CaptureTooltip();                      // last thing before EndArea — feeds the overlay
//   GUILayout.EndArea();
//
// Controls. Every clickable helper takes an optional tooltip (a UITips constant); prefer these
// over raw GUILayout.Button / GUILayout.Toggle so nothing ships without a hover explanation:
//   UITheme.Button("Refresh", UITips.X);  UITheme.ToggleButton(on, "Draw paths", UITips.X);
//   UITheme.PrimaryButton("Save", tip);   UITheme.GhostButton("Cancel", tip);  UITheme.DangerButton(…);
//   UITheme.Checkbox(on, "Random rotation", tip);   UITheme.ListItem(on, id, tip)   // scroll-list rows
//   UITheme.Segmented(sel, labels, tips);  UITheme.Chip(text, active, tip);  UITheme.ThumbCell(tex, label, on, tip);
// The tooltip itself is drawn once per frame by UITheme.DrawTooltipOverlay() from UIShell.OnGUI.
//
// Calling any UITheme member installs the skin for the remainder of the current OnGUI pass.
public static class UITheme
{
    // ---- layout constants (shared so panels line up) ----
    public const int LeftPanelWidth  = 320;   // LibraryBrowser
    public const int RightPanelWidth = 300;   // EditController / TileBuildingEditor (redesign right rail)
    public const int Margin          = 10;
    public const int Pad             = 14;    // inner padding inside a panel card (redesign: 14–17px)
    public const float RowH          = 26f;   // standard control height
    public const float PrimaryH      = 44f;   // primary target / command-bar height (redesign)
    // Top band reserved for the centered command bar. Both docked rails start below it so the bar
    // never overlaps a rail at any window width.
    public static float RailTop => Margin + PrimaryH + Pad * 2f + 6f;

    // ---- palette (literal tokens from Redesign.html) ----
    // Ink ramp
    public static readonly Color Ink        = Hex(0x1F2228);  // --ink   near-black text
    public static readonly Color Ink2       = Hex(0x6B7177);  // --ink2  secondary text
    public static readonly Color Ink3       = Hex(0x9AA0A6);  // --ink3  hint / tertiary text
    // Surfaces
    public static readonly Color PanelCard  = new(0.988f, 0.988f, 0.984f, 0.985f); // --panel #FCFCFB, opaque over scene
    public static readonly Color Field      = Hex(0xFFFFFF);  // --field input background
    public static readonly Color Btn        = Hex(0xE8E8E2);  // --btn   secondary button (a step darker than the paper)
    public static readonly Color BtnHover   = Hex(0xDCDCD5);  // --btn-h
    public static readonly Color Tile       = Hex(0xEFEEE9);  // --tile  segmented track / inset
    public static readonly Color Tile2      = Hex(0xE6E5DF);  // --tile2
    // Accent + tint
    public static readonly Color Accent     = Hex(0x2E63C8);  // --accent
    public static readonly Color AccentInk  = Hex(0x1C4BA0);  // --accent-ink (also the primary-button hover)
    public static readonly Color Tint       = Hex(0xEAF1FC);  // --tint   active-row wash
    public static readonly Color TintHover  = Hex(0xDCE8FA);  // hover over a tinted (selected) row / thumb
    public static readonly Color TintLine   = Hex(0xBCD2F4);  // --tint-line
    public static readonly Color Ok         = Hex(0x2E9E6B);  // --ok
    public static readonly Color Danger     = Hex(0xB3261E);  // delete red
    // Hairlines
    public static readonly Color Line       = new(0.078f, 0.086f, 0.110f, 0.09f);  // --line
    public static readonly Color Line2      = new(0.078f, 0.086f, 0.110f, 0.14f);  // --line2
    public static readonly Color BtnLine    = new(0.078f, 0.086f, 0.110f, 0.30f);  // button / field edge

    // ---- back-compat aliases (older call sites used these names on the dark theme) ----
    public static readonly Color Panel      = PanelCard;
    public static readonly Color Inner      = Field;
    public static readonly Color AccentDim  = AccentInk;
    public static readonly Color TextColor  = Ink;
    public static readonly Color MutedColor = Ink2;
    public static readonly Color DividerCol = Line;

    static GUISkin   _skin;
    static GUIStyle  _title, _header, _sub;
    static GUIStyle  _cardStyle, _primary, _ghost, _chip, _chipOn, _segment, _listItem, _tip;
    static Texture2D _cardTex, _maskTex, _btnTex, _btnHover, _accentTex, _accentHoverTex, _fieldTex,
                     _outlineTex, _tileTex, _tintTex, _tintHoverTex, _tipTex, _white;
    static bool      _building;

    // ---- fonts (Public Sans for UI, IBM Plex Mono for numbers) ----
    // Loaded from Resources/Fonts at first Build(); fall back to the built-in font if missing.
    static Font _sans, _sansMedium, _sansSemi, _mono;
    static bool _fontsLoaded;

    static Font LoadFont(string name)
    {
        var f = Resources.Load<Font>("Fonts/" + name);
        return f;
    }

    static void EnsureFonts()
    {
        if (_fontsLoaded) return;
        _fontsLoaded = true;
        _sans       = LoadFont("PublicSans-Regular");
        _sansMedium = LoadFont("PublicSans-Medium")   ?? _sans;
        _sansSemi   = LoadFont("PublicSans-SemiBold") ?? _sansMedium ?? _sans;
        _mono       = LoadFont("IBMPlexMono-Medium")  ?? LoadFont("IBMPlexMono-Regular");
    }

    // IBM Plex Mono style for numeric readouts; falls back to the mono label style when unavailable.
    static GUIStyle _num, _numSmall;
    public static Font MonoFont { get { EnsureFonts(); return _mono; } }

    static Color Hex(int rgb) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, 1f);

    // Builds (once) and installs the skin for this OnGUI pass.
    static void Ensure()
    {
        if (_skin == null) Build();
        if (!_building) GUI.skin = _skin;
    }

    static Texture2D Solid(Color c)
    {
        var t = new Texture2D(1, 1, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        t.SetPixel(0, 0, c); t.Apply();
        return t;
    }

    // Anti-aliased rounded-rect texture, 9-slice friendly. When borderCol.a > 0 a 1px inner
    // border is baked along the rounded edge. Use the matching radius as the GUIStyle.border.
    // A fully transparent fill + a border gives an outline-only shape (ghost buttons).
    static Texture2D Rounded(int radius, Color fill, Color borderCol = default)
    {
        int size = radius * 2 + 6;
        var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
        var px = new Color[size * size];
        float r = radius;
        bool hasBorder = borderCol.a > 0.001f;
        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            // distance from the nearest straight edge into the rounded corner
            float cx = Mathf.Clamp(x + 0.5f, r, size - r);
            float cy = Mathf.Clamp(y + 0.5f, r, size - r);
            float d  = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            float cover = Mathf.Clamp01(r - d + 0.5f);     // 1 inside, 0 outside, AA on the edge
            Color c = fill;
            if (hasBorder)
            {
                float edge = Mathf.Clamp01(d - (r - 1.5f)); // 0 interior → 1 at the rim
                c = Color.Lerp(fill, borderCol, edge);
            }
            c.a *= cover;
            px[y * size + x] = c;
        }
        t.SetPixels(px); t.Apply();
        return t;
    }

    static void Build()
    {
        _building = true;   // guard: don't reassign GUI.skin while cloning from it

        _white          = Solid(Color.white);
        _cardTex        = Rounded(13, PanelCard, Line2);
        _maskTex        = Rounded(13, Color.white);
        _btnTex         = Rounded(7, Btn,      BtnLine);
        _btnHover       = Rounded(7, BtnHover, BtnLine);
        _accentTex      = Rounded(7, Accent);
        _accentHoverTex = Rounded(7, AccentInk);
        _fieldTex       = Rounded(7, Field,    BtnLine);
        _outlineTex     = Rounded(7, new Color(Btn.r, Btn.g, Btn.b, 0f), BtnLine);
        _tileTex        = Rounded(7, Tile,     Line);
        _tintTex        = Rounded(7, Tint,     TintLine);
        _tintHoverTex   = Rounded(7, TintHover, TintLine);
        _tipTex         = Rounded(6, Ink);

        EnsureFonts();

        _skin = Object.Instantiate(GUI.skin);
        _skin.hideFlags = HideFlags.HideAndDontSave;
        if (_sans != null) _skin.font = _sansMedium;   // Public Sans Medium as the base UI face

        // Label — 13 / medium per the redesign type scale
        var l = _skin.label;
        l.fontSize = 13; l.wordWrap = true;
        if (_sansMedium != null) l.font = _sansMedium;
        l.normal.textColor = Ink;
        l.padding = new RectOffset(2, 2, 3, 3);
        l.margin  = new RectOffset(2, 2, 1, 1);

        // Button — label & button text 13 / medium; accent fill when pressed / "on" (toggle-as-button).
        // No word-wrap (fixed-height buttons would clip a second line); long text clips at the edge
        // instead of widening the layout and summoning a horizontal scrollbar.
        var b = _skin.button;
        b.fontSize = 13; b.fontStyle = FontStyle.Normal;
        if (_sansMedium != null) b.font = _sansMedium;
        b.alignment = TextAnchor.MiddleCenter;
        b.wordWrap  = false;
        b.clipping  = TextClipping.Clip;
        b.padding = new RectOffset(10, 10, 6, 6);
        b.margin  = new RectOffset(3, 3, 3, 3);
        b.border  = new RectOffset(8, 8, 8, 8);
        SetBg(b.normal,   _btnTex,    Ink);
        SetBg(b.hover,    _btnHover,  Ink);
        SetBg(b.active,   _accentTex, Color.white);
        SetBg(b.focused,  _btnTex,    Ink);
        SetBg(b.onNormal, _accentTex, Color.white);
        SetBg(b.onHover,  _accentHoverTex, Color.white);
        SetBg(b.onActive, _accentTex, Color.white);

        // Toggle (checkbox) — label text in full ink so it reads like the other controls
        var t = _skin.toggle;
        t.fontSize = 12; t.wordWrap = false;
        t.normal.textColor   = Ink;       t.hover.textColor   = Ink;
        t.onNormal.textColor = AccentInk; t.onHover.textColor = AccentInk;
        t.margin = new RectOffset(2, 2, 2, 2);

        // Text field — white field, hairline border
        var tf = _skin.textField;
        tf.fontSize = 12;
        tf.padding = new RectOffset(8, 8, 5, 5);
        tf.margin  = new RectOffset(3, 3, 3, 3);
        tf.border  = new RectOffset(8, 8, 8, 8);
        tf.normal.textColor = tf.focused.textColor = tf.hover.textColor = Ink;
        SetBg(tf.normal,  _fieldTex, Ink);
        SetBg(tf.focused, _fieldTex, Ink);
        SetBg(tf.hover,   _fieldTex, Ink);

        // Box / scroll surfaces — soft tile inset
        _skin.box.normal.background = _tileTex;
        _skin.box.normal.textColor  = Ink2;
        _skin.box.border = new RectOffset(8, 8, 8, 8);
        _skin.horizontalSlider.margin = new RectOffset(3, 3, 9, 4);

        // Derived label styles
        // Title — 15 / semibold
        _title = new GUIStyle(l) { fontSize = 15, fontStyle = FontStyle.Bold, margin = new RectOffset(2, 2, 2, 8) };
        if (_sansSemi != null) { _title.font = _sansSemi; _title.fontStyle = FontStyle.Normal; }
        _title.normal.textColor = Ink;

        // Section header — 11 / bold, UPPERCASE, letter-spaced look (caps applied in Header())
        _header = new GUIStyle(l) { fontSize = 11, fontStyle = FontStyle.Bold, margin = new RectOffset(2, 2, 10, 4) };
        if (_sansSemi != null) { _header.font = _sansSemi; _header.fontStyle = FontStyle.Normal; }
        _header.normal.textColor = AccentInk;

        // Hint / helper copy — 11 / regular
        _sub = new GUIStyle(l) { fontSize = 11 };
        if (_sans != null) _sub.font = _sans;
        _sub.normal.textColor = Ink2;

        // Numeric readouts — IBM Plex Mono
        _num = new GUIStyle(l) { fontSize = 13, alignment = TextAnchor.MiddleRight };
        if (_mono != null) _num.font = _mono;
        _num.normal.textColor = Ink;
        _numSmall = new GUIStyle(_num) { fontSize = 11 };
        _numSmall.normal.textColor = Ink2;

        // ---- component styles ----
        _cardStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        _cardStyle.normal.background = _cardTex;

        _primary = new GUIStyle(b);
        SetBg(_primary.normal,  _accentTex,      Color.white);
        SetBg(_primary.hover,   _accentHoverTex, Color.white);
        SetBg(_primary.active,  _accentHoverTex, Color.white);
        SetBg(_primary.focused, _accentTex,      Color.white);

        // Ghost: outline only at rest, fills on hover — quiet, but still obviously a button.
        _ghost = new GUIStyle(b);
        SetBg(_ghost.normal,  _outlineTex, Ink);
        SetBg(_ghost.hover,   _btnHover,   Ink);
        SetBg(_ghost.active,  _accentTex,  Color.white);
        SetBg(_ghost.focused, _outlineTex, Ink);

        _chip = new GUIStyle(b) { fontSize = 12, padding = new RectOffset(12, 12, 5, 5) };
        SetBg(_chip.normal, _btnTex, Ink); SetBg(_chip.hover, _btnHover, Ink);
        _chipOn = new GUIStyle(_chip);
        SetBg(_chipOn.normal, _accentTex, Color.white); SetBg(_chipOn.hover, _accentHoverTex, Color.white);

        _segment = new GUIStyle(_skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 0) };

        // Scroll-list row: left-aligned button-toggle that shrinks to the viewport and clips, so a
        // long id never widens the list.
        _listItem = new GUIStyle(b) { alignment = TextAnchor.MiddleLeft, clipping = TextClipping.Clip };

        // Tooltip card: dark, white text, wraps.
        _tip = new GUIStyle(l) { fontSize = 12, wordWrap = true, richText = false, alignment = TextAnchor.UpperLeft };
        if (_sans != null) _tip.font = _sans;
        _tip.normal.background = _tipTex;
        _tip.normal.textColor  = Color.white;
        _tip.border  = new RectOffset(7, 7, 7, 7);
        _tip.padding = new RectOffset(9, 9, 6, 7);

        _building = false;
        GUI.skin = _skin;
    }

    static void SetBg(GUIStyleState s, Texture2D bg, Color text) { s.background = bg; s.textColor = text; }

    // GUIContent with an optional tooltip (null/empty tip → plain content).
    static GUIContent C(string text, string tip) =>
        string.IsNullOrEmpty(tip) ? new GUIContent(text ?? "") : new GUIContent(text ?? "", tip);

    static GUIContent[] C(string[] texts, string[] tips)
    {
        var arr = new GUIContent[texts.Length];
        for (int i = 0; i < texts.Length; i++)
            arr[i] = C(texts[i], tips != null && i < tips.Length ? tips[i] : null);
        return arr;
    }

    // Prepend MinWidth(0) + ExpandWidth so the control can shrink below its text width (with
    // clipping) instead of forcing the enclosing scroll view wider.
    static GUILayoutOption[] Shrinkable(GUILayoutOption[] opts)
    {
        var all = new GUILayoutOption[(opts?.Length ?? 0) + 2];
        all[0] = GUILayout.MinWidth(0f);
        all[1] = GUILayout.ExpandWidth(true);
        if (opts != null) opts.CopyTo(all, 2);
        return all;
    }

    // ---- drawing helpers ----

    // Rounded frosted-paper card behind a panel. Call before GUILayout.BeginArea(Inset(rect)).
    public static void PanelBackground(Rect rect)
    {
        Ensure();
        var prev = GUI.color;
        // soft drop shadow
        GUI.color = new Color(0.08f, 0.09f, 0.11f, 0.16f);
        GUI.Box(new Rect(rect.x - 2, rect.y + 4, rect.width + 4, rect.height + 2), GUIContent.none, _shadowStyle);
        // the card
        GUI.color = Color.white;
        GUI.Box(rect, GUIContent.none, _cardStyle);
        GUI.color = prev;
    }

    // a maskTex-backed style reused for shadows / rounded fills
    static GUIStyle _shadowStyleCache;
    static GUIStyle _shadowStyle
    {
        get
        {
            if (_shadowStyleCache == null)
            {
                _shadowStyleCache = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
                _shadowStyleCache.normal.background = _maskTex;
            }
            return _shadowStyleCache;
        }
    }

    // Content rect inset by Pad on all sides.
    public static Rect Inset(Rect rect) =>
        new(rect.x + Pad, rect.y + Pad, rect.width - Pad * 2, rect.height - Pad * 2);

    public static void Title(string text)  { Ensure(); GUILayout.Label(text, _title);  }
    // Section header renders UPPERCASE per the redesign (11/700 caps).
    public static void Header(string text) { Ensure(); GUILayout.Label(text == null ? "" : text.ToUpperInvariant(), _header); }
    // Helper copy. Shrinkable so one long unbroken token (a URL, a path) clips instead of widening
    // the panel / scroll view it sits in.
    public static void Note(string text)   { Ensure(); GUILayout.Label(text ?? "", _sub, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)); }

    // Inline IBM Plex Mono numeric label (right-aligned by default).
    public static void Num(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _num, opts); }
    public static void NumSmall(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _numSmall, opts); }

    // Flat clickable foldout row (▸ / ▾ + label) — reads like a section header, not a button.
    static GUIStyle _foldout;
    public static bool Foldout(bool open, string label) => Foldout(open, label, null);
    public static bool Foldout(bool open, string label, string tip)
    {
        Ensure();
        if (_foldout == null)
        {
            _foldout = new GUIStyle(_header) { alignment = TextAnchor.MiddleLeft };
            _foldout.normal.background = null;
            _foldout.hover.textColor = Accent;
            _foldout.padding = new RectOffset(2, 2, 4, 4);
            _foldout.margin  = new RectOffset(2, 2, 8, 2);
        }
        string text = $"{(open ? "▾  " : "▸  ")}{(label ?? "").ToUpperInvariant()}";
        if (GUILayout.Button(C(text, tip), _foldout, GUILayout.ExpandWidth(true))) open = !open;
        return open;
    }

    // Flat, left-aligned, full-width label that acts as a button — for list rows whose *name*
    // selects the thing it names (the library's Buildings/Objects rows). Washed with the active
    // tint when `selected`, so the row of the currently-selected instance reads as picked.
    // Shrinks + clips so a long name can't widen the list.
    static GUIStyle _rowLabel, _rowLabelOn;
    public static bool ListRowLabel(string text, bool selected, params GUILayoutOption[] opts) =>
        ListRowLabel(text, selected, null, opts);
    public static bool ListRowLabel(string text, bool selected, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_rowLabel == null)
        {
            _rowLabel = new GUIStyle(_ghost) { alignment = TextAnchor.MiddleLeft, fontSize = 12, clipping = TextClipping.Clip };
            _rowLabel.padding = new RectOffset(6, 6, 4, 4);
            _rowLabel.normal.background = null; _rowLabel.focused.background = null;
            _rowLabel.normal.textColor = Ink;
            _rowLabel.hover.textColor  = Accent;

            _rowLabelOn = new GUIStyle(_rowLabel) { border = new RectOffset(8, 8, 8, 8) };
            _rowLabelOn.normal.background = _tintTex;
            _rowLabelOn.hover.background  = _tintHoverTex;
            _rowLabelOn.normal.textColor  = _rowLabelOn.hover.textColor  = AccentInk;
        }
        return GUILayout.Button(C(text, tip), selected ? _rowLabelOn : _rowLabel, Shrinkable(opts));
    }

    // Thin horizontal divider that fills the current layout width.
    public static void Divider()
    {
        Ensure();
        var r = GUILayoutUtility.GetRect(1, 7, GUILayout.ExpandWidth(true));
        r.y += 3; r.height = 1;
        var prev = GUI.color; GUI.color = Line2;
        GUI.DrawTexture(r, _white); GUI.color = prev;
    }

    // ---- component helpers (opt-in, mirror the redesign kit) ----

    // Segmented control (e.g. Move / Rotate / Scale). Returns the selected index.
    public static int Segmented(int selected, string[] options) => Segmented(selected, options, null);
    public static int Segmented(int selected, string[] options, string[] tips)
    {
        Ensure();
        // GUILayout.Toolbar pins its minimum width to the summed label widths, so inside a scroll
        // view the moment the vertical scrollbar appears the toolbar keeps its full width and the
        // whole column is clipped on the right. MinWidth(0) lets it shrink with the viewport; the
        // buttons then share the width evenly, exactly as they do when there is room.
        return GUILayout.Toolbar(selected, C(options, tips), _segment,
                                 GUILayout.Height(RowH + 4), GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
    }

    // Standard button (the skin's default look). The `tip` overload is the one to use.
    public static bool Button(string text, params GUILayoutOption[] opts) => Button(text, null, opts);
    public static bool Button(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(C(text, tip), _skin.button, opts);
    }

    // Button-styled toggle (accent fill when on) — tool / mode / option pills.
    public static bool ToggleButton(bool on, string text, params GUILayoutOption[] opts) => ToggleButton(on, text, null, opts);
    public static bool ToggleButton(bool on, string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Toggle(on, C(text, tip), _skin.button, opts);
    }

    // Button-styled drag surface: press and drag horizontally to nudge a value. Returns the
    // horizontal pixel delta on captured MouseDrag events (0 otherwise). `started` fires on the
    // press that captures the control, `ended` on release (routed here even when the cursor left
    // the rect, via GetTypeForControl). Drawing the GUIContent through the style publishes the
    // tooltip exactly like a built-in control, so CaptureTooltip picks it up unchanged.
    public static float DragButton(string label, string tip, out bool started, out bool ended, params GUILayoutOption[] opts)
    {
        Ensure();
        started = ended = false;
        var content = C(label, tip);
        Rect r  = GUILayoutUtility.GetRect(content, _skin.button, opts);
        int  id = GUIUtility.GetControlID(FocusType.Passive, r);
        var  e  = Event.current;
        switch (e.GetTypeForControl(id))
        {
            case EventType.MouseDown:
                if (e.button == 0 && r.Contains(e.mousePosition) && GUI.enabled)
                { GUIUtility.hotControl = id; started = true; e.Use(); }
                break;
            case EventType.MouseDrag:
                if (GUIUtility.hotControl == id) { e.Use(); return e.delta.x; }
                break;
            case EventType.MouseUp:
                if (GUIUtility.hotControl == id)
                { GUIUtility.hotControl = 0; ended = true; e.Use(); }
                break;
            case EventType.Repaint:
                bool hot = GUIUtility.hotControl == id;
                _skin.button.Draw(r, content, r.Contains(e.mousePosition), hot, hot, false);
                break;
        }
        return 0f;
    }

    // Quiet toggle for rows inside a scrolled list: light outline pill when off, accent fill when
    // on. Keeps list rows lighter than a full grey button while staying obviously clickable.
    static GUIStyle _rowToggle;
    public static bool RowToggle(bool on, string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_rowToggle == null)
        {
            _rowToggle = new GUIStyle(_skin.button) { fontSize = 11 };
            SetBg(_rowToggle.normal,    _outlineTex,     Ink);
            SetBg(_rowToggle.hover,     _btnHover,       Ink);
            SetBg(_rowToggle.active,    _accentTex,      Color.white);
            SetBg(_rowToggle.focused,   _outlineTex,     Ink);
            SetBg(_rowToggle.onNormal,  _accentTex,      Color.white);
            SetBg(_rowToggle.onHover,   _accentHoverTex, Color.white);
            SetBg(_rowToggle.onActive,  _accentTex,      Color.white);
            SetBg(_rowToggle.onFocused, _accentTex,      Color.white);
        }
        return GUILayout.Toggle(on, C(text, tip), _rowToggle, opts);
    }

    // Small centered red ✕ for deleting one row inline: flat at rest, red-tinted fill on hover.
    // Fixed 22×22 unless options are passed, so it lines up with RowH-sized row controls.
    static GUIStyle  _rowDelete;
    static Texture2D _dangerHoverTex;
    public static bool RowDeleteButton(string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_rowDelete == null)
        {
            _dangerHoverTex = Rounded(6, new Color(Danger.r, Danger.g, Danger.b, 0.12f),
                                         new Color(Danger.r, Danger.g, Danger.b, 0.45f));
            _rowDelete = new GUIStyle(_skin.button)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding   = new RectOffset(0, 0, 0, 0),
                margin    = new RectOffset(2, 2, 2, 2),
            };
            SetBg(_rowDelete.normal,  null,            Danger);
            SetBg(_rowDelete.hover,   _dangerHoverTex, Danger);
            SetBg(_rowDelete.active,  _dangerHoverTex, Danger);
            SetBg(_rowDelete.focused, null,            Danger);
        }
        var o = (opts != null && opts.Length > 0) ? opts : new[] { GUILayout.Width(22f), GUILayout.Height(22f) };
        return GUILayout.Button(C("✕", tip), _rowDelete, o);
    }

    // Plain checkbox toggle.
    public static bool Checkbox(bool on, string text, string tip = null, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Toggle(on, C(text, tip), _skin.toggle, opts);
    }

    // One selectable row inside a scrolled list (palette ids, uploaded files…). Left-aligned,
    // fills the viewport width, and clips rather than widening it.
    public static bool ListItem(bool on, string text, string tip = null)
    {
        Ensure();
        return GUILayout.Toggle(on, C(text, tip), _listItem, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true), GUILayout.Height(RowH));
    }

    // Accent primary action button — 44px tall target (UITheme.PrimaryH) per the redesign.
    public static bool PrimaryButton(string text, params GUILayoutOption[] opts) => PrimaryButton(text, null, opts);
    public static bool PrimaryButton(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        if (opts == null || opts.Length == 0) opts = new[] { GUILayout.Height(PrimaryH) };
        return GUILayout.Button(C(text, tip), _primary, opts);
    }

    // Secondary (default) button — same look as GUI.skin.button, named for clarity.
    public static bool SecondaryButton(string text, params GUILayoutOption[] opts) => SecondaryButton(text, null, opts);
    public static bool SecondaryButton(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(C(text, tip), _skin.button, opts);
    }

    // Outlined ghost / cancel button.
    public static bool GhostButton(string text, params GUILayoutOption[] opts) => GhostButton(text, null, opts);
    public static bool GhostButton(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(C(text, tip), _ghost, opts);
    }

    // Filter pill / chip. Returns true when clicked.
    public static bool Chip(string text, bool active, params GUILayoutOption[] opts) => Chip(text, active, null, opts);
    public static bool Chip(string text, bool active, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(C(text, tip), active ? _chipOn : _chip, opts);
    }

    // Inline status badge: a dot + label, green when ok else muted.
    public static void StatusBadge(string text, bool ok)
    {
        Ensure();
        var prev = GUI.contentColor;
        GUI.contentColor = ok ? Ok : Ink2;
        GUILayout.Label((ok ? "● " : "○ ") + text, _sub);
        GUI.contentColor = prev;
    }

    // Danger / destructive action — outlined, red text, used for Delete.
    static GUIStyle _danger;
    public static bool DangerButton(string text, params GUILayoutOption[] opts) => DangerButton(text, null, opts);
    public static bool DangerButton(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_danger == null)
        {
            _danger = new GUIStyle(_ghost);
            _danger.normal.textColor  = Danger;
            _danger.hover.textColor   = Danger;
            _danger.focused.textColor = Danger;
            _danger.fontStyle = FontStyle.Bold;
        }
        return GUILayout.Button(C(text, tip), _danger, opts);
    }

    // ---- redesign kit: steppers, sliders, rows, thumbnails, command bar ----

    // Labelled stepper: caption on the left, mono value + −/+ on the right. Returns the new value.
    // `value` is nudged by ±step when a button is pressed; caller clamps if needed.
    public static float Stepper(string caption, float value, float step, string fmt = "0.0", string unit = "")
    {
        Ensure();
        GUILayout.BeginHorizontal();
        GUILayout.Label(caption, _sub, GUILayout.ExpandWidth(true));
        if (GUILayout.Button("–", _skin.button, GUILayout.Width(28), GUILayout.Height(RowH)))
            value -= step;
        GUILayout.Label(value.ToString(fmt) + unit, _num, GUILayout.Width(58));
        if (GUILayout.Button("+", _skin.button, GUILayout.Width(28), GUILayout.Height(RowH)))
            value += step;
        GUILayout.EndHorizontal();
        return value;
    }

    // Labelled slider with a trailing mono readout (e.g. "Radius … 5.0 m").
    public static float SliderRow(string caption, float value, float min, float max, string fmt = "0.0", string unit = "")
        => Slider(caption, value, min, max, null, fmt, unit);

    // SliderRow with a tooltip on the caption, the readout and the slider track itself.
    public static float Slider(string caption, float value, float min, float max, string tip,
                               string fmt = "0.0", string unit = "")
    {
        Ensure();
        GUILayout.BeginHorizontal();
        GUILayout.Label(C(caption, tip), _sub, GUILayout.ExpandWidth(true));
        GUILayout.Label(C(value.ToString(fmt) + unit, tip), _num, GUILayout.Width(60));
        GUILayout.EndHorizontal();
        float v = GUILayout.HorizontalSlider(value, min, max);
        TipOverLastRect(tip);
        return v;
    }

    // A plain text line that shows `tip` on hover (a Note with help attached).
    public static void Label(string text, string tip)
    {
        Ensure();
        GUILayout.Label(C(text, tip), _sub, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
    }

    // Same, with explicit layout options (e.g. a fixed width beside a Segmented control).
    public static void Label(string text, string tip, params GUILayoutOption[] opts)
    {
        Ensure();
        GUILayout.Label(C(text, tip), _sub, opts);
    }

    // Publishes `tip` for the control just laid out. A slider has no GUIContent of its own, so it
    // never sets GUI.tooltip; on Repaint, with the mouse over its rect, set it the way a labelled
    // control would and CaptureTooltip picks it up unchanged.
    static void TipOverLastRect(string tip)
    {
        if (string.IsNullOrEmpty(tip)) return;
        var e = Event.current;
        if (e != null && e.type == EventType.Repaint && GUILayoutUtility.GetLastRect().Contains(e.mousePosition))
            GUI.tooltip = tip;
    }

    // A list row washed with the active-tint when `active`: a title plus an optional state line.
    // Returns true when the row body is clicked. Drawn as a vertical group whose own background is
    // the tile/tint texture, so the panel auto-sizes the row and the labels paint on top of it
    // (no GUILayout.BeginArea / manual rects, which previously hid the title behind the wash).
    public static bool StateRow(string title, string state, bool active, bool muted = false) =>
        StateRow(title, state, active, null, muted);
    public static bool StateRow(string title, string state, bool active, string tip, bool muted = false)
    {
        Ensure();
        EnsureRowStyles();

        GUILayout.BeginVertical(active ? _rowOn : (muted ? _rowMuted : _rowFlat));
        GUILayout.Label(C(title, tip), _rowTitle);
        if (!string.IsNullOrEmpty(state))
            GUILayout.Label(C(state, tip), active ? _rowStateOn : _rowState);
        GUILayout.EndVertical();

        var r = GUILayoutUtility.GetLastRect();
        var e = Event.current;
        if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
        {
            e.Use();
            return true;
        }
        return false;
    }

    static GUIStyle _rowOn, _rowMuted, _rowFlat, _rowTitle, _rowState, _rowStateOn;
    static void EnsureRowStyles()
    {
        if (_rowOn != null) return;
        var pad = new RectOffset(11, 11, 8, 8);
        var mrg = new RectOffset(0, 0, 2, 2);
        _rowOn = new GUIStyle { border = new RectOffset(8, 8, 8, 8), padding = pad, margin = mrg };
        _rowOn.normal.background = _tintTex;        // active row: blue wash
        _rowMuted = new GUIStyle { border = new RectOffset(8, 8, 8, 8), padding = pad, margin = mrg };
        _rowMuted.normal.background = _tileTex;     // backdrop row: neutral tile
        _rowFlat = new GUIStyle { padding = pad, margin = mrg };

        _rowTitle = new GUIStyle(_sub) { fontSize = 13, wordWrap = true };   // long names wrap, never overflow
        if (_sansMedium != null) _rowTitle.font = _sansMedium;
        _rowTitle.normal.textColor = Ink;
        _rowState = new GUIStyle(_sub) { fontSize = 11 };
        _rowState.normal.textColor = Ink2;
        _rowStateOn = new GUIStyle(_rowState);
        _rowStateOn.normal.textColor = AccentInk;
    }

    // Thumbnail tile button: image (or color swatch) with a caption, accent ring when selected.
    public static bool Thumb(Texture tex, string label, bool selected, float size = 64f) =>
        Thumb(tex, label, selected, null, size);
    public static bool Thumb(Texture tex, string label, bool selected, string tip, float size = 64f)
    {
        Ensure();
        EnsureThumbStyles();
        var style = selected ? _thumbOn : _thumb;
        GUILayout.BeginVertical(GUILayout.Width(size));
        var clicked = GUILayout.Button(C("", tip), style, GUILayout.Width(size), GUILayout.Height(size));
        var r = GUILayoutUtility.GetLastRect();
        if (tex != null)
        {
            var pad = new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8);
            GUI.DrawTexture(pad, tex, ScaleMode.ScaleToFit);
        }
        if (!string.IsNullOrEmpty(label))
            GUILayout.Label(C(label, tip), _thumbCap, GUILayout.Width(size), GUILayout.Height(26f));
        GUILayout.EndVertical();
        return clicked;
    }

    // Three-column thumbnail grid for the 300px right rail. Wrap ThumbCell calls in
    // BeginThumbGrid / EndThumbGrid; rows break automatically.
    public const int ThumbCols = 3;
    public const float ThumbSize = 76f;
    static int _thumbCol;
    public static void BeginThumbGrid() { _thumbCol = 0; GUILayout.BeginHorizontal(); }
    public static void EndThumbGrid()   { GUILayout.EndHorizontal(); }
    public static bool ThumbCell(Texture tex, string label, bool selected, string tip = null)
    {
        if (_thumbCol >= ThumbCols) { GUILayout.EndHorizontal(); GUILayout.BeginHorizontal(); _thumbCol = 0; }
        _thumbCol++;
        return Thumb(tex, label, selected, tip, ThumbSize);
    }

    // "roof_tar_weathered" -> "Roof Tar Weathered"
    public static string PrettyId(string id)
    {
        if (string.IsNullOrEmpty(id)) return id;
        var parts = id.Replace('_', ' ').Split(' ');
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i].Substring(1);
        return string.Join(" ", parts);
    }

    static GUIStyle _thumb, _thumbOn, _thumbCap;
    static void EnsureThumbStyles()
    {
        if (_thumb != null) return;
        _thumb = new GUIStyle { border = new RectOffset(8, 8, 8, 8), margin = new RectOffset(3, 3, 3, 3) };
        _thumb.normal.background = _fieldTex;
        _thumb.hover.background  = _tintTex;
        _thumbOn = new GUIStyle(_thumb);
        _thumbOn.normal.background = _tintTex;        // accent-tinted host
        _thumbOn.hover.background  = _tintHoverTex;
        _thumbCap = new GUIStyle(_sub) { fontSize = 11, alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
        _thumbCap.normal.textColor = Ink;
    }

    // Top command bar (Browse / Place / Terrain / Build / Generate). Returns selected index.
    public static int CommandBar(int selected, string[] items) => CommandBar(selected, items, null);
    public static int CommandBar(int selected, string[] items, string[] tips)
    {
        Ensure();
        EnsureCommandStyle();
        return GUILayout.Toolbar(selected, C(items, tips), _command, GUILayout.Height(PrimaryH));
    }

    // One-line status strip drawn in place of the command bar while the walkthrough is engaged.
    public static void HintStrip(string text)
    {
        Ensure();
        EnsureHintStyle();
        GUILayout.Label(text ?? "", _hint, GUILayout.Height(PrimaryH), GUILayout.ExpandWidth(true));
    }

    static GUIStyle _hint;
    static void EnsureHintStyle()
    {
        if (_hint != null) return;
        _hint = new GUIStyle(_sub) { fontSize = 13, alignment = TextAnchor.MiddleCenter };
        if (_sansMedium != null) _hint.font = _sansMedium;
        _hint.normal.textColor = Ink;
    }

    static GUIStyle _command;
    static void EnsureCommandStyle()
    {
        if (_command != null) return;
        _command = new GUIStyle(_skin.button) { fontSize = 13, fontStyle = FontStyle.Normal, fixedHeight = PrimaryH, padding = new RectOffset(14, 14, 0, 0) };
        if (_sansMedium != null) _command.font = _sansMedium;
    }

    // ---- tooltips ----
    // IMGUI sets GUI.tooltip while the mouse is over a control drawn with a GUIContent tooltip, but
    // only for the OnGUI call that drew it. Each panel therefore calls CaptureTooltip() right before
    // its GUILayout.EndArea(); the value (plus the mouse position in screen space) is parked here and
    // UIShell draws it once per frame, on top of every rail, via DrawTooltipOverlay(). Disabled
    // controls report a tooltip too, so a tip can say why a button is off.

    const float TipDelay    = 0.4f;    // seconds the same tip must be hovered before it shows
    const float TipMaxWidth = 260f;

    static string  _tipText;       // most recently captured tooltip
    static Vector2 _tipPos;        // GUI screen-space mouse position at capture
    static int     _tipFrame = -10;
    static string  _tipShown;      // tip the delay timer is running for
    static float   _tipSince;

    // OnGUI-side sample of "a text field / slider holds keyboard focus", recorded by every panel's
    // CaptureTooltip call. Update()-side guards read this alongside GUIUtility.keyboardControl so
    // scene keys (WASD, hotkeys) stay suppressed while typing, with no OnGUI/Update timing gap.
    static int _typingFrame = -10;
    public static bool TypingInUI => Time.frameCount - _typingFrame <= 1;

    public static void CaptureTooltip()
    {
        var e = Event.current;
        if (e == null) return;
        if (GUIUtility.keyboardControl != 0) _typingFrame = Time.frameCount;
        if (e.type != EventType.Repaint) return;
        string t = GUI.tooltip;
        if (string.IsNullOrEmpty(t)) return;
        _tipText  = t;
        _tipFrame = Time.frameCount;
        _tipPos   = GUIUtility.GUIToScreenPoint(e.mousePosition);
    }

    public static void DrawTooltipOverlay()
    {
        Ensure();
        bool live = !string.IsNullOrEmpty(_tipText) && Time.frameCount - _tipFrame <= 1;
        if (!live) { _tipShown = null; return; }
        if (_tipShown != _tipText)
        {
            _tipShown = _tipText;
            _tipSince = Time.unscaledTime;
        }
        if (Time.unscaledTime - _tipSince < TipDelay) return;
        if (Event.current.type != EventType.Repaint) return;

        var content = new GUIContent(_tipText);
        float w = Mathf.Min(TipMaxWidth, _tip.CalcSize(content).x);
        float h = _tip.CalcHeight(content, w);
        float x = _tipPos.x + 14f, y = _tipPos.y + 22f;
        if (x + w > Screen.width  - 4f) x = Mathf.Max(4f, _tipPos.x - w - 6f);
        if (y + h > Screen.height - 4f) y = Mathf.Max(4f, _tipPos.y - h - 8f);
        GUI.Label(new Rect(x, y, w, h), content, _tip);
    }
}
