// Tansu.Cloud Public Repository:    https://github.com/MusaGursoy/TansuCloud

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace TansuCloud.E2E.Tests;

public sealed class LoopbackLiteralGuardTests
{
    [Fact]
    public void Loopback_literals_are_not_reintroduced_outside_allowlist()
    {
        var repoRoot = FindRepositoryRoot();
        var disallowedTokens = new[]
        {
            "127.0.0.1:8080",
            "localhost:8080",
            "http://127.0.0.1:8080",
            "https://127.0.0.1:8080",
            "http://localhost:8080",
            "https://localhost:8080",
            "http://127.0.0.1:5095",
            "http://localhost:5095",
            "http://127.0.0.1:5257",
            "http://localhost:5257",
            "http://127.0.0.1:5278",
            "http://localhost:5278"
        };

        var allowedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Normalize("dev/tools/common.ps1"),
            Normalize("tests/TansuCloud.E2E.Tests/TestUrls.cs"),
            Normalize("tests/TansuCloud.E2E.Tests/LoopbackLiteralGuardTests.cs"),
            Normalize("tests/TansuCloud.Gateway.UnitTests/TenantResolverTests.cs"),
            Normalize("tests/TansuCloud.Dashboard.UnitTests/DashboardWebAppFactory.cs"),
            Normalize("TansuCloud.Identity/Properties/launchSettings.json"),
            Normalize("TansuCloud.Gateway/Properties/launchSettings.json"),
            Normalize("TansuCloud.Database/Properties/launchSettings.json"),
            Normalize("TansuCloud.Storage/Properties/launchSettings.json"),
            Normalize("docker-compose.yml"),
            Normalize("docker-compose.prod.yml"),
            Normalize("diag-oidc.json")
        };

        var allowedPrefixes = new[]
        {
            Normalize("dev/tools/"),
            Normalize("dev/playwright/"),
            Normalize("dev/scripts/"),
            Normalize("docs/"),
        };

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".cs",
            ".json",
            ".csproj",
            ".props",
            ".targets",
            ".ps1",
            ".psm1",
            ".yml",
            ".yaml",
            ".razor",
            ".http"
        };

        var forbiddenUsages = new List<string>();

        foreach (var file in EnumerateCandidateFiles(repoRoot, extensions))
        {
            var relativePath = Normalize(Path.GetRelativePath(repoRoot, file));
            if (allowedPaths.Contains(relativePath) || StartsWithAny(relativePath, allowedPrefixes))
            {
                continue;
            }

            var extension = Path.GetExtension(relativePath);
            if (string.Equals(extension, ".http", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                var trimmed = line.TrimStart();

                // Skip obvious comment lines to avoid flagging documentation examples.
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("#", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var token in disallowedTokens)
                {
                    if (!line.Contains(token, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    forbiddenUsages.Add($"{relativePath}:{index + 1} contains '{token}'");
                    break;
                }
            }
        }

        if (forbiddenUsages.Count > 0)
        {
            var message = "Disallowed loopback literals found:\n" + string.Join(Environment.NewLine, forbiddenUsages);
            Assert.True(forbiddenUsages.Count == 0, message);
        }
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root, HashSet<string> extensions)
    {
        var excludeSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bin",
            "obj",
            ".git",
            ".vs",
            "TestResults",
            ".playwright",
            "node_modules"
        };

        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            foreach (var directory in Directory.GetDirectories(current))
            {
                var name = Path.GetFileName(directory);
                if (excludeSegments.Contains(name))
                {
                    continue;
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.GetFiles(current))
            {
                var extension = Path.GetExtension(file);
                if (extensions.Contains(extension))
                {
                    yield return file;
                }
            }
        }
    }

    private static bool StartsWithAny(string value, IEnumerable<string> prefixes)
    {
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var solutionPath = Path.Combine(dir.FullName, "TansuCloud.sln");
            if (File.Exists(solutionPath))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Unable to locate repository root (looked for TansuCloud.sln).");
    }

    private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');
} // End of Class LoopbackLiteralGuardTests