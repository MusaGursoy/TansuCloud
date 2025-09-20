// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.IO;

namespace TansuCloud.E2E.Tests;

internal static class TestArtifacts
{
    private static readonly object _lock = new();

    public static void PersistArtifactError(string context, Exception ex)
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            var line =
                $"[{DateTime.UtcNow:O}] {context} {ex.GetType().Name} {ex.Message}\n{ex.StackTrace}\n";
            lock (_lock)
            {
                File.AppendAllText(Path.Combine(outDir, "artifact-errors.log"), line);
            }
        }
        catch
        {
            // Best effort; do not throw from diagnostics
        }
    }

    public static void PersistArtifactError(string context, string message)
    {
        try
        {
            var outDir = Path.Combine(Directory.GetCurrentDirectory(), "test-results");
            Directory.CreateDirectory(outDir);
            var line = $"[{DateTime.UtcNow:O}] {context} MSG {message}\n";
            lock (_lock)
            {
                File.AppendAllText(Path.Combine(outDir, "artifact-errors.log"), line);
            }
        }
        catch { }
    }
}
