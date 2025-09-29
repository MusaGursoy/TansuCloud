// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud
using System;
using System.IO;
using System.Threading;

namespace TansuCloud.Storage.UnitTests;

internal static class TestEnvironment
{
    private static int _initialized;
    private static readonly object _lock = new();

    public static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return;
        }

        lock (_lock)
        {
            if (_initialized == 1)
            {
                return;
            }

            TryLoadDotEnv();
            _initialized = 1;
        }
    }

    private static void TryLoadDotEnv()
    {
        try
        {
            var root = FindRepositoryRoot();
            if (root is null)
            {
                return;
            }

            var envPath = Path.Combine(root, ".env");
            if (!File.Exists(envPath))
            {
                return;
            }

            foreach (var rawLine in File.ReadLines(envPath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith("export ", StringComparison.Ordinal))
                {
                    line = line[7..].Trim();
                }

                var separatorIndex = line.IndexOf('=');
                if (separatorIndex <= 0)
                {
                    continue;
                }

                var key = line[..separatorIndex].Trim();
                if (key.Length == 0)
                {
                    continue;
                }

                var rawValue = line[(separatorIndex + 1)..];
                if (rawValue is null)
                {
                    continue;
                }

                var value = rawValue.Trim();
                var wasQuoted = false;
                if (value.Length >= 2)
                {
                    if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                    {
                        value = value[1..^1];
                        wasQuoted = true;
                    }
                }

                if (!wasQuoted)
                {
                    var commentIndex = value.IndexOf('#');
                    if (commentIndex >= 0)
                    {
                        value = value[..commentIndex].TrimEnd();
                    }
                }

                if (value.Length == 0)
                {
                    continue;
                }

                // In tests we prefer the .env value unless an explicit env var already exists.
                var existing = Environment.GetEnvironmentVariable(key);
                if (!string.IsNullOrEmpty(existing))
                {
                    continue;
                }

                Environment.SetEnvironmentVariable(key, value);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TestEnvironment] Skipping .env load: {ex.Message}");
        }
    }

    private static string? FindRepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(directory))
        {
            var envPath = Path.Combine(directory, ".env");
            if (File.Exists(envPath))
            {
                return directory;
            }

            var parent = Directory.GetParent(directory);
            if (parent is null)
            {
                break;
            }

            directory = parent.FullName;
        }

        return null;
    }
}
