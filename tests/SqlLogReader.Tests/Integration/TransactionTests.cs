using SqlLogReader.Analysis;
using SqlLogReader.Log;

namespace SqlLogReader.Tests.Integration;

public class TransactionTests
{
    [SqlServerFact]
    public async Task Rolled_back_changes_are_hidden_unless_requested()
    {
        await using var db = await TestDatabase.CreateAsync("lr_rb");
        await db.ExecuteAsync("CREATE TABLE dbo.A(id int PRIMARY KEY, v varchar(20));");
        await db.ExecuteAsync("INSERT dbo.A VALUES (1, 'kept'), (2, 'two');");
        await db.ExecuteAsync("BEGIN TRAN oops; INSERT dbo.A VALUES (3, 'gone'); UPDATE dbo.A SET v = 'changed' WHERE id = 1; DELETE dbo.A WHERE id = 2; ROLLBACK;");
        await db.ExecuteAsync("UPDATE dbo.A SET v = 'after rollback' WHERE id = 1;");

        var normal = await db.ReadLogAsync();
        Assert.Equal(3, normal.Changes.Count); // 2 inserts + 1 update
        Assert.All(normal.Changes, c => Assert.Equal(TransactionState.Committed, c.Transaction.State));
        var update = normal.Changes.Single(c => c.Operation == ChangeOperation.Update);
        Assert.Equal("kept", update.Before!["v"]!.Value);
        Assert.Equal("after rollback", update.After!["v"]!.Value);

        var all = await db.ReadLogAsync(new AnalyzerOptions { IncludeRolledBack = true });
        var rolledBack = all.Changes.Where(c => c.Transaction.State == TransactionState.RolledBack).ToList();
        Assert.Equal(3, rolledBack.Count);
        Assert.All(rolledBack, c => Assert.Equal("oops", c.Transaction.Name));
        var undone = rolledBack.Single(c => c.Operation == ChangeOperation.Update);
        Assert.Equal("kept", undone.Before!["v"]!.Value);
        Assert.Equal("changed", undone.After!["v"]!.Value);
        Assert.Contains(all.Transactions, t => t.Name == "oops" && t.State == TransactionState.RolledBack);
    }

    [SqlServerFact]
    public async Task Changes_undone_by_a_savepoint_rollback_are_hidden()
    {
        await using var db = await TestDatabase.CreateAsync("lr_sp");
        await db.ExecuteAsync("CREATE TABLE dbo.A(id int PRIMARY KEY, v varchar(20));");
        await db.ExecuteAsync("BEGIN TRAN; INSERT dbo.A VALUES (10, 'a'); SAVE TRAN sp; INSERT dbo.A VALUES (11, 'b'); UPDATE dbo.A SET v = 'x' WHERE id = 10; ROLLBACK TRAN sp; UPDATE dbo.A SET v = 'final' WHERE id = 10; COMMIT;");
        var result = await db.ReadLogAsync(new AnalyzerOptions { UseCurrentData = false });

        var ids = result.Changes.Select(c => (c.Operation, (int)(c.After ?? c.Before)!["id"]!.Value!)).ToList();
        Assert.Contains((ChangeOperation.Insert, 10), ids);
        Assert.DoesNotContain((ChangeOperation.Insert, 11), ids);
        var updates10 = result.Changes.Where(c => c.Operation == ChangeOperation.Update && Equals(c.After!["id"]!.Value, 10)).ToList();
        var final = Assert.Single(updates10);
        Assert.Equal("a", final.Before!["v"]!.Value); // the 'x' update was undone by the savepoint rollback
        Assert.Equal("final", final.After!["v"]!.Value);
    }

    [SqlServerFact]
    public async Task Filters_select_tables_operations_users_and_time()
    {
        await using var db = await TestDatabase.CreateAsync("lr_filter");
        await db.ExecuteAsync("CREATE TABLE dbo.A(id int PRIMARY KEY); CREATE TABLE dbo.B(id int PRIMARY KEY);");
        await db.ExecuteAsync("INSERT dbo.A VALUES (1), (2); INSERT dbo.B VALUES (1);");
        await Task.Delay(1100);
        var middle = (DateTime)(await db.QueryAsync("SELECT GETDATE() AS now"))[0]["now"]!;
        await Task.Delay(1100);
        await db.ExecuteAsync("DELETE dbo.A WHERE id = 2; UPDATE dbo.B SET id = 5;");

        Assert.Equal(3, (await db.ReadLogAsync(new AnalyzerOptions { Tables = new[] { "A" } })).Changes.Count);
        Assert.Single((await db.ReadLogAsync(new AnalyzerOptions { Tables = new[] { "dbo.A" }, Operations = new[] { ChangeOperation.Delete } })).Changes);
        Assert.Equal(3, (await db.ReadLogAsync(new AnalyzerOptions { To = middle })).Changes.Count);

        // Updating a clustered key column is logged as delete + insert.
        var late = await db.ReadLogAsync(new AnalyzerOptions { From = middle });
        Assert.Equal(new[] { ChangeOperation.Delete, ChangeOperation.Delete, ChangeOperation.Insert }, late.Changes.Select(c => c.Operation));

        var login = (string)(await db.QueryAsync("SELECT SUSER_SNAME() AS n"))[0]["n"]!;
        Assert.Equal(6, (await db.ReadLogAsync(new AnalyzerOptions { UserName = login })).Changes.Count);
        Assert.Empty((await db.ReadLogAsync(new AnalyzerOptions { UserName = "nobody_" + Guid.NewGuid() })).Changes);
    }

