using SqlLogReader.Analysis;
using SqlLogReader.Decoding;
using SqlLogReader.Scripting;

namespace SqlLogReader.Tests.Integration;

public class UpdateTests
{
    private const string CreatePatients = """
        CREATE TABLE dbo.Patients(
            PatientId int IDENTITY PRIMARY KEY, FirstName nvarchar(50) NOT NULL, LastName nvarchar(50) NULL,
            BirthDate date NULL, Balance decimal(10,2) NULL, IsActive bit NOT NULL DEFAULT 1, Code char(5) NULL,
            Notes varchar(max) NULL, Score float NULL, Flag2 bit NULL);
        INSERT dbo.Patients(FirstName, LastName, BirthDate, Balance, Code, Notes, Score, Flag2) VALUES
            (N'Ali', N'Khan', '1990-05-17', 1234.56, 'AB123', 'first note', 3.14, 0),
            (N'Sara', NULL, NULL, NULL, NULL, NULL, NULL, 1),
            (N'Omar', N'Farooq', '1985-01-01', 0, 'ZZ999', NULL, 1, NULL);
        """;

    private static readonly string[] Steps =
    {
        "UPDATE dbo.Patients SET LastName = N'Ahmed', Balance = 99.99 WHERE PatientId = 2;",
        "UPDATE dbo.Patients SET FirstName = N'Sarah', Score = 2.5 WHERE PatientId = 2;",
        "UPDATE dbo.Patients SET Notes = REPLICATE('n', 300), IsActive = 0, Flag2 = NULL WHERE PatientId = 2;",
        "UPDATE dbo.Patients SET LastName = NULL, Code = 'QQ' WHERE PatientId IN (1, 3);",
        "UPDATE dbo.Patients SET Balance = Balance + 1 WHERE Balance IS NOT NULL;",
        "UPDATE dbo.Patients SET Notes = 'short' WHERE PatientId = 2;",
    };

    private static async Task<List<List<Dictionary<string, object?>>>> RunStepsAsync(TestDatabase db)
    {
        var snapshots = new List<List<Dictionary<string, object?>>> { await db.QueryAsync("SELECT * FROM dbo.Patients ORDER BY PatientId") };
        foreach (var step in Steps)
        {
            await db.ExecuteAsync(step);
            snapshots.Add(await db.QueryAsync("SELECT * FROM dbo.Patients ORDER BY PatientId"));
        }
        return snapshots;
    }

    /// <summary>Each update must show the row exactly as it was before and after its statement.</summary>
    private static void AssertUpdatesMatchSnapshots(IReadOnlyList<RowChange> updates, List<List<Dictionary<string, object?>>> snapshots)
    {
        // One change per affected row and statement, in statement order.
        var expected = new List<(int Step, Dictionary<string, object?> Before, Dictionary<string, object?> After)>();
        for (int s = 0; s < Steps.Length; s++)
        {
            for (int r = 0; r < snapshots[s].Count; r++)
            {
                var before = snapshots[s][r];
                var after = snapshots[s + 1][r];
                if (before.Any(kv => !Equals(kv.Value, after[kv.Key])))
                    expected.Add((s, before, after));
            }
        }

        Assert.Equal(expected.Count, updates.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var (step, before, after) = expected[i];
            var change = updates[i];
            Assert.True(change.IsComplete, $"step {step}: {string.Join("; ", change.Notes)}");
            foreach (var (name, value) in before)
                Comparison.AssertSameValue(value, change.Before![name]!, $"step {step} before {name}");
            foreach (var (name, value) in after)
                Comparison.AssertSameValue(value, change.After![name]!, $"step {step} after {name}");
            var changedNames = before.Keys.Where(k => !Equals(before[k], after[k])).OrderBy(k => k);
            Assert.Equal(changedNames, change.ChangedColumns.Select(c => c.Name).OrderBy(k => k));
        }
    }

    [SqlServerFact]
    public async Task Updates_are_rebuilt_from_the_inserts_in_the_log()
    {
        await using var db = await TestDatabase.CreateAsync("lr_upd");
        await db.ExecuteAsync(CreatePatients);
        var snapshots = await RunStepsAsync(db);

        var result = await db.ReadLogAsync(new AnalyzerOptions { UseCurrentData = false, Operations = new[] { ChangeOperation.Update } });
        AssertUpdatesMatchSnapshots(result.Changes, snapshots);
    }

