using TMPro;
using UnityEngine;

// Connection status inside the headset (VRViewer). IMGUI and overlay canvases never reach the
// eye textures, so without this a viewer that cannot reach the server is an empty world with no
// explanation. Shows the server address and SyncClient's status a little in front of the head,
// and hides as soon as an environment is on screen. Put it on the XR camera.
public class VRStatusText : MonoBehaviour
{
    // USER WIRES THIS IN INSPECTOR (optional, falls back to a scene lookup in Start):
    [SerializeField] private SyncClient syncClient;

    [SerializeField] private float distance = 1.5f;   // metres in front of the head
    [SerializeField] private float drop     = 0.15f;  // a little under eye level

    private TextMeshPro _text;
    private string _shown;

    private void Start()
    {
        if (syncClient == null) syncClient = FindFirstObjectByType<SyncClient>();

        var go = new GameObject("Status Text") { layer = 2 };   // Ignore Raycast
        go.transform.SetParent(transform, false);
        go.transform.localPosition = new Vector3(0f, -drop, distance);

        _text = go.AddComponent<TextMeshPro>();
        _text.alignment          = TextAlignmentOptions.Center;
        _text.fontSize           = 0.6f;
        _text.textWrappingMode   = TextWrappingModes.Normal;
        _text.rectTransform.sizeDelta = new Vector2(1.2f, 0.5f);
        _text.color              = Color.white;
        _text.outlineWidth       = 0.2f;
        _text.outlineColor       = Color.black;
        // fontAsset stays unset so TMP Settings supplies the project default, as BuildingSignSpawner does.
    }

    private void Update()
    {
        if (_text == null) return;
        bool show = syncClient == null || !syncClient.HasRendered;
        if (_text.gameObject.activeSelf != show) _text.gameObject.SetActive(show);
        if (!show) return;

        string msg = syncClient == null
            ? "No SyncClient in this scene."
            : $"Server {syncClient.ServerUrl}\n{syncClient.Status}";
        if (msg == _shown) return;
        _shown = msg;
        _text.text = msg;
    }
}
