namespace SqlLogReader.Schema;

/// <summary>Table metadata of one database, indexed by in-row allocation unit id.</summary>
public sealed class DatabaseSchema
{
    private readonly Dictionary<long, TableInfo> _byAllocationUnit;

    public DatabaseSchema(string databaseName, IEnumerable<TableInfo> tables)
    {
        DatabaseName = databaseName;
        _byAllocationUnit = tables.ToDictionary(t => t.AllocationUnitId);
    }

    public string DatabaseName { get; }

    public IReadOnlyCollection<TableInfo> Tables => _byAllocationUnit.Values;

    public bool TryGetTable(long allocationUnitId, out TableInfo table) =>
        _byAllocationUnit.TryGetValue(allocationUnitId, out table!);

    /// <summary>Distinct table names (schema.table), sorted.</summary>
    public IReadOnlyList<string> TableNames =>
        _byAllocationUnit.Values.Select(t => t.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
}