    [SqlServerFact]
    public async Task Updates_are_rebuilt_from_the_current_page_when_the_inserts_are_gone()
    {
        await using var db = await TestDatabase.CreateAsync("lr_upd_cur");
        await db.ExecuteAsync(CreatePatients);
        await db.TruncateLogAsync();
        var snapshots = await RunStepsAsync(db);

        var result = await db.ReadLogAsync(new AnalyzerOptions { Operations = new[] { ChangeOperation.Update } });
        Assert.DoesNotContain(result.Changes, c => c.Operation == ChangeOperation.Insert);
        AssertUpdatesMatchSnapshots(result.Changes, snapshots);

        // Undo every update and check that the table is back to its starting state.
        await db.ExecuteAsync(ScriptGenerator.GenerateScript(result.Changes, ScriptKind.Undo));
        var restored = await db.QueryAsync("SELECT * FROM dbo.Patients ORDER BY PatientId");
        Assert.Equal(snapshots[0], restored);
    }

    [SqlServerFact]
    public async Task Without_row_images_fixed_length_columns_inside_the_logged_bytes_are_decoded()
    {
        await using var db = await TestDatabase.CreateAsync("lr_upd_part");
        await db.ExecuteAsync(CreatePatients);
        await db.TruncateLogAsync();
        // SQL Server logs only the bytes that differ: every byte of Code changes ('AB123' -> 'XY789'),
        // while the sign byte of Balance stays the same, so Balance cannot be rebuilt from the log alone.
        await db.ExecuteAsync("UPDATE dbo.Patients SET Code = 'XY789', Balance = 5.25 WHERE PatientId = 1;");

        var result = await db.ReadLogAsync(new AnalyzerOptions { UseCurrentData = false });
        var change = Assert.Single(result.Changes);
        Assert.False(change.IsComplete);
        Assert.Equal(1, change.Key!["PatientId"]!.Value);

        var code = change.ChangedColumns.Single(c => c.Name == "Code");
        Assert.Equal("AB123", code.Before!.Value);
        Assert.Equal("XY789", code.After!.Value);
        var balance = change.ChangedColumns.Single(c => c.Name == "Balance");
        Assert.Equal(ValueState.Unavailable, balance.Before!.State);
        Assert.DoesNotContain(change.ChangedColumns, c => c.Name is "IsActive" or "Score");

        var undo = ScriptGenerator.Generate(change, ScriptKind.Undo);
        Assert.Contains("SET [Code] = 'AB123' WHERE [PatientId] = 1;", undo);
        Assert.Contains("WARNING", undo);
    }

    [SqlServerFact]
    public async Task Heap_rows_moved_by_an_update_are_not_reported_as_inserts()
    {
        await using var db = await TestDatabase.CreateAsync("lr_fwd");
        await db.ExecuteAsync("CREATE TABLE dbo.H(id int NOT NULL, payload varchar(8000) NULL);");
        await db.ExecuteAsync("INSERT dbo.H SELECT TOP (40) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), REPLICATE('a', 150) FROM sys.all_objects;");
        await db.ExecuteAsync("UPDATE dbo.H SET payload = REPLICATE('b', 5000) WHERE id = 3;"); // no longer fits: forwarded
        var forwarded = await db.QueryAsync("SELECT forwarded_record_count FROM sys.dm_db_index_physical_stats(DB_ID(), OBJECT_ID('dbo.H'), 0, NULL, 'DETAILED')");
        Assert.True(Convert.ToInt64(forwarded[0]["forwarded_record_count"]) > 0, "test setup: the row should have been forwarded");

        var result = await db.ReadLogAsync();
        Assert.Equal(40, result.Changes.Count(c => c.Operation == ChangeOperation.Insert));
        Assert.DoesNotContain(result.Changes, c => c.Operation == ChangeOperation.Delete);
        var update = Assert.Single(result.Changes, c => c.Operation == ChangeOperation.Update);
        Assert.Equal(3, (update.Before ?? update.Key)?["id"]?.Value ?? update.After?["id"]?.Value);
    }

