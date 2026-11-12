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

    // What goes on the wire for a request (tests read it; SendAsync sends exactly this).
    internal static string BodyJson(AiRequest request, TimeSpan budget) => JsonSerializer.Serialize(Body(request, budget), _json);

    private static SidecarRequest Body(AiRequest request, TimeSpan budget) =>
        new(
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
            request.Party,
            request.Exchange,
            request.Falling,
            request.Correction
        );

    public async Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token)
    {
        var body = Body(request, budget);

        using var response = await _http.PostAsJsonAsync(request.Mode switch { "plan" => "v1/npc/plan", "summarize" => "v1/npc/summarize", "exchange" => "v1/npc/exchange", "exchange_cancel" => "v1/npc/exchange/cancel", _ => "v1/npc/respond" }, body, _json, token).ConfigureAwait(false);
        var reply = await response.Content.ReadFromJsonAsync<SidecarResponse>(_json, token).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode || reply is not { Ok: true } || string.IsNullOrWhiteSpace(reply.Say))
        {
            return AiReply.Fail(AiOutcome.Failed, reply?.Error ?? $"HTTP {(int)response.StatusCode}");
        }

        return AiReply.Success(reply.Say.Trim(), reply.Model) with
        {
            Action = reply.Action ?? "none", Rumour = reply.Rumour, MemoryCards = reply.MemoryCards, Conduct = reply.Conduct,
            Kind = reply.Kind, Topics = CleanTopics(reply.Topics), ReactSort = CleanReact(reply.Reaction),
            Correctable = CleanCorrectable(reply.Correctable), Correction = CleanCorrection(reply.Correction)
        };
    }

    // Rumour step, Part A: auxiliary metadata, like topics and the reaction. A shape that is not exactly right is "none":
    // the ordinary reply (the neutral line) is kept and nothing is recorded. Never throws.
    internal static CorrectableStory CleanCorrectable(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj || !Text(obj, "id", 40, out var id) ||
            !Text(obj, "type", 40, out var type))
        {
            return null;
        }

        return new CorrectableStory(id, type);
    }

    internal static CorrectionOffer CleanCorrection(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj || !Text(obj, "story", 40, out var story) ||
            !Text(obj, "accept", 200, out var accept) || !obj.TryGetProperty("changes", out var changes) ||
            changes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int? count = null;
        string thing = null, place = null;
        foreach (var change in changes.EnumerateObject())
        {
            switch (change.Name)
            {
                case "count" when change.Value.ValueKind == JsonValueKind.Number && change.Value.TryGetInt32(out var n):
                    count = n;
                    break;
                case "thing" when change.Value.ValueKind == JsonValueKind.String && change.Value.GetString() is { Length: >= 1 and <= 40 } t:
                    thing = t;
                    break;
                case "place" when change.Value.ValueKind == JsonValueKind.String && change.Value.GetString() is { Length: >= 1 and <= 40 } p:
                    place = p;
                    break;
                default:
                    return null; // an unknown field, or a known one of the wrong type: the whole candidate is refused
            }
        }

        return count == null && thing == null && place == null ? null : new CorrectionOffer(story, count, thing, place, accept);
    }

    private static bool Text(JsonElement obj, string name, int max, out string value)
    {
        value = obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        return value is { Length: >= 1 } && value.Length <= max;
    }

    // Rumour step C1: auxiliary metadata, like topics. Only {"sort": 0..5} counts; anything else is "none". Never throws,
    // never loses `say`.
    internal static int? CleanReact(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty("sort", out var sort) ||
            sort.ValueKind != JsonValueKind.Number || !sort.TryGetInt32(out var n) || n < 0 || n >= Exchange.ReactSorts)
        {
            return null;
        }

        return n;
    }

    // Stage A3 step 2 (Codex design R2): auxiliary metadata. Anything that is not a list is "not offered" (null); inside
    // a list only short lowercase ids are kept, without duplicates, at most two. Never throws, never loses `say`.
    internal static string[] CleanTopics(JsonElement? raw)
    {
        if (raw is not { ValueKind: JsonValueKind.Array } list)
        {
            return null;
        }

        var kept = new System.Collections.Generic.List<string>(2);
        foreach (var x in list.EnumerateArray())
        {
            if (kept.Count >= 2)
            {
                break;
            }

            if (x.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var id = x.GetString();
            if (id is { Length: >= 1 and <= 16 } && !kept.Contains(id) && IsId(id))
            {
                kept.Add(id);
            }
        }

        return kept.ToArray();
    }

    private static bool IsId(string s)
    {
        foreach (var ch in s)
        {
            if (ch is < 'a' or > 'z')
            {
                return false;
            }
        }

        return true;
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
        TravelParty.PartyAsk Party,
        ExchangeAsk Exchange,
        string Falling,
        // Rumour step, Part A: left out entirely unless this line uses up a correction ticket
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        CorrectionAsk Correction = null
    );

    private sealed record SidecarResponse(
        bool Ok, string Say, string Action, string Model, string Error, RumourInfo Rumour, string[] MemoryCards, string Conduct,
        string Kind, JsonElement? Topics = null, JsonElement? Reaction = null, JsonElement? Correctable = null,
        JsonElement? Correction = null
    );
}
