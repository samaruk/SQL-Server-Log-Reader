using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlLogReader.Decoding;
using SqlLogReader.Scripting;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>
/// Reads current row images from data pages with <c>DBCC PAGE ... WITH TABLERESULTS</c>.
/// Clustered rows are located through <c>%%physloc%%</c>. Requires sysadmin.
/// </summary>
public sealed partial class SqlPageRowSource : ICurrentRowSource
{
    private readonly SqlConnection _connection;
    private readonly Dictionary<(int File, int Page), Dictionary<int, byte[]>> _pages = new();
    private bool _unavailable;

    public SqlPageRowSource(SqlConnection connection)
    {
        _connection = connection;
    }

    public async Task<byte[]?> GetCurrentRowAsync(RowLocator locator, CancellationToken cancellationToken)
    {
        if (_unavailable) return null;
        var table = locator.Table;
        if (table.IsHeap)
        {
            var slots = await GetPageAsync(locator.FileId, locator.PageId, cancellationToken);
            return slots != null && slots.TryGetValue(locator.SlotId, out var row) ? row : null;
        }

        if (locator.Key == null) return null;
        var predicates = new List<string>();
        foreach (var keyColumn in table.ClusteredKeyColumns.Where(c => !c.IsUniqueifier))
        {
            var value = locator.Key[keyColumn.Name];
            if (value == null) return null;
            var literal = SqlLiteral.TryFormat(value);
            if (literal == null) return null;
            predicates.Add(value.IsNull ? $"{TableInfo.Quote(keyColumn.Name)} IS NULL" : $"{TableInfo.Quote(keyColumn.Name)} = {literal}");
        }
        if (predicates.Count == 0) return null;

        var sql = $"SELECT TOP (64) %%physloc%% FROM {table.QualifiedName} WITH (NOLOCK) WHERE {string.Join(" AND ", predicates)};";
        var locations = new List<(int File, int Page, int Slot)>();
        try
        {
            await using var cmd = new SqlCommand(sql, _connection);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var loc = (byte[])reader.GetValue(0);
                if (loc.Length < 8) continue;
                locations.Add((BitConverter.ToUInt16(loc, 4), BitConverter.ToInt32(loc, 0), BitConverter.ToUInt16(loc, 6)));
            }
        }
        catch (SqlException)
        {
            return null; // table dropped/renamed, or the key literal does not convert
        }

        foreach (var (file, page, slot) in locations)
        {
            var slots = await GetPageAsync(file, page, cancellationToken);
            if (slots == null || !slots.TryGetValue(slot, out var row)) continue;
            if (RowDecoder.TryDecode(table, row, out var decoded, out _) && RowIdentity.ForClusteredKey(table, decoded) == locator.Identity)
                return row;
        }
        return null;
    }

    private async Task<Dictionary<int, byte[]>?> GetPageAsync(int file, int page, CancellationToken ct)
    {
        if (_pages.TryGetValue((file, page), out var cached)) return cached;
        try
        {
            await using var cmd = new SqlCommand("DECLARE @db int = DB_ID(); DBCC PAGE(@db, @file, @page, 1) WITH TABLERESULTS, NO_INFOMSGS;", _connection);
            cmd.Parameters.AddWithValue("@file", file);
            cmd.Parameters.AddWithValue("@page", page);
            var lines = new List<(string Parent, string Value)>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    var parent = reader.IsDBNull(0) ? "" : Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture) ?? "";
                    var obj = reader.IsDBNull(1) ? "" : Convert.ToString(reader.GetValue(1), CultureInfo.InvariantCulture) ?? "";
                    var value = reader.IsDBNull(3) ? "" : Convert.ToString(reader.GetValue(3), CultureInfo.InvariantCulture) ?? "";
                    if (obj.StartsWith("Memory Dump", StringComparison.OrdinalIgnoreCase))
                        lines.Add((parent, value));
                }
            }
            var slots = ParseSlotDumps(lines);
            _pages[(file, page)] = slots;
            return slots;
        }
        catch (SqlException ex) when (ex.Number is 229 or 230 or 297 or 300 or 2571 or 7983 or 8189)
        {
            _unavailable = true;
            throw new CurrentRowSourceUnavailableException(
                "Current row images could not be read (DBCC PAGE needs sysadmin), so some updates are shown only partially.", ex);
        }
        catch (SqlException)
        {
            _pages[(file, page)] = new Dictionary<int, byte[]>();
            return null;
        }
    }

    /// <summary>
    /// Parses the per-slot memory dumps of <c>DBCC PAGE (..., 1) WITH TABLERESULTS</c>:
    /// <c>ParentObject = "Slot 0, Offset 0xeb, Length 150, DumpStyle BYTE"</c>,
    /// <c>VALUE = "0000000000000000:   30007000 01000000 ...  0.p....."</c>.
    /// </summary>
    internal static Dictionary<int, byte[]> ParseSlotDumps(IEnumerable<(string Parent, string Value)> lines)
    {
        var buffers = new Dictionary<int, (byte[] Data, int Length)>();
        foreach (var (parent, value) in lines)
        {
            var header = SlotHeader().Match(parent);
            if (!header.Success) continue;
            int slot = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
            int length = int.Parse(header.Groups[2].Value, CultureInfo.InvariantCulture);
            if (!buffers.TryGetValue(slot, out var buffer))
                buffers[slot] = buffer = (new byte[length], length);

            var line = DumpLine().Match(value);
            if (!line.Success) continue;
            int offset = int.Parse(line.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var hex = new StringBuilder();
            foreach (Capture group in line.Groups[2].Captures)
                hex.Append(group.Value.Trim());
            var bytes = Convert.FromHexString(hex.ToString());
            for (int i = 0; i < bytes.Length && offset + i < buffer.Length; i++)
                buffer.Data[offset + i] = bytes[i];
        }
        return buffers.ToDictionary(kv => kv.Key, kv => kv.Value.Data);
    }

    [GeneratedRegex(@"^Slot (\d+), Offset 0x[0-9a-fA-F]+, Length (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex SlotHeader();

    [GeneratedRegex(@"^([0-9A-Fa-f]{16}):\s+(?:([0-9A-Fa-f]{2,8}) ?)+", RegexOptions.CultureInvariant)]
    private static partial Regex DumpLine();
}
