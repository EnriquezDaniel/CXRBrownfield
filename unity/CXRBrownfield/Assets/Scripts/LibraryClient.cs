using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// HTTP client for the /api/environments and /api/buildings CRUD endpoints.
// All methods are fire-and-callback (coroutines). Safe to call from any MonoBehaviour.
// Requires Newtonsoft.Json (com.unity.nuget.newtonsoft-json).
public class LibraryClient : MonoBehaviour
{
    [SerializeField] private string serverBaseUrl = "http://localhost:5002";
    [SerializeField] private int timeoutSeconds = 10;

    // --- Environments ---

    public void GetEnvironments(Action<List<EnvironmentSummary>> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGetList<EnvironmentSummary, EnvListWrapper>($"{serverBaseUrl}/api/environments", r => r.environments, onSuccess, onError));

    public void GetEnvironment(string id, Action<EnvironmentDef> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGet<EnvironmentDef>($"{serverBaseUrl}/api/environments/{id}", onSuccess, onError));

    // kind: "user" (default) or "generated". onName receives the server-assigned (uniquified) name.
    public void PostEnvironment(EnvironmentDef env, Action<string> onSuccess = null, Action<string> onError = null, string kind = null, Action<string> onName = null)
        => StartCoroutine(CoPost($"{serverBaseUrl}/api/environments", env, onSuccess, onError, kind, onName));

    public void PutEnvironment(EnvironmentDef env, Action onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPut($"{serverBaseUrl}/api/environments/{env.id}", env, onSuccess, onError));

    // --- Active environment pointer (live multi-client sync) ---

    // One published environment (active or backdrop). version is read live from the env
    // record, so a poller can detect an edit (version bumps) per environment.
    public class LoadedEnvPointer
    {
        public string envId;
        public int    version;
        public string name;
    }

    // The shared pointer: the host's full loaded set plus which env is active. A poller can
    // detect loads/closes (loaded list changes), an active switch (envId changes), and edits
    // (a version bumps). envId/version/name mirror the active entry for back-compat.
    public class ActivePointer
    {
        public string status;
        public string envId;
        public int    version;
        public string name;
        public string updatedAt;
        public List<LoadedEnvPointer> loaded;
    }

    // Poll the server for the shared published set. envId is null when no env is active.
    public void GetActive(Action<ActivePointer> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGet<ActivePointer>($"{serverBaseUrl}/api/active", onSuccess, onError));

    // Publish the host's loaded set (host only): every loaded env id plus which one is active.
    // loadedIds may be null (publishes just the active env). Pass both null/empty to clear.
    public void SetActive(string envId, List<string> loadedIds = null, Action<ActivePointer> onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPostJson($"{serverBaseUrl}/api/active", new { envId, loadedIds }, onSuccess, onError));

    // --- Buildings ---

    public void GetBuildings(Action<List<BuildingSummary>> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGetList<BuildingSummary, BldgListWrapper>($"{serverBaseUrl}/api/buildings", r => r.buildings, onSuccess, onError));

    public void GetBuilding(string id, Action<BuildingDef> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGet<BuildingDef>($"{serverBaseUrl}/api/buildings/{id}", onSuccess, onError));

    // kind: "static" (default) or "cached". onName receives the server-assigned (uniquified) name.
    public void PostBuilding(BuildingDef bldg, Action<string> onSuccess = null, Action<string> onError = null, string kind = null, Action<string> onName = null)
        => StartCoroutine(CoPost($"{serverBaseUrl}/api/buildings", bldg, onSuccess, onError, kind, onName));

    public void PutBuilding(BuildingDef bldg, Action onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPut($"{serverBaseUrl}/api/buildings/{bldg.id}", bldg, onSuccess, onError));

    // --- Typed response wrappers (internal) ---

    private class EnvListWrapper  { public List<EnvironmentSummary> environments; }
    private class BldgListWrapper { public List<BuildingSummary>   buildings; }
    private class CreateResponse  { public string status; public string id; public string name; }
    private class FavoriteResponse { public string status; public string id; public bool favorite; }
    private class InputListWrapper { public List<string> inputs; }
    private class UploadResponse  { public string status; public string name; }

    // --- Coroutine helpers ---

    private IEnumerator CoGetList<TItem, TWrapper>(
        string url,
        Func<TWrapper, List<TItem>> extract,
        Action<List<TItem>> onSuccess,
        Action<string> onError)
    {
        using var req = UnityWebRequest.Get(url);
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        try
        {
            var wrapper = JsonConvert.DeserializeObject<TWrapper>(req.downloadHandler.text);
            onSuccess?.Invoke(extract(wrapper) ?? new List<TItem>());
        }
        catch (Exception e) { onError?.Invoke(e.Message); }
    }

