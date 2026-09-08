using UnityEngine;

// Tells a click from a drag for a press-and-hold tool (the tile editor's Add tool). A press arms
// the gate; the press becomes a drag the moment the cursor moves more than `thresholdPx` from where
// it went down; a release that never crossed the threshold is a click. Pure state so the EditMode
// tests can pin the rules down without an input device.
//
// The host commits a click on release (not on press), so a short press that lands on a frame or two
// of held input never acts more than once. 5 px matches EditController's box-select threshold.
public sealed class ClickDragGate
{
    public const float DefaultThresholdPx = 5f;

    private readonly float _thresholdSq;

    public bool    Armed    { get; private set; }   // press seen, release not yet
    public bool    Dragging { get; private set; }   // threshold crossed during this press
    public Vector2 PressPos { get; private set; }

    public ClickDragGate(float thresholdPx = DefaultThresholdPx)
    {
        _thresholdSq = thresholdPx * thresholdPx;
    }

    public void Press(Vector2 pos)
    {
        Armed    = true;
        Dragging = false;
        PressPos = pos;
    }

    // Call while the button is held. Returns true exactly once per press: on the update that turns
    // the press into a drag. Not armed or already dragging: false.
    public bool Update(Vector2 pos)
    {
        if (!Armed || Dragging) return false;
        if ((pos - PressPos).sqrMagnitude <= _thresholdSq) return false;
        Dragging = true;
        return true;
    }

    // Call on button up. True when the press was a click (armed and never dragged). Always disarms.
    public bool Release()
    {
        bool click = Armed && !Dragging;
        Armed    = false;
        Dragging = false;
        return click;
    }

    // Disarm without reporting a click (the press ended somewhere it must not commit).
    public void Cancel()
    {
        Armed    = false;
        Dragging = false;
    }
}
