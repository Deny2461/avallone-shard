using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Server.Avallone.Townsfolk;

// Townsfolk step, Stage 1 (Projects/Avallone-Townsfolk-Step-Design.md §3; Codex design R4 GO, 2026-11-15).
// "The place keeps the person": a resident townsperson's identity belongs to its SEAT - (spawner guid, kind, slot) -
// and is kept in a register OUTSIDE the world saves, so a crash or a restored older save brings the whole person back.

public enum SeatState
{
    Occupied, // bound to one live Mobile
    Vacant,   // identity kept, waiting for the spawner to make its next occupant
    Conflict, // ownership could not be established - quarantined until an operator clears it (never cleared implicitly)
    Retired   // never refilled; kept for the record
}

// Kind is the full type name of the spawned Mobile, fixed when the seat is made (§3.1). Slots are never renumbered.
public readonly record struct SeatKey(Guid Spawner, string Kind, int Slot)
{
    public override string ToString() => $"{Spawner:N}/{Kind}/{Slot}";
}

// The canonical identity besides the AI id (§3.2). Clothes are deliberately not kept in this step.
public sealed record SeatLooks(
    string Name, bool Female, int Body, int Hue, int Hair, int HairHue, int Beard, int BeardHue, int SpeechHue
);

public sealed class SeatRow
{
    public SeatKey Key { get; init; }
    public SeatState State { get; set; }
    public Guid AiId { get; init; }
    public SeatLooks Looks { get; set; }
    public uint LastSerial { get; set; }
    public string Reason { get; set; } // why it is in Conflict, in plain words
}

// The register file. Every commit rewrites the whole file through a temporary file that is flushed to disk and then
// renamed over the old one, so a crash leaves either the old register or the new one, never half of each. It is small
// (a few hundred bytes a seat) and commits are rare (a spawn, a deletion, the first migration).
public sealed class SeatRegister
{
    private readonly Dictionary<SeatKey, SeatRow> _rows = [];
    private readonly Dictionary<Guid, SeatKey> _byAiId = [];

    public Guid RegisterId { get; private set; } = Guid.NewGuid();
    public long Counter { get; private set; }
    public bool MigrationComplete { get; set; }

    public IReadOnlyCollection<SeatRow> Rows => _rows.Values;
    public int Count => _rows.Count;

    public bool TryGet(SeatKey key, out SeatRow row) => _rows.TryGetValue(key, out row);

    public bool TryGetByAiId(Guid id, out SeatRow row)
    {
        row = null;
        return _byAiId.TryGetValue(id, out var key) && _rows.TryGetValue(key, out row);
    }

    // No two rows hold one AI id (§3.2 [v4]). Returns false and changes nothing if the row cannot be added.
    public bool Add(SeatRow row)
    {
        if (row == null || row.AiId == Guid.Empty || string.IsNullOrEmpty(row.Key.Kind) || _rows.ContainsKey(row.Key) ||
            _byAiId.ContainsKey(row.AiId))
        {
            return false;
        }

        _rows[row.Key] = row;
        _byAiId[row.AiId] = row.Key;
        return true;
    }

    // The lowest-numbered vacant slot of a pool, or null (§3.3 Adopt). Slot numbers are compared, never positions.
    public SeatRow LowestVacant(Guid spawner, string kind)
    {
        SeatRow best = null;

        foreach (var row in _rows.Values)
        {
            if (row.State == SeatState.Vacant && row.Key.Spawner == spawner && row.Key.Kind == kind &&
                (best == null || row.Key.Slot < best.Key.Slot))
            {
                best = row;
            }
        }

        return best;
    }

    // A slot number never used in this pool: one more than the highest ever allocated, whatever its state.
    public int NextSlot(Guid spawner, string kind)
    {
        var next = 0;

        foreach (var key in _rows.Keys)
        {
            if (key.Spawner == spawner && key.Kind == kind && key.Slot >= next)
            {
                next = key.Slot + 1;
            }
        }

        return next;
    }

    public void Clear()
    {
        _rows.Clear();
        _byAiId.Clear();
    }

