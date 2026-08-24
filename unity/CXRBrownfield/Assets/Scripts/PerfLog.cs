using UnityEngine;

// Lightweight wall-clock logging for the save / undo / render hot paths. Callers time themselves
// with System.Diagnostics.Stopwatch and report here; a line prints only when the cost crosses the
// caller's threshold, so quiet frames stay quiet. Flip Enabled off to silence all [Perf] output.
public static class PerfLog
{
    public static bool Enabled = true;

    public static void Log(long elapsedMs, long thresholdMs, string message)
    {
        if (Enabled && elapsedMs >= thresholdMs) Debug.Log($"[Perf] {message}");
    }
}
