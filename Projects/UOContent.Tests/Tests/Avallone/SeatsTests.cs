using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Server;
using Server.Avallone.AI;
using Server.Avallone.Townsfolk;
using Server.Engines.Spawners;
using Server.Mobiles;
using Xunit;

namespace UOContent.Tests;

// Townsfolk step, Stage 1 - the identity lock (Projects/Avallone-Townsfolk-Step-Design.md §3; Codex design R4 GO).
// One named test per rule: the register, Step 0, the global checks G1-G6, every row of the load table, and the engine
// (adopt, vacate, refill, quarantine, first migration). The build checklist is design §13 items 1, 2 and 6.
[Collection("Sequential UOContent Tests")]
public class SeatsTests : IDisposable
{
    private const string TailorKind = "Server.Mobiles.Tailor";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "seats-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<IEntity> _made = [];

    private string RegisterFile => Path.Combine(_dir, "seat-register.json");

    public SeatsTests()
    {
        Directory.CreateDirectory(_dir);
        Seats.ResetForTests(true, RegisterFile);
    }

    public void Dispose()
    {
        foreach (var e in _made)
        {
            if (!e.Deleted)
            {
                e.Delete();
            }
        }

        Seats.ResetForTests(false, null);

        try
        {
            Directory.Delete(_dir, true);
        }
        catch
        {
            // a leftover temp folder is harmless
        }
    }

    // ---------- helpers

    private static SeatLooks Looks(string name, bool female = false) => new(name, female, female ? 0x191 : 0x190, 1002, 0x203B, 1102, 0, 0, 52);

    private static SeatRow Row(Guid spawner, int slot, SeatState state, Guid id, string name = "Garrick", uint serial = 0) => new()
    {
        Key = new SeatKey(spawner, TailorKind, slot), State = state, AiId = id, Looks = Looks(name), LastSerial = serial
    };

    private static SeatRegister Register(params SeatRow[] rows)
    {
        var reg = new SeatRegister { MigrationComplete = true };

        foreach (var r in rows)
        {
            Assert.True(reg.Add(r));
        }

        return reg;
    }

    private static SeatReconciler.Binding Bound(uint serial, SeatKey seat, Guid? savedId = null, string kind = TailorKind) =>
        new(serial, kind, seat, savedId);