    [SqlServerFact]
    public async Task Many_rows_updated_by_one_statement_are_all_rebuilt()
    {
        await using var db = await TestDatabase.CreateAsync("lr_many");
        await db.ExecuteAsync("CREATE TABLE dbo.Orders(id int PRIMARY KEY, status varchar(20) NOT NULL, amount money NOT NULL);");
        await db.ExecuteAsync("INSERT dbo.Orders SELECT TOP (1500) ROW_NUMBER() OVER (ORDER BY (SELECT 1)), 'new', 10 FROM sys.all_objects a CROSS JOIN sys.all_objects b;");
        await db.TruncateLogAsync();
        await db.ExecuteAsync("UPDATE dbo.Orders SET status = 'shipped', amount = amount * 2 WHERE id % 3 = 0;");

        var result = await db.ReadLogAsync();
        Assert.Equal(500, result.Changes.Count);
        Assert.All(result.Changes, c =>
        {
            Assert.True(c.IsComplete);
            Assert.Equal("new", c.Before!["status"]!.Value);
            Assert.Equal("shipped", c.After!["status"]!.Value);
            Assert.Equal(10m, c.Before!["amount"]!.Value);
            Assert.Equal(20m, c.After!["amount"]!.Value);
        });
    }

    [SqlServerFact]
    public async Task Rows_of_a_non_unique_clustered_index_are_told_apart()
    {
        await using var db = await TestDatabase.CreateAsync("lr_nu");
        await db.ExecuteAsync("CREATE TABLE dbo.NU(k int NOT NULL, v varchar(10)); CREATE CLUSTERED INDEX cx ON dbo.NU(k);");
        await db.ExecuteAsync("INSERT dbo.NU VALUES (5, 'x'), (5, 'y'), (5, 'z');");
        await db.TruncateLogAsync();
        await db.ExecuteAsync("UPDATE dbo.NU SET v = 'yy' WHERE v = 'y'; UPDATE dbo.NU SET v = 'zzz' WHERE v = 'z';");

        var result = await db.ReadLogAsync();
        Assert.Equal(2, result.Changes.Count);
        Assert.Equal(("y", "yy"), ((string)result.Changes[0].Before!["v"]!.Value!, (string)result.Changes[0].After!["v"]!.Value!));
        Assert.Equal(("z", "zzz"), ((string)result.Changes[1].Before!["v"]!.Value!, (string)result.Changes[1].After!["v"]!.Value!));
        Assert.All(result.Changes, c => Assert.Contains("TOP (1)", ScriptGenerator.Generate(c, ScriptKind.Undo)));
    }

    [SqlServerFact]
    public async Task Rows_written_before_a_column_was_added_or_dropped_decode()
    {
        await using var db = await TestDatabase.CreateAsync("lr_alter");
        await db.ExecuteAsync("CREATE TABLE dbo.T(id int PRIMARY KEY, a varchar(10), b int);");
        await db.ExecuteAsync("INSERT dbo.T VALUES (1, 'one', 10);");
        await db.ExecuteAsync("ALTER TABLE dbo.T DROP COLUMN a; ALTER TABLE dbo.T ADD c int NOT NULL DEFAULT 7, d nvarchar(10) NULL;");
        await db.ExecuteAsync("INSERT dbo.T(id, b, d) VALUES (2, 20, N'two');");
        await db.ExecuteAsync("DELETE dbo.T;");

        var result = await db.ReadLogAsync(new AnalyzerOptions { Operations = new[] { ChangeOperation.Delete } });
        Assert.Equal(2, result.Changes.Count);
        var first = result.Changes.Single(c => Equals(c.Before!["id"]!.Value, 1)).Before!;
        Assert.Equal(10, first["b"]!.Value);
        Assert.Equal(7, first["c"]!.Value);
        Assert.True(first["d"]!.IsNull);
        Assert.Null(first["a"]);
        var second = result.Changes.Single(c => Equals(c.Before!["id"]!.Value, 2)).Before!;
        Assert.Equal("two", second["d"]!.Value);
    }
}
