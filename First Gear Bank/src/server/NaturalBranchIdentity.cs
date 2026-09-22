/*
 * Derives stable natural-branch identities and random decisions from world/source facts using separate SHA-256 domains.
 * No mutable random stream is shared with economics, NPC replacement, candidate ordering, or other mods.  Adding a new
 * decision domain therefore cannot reroll an already recorded source selection or branch identity.
 */

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace FirstGearBank.Server;

/// Pure deterministic identity and uniform-value derivation for natural branch generation.
internal static class NaturalBranchIdentity
{



    //// Binds one source to world, dimension, exact canonical structure bounds, code, and format salt.
    ////
    internal static string SourceId(string world, NaturalStructureSource source)
    {
        return Convert.ToHexString(Hash("source", world, source.Code, Bounds(source.Bounds)));
    }



    //// Returns one stable [0,1) selection roll from the world seed and source ID.
    ////
    internal static double Roll(long seed, string sourceId)
    {
        var bytes = Hash("selection", seed.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceId);
        return (BinaryPrimitives.ReadUInt64BigEndian(bytes) >> 11) / 9007199254740992.0;
    }



    //// Creates a stable branch GUID from a distinct identity domain.
    ////
    internal static Guid Branch(string world, string sourceId)
    {
        return new Guid(Hash("branch", world, sourceId).AsSpan(0, 16));
    }



    //// Produces a stable ordering key for one candidate without consuming another candidate's state.
    ////
    internal static ulong Candidate(long seed, string sourceId, int x, int z, int rotation)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(Hash("candidate",
            seed.ToString(System.Globalization.CultureInfo.InvariantCulture), sourceId,
            x.ToString(System.Globalization.CultureInfo.InvariantCulture),
            z.ToString(System.Globalization.CultureInfo.InvariantCulture),
            rotation.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }



    //// Hashes length-delimited decision components under the fixed v1 natural-generation salt.
    ////
    private static byte[] Hash(string domain, params string[] values)
    {
        var text = "firstgearbank/natural/v1/" + domain + "/" +
            string.Join('/', values.Select(value => value.Length + ":" + value));
        return SHA256.HashData(Encoding.UTF8.GetBytes(text));
    }



    //// Canonicalizes all inclusive bounds and dimension fields without culture-sensitive formatting.
    ////
    private static string Bounds(BranchBounds bounds)
    {
        return FormattableString.Invariant($"{bounds.Dimension},{bounds.MinX},{bounds.MinY},{bounds.MinZ}," +
            $"{bounds.MaxX},{bounds.MaxY},{bounds.MaxZ}");
    }



}
