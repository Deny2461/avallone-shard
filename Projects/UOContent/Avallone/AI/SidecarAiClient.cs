using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Avallone.AI;

// Talks to the "brain" sidecar on localhost. Every await uses ConfigureAwait(false): nothing here
// touches game state, so HTTP and JSON work stays off the game thread.
public sealed class SidecarAiClient : IAiNpcClient
{
    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _http;

    public SidecarAiClient(Uri baseUri)
    {
        // The gateway owns the timeout through the cancellation token.
        _http = new HttpClient { BaseAddress = baseUri, Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    public string Name => $"sidecar {_http.BaseAddress}";

    public async Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
    {
        var body = new SidecarRequest(
            request.RequestId,
            request.NpcId,
            request.SubjectId,
            request.Priority.ToString(),
            request.PersonaKey,
            request.Persona,
            request.Utterance,
            request.History,
            (int)budget.TotalMilliseconds,
            request.Situation,
            request.Actions,
            request.PersonaVars,
            request.Secrets,
            request.RevealSecretId,
            request.Rumours,
            request.Named,
            request.Standing,
            request.Party
        );

        using var response = await _http.PostAsJsonAsync(request.Mode switch { "plan" => "v1/npc/plan", "summarize" => "v1/npc/summarize", _ => "v1/npc/respond" }, body, _json, token).ConfigureAwait(false);
        var reply = await response.Content.ReadFromJsonAsync<SidecarResponse>(_json, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || reply is not { Ok: true } || string.IsNullOrWhiteSpace(reply.Say))
        {
            return AiReply.Fail(AiOutcome.Failed, reply?.Error ?? $"HTTP {(int)response.StatusCode}");
        }

        return AiReply.Success(reply.Say.Trim(), reply.Model) with
        {
            Action = reply.Action ?? "none", Rumour = reply.Rumour, MemoryCards = reply.MemoryCards, Conduct = reply.Conduct,
            Kind = reply.Kind
        };
    }

    public async Task WarmAsync(CancellationToken token)
    {
        using var response = await _http.PostAsync("v1/warm", null, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private sealed record SidecarRequest(
        Guid RequestId,
        Guid NpcId,
        Guid SubjectId,
        string Priority,
        string PersonaKey,
        string Persona,
        string Utterance,
        AiLine[] History,
        int BudgetMs,
        string Situation,
        string[] Actions,
        System.Collections.Generic.Dictionary<string, string> PersonaVars,
        AiRequest.SecretOffer[] Secrets,
        string RevealSecretId,
        RumourAsk Rumours,
        NamedAsk Named,
        StandingAsk Standing,
        TravelParty.PartyAsk Party
    );

    private sealed record SidecarResponse(
        bool Ok, string Say, string Action, string Model, string Error, RumourInfo Rumour, string[] MemoryCards, string Conduct,
        string Kind
    );
}
