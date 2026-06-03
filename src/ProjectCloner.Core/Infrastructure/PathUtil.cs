namespace ProjectCloner.Core.Infrastructure;

public static class PathUtil
{
    /// <summary>
    /// Normalizes a user-entered path: expands environment variables and a leading <c>~</c>
    /// (the shell does this in a terminal, .NET does not), then resolves to an absolute path.
    /// Returns the input unchanged when empty.
    /// </summary>
    public static string Expand(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? string.Empty;

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded == "~")
            expanded = Home;
        else if (expanded.StartsWith("~/", StringComparison.Ordinal) || expanded.StartsWith("~\\", StringComparison.Ordinal))
            expanded = Path.Combine(Home, expanded[2..]);

        return Path.GetFullPath(expanded);
    }

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
