using System;
using System.Collections.Generic;
using System.IO;
using ModernUO.CodeGeneratedEvents;
using Server.Avallone.AI;
using Server.Engines.Spawners;
using Server.Items;
using Server.Logging;
using Server.Mobiles;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 - the seat engine (design §3; Codex design R4 GO, 2026-11-15).
//
// What it does: ties each resident townsperson that a point spawner made to a seat, keeps the seat's identity (AI id,
// name, sex, looks) in the register outside the saves, hands that identity to the next occupant when the spawner makes
// a replacement, and at every start reconciles the register against the loaded world before anything can spawn or
// speak. What it never does: guess. Ownership that cannot be established is quarantined for an operator.
//
// Switch: avallone.townsfolk.seats (default off). Off means "this build does nothing with seats".
// NOTE (design §3.8): once a seat is adopted on live, a build WITHOUT this code must not be run against that world.
public static class Seats
{
    private static readonly ILogger logger = LogFactory.GetLogger(typeof(Seats));

    private static SeatRegister _register;
    private static readonly Dictionary<Mobile, SeatKey> _occupants = [];
    private static readonly HashSet<Mobile> _quarantined = [];
    private static readonly HashSet<Guid> _deletingSpawners = [];
    private static bool _exposed; // false until the load reconcile (or the first migration's world save) has finished
    private static bool _active;  // this world has seats (or the switch is on): reconcile and enforce

    // The switch avallone.townsfolk.seats. Off = adopt no NEW seats and never run the first migration. It does NOT stop
    // seats that already exist being enforced (design §3.8): that is what Active says.
    public static bool Enabled { get; private set; }

    public static bool Active => _active;

    // Not null = the seat system is Off for this run (design §3.4a Step 0); the text says why.
    public static string OffReason { get; private set; }

    public static bool Off => OffReason != null;

    // Off because the loaded world save carries no seat data while the register is complete: the one Off case that the
    // operator command [Seats rematch repairs (design §14.3 item 1).
    public static bool OffSavePredatesRegister { get; private set; }

    public static bool Exposed => _exposed;

    public static string RegisterPath { get; private set; }

    public static int SeatCount => _register?.Count ?? 0;

    public static void Configure()
    {
        Enabled = ServerConfiguration.GetOrUpdateSetting("avallone.townsfolk.seats", false);
        RegisterPath = Path.Combine(Core.BaseDirectory, "Avallone", "seat-register.json");
        SeatOccupants.Configure();
        EventSink.ServerStarted += AfterLoad; // always: a world that has seats is reconciled whatever the switch says
    }

    // A resident that a point spawner made (design §3.1). Travellers (escortables), hand-placed NPCs, region spawners
    // and the named NPCs have no seat. Uses the raw type test, never IsTownsperson (which asks IsQuarantined).
    public static bool IsSeatable(Mobile m) =>
        m is BaseCreature bc && TownsfolkTalk.IsTownspersonType(bc) && bc is not BaseEscortable &&
        bc.Spawner is Spawner { Deleted: false } and not RegionSpawner;

    // THE one shared eligibility decision (design §3.4a, Quarantine): may this Mobile act as an AI NPC?
    // TownsfolkTalk.IsTownsperson and AiIdentity.GetOrCreate both ask it, so every entry point inherits the answer.
    public static bool IsQuarantined(Mobile m)
    {
        // Codex code R3: A PLAYER IS NEVER QUARANTINED - decided here, before any list or binding is looked at. A damaged
        // save can bind a seat to a player; the SEAT goes to conflict, the player's own AI identity stays usable.
        if (!_active || m == null || m is PlayerMobile)
        {
            return false;
        }

        if (_quarantined.Contains(m))
        {
            return true;
        }

        // Codex code R2 #1: an NPC that carries an AI id someone else also holds is quarantined BY THAT FACT, whatever
        // route it came by (load, first migration, an operator repair that declined it, never seated at all). It is a
        // property of the identity store, not a list some paths remember to add to. A player is never quarantined.
        if (AiIdentity.HasShared && m is not PlayerMobile && AiIdentity.TryGet(m, out var carried) &&
            !AiIdentity.IsSoleHolder(m, carried))
        {
            return true;
        }

        // Off, or not yet reconciled: nothing seatable is exposed.
        return (Off || !_exposed) && (_occupants.ContainsKey(m) || IsSeatable(m));
    }

    // The same decision asked about an ID instead of a Mobile - for work that captured an id earlier: a request waiting
    // in the gateway's queue, a record about to be written (Codex code R2 #3). True when ANY NPC that holds the id is
    // quarantined; every holder is looked at, and a player holding the same id does not hide the NPC.
    public static bool IsIdQuarantined(Guid id)
    {
        if (!_active || id == Guid.Empty)
        {
            return false;
        }

        foreach (var holder in AiIdentity.Holders(id))
        {
            if (holder is not PlayerMobile && IsQuarantined(holder))
            {
                return true;
            }
        }

        return false;
    }

    public static bool TryGetSeat(Mobile m, out SeatKey key) => _occupants.TryGetValue(m, out key);

