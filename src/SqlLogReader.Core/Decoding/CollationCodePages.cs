using System.Collections.Concurrent;

namespace SqlLogReader.Decoding;

/// <summary>
/// Maps SQL Server collation ids (as stored inside sql_variant values) to Windows code pages.
/// Collation ids are fixed by SQL Server, so the map is shared by all connections.
/// </summary>
public static class CollationCodePages
{
    private static readonly ConcurrentDictionary<int, int> Map = new();

    public static int Count => Map.Count;

    public static void Register(int collationId, int codePage)
    {
        if (codePage > 0) Map[collationId] = codePage;
    }

    /// <summary>Code page for the collation id; 1252 (Latin) when unknown.</summary>
    public static int Get(int collationId) => Map.TryGetValue(collationId, out var cp) ? cp : 1252;
}
