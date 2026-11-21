using System;
using System.Collections.Generic;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 - the load reconcile of design §3.4a [v4], as a pure function so every case is a named test.
//
// The ONLY proof that a Mobile owns a seat is its binding in the occupant file - the entry saved in the same world save
// as the Mobile. A saved AI id is used only as a consistency check that can REFUSE, never as a way to claim a seat.
// All global checks (G1-G6) are evaluated before any row is changed; a failure quarantines every row and Mobile
// involved, and nothing is applied to them.
public static class SeatReconciler
{
    // One loaded Mobile that has a binding. SavedAiId is the id the world save holds for it, if any.
    public sealed record Binding(uint Serial, string Kind, SeatKey Seat, Guid? SavedAiId);

    // Every AI id the world save holds, with its holder.
    public sealed record SavedId(Guid Id, uint Holder, bool IsPlayer);

    // What the loaded world save recorded about the register it was saved against. Present = the save carries the
    // occupant file at all (a save older than the first migration does not).
    public sealed record SaveStamp(bool Present, Guid RegisterId, long Counter);

    public sealed class Result
    {
        public string OffReason { get; init; }                       // not null: the seat system is Off for this run
        public bool SavePredatesRegister { get; init; }              // the one Off case the operator re-match repairs
        public bool NeedsFirstMigration { get; init; }
        public Dictionary<SeatKey, SeatState> NewState { get; } = [];  // rows whose state changes
        public Dictionary<SeatKey, uint> Occupant { get; } = [];       // rows that stand occupied -> their Mobile
        public Dictionary<SeatKey, string> Conflicts { get; } = [];    // rows put in (or kept in) Conflict -> why
        public HashSet<uint> Quarantined { get; } = [];                // Mobiles that may not act as AI NPCs
    }

    // Step 0 (§3.4a): is this register the one the world was saved against?
    public static Result Step0(SeatRegister register, string loadProblem, SaveStamp stamp, bool anyBindings)
    {
        if (register == null)
        {
            if (loadProblem == "missing" && !stamp.Present && !anyBindings)
            {
                return new Result { NeedsFirstMigration = true };
            }

            return new Result { OffReason = $"the seat register is {loadProblem} while the world save holds seat data" };
        }

        // Codex code R2 #2: the identity and rollback checks come FIRST, for every readable register. An
        // "incomplete migration" flag can itself come from a restored older file; it is never a reason to skip them, and
        // never authorises overwriting that file's inventory.
        if (stamp.Present && stamp.RegisterId != register.RegisterId)
        {
            return new Result { OffReason = "the seat register is not the one this world save was made against" };
        }

        if (stamp.Present && register.Counter < stamp.Counter)
        {
            return new Result { OffReason = "the seat register is older than this world save (it was rolled back)" };
        }

        if (!register.MigrationComplete)
        {
            // A CONSISTENT interrupted first migration: either the save carries no seat data (interrupted before its
            // world save was published) or it carries this very register at or below its counter (published, the
            // completion marker not written). Nothing was exposed; do it again.
            return new Result { NeedsFirstMigration = true };
        }

        if (!stamp.Present)
        {
            // Build finding, stricter than design row 5 for this one whole-world case: a world save that predates the
            // register would leave EVERY resident unbound while the register still holds their identities.
            return new Result
            {
                OffReason = "this world save is older than the seat register (it carries no seat data)",
                SavePredatesRegister = true
            };
        }

        if (stamp.RegisterId != register.RegisterId)
        {
            return new Result { OffReason = "the seat register is not the one this world save was made against" };
        }

        if (register.Counter < stamp.Counter)
        {
            return new Result { OffReason = "the seat register is older than this world save (it was rolled back)" };
        }

        return null; // proceed to the checks and the table
    }

