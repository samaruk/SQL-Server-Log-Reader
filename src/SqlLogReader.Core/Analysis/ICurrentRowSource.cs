using SqlLogReader.Decoding;
using SqlLogReader.Schema;

namespace SqlLogReader.Analysis;

/// <summary>Describes a row whose current on-page image is wanted.</summary>
public sealed class RowLocator
{
    public required TableInfo Table { get; init; }

    /// <summary>The identity the returned row must have (see <see cref="RowIdentity"/>).</summary>
    public required string Identity { get; init; }

    /// <summary>Clustered key values (clustered tables).</summary>
    public DecodedRow? Key { get; init; }

    /// <summary>Row location (heaps).</summary>
    public int FileId { get; init; }
    public int PageId { get; init; }
    public int SlotId { get; init; }
}

/// <summary>
/// Supplies the current bytes of a row from the live database. Used to rebuild the before/after
/// images of updates whose original INSERT is no longer in the log.
/// </summary>
public interface ICurrentRowSource
{
    /// <summary>Returns the row's current bytes, or null when it cannot be found.</summary>
    Task<byte[]?> GetCurrentRowAsync(RowLocator locator, CancellationToken cancellationToken);
}