    private Spawner NewSpawner()
    {
        var sp = new Spawner(3, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), 0, default, "Tailor");
        sp.MoveToWorld(new Point3D(1496, 1629, 0), Map.Trammel);
        _made.Add(sp);
        return sp;
    }

    // A tailor as a spawner would have just made it: placed, linked to the spawner, then offered for adoption.
    private Tailor SpawnTailor(Spawner sp, bool adopt = true)
    {
        var t = new Tailor();
        t.MoveToWorld(new Point3D(1496, 1629, 0), Map.Trammel);
        t.Spawner = sp;
        _made.Add(t);

        if (adopt)
        {
            Seats.OnSpawned(sp, t);
        }

        return t;
    }

    private PlayerMobile NewPlayer(string name)
    {
        var m = new PlayerMobile { Name = name };
        m.DefaultMobileInit();
        m.MoveToWorld(new Point3D(1495, 1629, 0), Map.Trammel);
        _made.Add(m);
        return m;
    }

    // Starts the engine on an empty, finished register, as after a first migration with no residents of ours.
    private void StartWithEmptyRegister()
    {
        var reg = new SeatRegister { MigrationComplete = true };
        reg.Commit(RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(true, reg.RegisterId, reg.Counter));
        Assert.False(Seats.Off, Seats.OffReason);
        Assert.True(Seats.Exposed);
    }

    // "Restart": what the next start would load - the bindings as a world save holds them, and the save's stamp.
    private void Restart(List<(Mobile, SeatKey)> bindings = null, SeatReconciler.SaveStamp stamp = null)
    {
        bindings ??= Seats.BindingsForTests();
        stamp ??= Seats.StampForTests();
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, stamp);
    }

    // ---------- the register

    [Fact]
    public void Register_RefusesTwoRowsWithOneAiId_AndARepeatedSeat() // G5
    {
        var sp = Guid.NewGuid();
        var id = Guid.NewGuid();
        var reg = Register(Row(sp, 0, SeatState.Occupied, id));

        Assert.False(reg.Add(Row(sp, 1, SeatState.Occupied, id)));                  // same AI id
        Assert.False(reg.Add(Row(sp, 0, SeatState.Occupied, Guid.NewGuid())));      // same seat
        Assert.False(reg.Add(Row(sp, 2, SeatState.Occupied, Guid.Empty)));          // no id
        Assert.Equal(1, reg.Count);
    }

    [Fact]
    public void Register_SlotsAreNeverRenumbered_LowestVacantIsByNumber()
    {
        var sp = Guid.NewGuid();
        var reg = Register(
            Row(sp, 0, SeatState.Vacant, Guid.NewGuid()),
            Row(sp, 1, SeatState.Occupied, Guid.NewGuid()),
            Row(sp, 2, SeatState.Vacant, Guid.NewGuid()),
            Row(sp, 3, SeatState.Retired, Guid.NewGuid())
        );

        Assert.Equal(0, reg.LowestVacant(sp, TailorKind).Key.Slot);
        Assert.Equal(4, reg.NextSlot(sp, TailorKind));                       // past the retired slot too
        Assert.Null(reg.LowestVacant(sp, "Server.Mobiles.Weaver"));          // another kind is another pool
        Assert.Equal(0, reg.NextSlot(Guid.NewGuid(), TailorKind));           // another spawner is another pool
    }

    [Fact]
    public void Register_CommitThenLoad_RoundTrips_AndTheCounterRises()
    {
        var sp = Guid.NewGuid();
        var id = Guid.NewGuid();
        var reg = Register(Row(sp, 0, SeatState.Occupied, id, "Mady", 77));
        reg.Commit(RegisterFile);
        reg.Commit(RegisterFile);

        var loaded = SeatRegister.Load(RegisterFile, out var problem);

        Assert.Null(problem);
        Assert.Equal(reg.RegisterId, loaded.RegisterId);
        Assert.Equal(2, loaded.Counter);
        Assert.True(loaded.MigrationComplete);
        Assert.True(loaded.TryGetByAiId(id, out var row));
        Assert.Equal("Mady", row.Looks.Name);
        Assert.Equal(77u, row.LastSerial);
        Assert.False(File.Exists(RegisterFile + ".tmp"));
    }

    [Fact]
    public void Register_Load_MissingAndDamagedFilesAreReported_NeverGuessed()
    {
        Assert.Null(SeatRegister.Load(RegisterFile, out var problem));
        Assert.Equal("missing", problem);

        File.WriteAllText(RegisterFile, "{ not json");
        Assert.Null(SeatRegister.Load(RegisterFile, out problem));
        Assert.StartsWith("unreadable", problem);

        // G5 on load: a file edited so that two rows share one AI id is refused whole.
        var sp = Guid.NewGuid();
        var reg = Register(Row(sp, 0, SeatState.Occupied, Guid.NewGuid()), Row(sp, 1, SeatState.Occupied, Guid.NewGuid()));
        reg.Commit(RegisterFile);
        var first = reg.Rows.First().AiId.ToString();
        var second = reg.Rows.Last().AiId.ToString();
        File.WriteAllText(RegisterFile, File.ReadAllText(RegisterFile).Replace(second, first));
        Assert.Null(SeatRegister.Load(RegisterFile, out problem));
        Assert.Contains("repeats", problem);
    }

    // ---------- Step 0

    [Fact]
    public void Step0_NoRegisterAndNoSeatDataInTheSave_IsAFirstMigration()
    {
        var r = SeatReconciler.Step0(null, "missing", new SeatReconciler.SaveStamp(false, Guid.Empty, 0), false);
        Assert.True(r.NeedsFirstMigration);
        Assert.Null(r.OffReason);
    }

    [Fact]
    public void Step0_RegisterMissingWhileTheSaveHoldsSeatData_IsOff()
    {
        var stamp = new SeatReconciler.SaveStamp(true, Guid.NewGuid(), 5);
        Assert.NotNull(SeatReconciler.Step0(null, "missing", stamp, true).OffReason);
        Assert.NotNull(SeatReconciler.Step0(null, "unreadable (JsonException)", stamp, false).OffReason);
        Assert.NotNull(SeatReconciler.Step0(null, "missing", new SeatReconciler.SaveStamp(false, Guid.Empty, 0), true).OffReason);
    }

    [Fact]
    public void Step0_ForeignRegister_IsOff()
    {
        var reg = Register();
        Assert.Contains("not the one", SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, Guid.NewGuid(), 0), false).OffReason);
    }

    [Fact]
    public void Step0_RegisterOlderThanTheLoadedSave_IsOff_ButEqualOrNewerProceeds()
    {
        var reg = Register();
        reg.Commit(RegisterFile); // counter 1

        Assert.Contains("rolled back", SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 2), false).OffReason);
        Assert.Null(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 1), false));
        Assert.Null(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 0), false)); // register newer: normal after a restore
    }

    [Fact]
    public void Step0_ProvisionalRegister_MigratesAgain()
    {
        var reg = new SeatRegister { MigrationComplete = false };
        Assert.True(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(false, Guid.Empty, 0), false).NeedsFirstMigration);
    }

    [Fact]
    public void Step0_AWorldSaveOlderThanTheRegister_IsOff()
    {
        // Build finding (design §14): a save from before the first migration would leave every resident unbound while
        // the register still holds their identities. Stricter than design row 5 for this one whole-world case.
        var reg = Register(Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid()));
        Assert.Contains("older than the seat register", SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(false, Guid.Empty, 0), false).OffReason);
    }

    // ---------- the load table

    [Fact]
    public void Row1_OccupiedWithItsOneBoundNpc_Stands_AlsoForAnEarlierIncarnation()
    {
        var sp = Guid.NewGuid();
        var id = Guid.NewGuid();
        var row = Row(sp, 0, SeatState.Occupied, id, serial: 900);
        var reg = Register(row);

        // The loaded NPC has another serial than the register last saw (an older save's incarnation): same person.
        var r = SeatReconciler.Reconcile(reg, [Bound(123, row.Key, id)], [new SeatReconciler.SavedId(id, 123, false)]);

        Assert.Empty(r.NewState);
        Assert.Empty(r.Conflicts);
        Assert.Empty(r.Quarantined);
        Assert.Equal(123u, r.Occupant[row.Key]);
    }

    [Fact]
    public void Row2_OccupiedWithNoLoadedNpc_BecomesVacant_IdentityKept() // R2-D1: the crash-before-save case
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid(), "Mady", 900);
        var reg = Register(row);

        var r = SeatReconciler.Reconcile(reg, [], []);

        Assert.Equal(SeatState.Vacant, r.NewState[row.Key]);
        Assert.Empty(r.Occupant);
        Assert.Empty(r.Conflicts);
        Assert.Equal("Mady", row.Looks.Name); // nothing about the person is touched
    }

    [Fact]
    public void Row3_VacantWithABoundNpc_BecomesOccupied()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Vacant, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [Bound(55, row.Key)], []);

        Assert.Equal(SeatState.Occupied, r.NewState[row.Key]);
        Assert.Equal(55u, r.Occupant[row.Key]);
    }

    [Fact]
    public void Row4_RetiredWithABoundNpc_IsConflict_NothingDeleted()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Retired, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [Bound(55, row.Key)], []);

        Assert.Equal(SeatState.Conflict, r.NewState[row.Key]);
        Assert.Contains(55u, r.Quarantined);
        Assert.Empty(r.Occupant);
    }

    // ---------- the global checks

    [Fact]
    public void G1_TwoNpcsBoundToOneSeat_QuarantinesBothAndTheRow()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [Bound(10, row.Key), Bound(11, row.Key)], []);

        Assert.Equal(SeatState.Conflict, r.NewState[row.Key]);
        Assert.StartsWith("G1", r.Conflicts[row.Key]);
        Assert.Contains(10u, r.Quarantined);
        Assert.Contains(11u, r.Quarantined);
        Assert.Empty(r.Occupant); // never "the last one enumerated"
    }

    [Fact]
    public void G2_BoundNpcOfAnotherKind_IsConflict()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [Bound(10, row.Key, null, "Server.Mobiles.Weaver")], []);

        Assert.StartsWith("G2", r.Conflicts[row.Key]);
        Assert.Contains(10u, r.Quarantined);
        Assert.Empty(r.Occupant);
    }

    [Fact]
    public void G2_ABindingToASeatTheRegisterDoesNotHold_QuarantinesTheNpc()
    {
        var known = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid());
        var unknown = new SeatKey(Guid.NewGuid(), TailorKind, 0);
        var r = SeatReconciler.Reconcile(Register(known), [Bound(10, unknown)], []);

        Assert.Contains(10u, r.Quarantined);
        Assert.Equal(SeatState.Vacant, r.NewState[known.Key]); // the known row simply has no occupant
    }

    [Fact]
    public void G3_R3Fixture_BoundToSeatA_WithSeatBsSavedId_QuarantinesBothRowsAndTheNpc_NothingApplied()
    {
        // Codex R3-D1: by v3's rule this NPC "proved" A by its binding and B by its saved id.
        var sp = Guid.NewGuid();
        var a = Row(sp, 0, SeatState.Occupied, Guid.NewGuid(), "Anna");
        var b = Row(sp, 1, SeatState.Occupied, Guid.NewGuid(), "Bryn");
        var reg = Register(a, b);

        var r = SeatReconciler.Reconcile(reg, [Bound(10, a.Key, b.AiId)], [new SeatReconciler.SavedId(b.AiId, 10, false)]);

        Assert.StartsWith("G3", r.Conflicts[a.Key]);
        Assert.StartsWith("G4", r.Conflicts[b.Key]);
        Assert.Equal(SeatState.Conflict, r.NewState[a.Key]);
        Assert.Equal(SeatState.Conflict, r.NewState[b.Key]);
        Assert.Contains(10u, r.Quarantined);
        Assert.Empty(r.Occupant); // neither row is applied; no row order decides anything
    }

    [Fact]
    public void G4_ASeatsAiIdHeldByAnUnboundNpc_QuarantinesTheRowAndThatNpc()
    {
        // A world save from before that NPC was bound: it carries the seat's id but no binding. It never gets the seat
        // on that evidence (design row 5), and the seat is not handed to a newcomer either.
        var row = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [], [new SeatReconciler.SavedId(row.AiId, 42, false)]);

        Assert.StartsWith("G4", r.Conflicts[row.Key]);
        Assert.Equal(SeatState.Conflict, r.NewState[row.Key]); // not Vacant: nobody new may take this identity
        Assert.Contains(42u, r.Quarantined);
    }

    [Fact]
    public void G4_ASeatsAiIdHeldByAPlayer_IsConflict()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Occupied, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(row), [Bound(10, row.Key)], [new SeatReconciler.SavedId(row.AiId, 7, true)]);

        Assert.Contains("player", r.Conflicts[row.Key]);
        Assert.Contains(10u, r.Quarantined);
        Assert.DoesNotContain(7u, r.Quarantined); // the player keeps its own AI identity
        Assert.Empty(r.Occupant);
    }

    [Fact]
    public void G6_AConflictRowStaysConflict_EvenWhenTheWorldNowLooksClean()
    {
        var row = Row(Guid.NewGuid(), 0, SeatState.Conflict, Guid.NewGuid());
        row.Reason = "G1: more than one loaded NPC is bound to this seat";
        var r = SeatReconciler.Reconcile(Register(row), [Bound(10, row.Key, row.AiId)], [new SeatReconciler.SavedId(row.AiId, 10, false)]);

        Assert.False(r.NewState.ContainsKey(row.Key)); // still Conflict
        Assert.True(r.Conflicts.ContainsKey(row.Key));
        Assert.Contains(10u, r.Quarantined);
        Assert.Empty(r.Occupant);
    }

    [Fact]
    public void Checks_AProblemOnOneSeat_LeavesTheRestOfTheWorldAlone()
    {
        var sp = Guid.NewGuid();
        var good = Row(sp, 0, SeatState.Occupied, Guid.NewGuid());
        var bad = Row(sp, 1, SeatState.Occupied, Guid.NewGuid());
        var r = SeatReconciler.Reconcile(Register(good, bad), [Bound(10, good.Key), Bound(11, bad.Key), Bound(12, bad.Key)], []);

        Assert.Equal(10u, r.Occupant[good.Key]);
        Assert.DoesNotContain(10u, r.Quarantined);
        Assert.Equal(SeatState.Conflict, r.NewState[bad.Key]);
    }

    // ---------- the engine

    [Fact]
    public void Adopt_ANewSpawn_GetsASeat_ARandomId_AndIsRecordedBeforeItIsExposed()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp);

        Assert.True(Seats.TryGetSeat(t, out var key));
        Assert.Equal(new SeatKey(sp.Guid, TailorKind, 0), key);
        Assert.True(Seats.TryGetRow(key, out var row));
        Assert.Equal(SeatState.Occupied, row.State);
        Assert.Equal(t.Name, row.Looks.Name);
        Assert.Equal(t.Female, row.Looks.Female);
        Assert.True(AiIdentity.TryGet(t, out var id));
        Assert.Equal(row.AiId, id);
        Assert.True(TownsfolkTalk.IsTownsperson(t));

        // On disk already: a crash right now would not lose the person.
        var onDisk = SeatRegister.Load(RegisterFile, out _);
        Assert.True(onDisk.TryGetByAiId(id, out var saved));
        Assert.Equal(t.Name, saved.Looks.Name);
    }

    [Fact]
    public void Replace_TheNextSpawnOfThatKind_IsTheSamePerson() // "the place keeps the person"
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var first = SpawnTailor(sp);
        Seats.TryGetSeat(first, out var key);
        Seats.TryGetRow(key, out var row);
        var id = row.AiId;
        var name = first.Name;
        var female = first.Female;
        var hue = first.Hue;

        first.Delete();
        Assert.Equal(SeatState.Vacant, row.State);
        Assert.False(AiIdentity.TryGet(first, out _));

        var second = SpawnTailor(sp);

        Assert.True(Seats.TryGetSeat(second, out var key2));
        Assert.Equal(key, key2);
        Assert.Equal(SeatState.Occupied, row.State);
        Assert.True(AiIdentity.TryGet(second, out var id2));
        Assert.Equal(id, id2);
        Assert.Equal(name, second.Name);
        Assert.Equal(female, second.Female);
        Assert.Equal(hue, second.Hue);
        Assert.Equal(female ? 0x191 : 0x190, second.Body.BodyID);
    }

    [Fact]
    public void Replace_WithTwoOfAKind_TheSurvivorKeepsItsSlot_TheNewcomerTakesTheEmptyOne() // TF-D1 case 1
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        var b = SpawnTailor(sp);
        Seats.TryGetSeat(a, out var keyA);
        Seats.TryGetSeat(b, out var keyB);
        Assert.Equal(0, keyA.Slot);
        Assert.Equal(1, keyB.Slot);
        AiIdentity.TryGet(a, out var idA);
        AiIdentity.TryGet(b, out var idB);

        a.Delete();
        var c = SpawnTailor(sp);

        Assert.True(Seats.TryGetSeat(b, out var keyBAfter));
        Assert.Equal(keyB, keyBAfter);                       // the survivor was not renumbered
        AiIdentity.TryGet(b, out var idBAfter);
        Assert.Equal(idB, idBAfter);                         // ... and did not become the missing person
        Seats.TryGetSeat(c, out var keyC);
        Assert.Equal(keyA, keyC);
        AiIdentity.TryGet(c, out var idC);
        Assert.Equal(idA, idC);
    }

    [Fact]
    public void Vacate_OnlyADeletionVacates_AnExtraSpawnWhileTheOccupantLivesIsANewPerson() // TF-D1 case 5
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        AiIdentity.TryGet(a, out var idA);

        a.Spawner = null; // detached from the spawner's list, still alive
        var b = SpawnTailor(sp);

        AiIdentity.TryGet(b, out var idB);
        Assert.NotEqual(idA, idB);
        Seats.TryGetSeat(b, out var keyB);
        Assert.Equal(1, keyB.Slot);
        Assert.True(Seats.TryGetSeat(a, out var keyA));
        Assert.Equal(0, keyA.Slot);
    }

    [Fact]
    public void Adopt_RefusesWhenTheNpcAlreadyHasAnAiId_Quarantined_NotRecordedUnderTwoIds() // TF-D1 case 10
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp, adopt: false);
        var early = AiIdentity.GetOrCreate(t); // another hook ran first

        Seats.OnSpawned(sp, t);

        Assert.True(Seats.IsQuarantined(t));
        Assert.False(TownsfolkTalk.IsTownsperson(t));
        Assert.True(Seats.TryGetSeat(t, out var key));
        Assert.True(Seats.TryGetRow(key, out var row));
        Assert.Equal(SeatState.Conflict, row.State);
        Assert.Equal(early, row.AiId);
        Assert.Equal(Guid.Empty, AiIdentity.GetOrCreate(t)); // no id is used while it is quarantined
    }

    [Fact]
    public void Rebind_NeverTakesAPlayersId_NorALiveNpcs()
    {
        StartWithEmptyRegister();
        var p = NewPlayer("Seatless");
        var playerId = AiIdentity.GetOrCreate(p);
        var sp = NewSpawner();
        var t = SpawnTailor(sp, adopt: false);
        var other = SpawnTailor(sp, adopt: false);
        var otherId = AiIdentity.GetOrCreate(other);

        Assert.False(AiIdentity.Rebind(t, playerId));
        Assert.False(AiIdentity.Rebind(t, otherId));
        Assert.False(AiIdentity.TryGet(t, out _));
        Assert.True(AiIdentity.TryGetPlayer(playerId, out var still));
        Assert.Same(p, still);

        other.Delete();
        Assert.True(AiIdentity.Rebind(t, otherId)); // a deleted NPC's id can pass on
        Assert.False(AiIdentity.Rebind(t, Guid.NewGuid())); // but an NPC never swaps the id it carries
    }

    [Fact]
    public void Quarantine_WhileOff_NoAiTalkAndNoAiId_ForEverySeatedNpc()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp);
        Assert.True(TownsfolkTalk.IsTownsperson(t));

        // Next start: the register is gone but the world save holds seat data -> Off.
        var bindings = Seats.BindingsForTests();
        var stamp = Seats.StampForTests();
        File.Delete(RegisterFile);
        Restart(bindings, stamp);

        Assert.True(Seats.Off);
        Assert.True(Seats.IsQuarantined(t));
        Assert.False(TownsfolkTalk.IsTownsperson(t));
        Assert.True(TownsfolkTalk.IsTownspersonType(t)); // still a tailor; stock trade is untouched
        Assert.False(File.Exists(RegisterFile));          // Off writes nothing and adopts nothing

        var t2 = SpawnTailor(sp);
        Assert.False(Seats.TryGetSeat(t2, out _));
        Assert.True(Seats.IsQuarantined(t2));

        // A save made during the Off run still names the register it belongs to.
        Assert.Equal(stamp.RegisterId, Seats.StampForTests().RegisterId);
    }

    [Fact]
    public void Load_CrashAfterAdoptionAndBeforeASave_ThePersonComesBackWhole() // R1/R2 counterexample, end to end
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var stampAtLastSave = Seats.StampForTests(); // the last world save: no tailor yet
        var lost = SpawnTailor(sp);
        AiIdentity.TryGet(lost, out var id);
        var name = lost.Name;
        var female = lost.Female;

        // Crash: the tailor made after the save is not in the loaded world, and no deletion event ever ran.
        _made.Remove(lost);
        AiIdentity.Release(lost);
        Restart([], stampAtLastSave);

        Assert.False(Seats.Off, Seats.OffReason);
        Assert.True(Seats.TryGetRow(new SeatKey(sp.Guid, TailorKind, 0), out var row));
        Assert.Equal(SeatState.Vacant, row.State); // row 2

        var back = SpawnTailor(sp);

        Assert.True(AiIdentity.TryGet(back, out var id2));
        Assert.Equal(id, id2);
        Assert.Equal(name, back.Name);
        Assert.Equal(female, back.Female);
        Assert.Equal(0, row.Key.Slot);
        Assert.Equal(SeatState.Occupied, row.State);
        lost.Delete();
    }

    [Fact]
    public void Load_TheRegisterIsTheAuthority_ARenamedNpcIsPutBack()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp);
        var name = t.Name;

        t.Name = "Somebody Else"; // a direct property edit, or an older save's looks
        Restart();

        Assert.False(Seats.Off, Seats.OffReason);
        Assert.Equal(name, t.Name);
        Assert.True(TownsfolkTalk.IsTownsperson(t));
    }

    [Fact]
    public void Morph_AfterAMapChangeTheSeatsIdentityIsPutBack()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp);
        var name = t.Name;

        t.Name = "Morphed";
        Seats.AfterMorph(t);

        Assert.Equal(name, t.Name);
    }

    [Fact]
    public void SwitchOff_ExistingSeatsAreStillRefilled_ButNoNewSeatIsMade() // design §3.8
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        AiIdentity.TryGet(a, out var idA);
        var bindings = Seats.BindingsForTests();
        var stamp = Seats.StampForTests();

        Seats.ResetForTests(false, RegisterFile); // the switch is off ...
        Seats.LoadForTests(bindings, stamp);      // ... but this world has seats

        Assert.True(Seats.Active);
        Assert.False(Seats.Enabled);
        Assert.False(Seats.Off, Seats.OffReason);

        a.Delete();
        var b = SpawnTailor(sp);
        AiIdentity.TryGet(b, out var idB);
        Assert.Equal(idA, idB); // enforced: the same person

        var c = SpawnTailor(sp);
        Assert.False(Seats.TryGetSeat(c, out _)); // no new seat while the switch is off
    }

    [Fact]
    public void SpawnerDeleted_ItsSeatsAreRetired_NotRefilled()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        Seats.TryGetSeat(a, out var key);
        Seats.TryGetRow(key, out var row);

        Seats.OnSpawnerDeleted(sp);
        a.Delete();

        Assert.Equal(SeatState.Retired, row.State);
        Assert.Null(SeatRegister.Load(RegisterFile, out _).LowestVacant(sp.Guid, TailorKind));
    }

    [Fact]
    public void FirstMigration_IsProvisionalUntilAWorldSaveFinishes_NothingExposedBefore_KeepsExistingIds()
    {
        Seats.ResetForTests(false, RegisterFile); // before this step existed: no seats, ids made on first talk
        var sp = NewSpawner();
        var known = SpawnTailor(sp, adopt: false);
        var legacyId = AiIdentity.GetOrCreate(known); // an NPC that was spoken to before this step
        var fresh = SpawnTailor(sp, adopt: false);

        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(false, Guid.Empty, 0)); // no register, no seat data

        // Phase 1: bound and written, but provisional - and nobody seatable is exposed.
        Assert.False(Seats.Off, Seats.OffReason);
        Assert.False(Seats.Exposed);
        Assert.True(Seats.IsQuarantined(known));
        Assert.False(TownsfolkTalk.IsTownsperson(fresh));
        Assert.False(SeatRegister.Load(RegisterFile, out _).MigrationComplete);
        Assert.True(Seats.TryGetSeat(known, out var k1));
        Assert.True(Seats.TryGetSeat(fresh, out var k2));
        Assert.NotEqual(k1, k2);

        // Interrupted here: the next start finds a provisional register and migrates again - same ids, same slots.
        var bindings = Seats.BindingsForTests();
        Restart(bindings, Seats.StampForTests());
        Assert.False(Seats.Exposed);
        Assert.True(Seats.TryGetSeat(known, out var k1Again));
        Assert.Equal(k1, k1Again);
        Assert.True(AiIdentity.TryGet(known, out var idAgain));
        Assert.Equal(legacyId, idAgain); // a remembered id is never swapped

        // Phase 2: the world save holding the bindings has finished AND is seen on disk (Codex code R1 F1).
        var nonce = Seats.SerializeForTests();
        Seats.PublishedContains = n => n == nonce;
        Seats.OnWorldSaved();

        Assert.True(Seats.Exposed);
        Assert.True(TownsfolkTalk.IsTownsperson(known));
        Assert.True(TownsfolkTalk.IsTownsperson(fresh));
        Assert.True(SeatRegister.Load(RegisterFile, out _).MigrationComplete);
        Assert.True(Seats.TryGetRow(k1, out var row));
        Assert.Equal(legacyId, row.AiId);
    }

    [Fact]
    public void RowFive_AnUnboundNpcIsNeverMatchedToAWaitingSeat() // design §13 item 6
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        var name = a.Name;
        var female = a.Female;
        var hue = a.Hue;
        AiIdentity.TryGet(a, out var idA);
        a.Delete(); // seat 0 is vacant, holding a whole identity

        // A look-alike with no binding (same name, sex and looks), as an older world might hold.
        var twin = SpawnTailor(sp, adopt: false);
        twin.Name = name;
        twin.Female = female;
        twin.Hue = hue;

        Restart([], Seats.StampForTests());

        Assert.False(Seats.TryGetSeat(twin, out _));          // not seated on the strength of its looks
        Assert.False(AiIdentity.TryGet(twin, out var idTwin) && idTwin == idA);
        Assert.True(Seats.TryGetRow(new SeatKey(sp.Guid, TailorKind, 0), out var row));
        Assert.Equal(SeatState.Vacant, row.State);            // the identity is still waiting, untouched
    }

    // ---------- the operator's commands

    [Fact]
    public void Operator_ClearConflict_OnlyForTheSeatsOneBoundNpc_AndItSaysWhy()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp, adopt: false);
        AiIdentity.GetOrCreate(t);
        Seats.OnSpawned(sp, t); // conflict: it had an id before adoption
        Assert.True(Seats.IsQuarantined(t));

        var stranger = SpawnTailor(sp, adopt: false);
        Assert.False(Seats.ClearConflict(stranger, out var no));
        Assert.Contains("not bound", no);

        Assert.True(Seats.ClearConflict(t, out var report));
        Assert.Contains("an AI id existed before adoption", report);
        Assert.False(Seats.IsQuarantined(t));
        Assert.True(TownsfolkTalk.IsTownsperson(t));
        Seats.TryGetSeat(t, out var key);
        Assert.Equal(SeatState.Occupied, SeatRegister.Load(RegisterFile, out _).TryGet(key, out var row) ? row.State : SeatState.Conflict);

        Assert.False(Seats.ClearConflict(t, out var again)); // nothing left to clear
        Assert.Contains("not in conflict", again);
    }

    [Fact]
    public void Operator_SeatUnbound_GivesANewSeat_NeverAWaitingIdentity()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var gone = SpawnTailor(sp);
        AiIdentity.TryGet(gone, out var waitingId);
        gone.Delete(); // seat 0 is vacant and holds a whole person

        Seats.ResetForTests(false, RegisterFile); // an NPC spawned while the switch was off: unseated
        var loose = SpawnTailor(sp);
        Assert.False(Seats.TryGetSeat(loose, out _));
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], Seats.StampForTests() with { Present = true, RegisterId = SeatRegister.Load(RegisterFile, out _).RegisterId, Counter = 0 });

        Assert.True(Seats.SeatUnbound(loose, out var report), report);

        Assert.True(Seats.TryGetSeat(loose, out var key));
        Assert.Equal(1, key.Slot);                                 // a new slot, not the vacant slot 0
        Assert.True(AiIdentity.TryGet(loose, out var id));
        Assert.NotEqual(waitingId, id);
        Assert.True(Seats.TryGetRow(new SeatKey(sp.Guid, TailorKind, 0), out var waiting));
        Assert.Equal(SeatState.Vacant, waiting.State);             // still waiting for its own person
        Assert.False(Seats.SeatUnbound(loose, out _));             // it has a seat now
    }

    [Fact]
    public void Operator_Rematch_AfterAnOlderSave_MatchesOnlyWhereItIsUniqueBothWays()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var mady = SpawnTailor(sp, adopt: false);
        mady.Name = "Mady";
        var garrick = SpawnTailor(sp, adopt: false);
        garrick.Name = "Garrick";
        garrick.Female = !mady.Female;
        garrick.Body = garrick.Female ? 0x191 : 0x190;
        var twinA = SpawnTailor(sp, adopt: false);
        twinA.Name = "Twin";
        var twinB = SpawnTailor(sp, adopt: false);
        twinB.Name = "Twin";
        twinB.Female = twinA.Female;
        twinB.Body = twinA.Body;

        foreach (var t in new[] { mady, garrick, twinA, twinB })
        {
            Seats.OnSpawned(sp, t);
        }

        AiIdentity.TryGet(mady, out var madyId);
        AiIdentity.TryGet(garrick, out var garrickId);

        // An older world save is restored: the same NPCs, with no seat data and (never spoken to then) no AI ids.
        foreach (var t in new[] { mady, garrick, twinA, twinB })
        {
            AiIdentity.Release(t);
        }

        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(false, Guid.Empty, 0));

        Assert.True(Seats.Off);
        Assert.True(Seats.OffSavePredatesRegister);
        Assert.Contains("rematch", Seats.Status());
        Assert.False(TownsfolkTalk.IsTownsperson(mady)); // stopped, not silently made new people

        Assert.True(Seats.Rematch(out var report), report);

        Assert.False(Seats.Off);
        Assert.True(AiIdentity.TryGet(mady, out var madyAgain));
        Assert.Equal(madyId, madyAgain);
        Assert.True(AiIdentity.TryGet(garrick, out var garrickAgain));
        Assert.Equal(garrickId, garrickAgain);
        Assert.True(TownsfolkTalk.IsTownsperson(mady));

        // The twins fit each other's seats: neither is matched, and their two seats wait, vacant.
        Assert.False(Seats.TryGetSeat(twinA, out _));
        Assert.False(Seats.TryGetSeat(twinB, out _));
        Assert.Contains("2 are left unseated", report);
        Assert.Contains("2 seats are vacant", report);

        Assert.False(Seats.Rematch(out _)); // only for that one situation
    }

    [Fact]
    public void Operator_SetName_GoesThroughTheRegister_SoItSurvivesARestart()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var t = SpawnTailor(sp);

        Assert.False(Seats.SetName(t, "Bad\nName", out _));
        Assert.True(Seats.SetName(t, "Elowen", out var report), report);
        Assert.Equal("Elowen", t.Name);

        t.Name = "Edited Directly"; // a plain property edit is not the seat's name
        Restart();
        Assert.Equal("Elowen", t.Name);
    }

    [Fact]
    public void Operator_Verify_ShowsPeopleWhoExistedOnlyAfterTheRestoredCopy()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var early = SpawnTailor(sp);
        early.Name = "Early";
        Seats.SetName(early, "Early", out _);
        var copyAtSnapshot = RegisterFile + ".snapshot";
        File.Copy(RegisterFile, copyAtSnapshot);

        var late = SpawnTailor(sp); // adopted after the snapshot
        Seats.SetName(late, "Late", out _);
        var copyBeforeRestore = RegisterFile + ".before-restore";
        File.Copy(RegisterFile, copyBeforeRestore);

        // The register is put back to the snapshot (the stated limit: Step 0 cannot see this). The verify step, run
        // against the copy taken BEFORE the restore, is what shows who was lost.
        File.Copy(copyAtSnapshot, RegisterFile, true);
        Restart(Seats.BindingsForTests().Where(b => b.Mobile == early).ToList(), new SeatReconciler.SaveStamp(true, SeatRegister.Load(RegisterFile, out _).RegisterId, 0));
        Assert.False(Seats.Off, Seats.OffReason);

        var lines = Seats.Verify(copyBeforeRestore);

        Assert.Contains("1 lost", lines[0]);
        Assert.Contains(lines, l => l.StartsWith("LOST") && l.Contains("Late"));
        Assert.DoesNotContain(lines, l => l.Contains("Early") && l.StartsWith("LOST"));
        Assert.StartsWith("The copy at", Seats.Verify(RegisterFile + ".nope")[0]);
    }

    // =====================================================================================================================
    // Codex code R1 (Admin/Review-2026-11-15-Codex-Avallone-Townsfolk-Stage1-Code-R1.md) - one group per finding.
    // =====================================================================================================================

    // Two unseated tailors on one spawner and a first migration under way (provisional, nothing exposed).
    private (Tailor A, Tailor B) BeginFirstMigration()
    {
        Seats.ResetForTests(false, RegisterFile);
        var sp = NewSpawner();
        var a = SpawnTailor(sp, adopt: false);
        var b = SpawnTailor(sp, adopt: false);
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(false, Guid.Empty, 0));
        Assert.False(Seats.Exposed);
        return (a, b);
    }

    // ---------- F1: the first migration completes only on a PUBLISHED save that holds its bindings

    [Fact]
    public void F1_AWorldSaveThatFailed_LeavesTheMigrationProvisional_ARetryThatSucceedsCompletesIt()
    {
        var (a, _) = BeginFirstMigration();
        var published = new HashSet<string>();
        Seats.PublishedContains = published.Contains;

        var first = Seats.SerializeForTests(); // the bindings were serialised ...
        Seats.OnWorldSaved();                  // ... but the snapshot was never published (PostWorldSave still runs)

        Assert.False(Seats.Exposed);
        Assert.True(Seats.IsQuarantined(a));
        Assert.False(SeatRegister.Load(RegisterFile, out _).MigrationComplete);

        var second = Seats.SerializeForTests(); // the retry
        published.Add(second);
        Seats.OnWorldSaved();

        Assert.NotEqual(first, second);
        Assert.True(Seats.Exposed);
        Assert.True(TownsfolkTalk.IsTownsperson(a));
        Assert.True(SeatRegister.Load(RegisterFile, out _).MigrationComplete);
    }

    [Fact]
    public void F1_ASaveAlreadyUnderWayBeforeTheBindingsExisted_DoesNotComplete_NorDoesAnEarlierSerialisation()
    {
        BeginFirstMigration();
        Seats.PublishedContains = _ => true; // some save was published - but nothing with our bindings was serialised

        Seats.OnWorldSaved();
        Assert.False(Seats.Exposed);

        // Serialised twice; only the FIRST snapshot reached the disk. The latest serialisation is the one that counts.
        var published = new HashSet<string>();
        Seats.PublishedContains = published.Contains;
        published.Add(Seats.SerializeForTests());
        Seats.SerializeForTests();
        Seats.OnWorldSaved();
        Assert.False(Seats.Exposed);
        Assert.False(SeatRegister.Load(RegisterFile, out _).MigrationComplete);
    }

    [Fact]
    public void F1_AnUnreadablePublishedSave_IsNotCompletion()
    {
        BeginFirstMigration();
        Seats.SerializeForTests();
        Seats.PublishedContains = _ => throw new IOException("disk gone");

        Seats.OnWorldSaved();

        Assert.False(Seats.Exposed);
        Assert.False(Seats.Off); // provisional, and it will try again - not a failure of the seat system
    }

    [Fact]
    public void F1_OutsideAProvisionalMigration_NoNonceIsWritten()
    {
        StartWithEmptyRegister();
        Assert.Equal("", Seats.SerializeForTests());
    }

    // ---------- F2: an AI id with more than one saved owner, in BOTH load orders

    // The world save holds one id for a player and for an NPC. npcLast = the NPC is read last, so the reverse map
    // shows the NPC and hides the player (the order Codex's probe used).
    private Guid ShareAnId(PlayerMobile player, Mobile npc, bool npcLast)
    {
        var id = Guid.NewGuid();
        AiIdentity.Release(player);
        AiIdentity.Release(npc);

        if (npcLast)
        {
            AiIdentity.LoadForTests((player, id), (npc, id));
        }
        else
        {
            AiIdentity.LoadForTests((npc, id), (player, id));
        }

        return id;
    }

    private static void AssertPlayerKeepsItsIdentity(PlayerMobile player, Guid id)
    {
        Assert.True(AiIdentity.TryGet(player, out var held));
        Assert.Equal(id, held);
        Assert.Equal(id, AiIdentity.GetOrCreate(player)); // still usable
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F2_RebindAndIsSoleHolder_SeeEverySavedOwner(bool npcLast)
    {
        StartWithEmptyRegister();
        var player = NewPlayer("Shared");
        var sp = NewSpawner();
        var npc = SpawnTailor(sp, adopt: false);
        var id = ShareAnId(player, npc, npcLast);

        Assert.False(AiIdentity.IsSoleHolder(npc, id));
        Assert.False(AiIdentity.Rebind(npc, id));
        AssertPlayerKeepsItsIdentity(player, id);
        Assert.True(AiIdentity.TryGet(npc, out var still) && still == id); // both maps left as they were
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F2_FirstMigration_AnNpcSharingAnIdWithAPlayerIsQuarantined_NeverSeatedUnderThatId(bool npcLast)
    {
        Seats.ResetForTests(false, RegisterFile);
        var player = NewPlayer("Shared");
        var sp = NewSpawner();
        var npc = SpawnTailor(sp, adopt: false);
        var clean = SpawnTailor(sp, adopt: false);
        var id = ShareAnId(player, npc, npcLast);

        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(false, Guid.Empty, 0));
        var nonce = Seats.SerializeForTests();
        Seats.PublishedContains = n => n == nonce;
        Seats.OnWorldSaved();

        Assert.True(Seats.Exposed);
        Assert.True(Seats.IsQuarantined(npc));
        Assert.False(TownsfolkTalk.IsTownsperson(npc));
        Assert.True(TownsfolkTalk.IsTownsperson(clean)); // the rest of the town is unaffected
        Assert.True(Seats.TryGetSeat(npc, out var key));
        Assert.True(Seats.TryGetRow(key, out var row));
        Assert.Equal(SeatState.Conflict, row.State);
        Assert.NotEqual(id, row.AiId); // the seat never holds the player's id
        AssertPlayerKeepsItsIdentity(player, id);

        // Interrupted and done again: the same outcome.
        Restart();
        Assert.True(Seats.IsQuarantined(npc));
        AssertPlayerKeepsItsIdentity(player, id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F2_OperatorClear_IsRefusedWhileTheCauseIsStillThere(bool npcLast)
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var npc = SpawnTailor(sp);
        var player = NewPlayer("Shared");
        var id = ShareAnId(player, npc, npcLast); // a later world save holds the seat's id for a player too

        // Make the seat hold that id, as a register written before the clash would.
        var bindings = Seats.BindingsForTests();
        var reg = SeatRegister.Load(RegisterFile, out _);
        var old = reg.Rows.Single();
        var forged = new SeatRegister { MigrationComplete = true };
        forged.Add(new SeatRow { Key = old.Key, State = SeatState.Occupied, AiId = id, Looks = old.Looks, LastSerial = old.LastSerial });
        forged.Commit(RegisterFile);
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, new SeatReconciler.SaveStamp(true, forged.RegisterId, 0));

        Assert.False(Seats.Off, Seats.OffReason);
        Assert.True(Seats.IsQuarantined(npc)); // G4 at load: a player holds the seat's id
        Assert.True(Seats.TryGetRow(old.Key, out var row));
        Assert.Equal(SeatState.Conflict, row.State);

        Assert.False(Seats.ClearConflict(npc, out var report));
        Assert.Contains("cause is still there", report);
        Assert.Equal(SeatState.Conflict, row.State);
        Assert.True(Seats.IsQuarantined(npc));
        AssertPlayerKeepsItsIdentity(player, id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F2_OperatorSeat_RefusesAnNpcWhoseIdIsShared(bool npcLast)
    {
        StartWithEmptyRegister();
        Seats.ResetForTests(false, RegisterFile);
        var sp = NewSpawner();
        var npc = SpawnTailor(sp); // unseated: the switch was off
        var player = NewPlayer("Shared");
        var id = ShareAnId(player, npc, npcLast);
        Seats.ResetForTests(true, RegisterFile);
        var reg = SeatRegister.Load(RegisterFile, out _);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(true, reg.RegisterId, 0));

        Assert.False(Seats.SeatUnbound(npc, out var report));
        Assert.Contains("also held", report);
        Assert.False(Seats.TryGetSeat(npc, out _));
        AssertPlayerKeepsItsIdentity(player, id);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void F2_OperatorRematch_DoesNotMatchASeatWhoseIdAPlayerAlsoHolds(bool npcLast)
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var mady = SpawnTailor(sp, adopt: false);
        mady.Name = "Mady";
        var garrick = SpawnTailor(sp, adopt: false);
        garrick.Name = "Garrick";
        Seats.OnSpawned(sp, mady);
        Seats.OnSpawned(sp, garrick);
        AiIdentity.TryGet(garrick, out var garrickId);
        AiIdentity.TryGet(mady, out var madyId);

        // An older save is restored. In it Mady's seat id is ALSO a player's (and, in one order, hers too).
        var player = NewPlayer("Shared");
        AiIdentity.Release(garrick);
        AiIdentity.Release(mady);
        AiIdentity.Release(player);
        if (npcLast)
        {
            AiIdentity.LoadForTests((player, madyId), (mady, madyId));
        }
        else
        {
            AiIdentity.LoadForTests((mady, madyId), (player, madyId));
        }

        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests([], new SeatReconciler.SaveStamp(false, Guid.Empty, 0));
        Assert.True(Seats.OffSavePredatesRegister);

        Assert.True(Seats.Rematch(out var report), report);

        Assert.True(AiIdentity.TryGet(garrick, out var g) && g == garrickId); // the clean one is matched
        Assert.True(Seats.TryGetSeat(garrick, out _));
        Assert.True(TownsfolkTalk.IsTownsperson(garrick));                  // ... and works
        Assert.False(Seats.TryGetSeat(mady, out _));                        // the shared one is not
        AssertPlayerKeepsItsIdentity(player, madyId);

        // Codex code R2 #1: declined is not the same as free to go. After the repair has left Off, Mady must not be
        // usable under the player's id by ANY route.
        Assert.False(Seats.Off);
        Assert.True(Seats.IsQuarantined(mady));
        Assert.False(TownsfolkTalk.IsTownsperson(mady));                    // no talk, no exchange, not on the roster
        Assert.Equal(Guid.Empty, AiIdentity.GetOrCreate(mady));             // no usable id
        Assert.DoesNotContain(Seats.OccupiedForRoster(), o => o.Mobile == mady);
        Assert.True(Seats.TryGetRow(new SeatKey(sp.Guid, TailorKind, 0), out var madySeat));
        Assert.Equal(SeatState.Conflict, madySeat.State);                   // her seat waits in conflict, not vacant
        Assert.Contains("in conflict", report);

        AiLedger.ResetForTests(7, 100);
        AiLedger.Record(mady, player, "visited");
        AiLedger.Record(madyId, player, "visited");
        Assert.False(AiLedger.TryRecord(madyId, player, "visited", null));
        Assert.Equal(0, AiLedger.OutboxCountForTests);                      // nothing recorded under the shared id as an NPC
        AiLedger.Record(garrick, player, "visited");
        Assert.Equal(1, AiLedger.OutboxCountForTests);                      // the player is still a usable CHARACTER
        AiLedger.ResetForTests(0, 100);

        // And a replacement can never be handed Mady's waiting identity while it is disputed.
        var newcomer = SpawnTailor(sp);
        Assert.True(Seats.TryGetSeat(newcomer, out var newKey));
        Assert.NotEqual(0, newKey.Slot);
    }

    // ---------- F3: nothing writes the register while the seat system is Off

    [Fact]
    public void F3_WhileOff_ADeletionWritesNothing_AndTheNextStartIsStillOff()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        var b = SpawnTailor(sp);
        var bindings = Seats.BindingsForTests();
        var counter = SeatRegister.Load(RegisterFile, out _).Counter;
        var floor = new SeatReconciler.SaveStamp(true, SeatRegister.Load(RegisterFile, out _).RegisterId, counter + 1);

        // The world save was made against a NEWER register than the one on disk: rolled back -> Off.
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, floor);
        Assert.True(Seats.Off);
        Assert.Contains("rolled back", Seats.OffReason);
        var bytes = File.ReadAllBytes(RegisterFile);

        a.Delete(); // a bound NPC whose row and serial match

        Assert.Equal(bytes, File.ReadAllBytes(RegisterFile)); // not a byte, so not the counter either
        Assert.Equal(counter, SeatRegister.Load(RegisterFile, out _).Counter);
        Assert.Equal(floor.Counter, Seats.StampForTests().Counter); // a save made now still carries the loaded floor

        // The next start against the same world floor must stop again - the deletion did not erase the stop.
        var after = Seats.BindingsForTests();
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(after, floor);
        Assert.True(Seats.Off);
        Assert.True(Seats.IsQuarantined(b));
    }

    [Fact]
    public void F3_WithTheSwitchOffButTheSystemHealthy_ADeletionStillVacatesAndIsWritten()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var a = SpawnTailor(sp);
        Seats.TryGetSeat(a, out var key);
        var bindings = Seats.BindingsForTests();
        var stamp = Seats.StampForTests();
        Seats.ResetForTests(false, RegisterFile); // the feature switch is off; the world has seats and is healthy
        Seats.LoadForTests(bindings, stamp);
        Assert.False(Seats.Off, Seats.OffReason);

        a.Delete();

        Assert.True(SeatRegister.Load(RegisterFile, out _).TryGet(key, out var row));
        Assert.Equal(SeatState.Vacant, row.State);
    }

    // ---------- F4: a quarantine is honoured at delivery and at the recording boundary

    [Fact]
    public void F4_CanDeliver_IsFalseOnceTheNpcIsQuarantined()
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var npc = SpawnTailor(sp);
        var player = NewPlayer("Listener");
        var online = TownsfolkTalk.Online;
        TownsfolkTalk.Online = _ => true;

        try
        {
            Assert.True(TownsfolkTalk.CanDeliver(npc, player)); // the control: an eligible NPC may speak
            Seats.QuarantineForTests(npc);
            Assert.False(TownsfolkTalk.CanDeliver(npc, player));
            Seats.ReleaseForTests(npc);
            Assert.True(TownsfolkTalk.CanDeliver(npc, player));
        }
        finally
        {
            TownsfolkTalk.Online = online;
        }
    }

    [Fact]
    public void F4_TheLedgerRecordsNothingForAQuarantinedNpc_NotUnderACapturedId_NotUnderAnEmptyId()
    {
        StartWithEmptyRegister();
        AiLedger.ResetForTests(7, 100);
        var sp = NewSpawner();
        var npc = SpawnTailor(sp);
        var player = NewPlayer("Listener");
        var captured = AiIdentity.GetOrCreate(npc); // an id a pending request captured earlier

        AiLedger.Record(npc, player, "visited"); // the control: recorded
        Assert.Equal(1, AiLedger.OutboxCountForTests);
        Assert.True(AiLedger.TryRecord(captured, player, "visited", null));
        Assert.Equal(2, AiLedger.OutboxCountForTests);

        Seats.QuarantineForTests(npc);

        AiLedger.Record(npc, player, "visited");                               // the Mobile overload (GetOrCreate gives no id)
        AiLedger.Record(captured, player, "visited");                          // the id overload, with the captured id
        Assert.False(AiLedger.TryRecord(captured, player, "visited", null));
        Assert.False(AiLedger.TryRecord(Guid.Empty, player, "visited", null)); // an empty NPC id is never recorded
        Assert.Equal(2, AiLedger.OutboxCountForTests);

        AiLedger.ResetForTests(0, 100);
    }

    // =====================================================================================================================
    // Codex code R2 (Admin/Review-2026-11-15-Codex-Avallone-Townsfolk-Stage1-Code-R2.md)
    // =====================================================================================================================

    // ---------- #2: the register's id and floor are checked BEFORE an "incomplete migration" flag is believed

    [Fact]
    public void R2_Step0_AProvisionalRegisterBelowTheSavedFloor_OrForeign_IsOff_NotAMigration()
    {
        var reg = new SeatRegister { MigrationComplete = false };
        reg.Commit(RegisterFile); // counter 1

        var below = SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 2), true);
        Assert.Contains("rolled back", below.OffReason);
        Assert.False(below.NeedsFirstMigration);

        var foreign = SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, Guid.NewGuid(), 0), false);
        Assert.Contains("not the one", foreign.OffReason);
        Assert.False(foreign.NeedsFirstMigration);
    }

    [Fact]
    public void R2_Step0_AConsistentInterruptedMigration_StillMigratesAgain() // the positive control
    {
        var reg = new SeatRegister { MigrationComplete = false };
        reg.Commit(RegisterFile); // counter 1

        // Interrupted before its world save was published: the save carries no seat data.
        Assert.True(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(false, Guid.Empty, 0), false).NeedsFirstMigration);
        // Published, the completion marker not written: the save carries this very register, at its counter.
        Assert.True(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 1), true).NeedsFirstMigration);
        Assert.True(SeatReconciler.Step0(reg, null, new SeatReconciler.SaveStamp(true, reg.RegisterId, 0), true).NeedsFirstMigration);
    }

    [Theory]
    [InlineData(true, false)]  // switch on, same register below the floor
    [InlineData(false, false)] // switch off
    [InlineData(true, true)]   // switch on, a foreign provisional register
    [InlineData(false, true)]
    public void R2_Load_AStaleProvisionalRegister_StopsAndItsInventoryIsNotOverwritten(bool switchOn, bool foreign)
    {
        // An older register file is put back: it still says "migration incomplete" and holds a vacant seat.
        var sp = NewSpawner();
        var stale = new SeatRegister { MigrationComplete = false };
        stale.Add(Row(sp.Guid, 0, SeatState.Vacant, Guid.NewGuid(), "Waiting"));
        stale.Add(Row(sp.Guid, 1, SeatState.Occupied, Guid.NewGuid(), "Present", 1));
        stale.Commit(RegisterFile); // counter 1
        var bytes = File.ReadAllBytes(RegisterFile);
        var stamp = new SeatReconciler.SaveStamp(true, foreign ? Guid.NewGuid() : stale.RegisterId, 2);

        Seats.ResetForTests(false, RegisterFile);
        var resident = SpawnTailor(sp, adopt: false);
        Seats.ResetForTests(switchOn, RegisterFile);
        Seats.LoadForTests([], stamp);

        Assert.True(Seats.Off);
        Assert.False(Seats.OffSavePredatesRegister);
        Assert.Equal(bytes, File.ReadAllBytes(RegisterFile)); // not migrated over: same id, both rows, same counter
        Assert.True(Seats.IsQuarantined(resident));
        var kept = SeatRegister.Load(RegisterFile, out _);
        Assert.Equal(stale.RegisterId, kept.RegisterId);
        Assert.Equal(2, kept.Count);
        Assert.NotNull(kept.LowestVacant(sp.Guid, TailorKind));
    }

    // ---------- #3: the recording boundary looks at EVERY holder of an id (both load orders)

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R2_Recording_AnNpcSharingAnIdWithAPlayer_RecordsNothingAtAnyEntryPoint_InEitherLoadOrder(bool npcLast)
    {
        StartWithEmptyRegister();
        AiLedger.ResetForTests(7, 100);
        AiLedger.SetHeardIngestForTests(true);
        var player = NewPlayer("Shared");
        var listener = NewPlayer("Listener");
        var sp = NewSpawner();
        var npc = SpawnTailor(sp, adopt: false);
        var other = SpawnTailor(sp);

        // The control: before the clash, every entry point records.
        var before = AiIdentity.GetOrCreate(npc);
        AiLedger.Record(npc, listener, "visited");
        AiLedger.Record(before, listener, "visited");
        Assert.True(AiLedger.TryRecord(before, listener, "visited", null));
        Assert.Equal(3, AiLedger.OutboxCountForTests);

        var id = ShareAnId(player, npc, npcLast);

        Assert.True(Seats.IsQuarantined(npc));   // by the fact of the shared id - no path had to add it to a list
        Assert.False(Seats.IsQuarantined(player));
        Assert.True(Seats.IsIdQuarantined(id));  // ... whichever owner the reverse map shows
        AiLedger.Record(npc, listener, "visited");
        AiLedger.Record(id, listener, "visited");
        Assert.False(AiLedger.TryRecord(id, listener, "visited", null));
        Assert.Equal(3, AiLedger.OutboxCountForTests);

        // The player is untouched as a CHARACTER: something another NPC records about them is still recorded.
        AiLedger.Record(other, player, "visited");
        Assert.Equal(4, AiLedger.OutboxCountForTests);
        AiLedger.ResetForTests(0, 100);
    }

    [Theory]
    [InlineData(false)] // the control: a story really said is recorded as heard
    [InlineData(true)]
    public void R2_Recording_ARumourReceiptForAQuarantinedNpcIsNotRecorded(bool quarantine)
    {
        StartWithEmptyRegister();
        AiLedger.ResetForTests(7, 100);
        Server.Avallone.Rumours.RumourSpeech.ResetForTests(true); // the heard-record switch, as HeardRecordTests sets it
        Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();
        AiLedger.SetHeardIngestForTests(true);
        var listener = NewPlayer("Listener");
        AiIdentity.GetOrCreate(listener);
        var sp = NewSpawner();
        var npc = SpawnTailor(sp);
        var said = AiReply.Success("They say a wyrm was slain.", "test") with
        {
            Rumour = new RumourInfo("", "", "a fisher", ["7-3"], "news", [new RumourDelivered("7-3", "s:7-3@0123456789abcdef", "Britain")])
        };

        if (quarantine)
        {
            Seats.QuarantineForTests(npc);
        }

        Server.Avallone.Rumours.RumourSpeech.OnSpoken(npc, listener, said);

        Assert.Equal(quarantine ? 0 : 1, AiLedger.OutboxForTests.Count(e => e.Kind == "rumour_heard"));
        Server.Avallone.Rumours.RumourSpeech.ResetForTests(false);
        Server.Avallone.Rumours.RumourSpeech.ResetRosterForTests();
        AiLedger.ResetForTests(0, 100);
    }

    [Fact]
    public void R2_FirstMigration_InterruptedTwice_IsStillAMigration_NotAForeignRegister()
    {
        // 1: a migration whose world save was published, then a crash before the completion marker.
        var (a, _) = BeginFirstMigration();
        var id1 = SeatRegister.Load(RegisterFile, out _).RegisterId;
        var bindings = Seats.BindingsForTests();
        var stamp = Seats.StampForTests(); // what that published save carries: register id1, its counter
        Assert.Equal(id1, stamp.RegisterId);

        // 2: restart -> done again. Then a crash BEFORE any new save: the world on disk still names id1.
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, stamp);
        Assert.False(Seats.Off, Seats.OffReason);
        Assert.False(Seats.Exposed);
        Assert.Equal(id1, SeatRegister.Load(RegisterFile, out _).RegisterId); // the same register goes on

        // 3: restart again, against the same published save.
        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, stamp);
        Assert.False(Seats.Off, Seats.OffReason);

        var nonce = Seats.SerializeForTests();
        Seats.PublishedContains = n => n == nonce;
        Seats.OnWorldSaved();
        Assert.True(Seats.Exposed);
        Assert.True(TownsfolkTalk.IsTownsperson(a));
    }

    // =====================================================================================================================
    // Codex code R3: a damaged save can bind a seat to a PLAYER. The seat goes to conflict; the player is never
    // quarantined, keeps its AI identity, and no event with an empty character id can be queued.
    // =====================================================================================================================

    public enum BadBinding
    {
        WrongKind,   // the player is the one claimant of a tailor's seat
        MissingRow,  // the binding names a seat the register does not hold
        Duplicate,   // the player and a real tailor both claim one seat
        ConflictRow  // the seat was already in conflict
    }

    [Theory]
    [InlineData(BadBinding.WrongKind, false)]
    [InlineData(BadBinding.MissingRow, false)]
    [InlineData(BadBinding.Duplicate, false)]
    [InlineData(BadBinding.ConflictRow, false)]
    [InlineData(BadBinding.WrongKind, true)]
    [InlineData(BadBinding.MissingRow, true)]
    [InlineData(BadBinding.Duplicate, true)]
    [InlineData(BadBinding.ConflictRow, true)]
    public void R3_APlayerNamedByABadBinding_IsNeverQuarantined_AndItsIdentityStaysUsable(BadBinding kind, bool off)
    {
        StartWithEmptyRegister();
        AiLedger.ResetForTests(7, 100);
        var sp = NewSpawner();
        var tailor = SpawnTailor(sp);   // seat 0, a real NPC
        var clean = SpawnTailor(sp);    // seat 1, a clean NPC that will record something about the player
        var player = NewPlayer("Bound By Mistake");
        var playerId = AiIdentity.GetOrCreate(player);
        Seats.TryGetSeat(tailor, out var seat0);

        var bindings = Seats.BindingsForTests();
        var stamp = Seats.StampForTests();

        switch (kind)
        {
            case BadBinding.WrongKind:
                {
                    bindings.RemoveAll(b => b.Mobile == tailor);
                    bindings.Add((player, seat0));
                    break;
                }
            case BadBinding.MissingRow:
                {
                    bindings.Add((player, new SeatKey(Guid.NewGuid(), TailorKind, 0)));
                    break;
                }
            case BadBinding.Duplicate:
                {
                    bindings.Add((player, seat0));
                    break;
                }
            case BadBinding.ConflictRow:
                {
                    var reg = SeatRegister.Load(RegisterFile, out _);
                    var forged = new SeatRegister { MigrationComplete = true };
                    foreach (var r in reg.Rows)
                    {
                        forged.Add(new SeatRow
                        {
                            Key = r.Key, AiId = r.AiId, Looks = r.Looks, LastSerial = r.LastSerial,
                            State = r.Key == seat0 ? SeatState.Conflict : r.State, Reason = r.Key == seat0 ? "an earlier conflict" : null
                        });
                    }

                    forged.Commit(RegisterFile);
                    stamp = new SeatReconciler.SaveStamp(true, forged.RegisterId, 0);
                    bindings.RemoveAll(b => b.Mobile == tailor);
                    bindings.Add((player, seat0));
                    break;
                }
        }

        if (off)
        {
            stamp = stamp with { Counter = stamp.Counter + 100 }; // the register is older than the world: Off
        }

        Seats.ResetForTests(true, RegisterFile);
        Seats.LoadForTests(bindings, stamp);
        Assert.Equal(off, Seats.Off);

        // The player: never quarantined, its original id still the one it gets.
        Assert.False(Seats.IsQuarantined(player));
        Assert.Equal(playerId, AiIdentity.GetOrCreate(player));
        Assert.False(Seats.IsIdQuarantined(playerId));

        if (!off)
        {
            // The positive controls: the invalid seat is in conflict, and a real NPC in a bad binding IS quarantined.
            if (kind != BadBinding.MissingRow)
            {
                Assert.True(Seats.TryGetRow(seat0, out var row));
                Assert.Equal(SeatState.Conflict, row.State);
            }

            if (kind == BadBinding.Duplicate)
            {
                Assert.True(Seats.IsQuarantined(tailor));
                Assert.False(TownsfolkTalk.IsTownsperson(tailor));
            }

            // A valid NPC still records the player as its character - under the player's REAL id.
            Assert.True(TownsfolkTalk.IsTownsperson(clean));
            AiLedger.Record(clean, player, "visited");
            var e = Assert.Single(AiLedger.OutboxForTests);
            Assert.Equal(playerId, e.CharId);
        }

        Assert.DoesNotContain(AiLedger.OutboxForTests, e => e.CharId == Guid.Empty || e.NpcId == Guid.Empty);
        AiLedger.ResetForTests(0, 100);
    }

    [Fact]
    public void R3_AnActualWrongKindNpc_IsStillQuarantined_AndItsSeatIsInConflict() // the control for the theory above
    {
        StartWithEmptyRegister();
        var sp = NewSpawner();
        var tailor = SpawnTailor(sp);
        Seats.TryGetSeat(tailor, out var seat0);
        var weaver = new Weaver();
        weaver.MoveToWorld(new Point3D(1496, 1629, 0), Map.Trammel);
        weaver.Spawner = sp;
        _made.Add(weaver);

        var bindings = Seats.BindingsForTests();
        bindings.RemoveAll(b => b.Mobile == tailor);
        bindings.Add((weaver, seat0)); // a weaver bound to a tailor's seat
        Restart(bindings, Seats.StampForTests());

        Assert.True(Seats.IsQuarantined(weaver));
        Assert.False(TownsfolkTalk.IsTownsperson(weaver));
        Assert.True(Seats.TryGetRow(seat0, out var row));
        Assert.Equal(SeatState.Conflict, row.State);
    }

    [Fact]
    public void R3_NoEventIsQueuedWithAnEmptyCharacterId()
    {
        StartWithEmptyRegister();
        AiLedger.ResetForTests(7, 100);
        var sp = NewSpawner();
        var npc = SpawnTailor(sp);
        var refused = SpawnTailor(sp);
        var player = NewPlayer("Listener");

        AiLedger.Record(npc, player, "visited"); // the control
        Assert.Equal(1, AiLedger.OutboxCountForTests);

        // A "character" that can be given no identity (here: a quarantined NPC standing in the character's place).
        Seats.QuarantineForTests(refused);
        Assert.Equal(Guid.Empty, AiIdentity.GetOrCreate(refused));
        AiLedger.Record(npc, refused, "visited");
        Assert.False(AiLedger.TryRecord(AiIdentity.GetOrCreate(npc), refused, "visited", null));
        AiLedger.RecordWorld(refused, "deed");

        Assert.Equal(1, AiLedger.OutboxCountForTests);
        Assert.DoesNotContain(AiLedger.OutboxForTests, e => e.CharId == Guid.Empty);
        AiLedger.ResetForTests(0, 100);
    }
}