    public static Result Reconcile(
        SeatRegister register, IReadOnlyList<Binding> bindings, IReadOnlyList<SavedId> savedIds
    )
    {
        var result = new Result();
        var bySeat = new Dictionary<SeatKey, List<Binding>>();

        foreach (var b in bindings)
        {
            if (!bySeat.TryGetValue(b.Seat, out var list))
            {
                bySeat[b.Seat] = list = [];
            }

            list.Add(b);
        }

        var badRows = new Dictionary<SeatKey, string>();
        var badMobiles = new HashSet<uint>();

        void Bad(SeatKey key, string why)
        {
            if (register.TryGet(key, out _) && !badRows.ContainsKey(key))
            {
                badRows[key] = why;
            }

            if (bySeat.TryGetValue(key, out var claimants))
            {
                foreach (var c in claimants)
                {
                    badMobiles.Add(c.Serial);
                }
            }
        }

        // G6 - a row already in Conflict stays there whatever the world now shows.
        foreach (var row in register.Rows)
        {
            if (row.State == SeatState.Conflict)
            {
                Bad(row.Key, row.Reason ?? "already in conflict");
            }
        }

        foreach (var (key, claimants) in bySeat)
        {
            // G2 (second half) - a binding that names a row the register does not hold.
            if (!register.TryGet(key, out var row))
            {
                foreach (var c in claimants)
                {
                    badMobiles.Add(c.Serial);
                }

                continue;
            }

            // G1 - two or more bound Mobiles for one row.
            if (claimants.Count > 1)
            {
                Bad(key, "G1: more than one loaded NPC is bound to this seat");
            }

            foreach (var c in claimants)
            {
                // G2 - the bound Mobile is not the row's kind.
                if (c.Kind != key.Kind)
                {
                    Bad(key, $"G2: the bound NPC is a {c.Kind}, the seat is for a {key.Kind}");
                }

                // G3 - the bound Mobile's saved AI id is not its row's AI id.
                if (c.SavedAiId is { } saved && saved != row.AiId)
                {
                    Bad(key, "G3: the bound NPC's saved AI id is not this seat's");

                    // ... and if that id is another row's, G4 below quarantines that row as well.
                }
            }
        }

        // G4 - a row's AI id is held in the world save by anyone other than that row's (single) bound Mobile.
        foreach (var s in savedIds)
        {
            if (!register.TryGetByAiId(s.Id, out var row))
            {
                continue;
            }

            var owner = bySeat.TryGetValue(row.Key, out var claimants) && claimants.Count == 1 ? claimants[0].Serial : 0u;

            if (s.IsPlayer || s.Holder != owner)
            {
                Bad(row.Key, s.IsPlayer
                    ? "G4: this seat's AI id is held by a player character"
                    : "G4: this seat's AI id is held by an NPC that is not bound to it");

                if (!s.IsPlayer)
                {
                    badMobiles.Add(s.Holder); // a player is never quarantined: only the seat is
                }
            }
        }

        // G5 (two rows with one AI id) cannot exist in a SeatRegister: Add and Load both refuse it.

        foreach (var (key, why) in badRows)
        {
            result.Conflicts[key] = why;

            if (register.TryGet(key, out var row) && row.State != SeatState.Conflict)
            {
                result.NewState[key] = SeatState.Conflict;
            }
        }

        foreach (var serial in badMobiles)
        {
            if (serial != 0)
            {
                result.Quarantined.Add(serial);
            }
        }

        // Step 3 - the table, only for rows and Mobiles that passed every check.
        foreach (var row in register.Rows)
        {
            if (badRows.ContainsKey(row.Key))
            {
                continue;
            }

            bySeat.TryGetValue(row.Key, out var claimants);
            var bound = claimants is { Count: 1 } && !badMobiles.Contains(claimants[0].Serial) ? claimants[0] : null;

            if (bound == null && claimants is { Count: > 0 })
            {
                // A claimant that failed a check elsewhere: the row is not empty, so it is never declared vacant.
                result.NewState[row.Key] = SeatState.Conflict;
                result.Conflicts[row.Key] = "its bound NPC failed a consistency check";
                continue;
            }

            switch (row.State)
            {
                case SeatState.Occupied when bound != null: // row 1 - stands (incl. an earlier incarnation)
                    {
                        result.Occupant[row.Key] = bound.Serial;
                        break;
                    }
                case SeatState.Occupied: // row 2 - no loaded occupant: vacant, identity kept whole
                    {
                        result.NewState[row.Key] = SeatState.Vacant;
                        break;
                    }
                case SeatState.Vacant when bound != null: // row 3 - an older save restores the previous occupant
                    {
                        result.NewState[row.Key] = SeatState.Occupied;
                        result.Occupant[row.Key] = bound.Serial;
                        break;
                    }
                case SeatState.Retired when bound != null: // row 4 - never un-retired or deleted automatically
                    {
                        result.NewState[row.Key] = SeatState.Conflict;
                        result.Conflicts[row.Key] = "row 4: a retired seat has a loaded NPC bound to it";
                        result.Quarantined.Add(bound.Serial);
                        break;
                    }
            }
        }

        // Row 5 - a seatable Mobile with no binding - needs no entry here: it is simply not seated, is never matched to
        // a row, and (if its saved AI id is a row's) G4 has already quarantined it and that row.
        return result;
    }
}
