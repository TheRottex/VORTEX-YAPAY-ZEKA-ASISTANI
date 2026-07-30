using System.Diagnostics;
using System.Text.Json;

namespace Vortex.Public.Tests;

public sealed class RepositoryHygieneTests
{
    private static readonly string[] ForbiddenDirectories = ["HermesWorker", "components", "deploy", "docker"];
    private static readonly string[] ForbiddenExtensions = [".db", ".sqlite", ".sqlite3", ".log", ".zip", ".tar.gz", ".deb", ".rpm", ".pfx", ".p12", ".pem", ".key"];
    private static readonly string PrivateLocalSourcePath = Path.Combine("D:\\.YerelBelgeler", "VORTEX YA");
    private static readonly string[] ForbiddenReleaseIndicators = ["artifact", "checksum", "private-storage", "private storage", "manual-upload", "manual upload", "sha-256", ".sha256"];
    private static readonly string[] RequiredTopLevelLayout = ["Vortex.Admin", "Vortex.Desktop", "Vortex.HermesWorker", "Vortex.LocalAgent", "Vortex.Server", "Vortex.Shared", "Vortex.Tests", "Vortex.Web"];
    private static readonly string[] AllowedDesktopPresentationSourcePaths =
    [
        "Vortex.Desktop/Services/BridgeMessage.cs",
        "Vortex.Desktop/Services/LocalAgentIntentRouter.cs",
        "Vortex.Desktop/Services/WakeWordEngineStatusService.cs",
        "Vortex.Desktop/Services/ClapDetectionService.cs",
        "Vortex.Desktop/Assets/Web/index.html",
        "Vortex.Desktop/Assets/Web/app.js",
        "Vortex.Desktop/Assets/Web/styles.css",
        "Vortex.Desktop/Assets/Web/compact-orb.html",
        "Vortex.Desktop/Assets/Web/compact-orb.js",
        "Vortex.Desktop/Assets/Web/compact-orb.css"
    ];

    [Fact]
    public void ManifestExactlyMatchesTrackedPublicFilesAndContainsNoPrivateMaterial()
    {
        var root = FindRepositoryRoot();
        var manifestPath = Path.Combine(root, "docs", "PUBLIC_EXPORT_MANIFEST.json");
        var manifest = JsonSerializer.Deserialize<PublicExportManifest>(File.ReadAllText(manifestPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Public export manifest could not be parsed.");
        var listedPaths = manifest.IncludedPaths ?? throw new InvalidOperationException("Public export manifest is missing includedPaths.");

        Assert.NotEmpty(listedPaths);
        Assert.Equal(listedPaths.Count, listedPaths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(listedPaths, ValidateManifestPath);
        Assert.Contains("docs/PUBLIC_EXPORT_MANIFEST.json", listedPaths);

        var manifestFiles = listedPaths.ToHashSet(StringComparer.Ordinal);
        var trackedFiles = GetTrackedFiles(root);
        var missingFromGit = manifestFiles.Except(trackedFiles).OrderBy(path => path).ToArray();
        var unexpectedInGit = trackedFiles.Except(manifestFiles).OrderBy(path => path).ToArray();
        Assert.True(missingFromGit.Length == 0 && unexpectedInGit.Length == 0,
            $"Public export manifest must exactly match `git ls-files -z`. Missing from Git: {FormatPaths(missingFromGit)}. Unexpected in Git: {FormatPaths(unexpectedInGit)}.");

        var missingFromWorkspace = manifestFiles
            .Where(path => !File.Exists(Path.Combine(root, path)))
            .OrderBy(path => path)
            .ToArray();
        Assert.True(missingFromWorkspace.Length == 0,
            $"Every public export manifest entry must exist in the checked-out workspace: {string.Join(", ", missingFromWorkspace)}.");

        Assert.DoesNotContain(manifestFiles, IsForbiddenPath);
        Assert.All(RequiredTopLevelLayout, directory => Assert.Contains($"{directory}/README.md", manifestFiles));
        Assert.Equal(AllowedDesktopPresentationSourcePaths.OrderBy(path => path), manifestFiles
            .Where(path => path.StartsWith("Vortex.Desktop/", StringComparison.Ordinal))
            .Where(path => !path.EndsWith("README.md", StringComparison.Ordinal))
            .OrderBy(path => path));
        Assert.False(Directory.Exists(Path.Combine(root, "components")), "The obsolete generated components directory must not be present.");
        var solutionText = File.ReadAllText(Path.Combine(root, "VortexAI.Public.sln"));
        Assert.DoesNotContain("Vortex.Desktop", solutionText, StringComparison.Ordinal);
        var releaseFiles = Directory.EnumerateFiles(Path.Combine(root, "Release"), "*", SearchOption.AllDirectories)
            .Select(path => NormalizePath(Path.GetRelativePath(root, path)))
            .ToArray();
        Assert.All(releaseFiles, path => Assert.Equal("Release/v1.0.1.md", path));
        foreach (var relativePath in manifestFiles.Where(IsTextSource))
        {
            var content = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.DoesNotContain(PrivateLocalSourcePath, content, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var relativePath in releaseFiles)
        {
            var content = File.ReadAllText(Path.Combine(root, relativePath));
            Assert.DoesNotContain(ForbiddenReleaseIndicators, indicator => content.Contains(indicator, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static void ValidateManifestPath(string path)
    {
        Assert.False(string.IsNullOrWhiteSpace(path), "Manifest paths must not be empty.");
        Assert.Equal(path, NormalizePath(path));
        Assert.False(Path.IsPathRooted(path), $"Manifest path must be relative: {path}");
        Assert.DoesNotContain("..", path.Split('/'));
        Assert.DoesNotContain('*', path);
        Assert.DoesNotContain('?', path);
        Assert.False(IsForbiddenPath(path), $"Manifest contains a forbidden path: {path}");
    }

    private static bool IsForbiddenPath(string path)
    {
        var normalized = NormalizePath(path);
        return normalized.Split('/').Any(segment => ForbiddenDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase))
            || ForbiddenExtensions.Any(extension => normalized.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || normalized.Contains("private", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlySet<string> GetTrackedFiles(string root)
    {
        using var git = Process.Start(new ProcessStartInfo("git", "ls-files -z")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        }) ?? throw new InvalidOperationException("Could not start `git ls-files -z`.");
        var output = git.StandardOutput.ReadToEnd();
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, $"`git ls-files -z` failed with exit code {git.ExitCode}: {error.Trim()}");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(NormalizePath)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string FormatPaths(IReadOnlyCollection<string> paths) => paths.Count == 0 ? "(none)" : string.Join(", ", paths);

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Public repository root was not found.");
    }

    private static bool IsTextSource(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".csproj" or ".sln" or ".json" or ".md" or ".txt" or ".yml" or ".yaml" or ".xml" or ".props" or ".targets";

    private sealed record PublicExportManifest(IReadOnlyList<string>? IncludedPaths);
}
