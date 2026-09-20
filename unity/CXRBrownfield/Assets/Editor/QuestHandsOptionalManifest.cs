using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

// Lets the Quest viewer start with the Touch controllers asleep. The Quest OS treats an app whose
// manifest says nothing about hand tracking as controller-only and blocks the launch behind a
// "Controllers required" dialog. Declaring hand tracking as supported but NOT required
// (required="false" = "controllers or hands") removes the dialog; controller input is unchanged.
//
// OpenXR's Meta Quest Support feature writes the rest of the Quest manifest entries and has no
// switch for these two, so they are added here after it has run. Android builds only: the
// Desktop and PCVR builds never reach this callback. No hand input is read anywhere; without
// controllers the viewer is look-around only.
public class QuestHandsOptionalManifest : IPostGenerateGradleAndroidProject
{
    private const string AndroidNs      = "http://schemas.android.com/apk/res/android";
    private const string HandPermission = "com.oculus.permission.HAND_TRACKING";
    private const string HandFeature    = "oculus.software.handtracking";

    // After XR Management's manifest pass, so nothing rewrites the file behind us.
    public int callbackOrder => 10000;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src", "main", "AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[QuestHandsOptionalManifest] No manifest at {manifestPath}; the build will still ask for controllers.");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);
        var manifest = doc.SelectSingleNode("/manifest") as XmlElement;
        if (manifest == null)
        {
            Debug.LogWarning("[QuestHandsOptionalManifest] Manifest has no <manifest> root; left untouched.");
            return;
        }

        Ensure(doc, manifest, "uses-permission", HandPermission);
        Ensure(doc, manifest, "uses-feature", HandFeature).SetAttribute("required", AndroidNs, "false");

        doc.Save(manifestPath);
        Debug.Log("[QuestHandsOptionalManifest] Hand tracking declared optional; the app starts without controllers.");
    }

    // The <tag android:name="name"> child of the manifest root, created when missing.
    private static XmlElement Ensure(XmlDocument doc, XmlElement manifest, string tag, string name)
    {
        foreach (XmlNode node in manifest.SelectNodes(tag))
            if (node is XmlElement e && e.GetAttribute("name", AndroidNs) == name) return e;

        var created = doc.CreateElement(tag);
        created.SetAttribute("name", AndroidNs, name);
        manifest.AppendChild(created);
        return created;
    }
}
