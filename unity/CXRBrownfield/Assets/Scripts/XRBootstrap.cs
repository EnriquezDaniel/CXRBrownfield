using System.Collections;
using UnityEngine;
using UnityEngine.XR.Management;

// Starts XR for THIS scene only. "Initialize XR on Startup" is OFF in XR Plug-in Management, so the
// default desktop/PC scenes (BasicModel) run flat — no headset, no VR. Only the VRViewer scene carries
// this component, so VR is strictly opt-in and scene-driven. Works for both PCVR (Windows + OpenXR)
// and Quest (Android + OpenXR): it starts whatever loader is assigned for the active build target, and
// degrades to a flat window when no headset/loader is available (handy for editor preview).
public class XRBootstrap : MonoBehaviour
{
    private IEnumerator Start()
    {
        var xr = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (xr == null)
        {
            Debug.LogWarning("[XRBootstrap] No XRManagerSettings found — XR Plug-in Management not configured for this target. Running flat.");
            yield break;
        }
        if (xr.activeLoader != null) yield break;   // already initialized (e.g. init-on-startup left on)

        yield return xr.InitializeLoader();
        if (xr.activeLoader == null)
        {
            Debug.LogWarning("[XRBootstrap] No XR loader started (no headset, or OpenXR not enabled for this build target). Running flat.");
            yield break;
        }
        xr.StartSubsystems();
        Debug.Log($"[XRBootstrap] XR started: {xr.activeLoader.name}");
    }

    private void OnDestroy()
    {
        var xr = XRGeneralSettings.Instance != null ? XRGeneralSettings.Instance.Manager : null;
        if (xr != null && xr.activeLoader != null)
        {
            xr.StopSubsystems();
            xr.DeinitializeLoader();
            Debug.Log("[XRBootstrap] XR stopped.");
        }
    }
}
