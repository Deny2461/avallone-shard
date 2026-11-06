using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Avallone.AI;

// Admin-only questions to the brain sidecar ([ai persona / lore / reload). Kept apart from IAiNpcClient on
// purpose: these never go through the NPC queue, never touch the model, and never touch game state.
public static class AiBrainAdmin
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    private static HttpClient _http;

    private static HttpClient Http =>
        _http ??= new HttpClient
        {
            BaseAddress = new Uri(
                ServerConfiguration.GetOrUpdateSetting("avallone.ai.sidecarUrl", "http://127.0.0.1:8765/")
            ),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

    // personaKey may be null for a bare reload. Never throws: failures come back as Ok = false.
    public static async Task<LoreQueryResult> QueryAsync(string personaKey, string words, bool reload)
    {
        try
        {
            using var cts = new CancellationTokenSource(Timeout);
            var body = new LoreQuery(personaKey, words ?? "", reload);
            using var response = await Http.PostAsJsonAsync("v1/lore/query", body, _json, cts.Token)
                .ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<LoreQueryResult>(_json, cts.Token).ConfigureAwait(false)
                   ?? new LoreQueryResult { Error = "empty reply" };
        }
        catch (Exception ex)
        {
            return new LoreQueryResult { Error = ex is OperationCanceledException ? "brain did not answer in 3 s" : ex.Message };
        }
    }

    private sealed record LoreQuery(string PersonaKey, string Words, bool Reload);
}

public sealed class LoreQueryResult
{
    public bool Ok { get; init; }
    public string Error { get; init; }
    public bool Reloaded { get; init; }
    public LoreStats Lore { get; init; }
    public PersonaInfo Persona { get; init; }
    public LoreSection[] Sections { get; init; } = [];
}

public sealed class LoreStats
{
    public string Version { get; init; }
    public int Pages { get; init; }
    public int Sections { get; init; }
    public string Error { get; init; }
}

public sealed class PersonaInfo
{
    public string Key { get; init; }
    public string Name { get; init; }
    public string Town { get; init; }
    public string[] Groups { get; init; } = [];
    public string[] Exclude { get; init; } = [];
}

public sealed class LoreSection
{
    public double Score { get; init; }
    public string Page { get; init; }
    public string Heading { get; init; }
    public string Text { get; init; }
    public bool Rumour { get; init; }
}
