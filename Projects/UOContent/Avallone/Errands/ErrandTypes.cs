using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Avallone.Errands;

// Errands build 1 (design v5, Projects/Avallone-Errands-Step-Design.md in the wiki; Codex "Errands-Design R5" GO WITH
// NOTES 2026-11-21). The data the errand module keeps and exchanges. Nothing here decides anything.

public enum ErrandState : byte
{
    Accepted = 0,  // agreed, not yet delivered
    Delivered = 1, // a round trip whose answer the character is carrying back
    Done = 2,
    Lapsed = 3     // deadline passed, or dropped by the player. No penalty.
}

// One part of a completion's pay (3.8). Karma uses Paid for "given". Review: the stock code threw while paying - the
// amount due is kept, nothing is retried by itself, an operator settles it.
public enum PayState : byte
{
    None = 0,
    Owed = 1,
    Paid = 2,
    Review = 3
}

// One accepted errand, owned by the shard and saved with the world (3.1). The destination, the route and the pay band
// are fixed at acceptance and never worked out again. The shard keeps no family copy: Digest is the brain's hash of the
// identity-level facts, compared on every later step.
public sealed class ErrandRow
{
    public int Id;
    public int Version;
    public string Kind;      // word, ask_after, introduce
    public string Route;     // kin, cousin, standin, contact
    public Guid Character;   // the character's own id - alts do not share errands
    public Guid Sender;
    public string SenderMap;
    public string SenderTown;
    public Guid Receiver;
    public string ReceiverMap;
    public string ReceiverTown;
    public string About;     // an off-stage relative's id (stand-in), or null
    public string LineKey;
    public string LineHash;
    public int ListsVersion;
    public string Digest;
    public string Band;      // same, near, far, none
    public ErrandState State;
    public long AcceptedAt;  // the character's errand time, seconds (3.9)
    public long DueAt;
    public string AnswerKey; // a round trip's answer, fixed when the relative gives it (3.7)
    public string AnswerHash;
    public string AnswerDigest;
    public int CoinDue;      // set once at completion, never changed
    public PayState Coin;
    public int KarmaRequested;
    public PayState Karma;
    public int KarmaGained;  // measured
    public string AccrualDay; // the Central day this completion took a place in the daily allowance, or null
    public string Evidence;  // what was seen round a pay part that went to review
    public bool HeadStart;   // introductions: this completion gave the receiver's once-ever head start
    public string SenderName; // as spoken at acceptance - for [Errands and system messages only, never for a decision
    public string ReceiverName;

    // Design v6 12.2 (Codex "Errands-Redesign" RD1): the fact digest pins who and what, never the wording. These bind
    // the SENTENCES THAT WERE SPOKEN, so a reminder is the same sentence or nothing. The shard still keeps no trade and
    // no town of its own: a seed and two hashes. Null on a row from before v6 - such a row is never repeated.
    public string AskSeed;      // the value that chose the ask's wording (the ticket id); never regenerated
    public string AskHash;      // SHA-256, lower-case hex, of the UTF-8 bytes of the ask sentence exactly as it was said
    public string DeliveryHash; // a round trip: the same, of the relative's sentence at the outward delivery

    // not saved: why the last step for this row was refused ("cannot be delivered just now")
    public string Waiting;

    public bool Open => State is ErrandState.Accepted or ErrandState.Delivered;
    public bool Unsettled => Coin is PayState.Owed or PayState.Review || Karma is PayState.Review;
}

public sealed record ErrandAnswerFix(string Key, string Hash, string Digest);

// A proof from the brain's fixed code (3.1): what it was sent, echoed; its data generation; the fact digest; and the
// code-written sentence(s) for this step. Parsed leniently - a field of the wrong shape is simply absent, and an
// absent field fails the comparison that needs it.
public sealed class ErrandProof
{
    public string Step, Kind, Route, Sender, Receiver, About, LineKey, LineHash, Digest, DataGen, Band;
    public string ReceiverMap, ReceiverTown, SenderMap, SenderTown;
    public string AskLine, YesLine, NoLine, Line;
    public int ListsVersion;
    public ErrandAnswerFix Answer;
    public bool AnswerChanged;

    // the echo
    public int? Gen, Row, Version, SpeechRev;
    public string Request, Ticket, Char, Stamp;

