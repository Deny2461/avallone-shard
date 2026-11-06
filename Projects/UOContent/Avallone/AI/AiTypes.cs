using System;
using System.Threading;
using System.Threading.Tasks;

namespace Server.Avallone.AI;

// Lower value = served first. Companions > face-to-face talk > enemy chatter > background work.
public enum AiPriority
{
    Companion = 0,
    Conversation = 1,
    Enemy = 2,
    Background = 3
}

public enum AiOutcome
{
    Ok,
    Disabled,    // kill switch is off
    BreakerOpen, // too many recent failures, not sending for a while
    QueueFull,   // no room, and nothing lower-priority to push out
    Busy,        // this NPC is already working on a reply for this subject
    Expired,     // waited in the queue past its deadline, never sent
    Evicted,     // pushed out of the queue by higher-priority work
    Timeout,     // sent, but no answer within the timeout
    Failed,      // sent, and the sidecar/model returned an error
    Quiet        // inside the daily quiet window (another scheduled job's run) - not a failure
}

// Phase 5b. Town = the NPC's region name (brain accepts real towns only); Pt = played seconds; Mode = news | follow | greet;
// Roster = current player-character names for the brain's reply check only (never put in a prompt).
// About: mode "about" only - the AI id of the player the line named ("" when that player has no AI identity yet).
public sealed record RumourAsk(
    string Town, long Pt, string Mode, string Follow, string[] Exclude, RumourRosterEntry[] Roster, string About = "",
    string[] Recent = null,  // heard in the last avallone.rumours.recentBlockMinutes: never retold on "what news"
    bool Ambiguous = false,  // "about" only: the line named two people, or a first name several share - ask which one
    // 5c-2 2c (design M7.6): "greet" sent WITHOUT the shard's 1-in-4 roll - the brain makes the one draw (it knows the
    // NPC's mood). False = the shard rolled (avallone.rumours.greetInBrain false, e.g. paired with a pre-2c brain).
    bool GreetUnrolled = false
);

public sealed record RumourRosterEntry(string Id, string Name);

// Phase 5c: a named NPC's request - its key, the household's day, the speaker's name (summaries scrub it) and, for a
// summary, the roster of real player names to scrub.
// 5c-2 2a: Pt / Mpt = the played clock and the memory clock at this request (or, for a summary, at the end of the
// visit); Mpt < 0 = no memory clock (an old shard): the brain then uses the 5c-1 recall.
public sealed record NamedAsk(string Key, string Today, string Speaker, RumourRosterEntry[] Roster, long Pt = 0, double Mpt = -1);

// 5c-2 2b (relationship dials): what the NPC can see of this player right now - karma (trust), fame (respect) - and the
// ambience around the NPC: game hour and moon phase ("now", never a clock events age by - Codex 2b review), its rumour town.
public sealed record StandingAsk(int Karma, int Fame, int Hour, string Moon, string Town, long Pt, double Mpt, string Likes)
{
    // Codex 2b code review F1: the shard epoch this standing belongs to; the brain reads the ledger only if it reconciled.
    public long Epoch { get; init; }
}

// What the brain offered: the one sentinel (or empty), whose character id, the descriptor if they are gone, rumour ids.
public sealed record RumourInfo(string Sentinel, string CharId, string Descriptor, string[] Ids, string Mode);

// One line of a conversation, oldest first. Speaker is "player" or "npc".
public readonly record struct AiLine(string Speaker, string Text);

public sealed record AiRequest(
    Guid NpcId,
    Guid SubjectId,
    string Persona,
    string Utterance,
    AiPriority Priority
)
{
    public Guid RequestId { get; } = Guid.NewGuid();

    // "plan" = a wanderer choosing its next goal alone (brain /v1/npc/plan); null = conversation (/v1/npc/respond).
    public string Mode { get; init; }

    // Names a persona file in the sidecar (brain/personas/<key>.txt). When set, Persona may be null.
    public string PersonaKey { get; init; }

    // Fills a template persona (brain/personas/townsfolk.md): name, role, town, trade. Plain short words only.
    public System.Collections.Generic.Dictionary<string, string> PersonaVars { get; init; }

    // Recent lines between this NPC and this subject, oldest first.
    public AiLine[] History { get; init; } = [];

    // Facts the server owns and the model should know right now (who is speaking, trust, recent events).
    public string Situation { get; init; }

    // Actions the model may pick from this turn; null or empty means talk only. The NPC re-validates the
    // pick before doing anything - the model choosing an action is a request, never permission.
    public string[] Actions { get; init; }

    // Phase 4b: each of this companion's secrets for this character - id, tier, state (closed/open/pending/revealed).
    // Never text; the brain holds that. Null for everyone else.
    public sealed record SecretOffer(string Id, int Tier, string State);

    public SecretOffer[] Secrets { get; init; }

    // Phase 4b stage 2: the shard has approved this one secret; the brain may now put its text in the prompt.
    public string RevealSecretId { get; init; }

    // Longer than the gateway default for a reveal (a few sentences, not one line).
    public TimeSpan? Timeout { get; init; }

    // Phase 5b: may this line carry live town gossip, and what the brain needs to pick and check it. Null = no.
    public RumourAsk Rumours { get; init; }

    // Phase 5c: set only for a named NPC (memory + familiarity); null keeps today's behaviour.
    public NamedAsk Named { get; init; }

    // 5c-2 2b: set on conversation requests to dial-eligible NPCs (Relations.Standing); null = no dials.
    public StandingAsk Standing { get; init; }

    // One conversation lease per NPC + subject: a second request while one is pending is refused.
    public string LeaseKey => $"{NpcId:N}:{SubjectId:N}";
}

public readonly record struct AiReply(AiOutcome Outcome, string Text, string Model, long ElapsedMs, string Error)
{
    public bool Ok => Outcome == AiOutcome.Ok;

    // The action the model picked from AiRequest.Actions ("none" if it picked nothing or none were offered).
    public string Action { get; init; }

    // Phase 5b: set when gossip was offered for this reply (the sentinel to fill, whose it is, which rumours).
    public RumourInfo Rumour { get; init; }

    // 5c-2 2a: the named NPC's memory card ids offered in this reply (ids only); recorded once it is really said.
    public string[] MemoryCards { get; init; }

    // 5c-2 2b (B4): "hateful_language" when the player's line tripped the narrow slur list (category only, no text).
    public string Conduct { get; init; }

    public static AiReply Success(string text, string model) => new(AiOutcome.Ok, text, model, 0, null);

    public static AiReply Fail(AiOutcome outcome, string error = null) => new(outcome, null, null, 0, error);
}

// The only thing NPC code talks to. Phase 0-1 use the sidecar; a direct-Ollama client (or a test fake)
// can be swapped in without touching NPC code.
// Implementations must not touch game state: they may run off the game thread.
public interface IAiNpcClient
{
    string Name { get; }

    Task<AiReply> SendAsync(AiRequest request, TimeSpan budget, CancellationToken token);

    // Ask the backend to load the model now, so the next real request is fast.
    Task WarmAsync(CancellationToken token);
}
