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

        // True when an entry for (scope, contextId) belongs to what the user can edit RIGHT NOW.
        // The list interleaves independent timelines — one per environment and per building — so
        // undo must take the newest entry for the current context, not the newest entry overall.
        // Without this, Ctrl+Z inside a tile-edit session pops another building's (or the
        // environment's) entry and yanks the user out of the building they're editing.
        bool IsUndoEligible(Scope scope, string contextId);
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
    // Both are ordered oldest -> newest; the newest ELIGIBLE entry is the one undo/redo takes.
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    // Open gesture: the baseline snapshot is pushed once on BeginGesture and dropped on EndGesture
    // if nothing actually changed during the drag.
    private bool     _gestureOpen;
    private Snapshot _gestureBaseline;

    // Redo entries the most recent Push() invalidated. A no-op gesture that EndGesture() drops never
    // really invalidated anything, so they are put back — otherwise a click that changed nothing
    // (a decorate drag that missed the mesh, a paint stroke on an occupied cell) silently kills redo.
    private readonly List<Snapshot> _redoDroppedByPush = new();

    public EditHistory(IHost host, int maxDepth = 100)
    {
        _host     = host;
        _maxDepth = System.Math.Max(1, maxDepth);
    }

    // Only entries for the context the user is editing right now can actually be stepped to.
    public bool CanUndo => NewestEligible(_undo) >= 0;
    public bool CanRedo => NewestEligible(_redo) >= 0;

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

        int last = _undo.Count - 1;
        if (last < 0 || _undo[last].json != _gestureBaseline.json) return;
        string cur = _host.Serialize(_gestureBaseline.scope, _gestureBaseline.contextId);
        if (cur == null || cur != _gestureBaseline.json) return;

        _undo.RemoveAt(last);
        // The gesture changed nothing, so the redo entries its Push() dropped are still valid.
        _redo.AddRange(_redoDroppedByPush);
        _redoDroppedByPush.Clear();
    }

    public void Undo() => Step(undo: true);
    public void Redo() => Step(undo: false);

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _redoDroppedByPush.Clear();
        _gestureOpen = false;
    }

    // Takes the newest entry BELONGING TO THE CURRENT EDIT CONTEXT off the source list, pushes the
    // current state onto the other, then restores it. Snapshots are whole-def replacements, so the
    // entries for different contexts (each environment, each building) are independent timelines and
    // taking the newest matching one — not the newest overall — is exactly right. When the current
    // context has no entries left, this is a no-op: Ctrl+Z simply stops rather than reaching into
    // another building's history and dragging the user there.
    private void Step(bool undo)
    {
        EndGesture();

        var  source = undo ? _undo : _redo;
        int  idx    = NewestEligible(source);
        if (idx < 0) return;

        Snapshot entry = source[idx];
        source.RemoveAt(idx);

        // Capture the present state (by the SAME context id) so the inverse operation can return.
        string cur = _host.Serialize(entry.scope, entry.contextId);
        if (cur != null)
        {
            var inverse = new Snapshot { scope = entry.scope, contextId = entry.contextId, json = cur, label = entry.label };
            if (undo) _redo.Add(inverse);
            else      _undo.Add(inverse);
        }

        _host.Restore(entry.scope, entry.contextId, entry.json);
    }

    // Index of the newest entry the host will accept for the current context, or -1.
    private int NewestEligible(List<Snapshot> list)
    {
        for (int i = list.Count - 1; i >= 0; i--)
            if (_host.IsUndoEligible(list[i].scope, list[i].contextId)) return i;
        return -1;
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
        _undo.Add(snap);

        // A new edit only invalidates the redo timeline it forked — editing building A must not
        // discard a pending redo in building B or in the environment.
        _redoDroppedByPush.Clear();
        for (int i = _redo.Count - 1; i >= 0; i--)
        {
            if (_redo[i].scope != snap.scope || _redo[i].contextId != snap.contextId) continue;
            _redoDroppedByPush.Add(_redo[i]);
            _redo.RemoveAt(i);
        }
        _redoDroppedByPush.Reverse();   // keep oldest -> newest so EndGesture can append them back

        while (_undo.Count > _maxDepth) _undo.RemoveAt(0);
    }
}
