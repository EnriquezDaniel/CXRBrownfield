using UnityEditor;
using UnityEngine;

// One-click builds under a "Build" menu. The DESKTOP build is the default (PC app, BasicModel scene,
// no VR scene shipped). The VR builds ship only the VRViewer scene; XR is started at runtime by
// XRBootstrap (XR Plug-in Management has "Initialize XR on Startup" OFF), so the desktop build never
// touches VR even though the XR packages are installed.
//
// Each item bakes in its own scene list + platform, so the two builds stay cleanly separate regardless
// of the shared Build Settings scene list (which stays BasicModel-only = the default PC build).
public static class BuildMenu
{
    private const string DesktopScene = "Assets/Scenes/BasicModel.unity";
    private const string VrScene      = "Assets/Scenes/VRViewer.unity";

    // Default build — the PC app exactly as before (no VR). Ctrl+Shift+D.
    [MenuItem("Build/Desktop (PC, Windows) %#d", priority = 0)]
    public static void BuildDesktop()
    {
        Build(new[] { DesktopScene }, BuildTarget.StandaloneWindows64,
              "Builds/Desktop/CXR-Desktop.exe");
    }

    // Quest / standalone-Android headset. OpenXR is already enabled for the Android target.
    //
    // On the headset "localhost" is the headset, so the server address is baked into the APK: a
    // Resources text file LibraryClient reads on Awake, written here and deleted after the build so
    // no other build ever contains it. The address is this PC's LAN IPv4 on port 5002; set the
    // EditorPrefs string "CXR.ViewerServerUrl" to force another one (a hotspot, a different host).
    // The PC's address changing means a rebuild.
    public const string ServerUrlPref = "CXR.ViewerServerUrl";
    private const string ServerUrlAsset = "Assets/Resources/" + LibraryClient.ServerUrlResource + ".txt";

    [MenuItem("Build/VR — Quest (Android) %#q", priority = 20)]
    public static void BuildVRQuest()
    {
        string url = EditorPrefs.GetString(ServerUrlPref, "");
        if (string.IsNullOrWhiteSpace(url)) url = DetectServerUrl();
        if (url == null)
        {
            Debug.LogError("[BuildMenu] No LAN IPv4 address found for the server. Set EditorPrefs " +
                           $"'{ServerUrlPref}' (for example http://192.168.1.20:5002) and build again.");
            return;
        }

        try
        {
            System.IO.File.WriteAllText(ServerUrlAsset, url);
            AssetDatabase.ImportAsset(ServerUrlAsset, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log($"[BuildMenu] Quest build will talk to {url}");
            Build(new[] { VrScene }, BuildTarget.Android,
                  "Builds/VR-Quest/CXR-VR.apk");
        }
        finally
        {
            AssetDatabase.DeleteAsset(ServerUrlAsset);
        }
    }

    // http://<ip>:5002 for the first up, non-loopback interface that has a default gateway (the
    // one the router hands out), so virtual adapters with no route are skipped. null when none.
    public static string DetectServerUrl()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
            var props = nic.GetIPProperties();
            bool hasGateway = false;
            foreach (var g in props.GatewayAddresses)
                if (g.Address != null && g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                    !g.Address.Equals(System.Net.IPAddress.Any)) { hasGateway = true; break; }
            if (!hasGateway) continue;
            foreach (var a in props.UnicastAddresses)
                if (a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    return $"http://{a.Address}:5002";
        }
        return null;
    }

    // Tethered PCVR (Windows). NOTE: requires OpenXR enabled for the *Standalone* target in
    // Project Settings → XR Plug-in Management (the Android target already has it). Without that this
    // produces a flat Windows app.
    [MenuItem("Build/VR — PCVR (Windows)", priority = 21)]
    public static void BuildVRPC()
    {
        Build(new[] { VrScene }, BuildTarget.StandaloneWindows64,
              "Builds/VR-PCVR/CXR-PCVR.exe");
    }

    private static void Build(string[] scenes, BuildTarget target, string outputPath)
    {
        var opts = new BuildPlayerOptions
        {
            scenes           = scenes,
            target           = target,
            targetGroup      = BuildPipeline.GetBuildTargetGroup(target),
            locationPathName = outputPath,
            options          = BuildOptions.None,
        };
        var report = BuildPipeline.BuildPlayer(opts);
        Debug.Log($"[BuildMenu] {target} build → {outputPath} : {report.summary.result} " +
                  $"({report.summary.totalErrors} errors)");
    }
}
