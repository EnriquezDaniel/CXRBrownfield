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
    [MenuItem("Build/VR — Quest (Android) %#q", priority = 20)]
    public static void BuildVRQuest()
    {
        Build(new[] { VrScene }, BuildTarget.Android,
              "Builds/VR-Quest/CXR-VR.apk");
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
