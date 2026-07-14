using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Shared helper: fetch the BuildingDefs referenced by an environment's building instances,
// caching them into a target dictionary (skipping ids already present so unchanged buildings
// aren't refetched). Run as a coroutine from any MonoBehaviour. Used by both LibraryBrowser
// (initial load) and SyncClient (live sync) so the fetch-chain isn't duplicated.
public static class BuildingFetch
{
    public static IEnumerator FetchInto(LibraryClient client, IEnumerable<string> buildingIds,
                                        IDictionary<string, BuildingDef> into, Action onDone = null)
    {
        var needed = new HashSet<string>();
        if (buildingIds != null)
            foreach (var id in buildingIds)
                if (!string.IsNullOrEmpty(id) && !into.ContainsKey(id)) needed.Add(id);

        foreach (string bid in needed)
        {
            bool done = false; string cid = bid;
            client.GetBuilding(cid,
                b   => { if (b != null) into[b.id] = b; done = true; },
                err => { Debug.LogError($"[BuildingFetch] building '{cid}': {err}"); done = true; });
            while (!done) yield return null;
        }
        onDone?.Invoke();
    }
}