    public static ErrandProof Parse(JsonElement? raw, JsonElement? echoRaw = null)
    {
        if (raw is not { ValueKind: JsonValueKind.Object } o)
        {
            return null;
        }

        var p = new ErrandProof
        {
            Step = S(o, "step"), Kind = S(o, "kind"), Route = S(o, "route"), Sender = S(o, "sender"),
            Receiver = S(o, "receiver"), About = S(o, "about"), LineKey = S(o, "line_key"), LineHash = S(o, "line_hash"),
            Digest = S(o, "digest"), DataGen = S(o, "data_gen"), Band = S(o, "band"),
            ReceiverMap = S(o, "receiver_map"), ReceiverTown = S(o, "receiver_town"), SenderMap = S(o, "sender_map"),
            SenderTown = S(o, "sender_town"), AskLine = S(o, "ask_line", 400), YesLine = S(o, "yes_line", 400),
            NoLine = S(o, "no_line", 400), Line = S(o, "line", 400), ListsVersion = I(o, "lists_version") ?? 0,
            AnswerChanged = o.TryGetProperty("answer_changed", out var ac) && ac.ValueKind == JsonValueKind.True
        };

        if (o.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.Object && S(a, "key") is { } k &&
            S(a, "hash") is { Length: 64 } h && S(a, "digest") is { Length: 64 } d)
        {
            p.Answer = new ErrandAnswerFix(k, h, d);
        }

        var e = echoRaw is { ValueKind: JsonValueKind.Object } given ? given :
            o.TryGetProperty("echo", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : default;
        if (e.ValueKind == JsonValueKind.Object)
        {
            p.Gen = I(e, "gen");
            p.Row = I(e, "row");
            p.Version = I(e, "version");
            p.SpeechRev = I(e, "speech_rev");
            p.Request = S(e, "request");
            p.Ticket = S(e, "ticket");
            p.Char = S(e, "char");
            p.Stamp = S(e, "stamp");
        }

        return p;
    }

    private static string S(JsonElement o, string name, int max = 80) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: >= 1 } s &&
        s.Length <= max ? s : null;

    private static int? I(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
}

// The readiness reply (3.9): values the brain already holds in memory. Null = no usable answer.
public sealed record ErrandReady(long Probe, int Gen, bool Capable, string Boot, string DataGen, bool StampOk);

// What the errand module needs of the brain. Tests put their own in Errands.Brain.
public interface IErrandBrain
{
    Task<ErrandReady> ReadyAsync(long probe, int gen, string stamp);

    // (proof or null, the brain's data generation or null). Never throws: any failure is (null, null) and Failed = true.
    Task<(ErrandProof Proof, string DataGen, bool Failed)> ProofAsync(object body);
}

public sealed class HttpErrandBrain : IErrandBrain
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private readonly HttpClient _http;

    // A probe or a proof is given up after 5 seconds (3.9). No model is behind either endpoint.
    public HttpErrandBrain(Uri baseUri) => _http = new HttpClient { BaseAddress = baseUri, Timeout = TimeSpan.FromSeconds(5) };

    public HttpErrandBrain(HttpMessageHandler handler) =>
        _http = new HttpClient(handler) { BaseAddress = new Uri("http://brain.test/"), Timeout = TimeSpan.FromSeconds(5) };

    public async Task<ErrandReady> ReadyAsync(long probe, int gen, string stamp)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/errands/ready", new { probe, gen, stamp }, _json, CancellationToken.None);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            if (!response.IsSuccessStatusCode || r.ValueKind != JsonValueKind.Object ||
                !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return null;
            }

            return new ErrandReady(
                r.TryGetProperty("probe", out var p) && p.TryGetInt64(out var pn) ? pn : -1,
                r.TryGetProperty("gen", out var g) && g.TryGetInt32(out var gn) ? gn : -1,
                r.TryGetProperty("errands", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var cn) && cn == 1,
                r.TryGetProperty("boot", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null,
                r.TryGetProperty("data_gen", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                r.TryGetProperty("stamp_ok", out var s) && s.ValueKind == JsonValueKind.True
            );
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task<(ErrandProof Proof, string DataGen, bool Failed)> ProofAsync(object body)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("v1/errands/proof", body, _json, CancellationToken.None);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var r = doc.RootElement;
            if (!response.IsSuccessStatusCode || r.ValueKind != JsonValueKind.Object ||
                !r.TryGetProperty("ok", out var ok) || ok.ValueKind != JsonValueKind.True)
            {
                return (null, null, true);
            }

            var gen = r.TryGetProperty("data_gen", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            JsonElement? echo = r.TryGetProperty("echo", out var e) ? e.Clone() : null;
            JsonElement? proof = r.TryGetProperty("proof", out var p) ? p.Clone() : null;
            return (ErrandProof.Parse(proof, echo), gen, false);
        }
        catch (Exception)
        {
            return (null, null, true);
        }
    }
}
