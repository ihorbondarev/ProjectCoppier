namespace ProjectCloner.Core.Infrastructure;

/// <summary>
/// Resolves a command-line tool to a full path. GUI apps launched from Finder/Explorer get a
/// minimal PATH that often misses Homebrew/MySQL locations, so we also probe common install dirs.
/// </summary>
public static class ExecutableResolver
{
    /// <summary>Returns the full path to <paramref name="name"/>, or null when it cannot be found.</summary>
    public static string? Resolve(string name, IEnumerable<string>? extraDirs = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // Already an explicit, existing path.
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        var exe = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name + ".exe"
            : name;

        var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var dir in pathDirs.Concat(extraDirs ?? Enumerable.Empty<string>()))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Common locations for MySQL client tools that may be absent from a GUI app's PATH.</summary>
    public static IReadOnlyList<string> CommonMysqlClientDirs() => OperatingSystem.IsWindows()
        ? new[]
        {
            @"C:\Program Files\MySQL\MySQL Server 8.0\bin",
            @"C:\Program Files\MySQL\MySQL Server 8.4\bin",
            @"C:\xampp\mysql\bin"
        }
        : new[]
        {
            "/opt/homebrew/bin",
            "/opt/homebrew/opt/mysql-client/bin",
            "/usr/local/bin",
            "/usr/local/mysql/bin",
            "/usr/local/opt/mysql-client/bin",
            "/opt/local/bin"
        };
}