    // A first migration done AGAIN after an interruption keeps the provisional register's id and counter: the world
    // save that was published before the interruption names this register, and must still agree with it afterwards.
    public static SeatRegister RedoOf(SeatRegister provisional) =>
        new() { RegisterId = provisional.RegisterId, Counter = provisional.Counter, MigrationComplete = false };

    private sealed class RowDto
    {
        public Guid Spawner { get; set; }
        public string Kind { get; set; }
        public int Slot { get; set; }
        public string State { get; set; }
        public Guid AiId { get; set; }
        public string Name { get; set; }
        public bool Female { get; set; }
        public int Body { get; set; }
        public int Hue { get; set; }
        public int Hair { get; set; }
        public int HairHue { get; set; }
        public int Beard { get; set; }
        public int BeardHue { get; set; }
        public int SpeechHue { get; set; }
        public uint LastSerial { get; set; }
        public string Reason { get; set; }
    }

    private sealed class FileDto
    {
        public int Format { get; set; } = 1;
        public Guid RegisterId { get; set; }
        public long Counter { get; set; }
        public bool MigrationComplete { get; set; }
        public List<RowDto> Rows { get; set; } = [];
    }

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    // Raises the counter and writes the file durably. Throws if it cannot - the caller must then NOT expose the NPC.
    public void Commit(string path)
    {
        Counter++;

        var dto = new FileDto { RegisterId = RegisterId, Counter = Counter, MigrationComplete = MigrationComplete };

        foreach (var row in _rows.Values)
        {
            dto.Rows.Add(
                new RowDto
                {
                    Spawner = row.Key.Spawner, Kind = row.Key.Kind, Slot = row.Key.Slot, State = row.State.ToString(),
                    AiId = row.AiId, Name = row.Looks?.Name, Female = row.Looks?.Female ?? false,
                    Body = row.Looks?.Body ?? 0, Hue = row.Looks?.Hue ?? 0, Hair = row.Looks?.Hair ?? 0,
                    HairHue = row.Looks?.HairHue ?? 0, Beard = row.Looks?.Beard ?? 0, BeardHue = row.Looks?.BeardHue ?? 0,
                    SpeechHue = row.Looks?.SpeechHue ?? 0, LastSerial = row.LastSerial, Reason = row.Reason
                }
            );
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = path + ".tmp";

        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, dto, _json);
            fs.Flush(true); // to the disk, not only to the operating system
        }

        File.Move(tmp, path, true);
    }

    // null + a reason when the file is missing or cannot be trusted. Never throws.
    public static SeatRegister Load(string path, out string problem)
    {
        problem = null;

        if (!File.Exists(path))
        {
            problem = "missing";
            return null;
        }

        try
        {
            FileDto dto;
            using (var fs = File.OpenRead(path))
            {
                dto = JsonSerializer.Deserialize<FileDto>(fs, _json);
            }

            if (dto == null || dto.Format != 1 || dto.RegisterId == Guid.Empty || dto.Rows == null)
            {
                problem = "unreadable (wrong shape)";
                return null;
            }

            var reg = new SeatRegister
            {
                RegisterId = dto.RegisterId, Counter = dto.Counter, MigrationComplete = dto.MigrationComplete
            };

            foreach (var r in dto.Rows)
            {
                if (!Enum.TryParse<SeatState>(r.State, out var state))
                {
                    problem = $"unreadable (state \"{r.State}\")";
                    return null;
                }

                var row = new SeatRow
                {
                    Key = new SeatKey(r.Spawner, r.Kind, r.Slot), State = state, AiId = r.AiId,
                    Looks = new SeatLooks(r.Name, r.Female, r.Body, r.Hue, r.Hair, r.HairHue, r.Beard, r.BeardHue, r.SpeechHue),
                    LastSerial = r.LastSerial, Reason = r.Reason
                };

                if (!reg.Add(row))
                {
                    // G5: two rows with one AI id, a repeated seat, or an empty id - the file was edited or damaged.
                    problem = $"unreadable (row {row.Key} repeats a seat or an AI id)";
                    return null;
                }
            }

            return reg;
        }
        catch (Exception e)
        {
            problem = $"unreadable ({e.GetType().Name})";
            return null;
        }
    }
}
