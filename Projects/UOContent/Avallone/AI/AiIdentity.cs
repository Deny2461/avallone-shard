using System;
using System.Collections.Generic;

namespace Server.Avallone.AI;

// Stable AI IDs for mobiles (NPCs and player characters). The AI's memory is keyed by these, never by
// serials or account names, so memory is per character and survives anything that renumbers serials.
//
// Saved as its own file in world saves ("AvalloneAi"). Never delete this type once it is in saves —
// retire it in place (plan v2, save compatibility).
public class AiIdentity : GenericPersistence
{
    private const int Version = 0;

    private static AiIdentity _instance;
    private static readonly Dictionary<Mobile, Guid> _ids = [];
    private static readonly Dictionary<Guid, Mobile> _byId = []; // Phase 5b: id -> character, for the current name

    public AiIdentity() : base("AvalloneAi", 10)
    {
    }

    public static int Count => _ids.Count;

    public static void Configure()
    {
        _instance = new AiIdentity();
    }

    public static Guid GetOrCreate(Mobile m)
    {
        if (m == null || m.Deleted)
        {
            return Guid.Empty;
        }

        if (!_ids.TryGetValue(m, out var id))
        {
            id = Guid.NewGuid();
            _ids[m] = id;
            _byId[id] = m;
        }

        return id;
    }

    public static bool TryGet(Mobile m, out Guid id) => _ids.TryGetValue(m, out id);

    // Only a live, non-deleted player character counts; anything else (deleted, unknown, an NPC) is "gone".
    public static bool TryGetPlayer(Guid id, out Server.Mobiles.PlayerMobile pm)
    {
        pm = _byId.TryGetValue(id, out var m) ? m as Server.Mobiles.PlayerMobile : null;
        if (pm is { Deleted: false })
        {
            return true;
        }

        pm = null;
        return false;
    }

    public override void Serialize(IGenericWriter writer)
    {
        writer.WriteEncodedInt(Version);

        var count = 0;
        foreach (var m in _ids.Keys)
        {
            if (!m.Deleted)
            {
                count++;
            }
        }

        writer.WriteEncodedInt(count);
        foreach (var (m, id) in _ids)
        {
            if (!m.Deleted)
            {
                writer.Write(m);
                writer.Write(id);
            }
        }
    }

    public override void Deserialize(IGenericReader reader)
    {
        reader.ReadEncodedInt(); // version

        var count = reader.ReadEncodedInt();
        for (var i = 0; i < count; i++)
        {
            var m = reader.ReadEntity<Mobile>();
            var id = reader.ReadGuid();

            if (m != null)
            {
                _ids[m] = id;
                _byId[id] = m;
            }
        }
    }
}