    public static bool TryGetRow(SeatKey key, out SeatRow row)
    {
        row = null;
        return _register != null && _register.TryGet(key, out row);
    }

    // What a world save records. While Off the stamp that was LOADED is written back unchanged, so a save made during
    // an Off run still says which register it belongs to.
    internal static Guid RegisterIdForSave => Off || _register == null ? SeatOccupants.Stamp.RegisterId : _register.RegisterId;

    internal static long CounterForSave => Off || _register == null ? SeatOccupants.Stamp.Counter : _register.Counter;

    internal static IEnumerable<KeyValuePair<Mobile, SeatKey>> OccupantsForSave => _occupants;

    // For the roster: every seated NPC whose seat is Occupied and who is not quarantined (design §4.1).
    public static IEnumerable<(Mobile Mobile, SeatKey Key)> OccupiedForRoster()
    {
        if (_register == null)
        {
            yield break;
        }

        foreach (var (m, key) in _occupants)
        {
            if (!m.Deleted && !_quarantined.Contains(m) && _register.TryGet(key, out var row) &&
                row.State == SeatState.Occupied)
            {
                yield return (m, key);
            }
        }
    }

    public static SeatLooks Capture(Mobile m) => new(
        m.Name, m.Female, m.Body.BodyID, m.Hue, m.HairItemID, m.HairHue, m.FacialHairItemID, m.FacialHairHue, m.SpeechHue
    );

    // Makes the Mobile the seat's person (name, sex, looks). Clothes are not kept; when the sex changes the outfit is
    // drawn again so a woman is not left in the outfit a man was created in.
    public static void ApplyLooks(Mobile m, SeatLooks looks)
    {
        if (m == null || m.Deleted || looks == null)
        {
            return;
        }

        var sexChanged = m.Female != looks.Female;

        m.Female = looks.Female;
        m.Body = looks.Body;

        if (sexChanged && m is BaseVendor v)
        {
            foreach (var item in new List<Item>(m.Items))
            {
                if (item is BaseClothing or BaseArmor or BaseWeapon)
                {
                    item.Delete();
                }
            }

            v.InitOutfit();
        }

        m.Name = looks.Name;
        m.Hue = looks.Hue;
        m.HairItemID = looks.Hair;
        m.HairHue = looks.HairHue;
        m.FacialHairItemID = looks.Beard;
        m.FacialHairHue = looks.BeardHue;
        m.SpeechHue = looks.SpeechHue;
    }

    // First migration (Codex code R1 F1): the nonce written into the occupant file by the LAST serialisation made while
    // the migration was provisional. The migration is complete only when a published save is seen to contain it.
    private static volatile string _serializedNonce;
    private static bool _migrationSaveQueued;

    // Does the published world save contain this nonce? Replaceable in tests.
    public static Func<string, bool> PublishedContains { get; set; } = DefaultPublishedContains;

