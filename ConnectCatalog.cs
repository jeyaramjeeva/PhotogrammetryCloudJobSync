using Trimble.Connect.Client;
using Trimble.Connect.Client.Common;
using ConnectRegion = Trimble.Connect.Client.Models.Region;
using Trimble.Connect.Client.Models;

namespace PhotogrammetryCloudJobSync;

public sealed record ConnectProjectItem(string Name, string Location, string Identifier)
{
    public string ProjectTrn => $"trn:connect:projects:{Location}:{Identifier}";
    public override string ToString() => $"{Name}  ({ProjectTrn})";
}

/// <summary>Lists Connect regions (servers) and projects — same APIs as SampleApp.</summary>
public static class ConnectCatalog
{
    public static async Task<IReadOnlyList<string>> ListRegionsAsync(
        AuthSession session,
        CancellationToken ct)
    {
        var token = await session.GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var client = CreateClient(session.ConnectApiBaseUrl, token);
        await InitializeUserAsync(client, token, ct).ConfigureAwait(false);

        RegionsConfig.Regions = Array.Empty<ConnectRegion>();
        var regions = await client.ReadConfigurationAsync(ct).ConfigureAwait(false)
                      ?? Array.Empty<ConnectRegion>();

        return regions
            .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Location))
            .Select(r => r.Location)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(l => l, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IReadOnlyList<ConnectProjectItem>> ListProjectsAsync(
        AuthSession session,
        string regionLocation,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(regionLocation))
            return Array.Empty<ConnectProjectItem>();

        var token = await session.GetAccessTokenAsync(ct).ConfigureAwait(false);
        using var client = CreateClient(session.ConnectApiBaseUrl, token);
        await InitializeUserAsync(client, token, ct).ConfigureAwait(false);

        // Ensure regions are loaded so GetProjectsAsync can resolve pods
        RegionsConfig.Regions = Array.Empty<ConnectRegion>();
        var regions = await client.ReadConfigurationAsync(ct).ConfigureAwait(false)
                      ?? Array.Empty<ConnectRegion>();
        client.Configuration = regions;

        var all = new List<Project>();
        await client
            .GetProjectsAsync(null, null, null, r => r.Location == regionLocation, ct)
            .ReceiveAllAsync(all.AddRange)
            .ConfigureAwait(false);

        return all
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Identifier))
            .Select(p => new ConnectProjectItem(
                string.IsNullOrWhiteSpace(p.Name) ? p.Identifier : p.Name,
                p.Location ?? regionLocation,
                p.Identifier))
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task InitializeUserAsync(
        TrimbleConnectClient client,
        string token,
        CancellationToken ct)
    {
#pragma warning disable CS0618 // obsolete overload that sets AuthenticationToken
        await client.InitializeTrimbleConnectUserAsync(token, ct).ConfigureAwait(false);
#pragma warning restore CS0618
    }

    private static TrimbleConnectClient CreateClient(string connectApiBaseUrl, string token)
    {
        var baseUri = connectApiBaseUrl.EndsWith('/') ? connectApiBaseUrl : connectApiBaseUrl + "/";
        var config = new TrimbleConnectClientConfig
        {
            ServiceURI = new Uri(baseUri),
            RetryConfig = new RetryConfig { MaxErrorRetry = 1 }
        };
        return new TrimbleConnectClient(config, new TokenCredentialsProvider(token));
    }
}
