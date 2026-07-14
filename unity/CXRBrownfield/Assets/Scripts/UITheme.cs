using UnityEngine;

// Shared IMGUI theme for every runtime tool panel (LibraryBrowser, EditController,
// TileBuildingEditor, BakePass, ModelRequesterUI).
//
// This is the Unity port of Assets/Redesign.html — the "calmer, clearer interface" visual
// target (Direction B: docked rails, light frosted-paper panels). Every panel renders through
// this one class, so reskinning here reskins the whole tool. The color/size tokens below are the
// literal values from the redesign's design system, so they line up 1:1 with its CSS variables.
//
// Usage inside OnGUI:
//   UITheme.PanelBackground(rect);                 // rounded frosted-paper card behind the panel
//   GUILayout.BeginArea(UITheme.Inset(rect));      // content inset with padding
//   UITheme.Title("…"); UITheme.Header("…"); …
//   GUILayout.EndArea();
//
// Newer component helpers mirror the redesign's kit and are opt-in:
//   UITheme.Segmented(sel, new[]{"Move","Rotate","Scale"});   // segmented control
//   UITheme.PrimaryButton("Generate"); UITheme.GhostButton("Cancel");
//   UITheme.Chip("Nature", active); UITheme.StatusBadge("Server connected", ok:true);
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
    public static readonly Color Btn        = Hex(0xF1F1EE);  // --btn   secondary button
    public static readonly Color BtnHover   = Hex(0xE8E8E3);  // --btn-h
    public static readonly Color Tile       = Hex(0xEFEEE9);  // --tile  segmented track / inset
    public static readonly Color Tile2      = Hex(0xE6E5DF);  // --tile2
    // Accent + tint
    public static readonly Color Accent     = Hex(0x2E63C8);  // --accent
    public static readonly Color AccentInk  = Hex(0x1C4BA0);  // --accent-ink
    public static readonly Color Tint       = Hex(0xEAF1FC);  // --tint   active-row wash
    public static readonly Color TintLine   = Hex(0xBCD2F4);  // --tint-line
    public static readonly Color Ok         = Hex(0x2E9E6B);  // --ok
    public static readonly Color Danger     = Hex(0xB3261E);  // delete red
    // Hairlines
    public static readonly Color Line       = new(0.078f, 0.086f, 0.110f, 0.09f);  // --line
    public static readonly Color Line2       = new(0.078f, 0.086f, 0.110f, 0.14f); // --line2

    // ---- back-compat aliases (older call sites used these names on the dark theme) ----
    public static readonly Color Panel      = PanelCard;
    public static readonly Color Inner      = Field;
    public static readonly Color AccentDim  = AccentInk;
    public static readonly Color TextColor  = Ink;
    public static readonly Color MutedColor = Ink2;
    public static readonly Color DividerCol = Line;

    static GUISkin   _skin;
    static GUIStyle  _title, _header, _sub;
    static GUIStyle  _cardStyle, _primary, _ghost, _chip, _chipOn, _segment;
    static Texture2D _cardTex, _maskTex, _btnTex, _btnHover, _accentTex, _fieldTex,
                     _tileTex, _tintTex, _white;
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

        _white     = Solid(Color.white);
        _cardTex   = Rounded(13, PanelCard, Line2);
        _maskTex   = Rounded(13, Color.white);
        _btnTex    = Rounded(7, Btn,    Line2);
        _btnHover  = Rounded(7, BtnHover, Line2);
        _accentTex = Rounded(7, Accent);
        _fieldTex  = Rounded(7, Field,  Line2);
        _tileTex   = Rounded(7, Tile,   Line);
        _tintTex   = Rounded(7, Tint,   TintLine);

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

        // Button — label & button text 13 / medium; accent fill when pressed / "on" (toggle-as-button)
        var b = _skin.button;
        b.fontSize = 13; b.fontStyle = FontStyle.Normal;
        if (_sansMedium != null) b.font = _sansMedium;
        b.alignment = TextAnchor.MiddleCenter;
        b.padding = new RectOffset(10, 10, 6, 6);
        b.margin  = new RectOffset(3, 3, 3, 3);
        b.border  = new RectOffset(8, 8, 8, 8);
        SetBg(b.normal,   _btnTex,    Ink);
        SetBg(b.hover,    _btnHover,  Ink);
        SetBg(b.active,   _accentTex, Color.white);
        SetBg(b.focused,  _btnTex,    Ink);
        SetBg(b.onNormal, _accentTex, Color.white);
        SetBg(b.onHover,  _accentTex, Color.white);
        SetBg(b.onActive, _accentTex, Color.white);

        // Toggle (used as list-selection text and tab buttons)
        var t = _skin.toggle;
        t.fontSize = 12; t.wordWrap = false;
        t.normal.textColor   = Ink2;   t.hover.textColor   = Ink;
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
        _numSmall.normal.textColor = Ink3;

        // ---- component styles ----
        _cardStyle = new GUIStyle { border = new RectOffset(14, 14, 14, 14) };
        _cardStyle.normal.background = _cardTex;

        _primary = new GUIStyle(b);
        SetBg(_primary.normal,  _accentTex, Color.white);
        SetBg(_primary.hover,   _accentTex, Color.white);
        SetBg(_primary.active,  _accentTex, Color.white);
        SetBg(_primary.focused, _accentTex, Color.white);

        _ghost = new GUIStyle(b);
        _ghost.normal.background = _ghost.hover.background = _ghost.active.background = _ghost.focused.background = null;
        _ghost.normal.textColor = Ink2; _ghost.hover.textColor = Ink;

        _chip = new GUIStyle(b) { fontSize = 11, padding = new RectOffset(12, 12, 5, 5) };
        SetBg(_chip.normal, _btnTex, Ink2); SetBg(_chip.hover, _btnHover, Ink);
        _chipOn = new GUIStyle(_chip);
        SetBg(_chipOn.normal, _accentTex, Color.white); SetBg(_chipOn.hover, _accentTex, Color.white);

        _segment = new GUIStyle(_skin.button) { fontSize = 12, fontStyle = FontStyle.Bold, margin = new RectOffset(0, 0, 0, 0) };

        _building = false;
        GUI.skin = _skin;
    }

    static void SetBg(GUIStyleState s, Texture2D bg, Color text) { s.background = bg; s.textColor = text; }

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
    public static void Note(string text)   { Ensure(); GUILayout.Label(text, _sub);    }

    // Inline IBM Plex Mono numeric label (right-aligned by default).
    public static void Num(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _num, opts); }
    public static void NumSmall(string text, params GUILayoutOption[] opts) { Ensure(); GUILayout.Label(text, _numSmall, opts); }

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
    public static int Segmented(int selected, string[] options)
    {
        Ensure();
        return GUILayout.Toolbar(selected, options, _segment, GUILayout.Height(RowH + 4));
    }

    // Accent primary action button — 44px tall target (UITheme.PrimaryH) per the redesign.
    public static bool PrimaryButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        if (opts == null || opts.Length == 0) opts = new[] { GUILayout.Height(PrimaryH) };
        return GUILayout.Button(text, _primary, opts);
    }

    // Secondary (default) button — same look as GUI.skin.button, named for clarity.
    public static bool SecondaryButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, _skin.button, opts);
    }

    // Borderless ghost / cancel button.
    public static bool GhostButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, _ghost, opts);
    }

    // Filter pill / chip. Returns true when clicked.
    public static bool Chip(string text, bool active, params GUILayoutOption[] opts)
    {
        Ensure();
        return GUILayout.Button(text, active ? _chipOn : _chip, opts);
    }

    // Inline status badge: a dot + label, green when ok else muted.
    public static void StatusBadge(string text, bool ok)
    {
        Ensure();
        var prev = GUI.contentColor;
        GUI.contentColor = ok ? Ok : Ink3;
        GUILayout.Label((ok ? "● " : "○ ") + text, _sub);
        GUI.contentColor = prev;
    }

    // Danger / destructive action — borderless red, used for Delete.
    static GUIStyle _danger;
    public static bool DangerButton(string text, params GUILayoutOption[] opts)
    {
        Ensure();
        if (_danger == null)
        {
            _danger = new GUIStyle(_ghost);
            _danger.normal.textColor = Danger;
            _danger.hover.textColor  = Danger;
            _danger.fontStyle = FontStyle.Bold;
        }
        return GUILayout.Button(text, _danger, opts);
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
    {
        Ensure();
        GUILayout.BeginHorizontal();
        GUILayout.Label(caption, _sub, GUILayout.ExpandWidth(true));
        GUILayout.Label(value.ToString(fmt) + unit, _num, GUILayout.Width(60));
        GUILayout.EndHorizontal();
        return GUILayout.HorizontalSlider(value, min, max);
    }

    // A list row washed with the active-tint when `active`: a title plus an optional state line.
    // Returns true when the row body is clicked. Drawn as a vertical group whose own background is
    // the tile/tint texture, so the panel auto-sizes the row and the labels paint on top of it
    // (no GUILayout.BeginArea / manual rects, which previously hid the title behind the wash).
    public static bool StateRow(string title, string state, bool active, bool muted = false)
    {
        Ensure();
        EnsureRowStyles();

        GUILayout.BeginVertical(active ? _rowOn : (muted ? _rowMuted : _rowFlat));
        GUILayout.Label(title, _rowTitle);
        if (!string.IsNullOrEmpty(state))
            GUILayout.Label(state, active ? _rowStateOn : _rowState);
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

        _rowTitle = new GUIStyle(_sub) { fontSize = 13, wordWrap = false };
        if (_sansMedium != null) _rowTitle.font = _sansMedium;
        _rowTitle.normal.textColor = Ink;
        _rowState = new GUIStyle(_sub) { fontSize = 11 };
        _rowState.normal.textColor = Ink3;
        _rowStateOn = new GUIStyle(_rowState);
        _rowStateOn.normal.textColor = AccentInk;
    }

    // Thumbnail tile button: image (or color swatch) with a caption, accent ring when selected.
    public static bool Thumb(Texture tex, string label, bool selected, float size = 64f)
    {
        Ensure();
        EnsureThumbStyles();
        var style = selected ? _thumbOn : _thumb;
        GUILayout.BeginVertical(GUILayout.Width(size));
        var clicked = GUILayout.Button(GUIContent.none, style, GUILayout.Width(size), GUILayout.Height(size));
        var r = GUILayoutUtility.GetLastRect();
        if (tex != null)
        {
            var pad = new Rect(r.x + 4, r.y + 4, r.width - 8, r.height - 8);
            GUI.DrawTexture(pad, tex, ScaleMode.ScaleToFit);
        }
        if (!string.IsNullOrEmpty(label))
            GUILayout.Label(label, _thumbCap, GUILayout.Width(size), GUILayout.Height(26f));
        GUILayout.EndVertical();
        return clicked;
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
        _thumbCap = new GUIStyle(_sub) { fontSize = 10, alignment = TextAnchor.UpperCenter, wordWrap = true, clipping = TextClipping.Clip };
        _thumbCap.normal.textColor = Ink2;
    }

    // Top command bar (Browse / Place / Terrain / Build / Manage / Generate). Returns selected index.
    public static int CommandBar(int selected, string[] items)
    {
        Ensure();
        EnsureCommandStyle();
        return GUILayout.Toolbar(selected, items, _command, GUILayout.Height(PrimaryH));
    }

    static GUIStyle _command;
    static void EnsureCommandStyle()
    {
        if (_command != null) return;
        _command = new GUIStyle(_skin.button) { fontSize = 13, fontStyle = FontStyle.Normal, fixedHeight = PrimaryH, padding = new RectOffset(14, 14, 0, 0) };
        if (_sansMedium != null) _command.font = _sansMedium;
    }
}
