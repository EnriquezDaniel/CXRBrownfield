using System.Collections.Generic;

// In-memory undo/redo for editor data edits. The editor is data-driven: every edit mutates a
// plain serializable def (EnvironmentDef for env-level edits, BuildingDef for tile-level edits)
// and the renderers are idempotent full rebuilds from that data. So undo needs no per-operation
// inverse logic — it snapshots the relevant def as JSON before an edit and restores-by-replace
// (+ re-render) to revert. See CLAUDE.md / the plan for the design and the host wiring.
//
// EditHistory itself does no serialization or Unity work: an IHost (EditController) supplies the
// current state for a scope and applies a restore. This keeps the history a pure, testable list.
//
// Granularity rules used by the host:
//   • Discrete edits (place / delete / single click / button / key) call RecordBefore() right
//     before mutating — one undo entry per edit.
//   • Continuous gestures (drag / brush stroke / freehand path / slider) call BeginGesture()
//     before each mutation (idempotent); the host closes them centrally on mouse-release via
//     EndGesture(), so a whole stroke collapses to a single undo entry. No-op gestures are dropped.
public class EditHistory
{
    public enum Scope { Environment, Building }

    public interface IHost
    {
        // The context id currently editable for `scope`: the active environment's id, or the
        // building id open in the tile editor. Null when nothing in that scope can be edited.
        string ActiveContextId(Scope scope);

        // Serialize the live state for `scope`/`contextId` to JSON, reading from the canonical
        // def (active env, or the building def in the library). Null when it can't be resolved.
        string Serialize(Scope scope, string contextId);

        // Replace the live state for `scope`/`contextId` from JSON and re-render. Restoring a
        // building the user isn't currently editing re-enters it (see EditController.Restore).
        void Restore(Scope scope, string contextId, string json);
    }

    private struct Snapshot
    {
        public Scope  scope;
        public string contextId;
        public string json;
        public string label;
    }

    private readonly IHost _host;
    private readonly int   _maxDepth;
    private readonly LinkedList<Snapshot> _undo = new();
    private readonly Stack<Snapshot>      _redo = new();

    // Open gesture: the baseline snapshot is pushed once on BeginGesture and dropped on EndGesture
    // if nothing actually changed during the drag.
    private bool     _gestureOpen;
    private Snapshot _gestureBaseline;

    public EditHistory(IHost host, int maxDepth = 100)
    {
        _host     = host;
        _maxDepth = System.Math.Max(1, maxDepth);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // Discrete edit: snapshot the pre-edit state. MUST be called immediately BEFORE the mutation.
    // No-op while a gesture is open (the baseline already captured the pre-gesture state).
    public void RecordBefore(Scope scope, string label)
    {
        if (_gestureOpen) return;
        if (TryCapture(scope, label, out var snap)) Push(snap);
    }

    // Continuous gesture: capture the pre-gesture baseline once. Idempotent until EndGesture, so it
    // can be called before every mutation in a drag. The host ends it when the mouse is released.
    public void BeginGesture(Scope scope, string label)
    {
        if (_gestureOpen) return;
        if (!TryCapture(scope, label, out var snap)) return;
        _gestureBaseline = snap;
        _gestureOpen     = true;
        Push(snap);
    }

    // Close an open gesture; discard the pushed entry if the state is unchanged (a no-op drag).
    public void EndGesture()
    {
        if (!_gestureOpen) return;
        _gestureOpen = false;

        if (_undo.Count == 0 || _undo.Last.Value.json != _gestureBaseline.json) return;
        string cur = _host.Serialize(_gestureBaseline.scope, _gestureBaseline.contextId);
        if (cur != null && cur == _gestureBaseline.json) _undo.RemoveLast();
    }

    public void Undo() => Step(undo: true);
    public void Redo() => Step(undo: false);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _gestureOpen = false;
    }

    // Pops one entry off the source stack, pushes the current state onto the other, then restores
    // the popped entry. Undo pops the tail of _undo; Redo pops _redo.
    private void Step(bool undo)
    {
        EndGesture();

        Snapshot entry;
        if (undo)
        {
            if (_undo.Count == 0) return;
            entry = _undo.Last.Value;
            _undo.RemoveLast();
        }
        else
        {
            if (_redo.Count == 0) return;
            entry = _redo.Pop();
        }

        // Capture the present state (by the SAME context id) so the inverse operation can return.
        string cur = _host.Serialize(entry.scope, entry.contextId);
        if (cur != null)
        {
            var inverse = new Snapshot { scope = entry.scope, contextId = entry.contextId, json = cur, label = entry.label };
            if (undo) _redo.Push(inverse);
            else      _undo.AddLast(inverse);
        }

        _host.Restore(entry.scope, entry.contextId, entry.json);
    }

    private bool TryCapture(Scope scope, string label, out Snapshot snap)
    {
        snap = default;
        string ctx = _host.ActiveContextId(scope);
        if (string.IsNullOrEmpty(ctx)) return false;
        string json = _host.Serialize(scope, ctx);
        if (json == null) return false;
        snap = new Snapshot { scope = scope, contextId = ctx, json = json, label = label };
        return true;
    }

    private void Push(Snapshot snap)
    {
        _undo.AddLast(snap);
        _redo.Clear();
        while (_undo.Count > _maxDepth) _undo.RemoveFirst();
    }
}