    private static bool DefaultPublishedContains(string nonce)
    {
        var file = Path.Combine(World.SavePath, "AvalloneSeats", "AvalloneSeats.bin");

        if (!File.Exists(file))
        {
            return false;
        }

        var bytes = File.ReadAllBytes(file);
        return bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(nonce)) >= 0 ||
               bytes.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(nonce)) >= 0;
    }

    // Called by SeatOccupants.Serialize. While the first migration is provisional every serialisation carries a fresh
    // nonce; otherwise the field is empty.
    internal static string NonceForSave()
    {
        if (!_active || Off || _register == null || _register.MigrationComplete)
        {
            return "";
        }

        var nonce = Guid.NewGuid().ToString("N");
        _serializedNonce = nonce;
        return nonce;
    }

    private static bool Commit(string what)
    {
        if (Off)
        {
            // Codex code R1 F3: while Off the register on disk is not trusted and is NEVER written - not its rows, not
            // its bytes, not its counter. Only the operator re-match leaves Off first and then commits.
            logger.Error("Seats: refused to write the register while OFF ({What})", what);
            return false;
        }

        try
        {
            _register.Commit(RegisterPath);
            return true;
        }
        catch (Exception e)
        {
            logger.Error(e, "Seats: the register could NOT be written ({What}) - the NPC involved is quarantined", what);
            return false;
        }
    }

    private static void Quarantine(Mobile m, string why)
    {
        if (m is PlayerMobile)
        {
            // Never a player (Codex code R3). The seat that named it is in conflict; that is the record of the problem.
            logger.Warning("Seats: a seat binding names the player {Name} ({Serial}) - the seat is in conflict; the player is NOT quarantined ({Why})", m.Name, m.Serial, why);
            return;
        }

        if (m != null && _quarantined.Add(m))
        {
            logger.Warning("Seats: {Name} ({Serial}) is quarantined - {Why}", m.Name, m.Serial, why);
        }
    }

    // ---- Adopt (design §3.3). Called from BaseSpawner.Spawn after OnSpawned, in the same call that made the Mobile.
    public static void OnSpawned(BaseSpawner spawner, Mobile m)
    {
        if (!_active || Off || _register == null || !IsSeatable(m) || _occupants.ContainsKey(m))
        {
            return;
        }

        var guid = spawner.Guid;
        var kind = m.GetType().FullName;

        // Another hook gave this Mobile an AI id before adoption: never record under two ids silently.
        if (AiIdentity.TryGet(m, out var early))
        {
            var bad = new SeatRow
            {
                Key = new SeatKey(guid, kind, _register.NextSlot(guid, kind)), State = SeatState.Conflict, AiId = early,
                Looks = Capture(m), LastSerial = m.Serial.Value, Reason = "an AI id existed before adoption"
            };

            if (_register.Add(bad))
            {
                _occupants[m] = bad.Key;
                Commit("conflict at adoption");
            }

            Quarantine(m, $"it already had AI id {early} when its seat was to be adopted");
            return;
        }

        var row = _register.LowestVacant(guid, kind);

        if (row != null)
        {
            if (!AiIdentity.Rebind(m, row.AiId))
            {
                row.State = SeatState.Conflict;
                row.Reason = "the seat's AI id is held by someone else";
                _occupants[m] = row.Key;
                Commit("conflict at refill");
                Quarantine(m, $"seat {row.Key}: its AI id is held by a player or another live NPC");
                return;
            }

            ApplyLooks(m, row.Looks);
            row.State = SeatState.Occupied;
            row.LastSerial = m.Serial.Value;
            _occupants[m] = row.Key;

            if (!Commit("refill"))
            {
                Quarantine(m, "its seat could not be written");
                return;
            }

            logger.Information("Seats: seat {Seat} refilled - {Name} is back ({Serial})", row.Key, m.Name, m.Serial);
            return;
        }

        if (!Enabled)
        {
            return; // switch off: existing seats are refilled above, but no new seat is made - this NPC is unseated
        }

        row = new SeatRow
        {
            Key = new SeatKey(guid, kind, _register.NextSlot(guid, kind)), State = SeatState.Occupied,
            AiId = Guid.NewGuid(), // random, never derived from the seat (design §3.2 [v3])
            Looks = Capture(m), LastSerial = m.Serial.Value
        };

        if (!_register.Add(row) || !AiIdentity.Rebind(m, row.AiId))
        {
            Quarantine(m, "a new seat could not be made for it");
            return;
        }

        _occupants[m] = row.Key;

        if (!Commit("new seat"))
        {
            Quarantine(m, "its seat could not be written");
        }
    }

    // ---- Vacate (design §3.3): only a deletion vacates a seat.
    [OnEvent(nameof(CreatureEvents.CreatureDeletedEvent))]
    public static void OnCreatureDeleted(BaseCreature bc)
    {
        if (!_active || bc == null)
        {
            return;
        }

        _quarantined.Remove(bc);

        if (Off)
        {
            // Codex code R1 F3: local cleanup only. The row keeps saying what the untrusted register said; nothing is
            // written, so the stop that switched the system Off cannot be erased by a deletion.
            _occupants.Remove(bc);
            return;
        }

        if (!_occupants.Remove(bc, out var key))
        {
            return;
        }

        AiIdentity.Release(bc);

        if (_register == null || !_register.TryGet(key, out var row) || row.LastSerial != bc.Serial.Value)
        {
            return;
        }

        if (row.State == SeatState.Occupied)
        {
            row.State = _deletingSpawners.Contains(key.Spawner) ? SeatState.Retired : SeatState.Vacant;
            Commit("vacate");
            logger.Information("Seats: seat {Seat} is now {State} - {Name} ({Serial}) was removed", key, row.State, row.Looks?.Name, bc.Serial);
        }
    }

    // A spawner that is itself deleted takes its people with it: its seats are retired (design §3.4 case 7).
    // Called from BaseSpawner.OnDelete before it removes its spawns.
    public static void OnSpawnerDeleted(BaseSpawner spawner)
    {
        if (!_active || Off || _register == null || spawner == null)
        {
            return;
        }

        var guid = spawner.Guid;
        var any = false;

        foreach (var row in _register.Rows)
        {
            if (row.Key.Spawner == guid && row.State == SeatState.Vacant)
            {
                row.State = SeatState.Retired;
                any = true;
            }
            else if (row.Key.Spawner == guid && row.State == SeatState.Occupied)
            {
                any = true; // retired as each occupant is deleted, via _deletingSpawners
            }
        }

        if (any)
        {
            _deletingSpawners.Add(guid);
            Commit("spawner deleted");
            logger.Warning("Seats: spawner {Guid} was deleted - its seats are retired; those people are gone", guid);
        }
    }

    // CheckMorph can rewrite a vendor's name and body (design §3.3 Morph): the seat's identity is put back.
    public static void AfterMorph(Mobile m)
    {
        if (_active && !Off && _exposed && _occupants.TryGetValue(m, out var key) && _register.TryGet(key, out var row) &&
            row.State == SeatState.Occupied)
        {
            ApplyLooks(m, row.Looks);
        }
    }

    // ---- Load (design §3.4a). Runs on ServerStarted: after the world loaded, before timers, speech or spawns.
    public static void AfterLoad()
    {
        _exposed = false;
        OffReason = null;
        _occupants.Clear();
        _quarantined.Clear();
        _deletingSpawners.Clear();

        var loaded = SeatOccupants.TakeLoaded();
        _active = Enabled || File.Exists(RegisterPath) || SeatOccupants.Stamp.Present || loaded.Count > 0;

        if (!_active)
        {
            return; // seats were never used in this world and the switch is off: this build does nothing with them
        }

        _register = SeatRegister.Load(RegisterPath, out var problem);

        var step0 = SeatReconciler.Step0(_register, problem, SeatOccupants.Stamp, loaded.Count > 0);

        OffSavePredatesRegister = false;

        if (step0?.OffReason != null)
        {
            OffReason = step0.OffReason;
            OffSavePredatesRegister = step0.SavePredatesRegister;

            foreach (var (m, key) in loaded)
            {
                _occupants[m] = key; // kept, so the bindings stay in the saves and these NPCs stay quarantined
            }

            logger.Error("Seats: OFF for this run - {Reason}. Every seated townsperson is quarantined (stock trade still works). Repair: restore Avallone/seat-register.json from the snapshot that goes with this world.", OffReason);
            return;
        }

        if (step0?.NeedsFirstMigration == true)
        {
            if (!Enabled)
            {
                OffReason = "a first migration was left unfinished and the seats switch is off";
                logger.Error("Seats: OFF for this run - {Reason}", OffReason);
                return;
            }

            FirstMigration(loaded, _register); // _register: the provisional register being redone, or null
            return;
        }

        foreach (var (m, key) in loaded)
        {
            _occupants[m] = key;
        }

        ReconcileAndApply();

        if (!Commit("load reconcile"))
        {
            OffReason = "the seat register could not be written at load";
            return;
        }

        _exposed = true;

        var counts = new int[4];
        foreach (var row in _register.Rows)
        {
            counts[(int)row.State]++;
        }

        logger.Information(
            "Seats: {Seats} seats - {Occupied} occupied, {Vacant} vacant, {Conflict} in conflict, {Retired} retired; {Quarantined} NPCs quarantined",
            _register.Count, counts[0], counts[1], counts[2], counts[3], _quarantined.Count
        );
    }

    // The global checks G1-G6 and the load table (design §3.4a), run over the bindings now held and every AI id in the
    // identity store, and applied: states, conflicts, quarantines, identities. Used at load AND by the operator
    // re-match (Codex code R2 #1), so no repair path has a weaker check than a start has.
    private static void ReconcileAndApply()
    {
        var bindings = new List<SeatReconciler.Binding>();
        var bySerial = new Dictionary<uint, Mobile>();

        foreach (var (m, key) in _occupants)
        {
            if (m.Deleted)
            {
                continue;
            }

            bySerial[m.Serial.Value] = m;
            bindings.Add(new SeatReconciler.Binding(
                m.Serial.Value, m.GetType().FullName, key, AiIdentity.TryGet(m, out var id) ? id : null
            ));
        }

        var saved = new List<SeatReconciler.SavedId>();

        foreach (var (id, holder) in AiIdentity.Snapshot())
        {
            saved.Add(new SeatReconciler.SavedId(id, holder.Serial.Value, holder is PlayerMobile));
            bySerial[holder.Serial.Value] = holder;
        }

        var res = SeatReconciler.Reconcile(_register, bindings, saved);

        foreach (var (key, state) in res.NewState)
        {
            if (_register.TryGet(key, out var row))
            {
                row.State = state;
            }
        }

        foreach (var (key, why) in res.Conflicts)
        {
            if (_register.TryGet(key, out var row))
            {
                row.Reason = why;
                logger.Warning("Seats: seat {Seat} is in conflict - {Why}", key, why);
            }
        }

        foreach (var serial in res.Quarantined)
        {
            if (bySerial.TryGetValue(serial, out var m))
            {
                Quarantine(m, "its seat ownership could not be established");
            }
        }

        foreach (var (key, serial) in res.Occupant)
        {
            if (!bySerial.TryGetValue(serial, out var m) || !_register.TryGet(key, out var row))
            {
                continue;
            }

            if (!AiIdentity.Rebind(m, row.AiId))
            {
                row.State = SeatState.Conflict;
                row.Reason = "the seat's AI id is held by someone else";
                Quarantine(m, $"seat {key}: its AI id is held by a player or another live NPC");
                continue;
            }

            ApplyLooks(m, row.Looks); // the register is the authority: the world is made to match it
            row.LastSerial = serial;
        }
    }

    // ---- First migration (design §3.3 [v4]): two-phase. Rows are provisional until the world save that contains the
    // bindings has finished; nothing seatable is exposed before that. An interrupted migration is simply done again.
    private static void FirstMigration(List<(Mobile Mobile, SeatKey Key)> loaded, SeatRegister provisional)
    {
        // Redone after an interruption: the same register id and counter go on (found while re-checking Codex code R2
        // #2 - with a fresh id, a SECOND interruption would look like a foreign register and stop the system).
        _register = provisional != null ? SeatRegister.RedoOf(provisional) : new SeatRegister { MigrationComplete = false };

        var bound = new Dictionary<Mobile, SeatKey>();
        foreach (var (m, key) in loaded)
        {
            bound[m] = key;
        }

        var residents = new List<Mobile>();
        foreach (var m in World.Mobiles.Values)
        {
            if (IsSeatable(m))
            {
                residents.Add(m);
            }
        }

        residents.Sort((a, b) => a.Serial.CompareTo(b.Serial));

        // Pass 1: residents that already carry a binding (an interrupted migration's world save) keep their slot.
        // Pass 2: the rest take the next unused slot of their pool, in serial order.
        for (var pass = 1; pass <= 2; pass++)
        {
            foreach (var m in residents)
            {
                var guid = ((BaseSpawner)m.Spawner).Guid;
                var kind = m.GetType().FullName;
                var had = bound.TryGetValue(m, out var key) && key.Spawner == guid && key.Kind == kind;

                if (had != (pass == 1) || _occupants.ContainsKey(m))
                {
                    continue;
                }

                if (!had || _register.TryGet(key, out _))
                {
                    key = new SeatKey(guid, kind, _register.NextSlot(guid, kind));
                }

                // An existing id is kept (design §3.4 case 9) - but only when this NPC is its SOLE holder. An id that a
                // player or another live Mobile also holds (Codex code R1 F2) is never given to a seat: the NPC gets a
                // seat in Conflict under a fresh id it does not carry, and stays quarantined until the identity store
                // itself is repaired. Both identity maps are left exactly as they were.
                var has = AiIdentity.TryGet(m, out var existing);

                if (has && !AiIdentity.IsSoleHolder(m, existing))
                {
                    var bad = new SeatRow
                    {
                        Key = key, State = SeatState.Conflict, AiId = Guid.NewGuid(), Looks = Capture(m),
                        LastSerial = m.Serial.Value, Reason = "its saved AI id is also held by a player or another NPC"
                    };

                    if (_register.Add(bad))
                    {
                        _occupants[m] = key;
                    }

                    Quarantine(m, "its saved AI id is also held by a player or another NPC");
                    continue;
                }

                var id = has ? existing : Guid.NewGuid();
                var row = new SeatRow
                {
                    Key = key, State = SeatState.Occupied, AiId = id, Looks = Capture(m), LastSerial = m.Serial.Value
                };

                if (!_register.Add(row) || !AiIdentity.Rebind(m, id))
                {
                    Quarantine(m, "its existing AI id could not be given a seat of its own");
                    continue;
                }

                _occupants[m] = key;
            }
        }

        if (!Commit("first migration, provisional"))
        {
            OffReason = "the seat register could not be written during the first migration";
            return;
        }

        logger.Information(
            "Seats: first migration - {Count} residents bound, provisional until a world save finishes; saving now",
            _occupants.Count
        );

        QueueMigrationSave(2);
    }

    private static void QueueMigrationSave(int seconds)
    {
        if (_migrationSaveQueued)
        {
            return;
        }

        _migrationSaveQueued = true;
        Timer.StartTimer(TimeSpan.FromSeconds(seconds), () =>
        {
            _migrationSaveQueued = false;

            if (_active && !Off && _register is { MigrationComplete: false })
            {
                World.Save();
            }
        });
    }

    // Called after every finished world save (SeatOccupants.PostWorldSave).
    public static void OnWorldSaved()
    {
        if (!_active || Off || _register == null || _register.MigrationComplete)
        {
            return;
        }

        // Codex code R1 F1: this callback also runs after a world save that FAILED, and after a save that was already
        // under way before the bindings existed. The migration is complete only when the published save is seen to
        // hold the nonce of a serialisation made with the provisional bindings in it.
        var nonce = _serializedNonce;
        bool published;

        try
        {
            published = !string.IsNullOrEmpty(nonce) && PublishedContains(nonce);
        }
        catch (Exception e)
        {
            logger.Error(e, "Seats: could not read the published world save");
            published = false;
        }

        if (!published)
        {
            logger.Warning("Seats: a world save ended, but the save holding the first migration's bindings is NOT on disk - still provisional, nothing exposed; saving again in 60 s");
            QueueMigrationSave(60);
            return;
        }

        _register.MigrationComplete = true;

        if (!Commit("first migration, complete"))
        {
            _register.MigrationComplete = false;
            return;
        }

        _exposed = true;
        logger.Information("Seats: first migration complete - {Count} seats, the world save holding them is on disk", _register.Count);
    }

    // =====================================================================================================================
    // Operator acts (design §3.3 "GM edits", §3.4a "an operator command", §3.6 "verify"). Each is run on purpose by a GM,
    // says exactly what it did, and never guesses: where the evidence is not unique it does nothing for that NPC.
    // =====================================================================================================================

    public static string Status()
    {
        if (!_active)
        {
            return Enabled ? "Seats: switch on, not started yet." : "Seats: switch off and this world has no seats.";
        }

        if (Off)
        {
            return $"Seats: OFF - {OffReason}. Seated townsfolk are quarantined (stock trade works)." +
                   (OffSavePredatesRegister ? " Repair: [Seats rematch." : " Repair: restore the register file that goes with this world.");
        }

        var counts = new int[4];
        foreach (var row in _register.Rows)
        {
            counts[(int)row.State]++;
        }

        return $"Seats: {_register.Count} seats - {counts[0]} occupied, {counts[1]} vacant, {counts[2]} in conflict, {counts[3]} retired; " +
               $"{_quarantined.Count} NPCs quarantined; register counter {_register.Counter}; switch {(Enabled ? "on" : "off")}; " +
               $"{(_exposed ? "exposed" : "NOT exposed yet (first migration waiting for a world save)")}.";
    }

    // Compares the register in use with a copy taken BEFORE a restore (design §13 item 7: only such a copy can show
    // people who existed after the restored snapshot). Reports; changes nothing.
    public static List<string> Verify(string inventoryPath)
    {
        var lines = new List<string>();

        if (_register == null)
        {
            lines.Add("No register is loaded.");
            return lines;
        }

        var before = SeatRegister.Load(inventoryPath, out var problem);

        if (before == null)
        {
            lines.Add($"The copy at {inventoryPath} is {problem}.");
            return lines;
        }

        int lost = 0, changed = 0;

        foreach (var old in before.Rows)
        {
            if (!_register.TryGet(old.Key, out var now))
            {
                lost++;
                lines.Add($"LOST: seat {old.Key} - {old.Looks?.Name} (id {old.AiId.ToString()[..8]}) is in the copy and not in the register in use.");
            }
            else if (now.AiId != old.AiId || now.Looks?.Name != old.Looks?.Name || now.Looks?.Female != old.Looks?.Female)
            {
                changed++;
                lines.Add($"DIFFERENT: seat {old.Key} - was {old.Looks?.Name} (id {old.AiId.ToString()[..8]}), is {now.Looks?.Name} (id {now.AiId.ToString()[..8]}).");
            }
        }

        lines.Insert(0, $"Copy: {before.Count} seats, counter {before.Counter}. In use: {_register.Count} seats, counter {_register.Counter}. " +
                        $"{lost} lost, {changed} different." + (before.RegisterId != _register.RegisterId ? " NOT THE SAME REGISTER (ids differ)." : ""));
        return lines;
    }

    // Clears ONE seat's conflict, for the NPC the GM points at - only when that NPC is the seat's single bound NPC, is
    // the seat's kind, and can carry the seat's AI id. The seat's stored identity is put on the NPC.
    public static bool ClearConflict(Mobile m, out string report)
    {
        if (!_active || Off || _register == null || m == null || !_occupants.TryGetValue(m, out var key) ||
            !_register.TryGet(key, out var row))
        {
            report = "That NPC is not bound to a seat (or the seat system is off).";
            return false;
        }

        if (row.State != SeatState.Conflict)
        {
            report = $"Seat {key} is {row.State}, not in conflict.";
            return false;
        }

        foreach (var (other, otherKey) in _occupants)
        {
            if (other != m && !other.Deleted && otherKey == key)
            {
                report = $"Refused: {other.Name} ({other.Serial}) is also bound to seat {key}. Remove one of them first.";
                return false;
            }
        }

        if (m.GetType().FullName != key.Kind)
        {
            report = $"Refused: that NPC is a {m.GetType().Name}; the seat is for {key.Kind}.";
            return false;
        }

        // The cause must actually be gone (Codex code R1 F2): nobody else may hold the seat's id - looking at every saved
        // owner, not only the one the reverse map shows - and this NPC may not carry a different id.
        if (!AiIdentity.IsSoleHolder(m, row.AiId) || AiIdentity.TryGet(m, out var carried) && carried != row.AiId ||
            !AiIdentity.Rebind(m, row.AiId))
        {
            report = "Refused: the cause is still there - the seat's AI id is held by a player or another NPC, or this NPC carries a different id. Nothing was changed.";
            return false;
        }

        var why = row.Reason;
        ApplyLooks(m, row.Looks);
        row.State = SeatState.Occupied;
        row.Reason = null;
        row.LastSerial = m.Serial.Value;

        if (!Commit("conflict cleared by an operator"))
        {
            row.State = SeatState.Conflict;
            row.Reason = why;
            report = "Refused: the register could not be written.";
            return false;
        }

        _quarantined.Remove(m);
        report = $"Cleared: {m.Name} ({m.Serial}) now holds seat {key}. It was in conflict because: {why}.";
        logger.Warning("Seats: operator cleared the conflict on seat {Seat} for {Name} ({Serial}); it was: {Why}", key, m.Name, m.Serial, why);
        return true;
    }

    // Seats an unbound NPC AS IT IS, in a new seat of its own (it keeps an AI id it already has). It is never given a
    // waiting identity: that is what design row 5 forbids on looks, serial or saved id.
    public static bool SeatUnbound(Mobile m, out string report)
    {
        if (!_active || Off || !Enabled || _register == null || !_exposed)
        {
            report = "The seat system is off, not exposed, or its switch is off.";
            return false;
        }

        if (!IsSeatable(m) || _occupants.ContainsKey(m))
        {
            report = "That NPC cannot take a seat (not a spawned resident townsperson) or already has one.";
            return false;
        }

        var guid = ((BaseSpawner)m.Spawner).Guid;
        var kind = m.GetType().FullName;
        var has = AiIdentity.TryGet(m, out var existing);

        if (has && !AiIdentity.IsSoleHolder(m, existing))
        {
            report = "Refused: the AI id this NPC carries is also held by a player or another NPC. Nothing was changed.";
            return false;
        }

        var id = has ? existing : Guid.NewGuid();
        var row = new SeatRow
        {
            Key = new SeatKey(guid, kind, _register.NextSlot(guid, kind)), State = SeatState.Occupied, AiId = id,
            Looks = Capture(m), LastSerial = m.Serial.Value
        };

        if (!_register.Add(row))
        {
            report = "Refused: its AI id already belongs to another seat.";
            return false;
        }

        if (!AiIdentity.Rebind(m, id) || !Commit("seated by an operator"))
        {
            report = "Refused: its AI id could not be bound, or the register could not be written.";
            Quarantine(m, "an operator seating failed half way");
            return false;
        }

        _occupants[m] = row.Key;
        _quarantined.Remove(m);
        report = $"Seated: {m.Name} ({m.Serial}) in a new seat {row.Key}, as it is.";
        logger.Warning("Seats: operator seated {Name} ({Serial}) in new seat {Seat}", m.Name, m.Serial, row.Key);
        return true;
    }

    // The repair for ONE situation: a world save from before the register existed was restored (Off, design §14.3 item 1).
    // The operator asserts "this world belongs with this register". Each unbound resident is matched to a seat of its
    // own spawn point and kind ONLY when the match is unique both ways on name, sex and body, and its saved AI id (if it
    // has one) is that seat's. Everyone else is left unseated, every unmatched seat is left vacant, and both are counted.
    public static bool Rematch(out string report)
    {
        if (!_active || !Off || !OffSavePredatesRegister || _register == null)
        {
            report = "Nothing to do: this is only for a world save older than the seat register (see [Seats status).";
            return false;
        }

        var wants = new Dictionary<SeatKey, List<Mobile>>();
        var residents = 0;

        foreach (var m in World.Mobiles.Values)
        {
            if (!IsSeatable(m))
            {
                continue;
            }

            residents++;
            var guid = ((BaseSpawner)m.Spawner).Guid;
            var kind = m.GetType().FullName;
            var hasId = AiIdentity.TryGet(m, out var mine);
            SeatRow found = null;
            var many = false;

            foreach (var row in _register.Rows)
            {
                if (row.Key.Spawner != guid || row.Key.Kind != kind || row.State is SeatState.Conflict or SeatState.Retired ||
                    row.Looks == null || row.Looks.Name != m.Name || row.Looks.Female != m.Female || row.Looks.Body != m.Body.BodyID)
                {
                    continue;
                }

                // Its saved id must be this seat's, and in either case NOBODY ELSE may hold the seat's id - every saved
                // owner is looked at (Codex code R1 F2), not only the one the reverse map shows.
                if (hasId && mine != row.AiId || !AiIdentity.IsSoleHolder(m, row.AiId))
                {
                    continue;
                }

                many = found != null;
                found = row;
            }

            if (found != null && !many)
            {
                if (!wants.TryGetValue(found.Key, out var list))
                {
                    wants[found.Key] = list = [];
                }

                list.Add(m);
            }
        }

        var matched = 0;

        foreach (var (key, list) in wants)
        {
            if (list.Count != 1 || !_register.TryGet(key, out var row) || !AiIdentity.Rebind(list[0], row.AiId))
            {
                continue; // two NPCs fit one seat, or the id cannot be bound: left alone
            }

            _occupants[list[0]] = key;
            row.LastSerial = list[0].Serial.Value;
            matched++;
        }

        // Codex code R2 #1: BEFORE leaving Off, the same global checks and table a start runs. An unmatched seat whose
        // AI id someone in the world still holds (the NPC that was declined, a player) is put in CONFLICT, not made
        // vacant, and that NPC is quarantined; only a seat nobody holds becomes vacant.
        ReconcileAndApply();

        int vacant = 0, conflict = 0;

        foreach (var row in _register.Rows)
        {
            vacant += row.State == SeatState.Vacant ? 1 : 0;
            conflict += row.State == SeatState.Conflict ? 1 : 0;
        }

        OffReason = null;
        OffSavePredatesRegister = false;

        if (!Commit("re-matched by an operator"))
        {
            OffReason = "the seat register could not be written during the re-match";
            report = "Failed: the register could not be written. Still off.";
            return false;
        }

        _exposed = true;
        report = $"Re-matched {matched} of {residents} resident townsfolk to their seats. {residents - matched} are left unseated " +
                 $"(no unique match), {vacant} seats are vacant and {conflict} are in conflict ({_quarantined.Count} NPCs quarantined). " +
                 "Save the world now so this is kept.";
        logger.Warning("Seats: operator re-match - {Matched} of {Residents} matched, {Unseated} unseated, {Vacant} seats vacant", matched, residents, residents - matched, vacant);
        return true;
    }

    // A GM renames a seated NPC THROUGH the register, so the name is the seat's from now on (design §3.3 "GM edits").
    public static bool SetName(Mobile m, string name, out string report)
    {
        name = name?.Trim();

        if (!_active || Off || _register == null || m == null || !_occupants.TryGetValue(m, out var key) ||
            !_register.TryGet(key, out var row) || row.State != SeatState.Occupied)
        {
            report = "That NPC does not hold a seat.";
            return false;
        }

        if (string.IsNullOrEmpty(name) || name.Length > 40 || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[A-Za-z][A-Za-z '\-]*$"))
        {
            report = "A name is plain letters, spaces, apostrophes and hyphens, 40 characters at most.";
            return false;
        }

        var old = row.Looks;
        row.Looks = old with { Name = name };

        if (!Commit("renamed by an operator"))
        {
            row.Looks = old;
            report = "Refused: the register could not be written.";
            return false;
        }

        m.Name = name;
        report = $"Renamed: seat {key} is now {name} (was {old.Name}).";
        logger.Warning("Seats: operator renamed seat {Seat} from {Old} to {New}", key, old.Name, name);
        return true;
    }

    // ---- Tests only.
    public static void QuarantineForTests(Mobile m)
    {
        _active = true;
        _quarantined.Add(m);
    }

    public static void ReleaseForTests(Mobile m) => _quarantined.Remove(m);

    public static string SerializeForTests() => NonceForSave();

    public static void ResetForTests(bool enabled, string registerPath)
    {
        _serializedNonce = null;
        _migrationSaveQueued = false;
        PublishedContains = DefaultPublishedContains;
        Enabled = enabled;
        _active = enabled;
        RegisterPath = registerPath;
        OffReason = null;
        OffSavePredatesRegister = false;
        _register = null;
        _exposed = false;
        _occupants.Clear();
        _quarantined.Clear();
        _deletingSpawners.Clear();
        SeatOccupants.ResetForTests();
    }

    public static void LoadForTests(List<(Mobile Mobile, SeatKey Key)> bindings, SeatReconciler.SaveStamp stamp)
    {
        SeatOccupants.SetLoadedForTests(bindings, stamp);
        AfterLoad();
    }

    public static SeatReconciler.SaveStamp StampForTests() => new(RegisterIdForSave != Guid.Empty, RegisterIdForSave, CounterForSave);

    public static List<(Mobile Mobile, SeatKey Key)> BindingsForTests()
    {
        var list = new List<(Mobile, SeatKey)>();
        foreach (var (m, key) in _occupants)
        {
            if (!m.Deleted)
            {
                list.Add((m, key));
            }
        }

        return list;
    }
}

// The occupant file - INSIDE the world save ("AvalloneSeats"): Mobile -> seat, plus the register id and counter this
// save was made against (design §3.2). Never delete this type once it is in saves; retire it in place.
public class SeatOccupants : GenericPersistence
{
    private const int Version = 1; // 1: + the first-migration nonce (Codex code R1 F1)

    private static SeatOccupants _instance;
    private static List<(Mobile Mobile, SeatKey Key)> _loaded = [];

    public static SeatReconciler.SaveStamp Stamp { get; private set; } = new(false, Guid.Empty, 0);

    public SeatOccupants() : base("AvalloneSeats", 10)
    {
    }

    public static void Configure()
    {
        _instance ??= new SeatOccupants();
    }

    internal static List<(Mobile Mobile, SeatKey Key)> TakeLoaded()
    {
        var list = _loaded;
        _loaded = [];
        return list;
    }

    internal static void ResetForTests()
    {
        _loaded = [];
        Stamp = new SeatReconciler.SaveStamp(false, Guid.Empty, 0);
    }

    internal static void SetLoadedForTests(List<(Mobile Mobile, SeatKey Key)> bindings, SeatReconciler.SaveStamp stamp)
    {
        _loaded = bindings ?? [];
        Stamp = stamp;
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);
        writer.Write(Seats.RegisterIdForSave);
        writer.Write(Seats.CounterForSave);
        writer.Write(Seats.NonceForSave()); // empty unless a first migration is provisional

        var count = 0;
        foreach (var (m, _) in Seats.OccupantsForSave)
        {
            if (!m.Deleted)
            {
                count++;
            }
        }

        writer.WriteEncodedInt(count);

        foreach (var (m, key) in Seats.OccupantsForSave)
        {
            if (!m.Deleted)
            {
                writer.Write(m);
                writer.Write(key.Spawner);
                writer.Write(key.Kind);
                writer.WriteEncodedInt(key.Slot);
            }
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        var version = reader.ReadEncodedInt();

        var registerId = reader.ReadGuid();
        var counter = reader.ReadLong();

        if (version >= 1)
        {
            reader.ReadString(); // the nonce: only ever looked for in the file's bytes
        }

        // A save made while seats were never switched on carries an empty register id: that is "no seat data".
        Stamp = new SeatReconciler.SaveStamp(registerId != Guid.Empty, registerId, counter);

        var count = reader.ReadEncodedInt();
        _loaded = new List<(Mobile, SeatKey)>(count);

        for (var i = 0; i < count; i++)
        {
            var m = reader.ReadEntity<Mobile>();
            var spawner = reader.ReadGuid();
            var kind = reader.ReadString();
            var slot = reader.ReadEncodedInt();

            if (m != null)
            {
                _loaded.Add((m, new SeatKey(spawner, kind, slot)));
            }
        }
    }

    public override void PostWorldSave() => Seats.OnWorldSaved();
}
