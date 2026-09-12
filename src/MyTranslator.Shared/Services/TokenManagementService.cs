using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using MyTranslator.Shared.Models;

namespace MyTranslator.Shared.Services;

public sealed class TokenManagementService(ApiClient api, IConfiguration configuration) : RunApiClientBase(api, configuration)
{
    public async Task<IReadOnlyList<ApiTokenSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Api.GetAsync(ApiUrl("/api/tokens"), cancellationToken);
        return await ReadResultAsync<ApiTokenSummary[]>(response, cancellationToken);
    }
    public async Task<CreatedApiToken> CreateAsync(string name, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl("/api/tokens")) { Content = JsonContent.Create(new { name = name.Trim() }) };
        using var response = await Api.SendAsync(request, cancellationToken);
        return await ReadResultAsync<CreatedApiToken>(response, cancellationToken);
    }
    public async Task RevokeAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, ApiUrl($"/api/tokens/{id}"));
        using var response = await Api.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw await ApiErrorException.FromResponseAsync(response, cancellationToken);
    }
}
