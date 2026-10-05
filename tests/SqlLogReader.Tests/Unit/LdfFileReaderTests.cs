using System.Globalization;
using System.IO.Compression;
using SqlLogReader.Analysis;
using SqlLogReader.Log;
using SqlLogReader.Schema;

namespace SqlLogReader.Tests.Unit;

/// <summary>
/// The fixture is the 1 MB log of a SQL Server 2022 database after:
/// <code>
/// CREATE TABLE dbo.Patients (... as in TestSchemas.Patients, CreatedAt DEFAULT '2026-10-04T10:00:00' ...);
/// INSERT (N'Ali', N'Khan', '1990-05-17', 1234.56, 'AB123', 'first note', 'A9E79E90-...', 3.14, 0), (N'Sara', NULL..., 1);
/// UPDATE dbo.Patients SET LastName = N'Ahmed', Balance = 99.99 WHERE PatientId = 2;
/// DELETE dbo.Patients WHERE PatientId = 1;
/// BEGIN TRAN undone; UPDATE dbo.Patients SET FirstName = N'Nobody' WHERE PatientId = 2; ROLLBACK;
/// UPDATE dbo.Patients SET Notes = 'vip' WHERE PatientId = 2;
/// </code>
/// together with fn_dblog's view of the same log (trace flag 2537 on).
/// </summary>
public class LdfFileReaderTests : IDisposable
{
    private const long PatientsAllocationUnit = 72057594052476928;
    private readonly string _ldfPath;

    public LdfFileReaderTests()
    {
        _ldfPath = Path.Combine(Path.GetTempPath(), $"ldffixture_{Guid.NewGuid():N}.ldf");
        using var input = new GZipStream(File.OpenRead(Fixture("ldffixture_log.ldf.gz")), CompressionMode.Decompress);
        using var output = File.Create(_ldfPath);
        input.CopyTo(output);
    }

    public void Dispose() => File.Delete(_ldfPath);

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static Dictionary<string, string[]> ExpectedRecords()
    {
        using var reader = new StreamReader(new GZipStream(File.OpenRead(Fixture("ldffixture_fn_dblog.txt.gz")), CompressionMode.Decompress));
        var result = new Dictionary<string, string[]>();
        while (reader.ReadLine() is { } line)
        {
            var p = line.Split('|');
            if (p.Length >= 15 && Lsn.TryParse(p[0], out _)) result[p[0]] = p;
        }
        return result;
    }

    [Fact]
    public void Reads_the_same_records_as_fn_dblog()
    {
        var result = LdfFileReader.Read(_ldfPath);
        Assert.Empty(result.Warnings);
        Assert.Equal(4, result.VirtualLogFiles.Count);

        var expected = ExpectedRecords();
        Assert.Equal(expected.Keys.OrderBy(k => k), result.Records.Select(r => r.Lsn.ToString()));

        foreach (var r in result.Records)
        {
            var p = expected[r.Lsn.ToString()];
            var where = $"{r.Lsn} {p[1]}";
            Assert.True(p[1] == r.Operation || r.Operation.StartsWith("LOP_0x", StringComparison.Ordinal), where);
            Assert.True(p[2] == r.Context || r.Context!.StartsWith("LCX_0x", StringComparison.Ordinal), where);
            Assert.Equal(p[3], r.TransactionId);
            if (LogOperationCodes.IsRowOperation(r.RecordBytes![22]))
            {
                Assert.Equal(p[4], r.AllocUnitId?.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(p[5], r.PageId, ignoreCase: true);
                Assert.Equal(p[6], r.SlotId?.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(p[7], r.OffsetInRow?.ToString(CultureInfo.InvariantCulture));
                Assert.Equal(p[8], r.ModifySize?.ToString(CultureInfo.InvariantCulture));
            }
            if (r.Operation == LogAnalyzer.BeginXact)
            {
                Assert.Equal(p[9], r.TransactionName);
                Assert.Equal(p[10], r.BeginTime?.ToString("yyyy/MM/dd HH:mm:ss:fff", CultureInfo.InvariantCulture));
                Assert.Equal(p[12], r.TransactionSid == null ? "" : Convert.ToHexString(r.TransactionSid));
            }
            if (r.Operation is LogAnalyzer.CommitXact or LogAnalyzer.AbortXact)
                Assert.Equal(p[11], r.EndTime?.ToString("yyyy/MM/dd HH:mm:ss:fff", CultureInfo.InvariantCulture));
            Assert.Equal(p[13].Contains("COMPENSATION"), r.IsCompensation);
            Assert.StartsWith(p[14], Convert.ToHexString(r.RecordBytes!), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Decodes_row_changes_offline_with_a_known_table_layout()
    {
        var records = LdfFileReader.Read(_ldfPath).Records;
        var schema = new DatabaseSchema("ldffixture", new[] { TestSchemas.Patients(PatientsAllocationUnit) });
        var analyzer = new LogAnalyzer(schema);

        var result = await analyzer.AnalyzeAsync(records);
        Assert.Equal(
            new[] { ChangeOperation.Insert, ChangeOperation.Insert, ChangeOperation.Update, ChangeOperation.Delete, ChangeOperation.Update },
            result.Changes.Select(c => c.Operation));
        Assert.All(result.Changes, c => Assert.True(c.IsComplete));

        var ali = result.Changes[0].After!;
        Assert.Equal("Ali", ali["FirstName"]!.Value);
        Assert.Equal(1234.56m, ali["Balance"]!.Value);
        Assert.Equal(new DateTime(2026, 10, 4, 10, 0, 0), ali["CreatedAt"]!.Value);

        var update = result.Changes[2];
        Assert.Equal(new[] { "LastName", "Balance" }, update.ChangedColumns.Select(c => c.Name));
        Assert.Equal("Ahmed", update.After!["LastName"]!.Value);

        Assert.Equal("Ali", result.Changes[3].Before!["FirstName"]!.Value);
        Assert.Equal("vip", result.Changes[4].After!["Notes"]!.Value);
        Assert.True(result.Changes[4].Before!["Notes"]!.IsNull);
        Assert.Equal("DELETE", result.Changes[3].Transaction.Name);
        Assert.Equal(new byte[] { 1 }, result.Changes[3].Transaction.UserSid);

        var withRollback = await analyzer.AnalyzeAsync(records, new AnalyzerOptions { IncludeRolledBack = true });
        var undone = Assert.Single(withRollback.Changes, c => c.Transaction.State == TransactionState.RolledBack);
        Assert.Equal("undone", undone.Transaction.Name);
        Assert.Equal("Sara", undone.Before!["FirstName"]!.Value);
        Assert.Equal("Nobody", undone.After!["FirstName"]!.Value);
    }

    [Fact]
    public void Rejects_files_that_are_not_logs()
    {
        var path = Path.Combine(Path.GetTempPath(), $"notalog_{Guid.NewGuid():N}.ldf");
        File.WriteAllBytes(path, new byte[100]);
        try
        {
            Assert.Throws<InvalidDataException>(() => LdfFileReader.Read(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