    private IEnumerator CoGet<T>(string url, Action<T> onSuccess, Action<string> onError)
    {
        using var req = UnityWebRequest.Get(url);
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        T obj;
        try { obj = JsonConvert.DeserializeObject<T>(req.downloadHandler.text); }
        catch (Exception e) { onError?.Invoke(e.Message); yield break; }
        // An empty or "null" body deserializes to null without throwing — route it
        // to onError so callers never receive a null record.
        if (obj == null) { onError?.Invoke($"Empty/invalid response from {url}"); yield break; }
        onSuccess?.Invoke(obj);
    }

    private IEnumerator CoPost<T>(string url, T body, Action<string> onSuccess, Action<string> onError, string kind = null, Action<string> onName = null)
    {
        string json;
        if (string.IsNullOrEmpty(kind))
        {
            json = JsonConvert.SerializeObject(body);
        }
        else
        {
            // Inject a transient "kind" field the server uses for routing (it pops it before saving).
            var jo = JObject.FromObject(body);
            jo["kind"] = kind;
            json = jo.ToString();
        }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        try
        {
            var resp = JsonConvert.DeserializeObject<CreateResponse>(req.downloadHandler.text);
            onSuccess?.Invoke(resp?.id);
            if (resp != null && !string.IsNullOrEmpty(resp.name)) onName?.Invoke(resp.name);
        }
        catch (Exception e) { onError?.Invoke(e.Message); }
    }

    // POST a JSON body and deserialize the response (used by the /api/active pointer).
    private IEnumerator CoPostJson<TResp>(string url, object body, Action<TResp> onSuccess, Action<string> onError)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        try { onSuccess?.Invoke(JsonConvert.DeserializeObject<TResp>(req.downloadHandler.text)); }
        catch (Exception e) { onError?.Invoke(e.Message); }
    }

    private IEnumerator CoPut<T>(string url, T body, Action onSuccess, Action<string> onError)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(body));
        using var req = new UnityWebRequest(url, "PUT");
        req.uploadHandler   = new UploadHandlerRaw(bytes);
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        onSuccess?.Invoke();
    }

    // --- Archive (soft-delete) ---

    public void ArchiveEnvironment(string id, Action onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPostEmpty($"{serverBaseUrl}/api/environments/{id}/archive", onSuccess, onError));

    public void ArchiveBuilding(string id, Action onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPostEmpty($"{serverBaseUrl}/api/buildings/{id}/archive", onSuccess, onError));

    private IEnumerator CoPostEmpty(string url, Action onSuccess, Action<string> onError)
    {
        using var req = new UnityWebRequest(url, "POST");
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        onSuccess?.Invoke();
    }

    // --- Favorite (toggle) ---

    public void ToggleFavoriteEnvironment(string id, Action<bool> onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPostFavorite($"{serverBaseUrl}/api/environments/{id}/favorite", onSuccess, onError));

    public void ToggleFavoriteBuilding(string id, Action<bool> onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoPostFavorite($"{serverBaseUrl}/api/buildings/{id}/favorite", onSuccess, onError));

    // POST with no body; parses the new favorite state from the response.
    private IEnumerator CoPostFavorite(string url, Action<bool> onSuccess, Action<string> onError)
    {
        using var req = new UnityWebRequest(url, "POST");
        req.downloadHandler = new DownloadHandlerBuffer();
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        try
        {
            var resp = JsonConvert.DeserializeObject<FavoriteResponse>(req.downloadHandler.text);
            onSuccess?.Invoke(resp != null && resp.favorite);
        }
        catch (Exception e) { onError?.Invoke(e.Message); }
    }

    // --- Input images (/api/inputs) ---

    // List uploaded source-image filenames available on the server.
    public void GetInputs(Action<List<string>> onSuccess, Action<string> onError = null)
        => StartCoroutine(CoGetList<string, InputListWrapper>($"{serverBaseUrl}/api/inputs", r => r.inputs, onSuccess, onError));

    // Upload a local image file to the server's input/ folder. Returns the stored name.
    public void UploadInput(string localPath, Action<string> onSuccess = null, Action<string> onError = null)
        => StartCoroutine(CoUploadInput(localPath, onSuccess, onError));

    private IEnumerator CoUploadInput(string localPath, Action<string> onSuccess, Action<string> onError)
    {
        byte[] data;
        try { data = File.ReadAllBytes(localPath); }
        catch (Exception e) { onError?.Invoke(e.Message); yield break; }

        string fileName = Path.GetFileName(localPath);
        var form = new List<IMultipartFormSection>
        {
            new MultipartFormFileSection("file", data, fileName, "application/octet-stream")
        };

        using var req = UnityWebRequest.Post($"{serverBaseUrl}/api/inputs", form);
        req.timeout = timeoutSeconds;
        yield return req.SendWebRequest();
        if (req.result != UnityWebRequest.Result.Success) { onError?.Invoke(req.error); yield break; }
        try
        {
            var resp = JsonConvert.DeserializeObject<UploadResponse>(req.downloadHandler.text);
            onSuccess?.Invoke(resp?.name);
        }
        catch (Exception e) { onError?.Invoke(e.Message); }
    }
}
