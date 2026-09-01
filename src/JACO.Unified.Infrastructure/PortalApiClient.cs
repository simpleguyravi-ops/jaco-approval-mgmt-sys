using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;

namespace JACO.Unified.Infrastructure;

public sealed record PortalAppUserRosterEntry(string UserName, string DisplayName, string? Email, string? Department, bool IsAdmin);

public sealed class PortalApiClient(HttpClient http, IConfiguration config)
{
    private string BaseUrl => config["PortalApi:BaseUrl"]?.TrimEnd('/') ?? "http://localhost:5010";

    public async Task<IReadOnlyList<PortalAppUserRosterEntry>> GetUsersWithAccessAsync(string appCode, CancellationToken ct = default)
    {
        try
        {
            var result = await http.GetFromJsonAsync<List<PortalAppUserRosterEntry>>($"{BaseUrl}/api/apps/{appCode}/users", ct);
            return result ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }
}
