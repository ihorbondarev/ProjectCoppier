using System.Text.Json;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

public interface IBuildRunner
{
    Task<BuildResult> RunAsync(string projectRoot, IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}

/// <summary>
/// Build gate: runs <c>npm ci</c> + <c>npm run build</c> for each React/Node project that has a build
/// script, and <c>dotnet build</c> for the .NET solution/projects. Any failure stops the gate.
/// </summary>
public sealed class BuildRunner : IBuildRunner
{
    private static readonly string Npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";

    private readonly ProcessRunner _runner;

    public BuildRunner(ProcessRunner runner) => _runner = runner;

    public async Task<BuildResult> RunAsync(string projectRoot, IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        var result = new BuildResult();
        Action<string> forward = line => log.Info(line);

        // --- React / Node ---
        foreach (var packageJson in FindBuildablePackageJsons(projectRoot))
        {
            var dir = Path.GetDirectoryName(packageJson)!;
            log.Step($"npm ci + build in {dir}");

            var install = await _runner.RunAsync(Npm, ["ci"], dir, onOutput: forward, cancellationToken: ct);
            if (!install.Success) return Fail(result, $"npm ci ({dir})", log);

            var build = await _runner.RunAsync(Npm, ["run", "build"], dir, onOutput: forward, cancellationToken: ct);
            if (!build.Success) return Fail(result, $"npm run build ({dir})", log);

            result.StepsRun.Add($"npm build: {dir}");
        }

        // --- .NET ---
        foreach (var project in FindDotnetTargets(projectRoot))
        {
            log.Step($"dotnet build {project}");
            var build = await _runner.RunAsync("dotnet",
                ["build", project, "-c", "Release", "--nologo"], projectRoot, onOutput: forward, cancellationToken: ct);
            if (!build.Success) return Fail(result, $"dotnet build ({Path.GetFileName(project)})", log);

            result.StepsRun.Add($"dotnet build: {project}");
        }

        if (result.StepsRun.Count == 0)
            log.Warning("No React or .NET projects detected — build gate had nothing to run.");

        return result;
    }

    private static BuildResult Fail(BuildResult result, string step, IProgress<ProgressReport>? log)
    {
        result.Success = false;
        result.FailedStep = step;
        log.Error($"Build step failed: {step}");
        return result;
    }

    private static IEnumerable<string> FindBuildablePackageJsons(string root)
        => EnumerateFiles(root, "package.json").Where(HasBuildScript);

    private static bool HasBuildScript(string packageJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJson));
            return doc.RootElement.TryGetProperty("scripts", out var scripts)
                   && scripts.TryGetProperty("build", out _);
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> FindDotnetTargets(string root)
    {
        var solutions = EnumerateFiles(root, "*.sln").ToList();
        if (solutions.Count > 0) return solutions;
        return EnumerateFiles(root, "*.csproj");
    }

    /// <summary>Enumerate files matching a pattern, skipping build/output directories.</summary>
    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
        => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(p => !PathContainsExcludedSegment(p, root));

    private static bool PathContainsExcludedSegment(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(s =>
            s.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }
}
