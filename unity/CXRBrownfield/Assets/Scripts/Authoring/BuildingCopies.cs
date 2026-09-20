using System;
using System.Collections.Generic;
using Newtonsoft.Json;

// Rules for pasted buildings. A paste (EditController.PasteClipboard) gives every building its own
// BuildingDef, so editing the copy never touches the original: CloneDef makes the record,
// NextCopyName picks its numbered name ("Coffee Shop" to "Coffee Shop 2"). The server mirrors the
// naming rule as its backstop (server.py _unique_name, spaced). Pure, so EditMode tests cover it.
public static class BuildingCopies
{
    public const string FallbackName = "Building";

    // Server kind tags (server.py BLDG_KINDS). The server re-tags a record when it is posted.
    private static readonly string[] KindTags = { "static", "cached" };

    // Splits a trailing " <number>" off a name: "Coffee Shop 2" gives ("Coffee Shop", 2). A name
    // with no such suffix (or one that is only a number) counts as copy 1 of itself.
    public static (string stem, int number) SplitNumber(string name)
    {
        string s = string.IsNullOrWhiteSpace(name) ? FallbackName : name.Trim();
        int sp = s.LastIndexOf(' ');
        if (sp > 0 && sp < s.Length - 1)
        {
            string tail = s.Substring(sp + 1);
            bool digits = true;
            foreach (char c in tail) if (c < '0' || c > '9') { digits = false; break; }
            if (digits && tail.Length <= 6 && int.TryParse(tail, out int n) && n >= 1)
                return (s.Substring(0, sp).TrimEnd(), n);
        }
        return (s, 1);
    }

    // The name a copy of `sourceName` gets: the stem plus the first free number above the source's
    // own. `taken` is every building name in use (library and unsaved copies), compared without
    // case. Copying "Coffee Shop 2" gives "Coffee Shop 3", never "Coffee Shop 2 2".
    public static string NextCopyName(string sourceName, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (taken != null)
            foreach (var t in taken)
                if (!string.IsNullOrWhiteSpace(t)) used.Add(t.Trim());

        var (stem, number) = SplitNumber(sourceName);
        int n = number + 1;
        while (used.Contains($"{stem} {n}")) n++;
        return $"{stem} {n}";
    }

    // True when another building (any id but `exceptId`) already carries `name`.
    public static bool IsNameTaken(string name, string exceptId, IEnumerable<(string id, string name)> all)
    {
        if (string.IsNullOrWhiteSpace(name) || all == null) return false;
        string want = name.Trim();
        foreach (var (id, other) in all)
        {
            if (id == exceptId || string.IsNullOrWhiteSpace(other)) continue;
            if (string.Equals(other.Trim(), want, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    // Deep copy of a def as a new record: own id and name, version 1, flagged hiddenCopy. Decor
    // ids are per def, so they are reissued. The sign is not here (it lives on the instance).
    public static BuildingDef CloneDef(BuildingDef src, string newId, string newName)
    {
        if (src == null) return null;
        var copy = JsonConvert.DeserializeObject<BuildingDef>(JsonConvert.SerializeObject(src));
        copy.id         = newId;
        copy.name       = newName;
        copy.version    = 1;
        copy.hiddenCopy = true;
        copy.tags?.RemoveAll(t => Array.IndexOf(KindTags, t) >= 0);
        if (copy.embeddedObjects != null)
            foreach (var e in copy.embeddedObjects)
                if (e != null && !string.IsNullOrEmpty(e.instanceId))
                    e.instanceId = Guid.NewGuid().ToString("D");
        return copy;
    }
}