    [SqlServerFact]
    public async Task Transaction_list_shows_ddl_and_dml_with_user_and_times()
    {
        await using var db = await TestDatabase.CreateAsync("lr_txlist");
        await db.ExecuteAsync("CREATE TABLE dbo.A(id int PRIMARY KEY, v int);");
        await db.ExecuteAsync("BEGIN TRAN batch1; INSERT dbo.A VALUES (1, 1); INSERT dbo.A VALUES (2, 2); UPDATE dbo.A SET v = 3; COMMIT;");
        await db.ExecuteAsync("TRUNCATE TABLE dbo.A;");

        var result = await db.ReadLogAsync();
        Assert.Contains(result.Transactions, t => t.Name == "CREATE TABLE");
        Assert.Contains(result.Transactions, t => t.Name == "TRUNCATE TABLE");
        var batch = Assert.Single(result.Transactions, t => t.Name == "batch1");
        Assert.Equal(4, batch.ChangeCount);
        Assert.Equal(TransactionState.Committed, batch.State);
        Assert.NotNull(batch.BeginTime);
        Assert.NotNull(batch.EndTime);
        Assert.False(string.IsNullOrEmpty(batch.UserName));
        Assert.All(result.Changes, c => Assert.Same(batch, c.Transaction));
    }

    [SqlServerFact]
    public async Task Log_backups_are_read_with_fn_dump_dblog()
    {
        await using var db = await TestDatabase.CreateAsync("lr_bak");
        var paths = await db.QueryAsync(
            "SELECT CONVERT(nvarchar(260), COALESCE(SERVERPROPERTY('InstanceDefaultBackupPath'), SERVERPROPERTY('InstanceDefaultDataPath'))) AS p");
        var dir = (string)paths[0]["p"]!;
        var file = dir.TrimEnd('/', '\\') + (dir.Contains('\\') ? "\\" : "/") + db.Name + ".trn";

        await db.ExecuteAsync("CREATE TABLE dbo.A(id int PRIMARY KEY, v nvarchar(20));");
        await db.ExecuteAsync("INSERT dbo.A VALUES (1, N'one'), (2, N'two');");
        await db.ExecuteOnMasterAsync($"BACKUP LOG [{db.Name}] TO DISK = N'{file}' WITH INIT;");
        await db.ExecuteAsync("DELETE dbo.A WHERE id = 1;");
        await db.ExecuteOnMasterAsync($"BACKUP LOG [{db.Name}] TO DISK = N'{file}' WITH NOINIT;"); // second backup set in the same file
        await db.ExecuteOnMasterAsync($"USE [{db.Name}]; CHECKPOINT;");
        await db.ExecuteAsync("UPDATE dbo.A SET v = N'TWO' WHERE id = 2;");

        var fromBackups = await db.ReadLogAsync(source: new LogSourceOptions { IncludeOnlineLog = false, BackupFiles = new[] { file } });
        Assert.Equal(new[] { ChangeOperation.Insert, ChangeOperation.Insert, ChangeOperation.Delete }, fromBackups.Changes.Select(c => c.Operation));
        Assert.Equal("one", fromBackups.Changes[2].Before!["v"]!.Value);

        var everything = await db.ReadLogAsync(source: new LogSourceOptions { BackupFiles = new[] { file } });
        Assert.Equal(4, everything.Changes.Count);
        var update = everything.Changes[3];
        Assert.Equal("two", update.Before!["v"]!.Value);
        Assert.Equal("TWO", update.After!["v"]!.Value);
        Assert.True(everything.Records.Select(r => r.Lsn).SequenceEqual(everything.Records.Select(r => r.Lsn).Distinct()));
    }
}
