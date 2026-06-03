using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ProjectCloner.Core.Config;
using ProjectCloner.Core.Infrastructure;
using ProjectCloner.Core.Models;

namespace ProjectCloner.Core.Services;

public sealed record BitbucketRepo(string CloneUrl, string HtmlUrl);

public interface IBitbucketClient
{
    Task<BitbucketRepo> CreateRepositoryAsync(BitbucketSettings settings, string repoSlug,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default);
}

/// <summary>Creates repositories via the Bitbucket Cloud REST API v2.0.</summary>
public sealed class BitbucketClient : IBitbucketClient
{
    private const string ApiBase = "https://api.bitbucket.org/2.0";

    private readonly HttpClient _http;

    public BitbucketClient(HttpClient? http = null) => _http = http ?? new HttpClient();

    public async Task<BitbucketRepo> CreateRepositoryAsync(BitbucketSettings settings, string repoSlug,
        IProgress<ProgressReport>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(settings.Workspace))
            throw new InvalidOperationException("Bitbucket workspace is not configured.");
        if (string.IsNullOrWhiteSpace(settings.Username) || string.IsNullOrWhiteSpace(settings.AppPassword))
            throw new InvalidOperationException("Bitbucket credentials are not configured.");

        var url = $"{ApiBase}/repositories/{settings.Workspace}/{repoSlug}";

        var body = new Dictionary<string, object?>
        {
            ["scm"] = "git",
            ["is_private"] = settings.MakePrivate
        };
        if (!string.IsNullOrWhiteSpace(settings.DefaultProjectKey))
            body["project"] = new Dictionary<string, object?> { ["key"] = settings.DefaultProjectKey };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.AppPassword}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", token);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        log.Info($"POST {url}");
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bitbucket API {(int)response.StatusCode}: {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var cloneUrl = ExtractCloneUrl(root) ?? $"https://bitbucket.org/{settings.Workspace}/{repoSlug}.git";
        var htmlUrl = ExtractHtmlUrl(root) ?? $"https://bitbucket.org/{settings.Workspace}/{repoSlug}";

        log.Success($"Repository created: {htmlUrl}");
        return new BitbucketRepo(cloneUrl, htmlUrl);
    }

    private static string? ExtractCloneUrl(JsonElement root)
    {
        if (root.TryGetProperty("links", out var links) &&
            links.TryGetProperty("clone", out var clone) &&
            clone.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in clone.EnumerateArray())
            {
                if (entry.TryGetProperty("name", out var name) && name.GetString() == "https" &&
                    entry.TryGetProperty("href", out var href))
                    return href.GetString();
            }
        }
        return null;
    }

    private static string? ExtractHtmlUrl(JsonElement root)
        => root.TryGetProperty("links", out var links) &&
           links.TryGetProperty("html", out var html) &&
           html.TryGetProperty("href", out var href)
            ? href.GetString()
            : null;
}
