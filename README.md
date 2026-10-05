# SQL Server Log Reader

A C# (.NET 8) tool that reads the **SQL Server transaction log** (`.ldf`) and turns it into readable history:
who inserted, updated or deleted which rows, when, with the old and new values. It can also generate
**undo** and **redo** T-SQL scripts, for example to bring back accidentally deleted rows.

It comes as:

| Project | What it is |
|---|---|
| `src/SqlLogReader.WinForms` | Windows desktop application (`SqlLogReader.exe`) |
| `src/SqlLogReader.Cli` | Cross-platform command line tool (`sqllogreader`) |
| `src/SqlLogReader.Core` | The library both are built on |
| `tests/SqlLogReader.Tests` | Unit tests, plus integration tests that run against a real SQL Server |

## What it reads

* **The online transaction log** of a database (`sys.fn_dblog`), optionally including the inactive part that has not been overwritten yet (trace flag 2537).
* **Transaction log backups** (`.trn`, read with `sys.fn_dump_dblog`), including files that hold several backup sets and backups striped over several files.
* **`.ldf` files directly, without SQL Server reading them**, for example `Care&Cure_log.ldf` from a detached or crashed database (*File → Open .ldf log file* in the GUI, or `sqllogreader ldf`).
  The file reader parses virtual log files, log blocks and records itself. It also recovers records from inactive
  parts of the log that `fn_dblog` no longer shows. To turn rows into column values it needs the table layouts:
  point it at a database with the same tables, such as the original database or an attached copy of the `.mdf`.
  Without one, it still lists the transactions (name, begin and end time, login SID and affected allocation units).
* **Database files you only have on disk** can also be attached: attach a *copy* of the `.mdf` + `.ldf` to a SQL Server instance (*Tools → Attach database files* in the GUI, or `sqllogreader attach`), then read the attached database's log like any other.

For every `INSERT`, `UPDATE` and `DELETE` on a table it shows:

* the LSN, transaction id and name, begin and commit time, login, and whether the transaction was committed or rolled back;
* every column value of inserted and deleted rows;
* the old and new values of updated rows. Each row's history is chained through the log (insert → updates → delete). When the original insert is no longer in the log, the reader starts from the row's current bytes on the data page (`DBCC PAGE`) and walks backwards. In both cases the result is the complete before and after row.

It also lists schema changes (CREATE / ALTER / DROP / TRUNCATE …) in the transaction list.

## Requirements

* Windows, Linux or macOS with the [.NET 8 SDK](https://dotnet.microsoft.com/download) (the GUI needs Windows).
* SQL Server 2012 or later. The test suite runs against SQL Server 2017, 2019 and 2022.
  Azure SQL Database does not expose `fn_dblog`.
* A login in the **sysadmin** server role. `fn_dblog`, `fn_dump_dblog` and `DBCC PAGE` require it.
  Reading an `.ldf` file needs no server at all. Decoding its rows needs table layouts from a database, and
  reading those (`sys.system_internals_partition_columns`) also requires sysadmin.

## Quick start: desktop application (Windows)

```powershell
dotnet run --project src/SqlLogReader.WinForms
```

1. Enter the server (for example `localhost`, `.\SQLEXPRESS`), choose Windows or SQL Server authentication, and click **Connect**.
2. Pick the database (for example `Care&Cure`). Keep **Online log** ticked, and optionally add `.trn` log backups. *From backup history…* lists the log backups that msdb knows about.
3. Optionally filter by time range, table, operation or login, then click **Read log**.
4. Select a change to see its row values (changed columns are highlighted), the undo/redo script and the raw log record.
   Right-click rows to copy or save undo/redo scripts. Use *File → Export* for CSV/JSON.

If you only have the `.mdf` / `.ldf` files, you have two options:

* **File → Open .ldf log file…** reads the log file directly. Connect and select a database with the same tables
  first, so that rows can be decoded.
* **Tools → Attach database files…** attaches copies of the files to the server. The new database then appears in the list.

## Quick start: command line

```bash
dotnet build src/SqlLogReader.Cli -c Release
cd src/SqlLogReader.Cli/bin/Release/net8.0

# Windows authentication; add -U <login> -P <password> for SQL authentication
sqllogreader databases -S localhost
sqllogreader read -S localhost -d "Care&Cure"                         # all changes in the online log
sqllogreader read -S localhost -d "Care&Cure" -t Patients -f details  # one table, every column, old/new values
sqllogreader read -S localhost -d "Care&Cure" --from "2026-10-04 08:00" --to "2026-10-04 18:00" --op delete
sqllogreader read -S localhost -d "Care&Cure" --op delete -f undo -o restore_deleted_rows.sql
sqllogreader read -S localhost -d "Care&Cure" --backup "D:\Backup\CareCure_log.trn" --no-online-log
sqllogreader transactions -S localhost -d "Care&Cure"
sqllogreader attach -S localhost -d CareCure_Copy --mdf "D:\Copy\Care&Cure.mdf" --ldf "D:\Copy\Care&Cure_log.ldf"

# read an .ldf file directly; -S/-d give the table layouts (here: the original database)
sqllogreader ldf --file "D:\Copy\Care&Cure_log.ldf" -S localhost -d "Care&Cure" -f details
sqllogreader ldf --file "D:\Copy\Care&Cure_log.ldf"            # no database: list transactions only
sqllogreader ldf --file "D:\Copy\Care&Cure_log.ldf" --vlfs     # virtual log files in the file
```

Output formats (`-f`): `table` (default), `details`, `csv`, `json`, `undo`, `redo`. Run `sqllogreader help` for every option.

Example (`-f table`):

```
LSN                    | Time                | User | Operation | Table        | Key           | Changes
-----------------------+---------------------+------+-----------+--------------+---------------+-------------------------------------------
0000002A:00000168:001C | 2026-10-04 10:08:46 | sa   | INSERT    | dbo.Patients | PatientId = 1 | PatientId = 1; FirstName = Ali; LastName = Khan; ...
0000002A:00000178:0002 | 2026-10-04 10:08:46 | sa   | UPDATE    | dbo.Patients | PatientId = 2 | LastName: NULL -> Ahmed; Balance: NULL -> 99.99
0000002A:00000180:0002 | 2026-10-04 10:08:46 | sa   | DELETE    | dbo.Patients | PatientId = 1 | PatientId = 1; FirstName = Ali; LastName = Khan; ...
```

Undo script for that delete (`-f undo`):

```sql
SET IDENTITY_INSERT [dbo].[Patients] ON;
INSERT INTO [dbo].[Patients] ([PatientId], [FirstName], [LastName], [BirthDate], [Balance], ...) VALUES (1, N'Ali', N'Khan', '1990-05-17', 1234.56, ...);
SET IDENTITY_INSERT [dbo].[Patients] OFF;
```

## How much history is in the log?

The log only keeps what has not been truncated yet:

* **SIMPLE recovery:** the log is truncated at every checkpoint, so only recent activity is available.
  `--include-inactive` (trace flag 2537) can recover more, as long as the space has not been reused.
* **FULL / BULK_LOGGED recovery:** everything since the last log backup is in the online log, and older activity is in the log backups.
  Read them with `--backup` or *Log backups* in the GUI. `sqllogreader backups -d <db>` lists them.

## Supported data

All common column types are decoded: `tinyint`, `smallint`, `int`, `bigint`, `bit`, `decimal`/`numeric` (up to 38 digits),
`money`, `smallmoney`, `float`, `real`, `date`, `time`, `datetime`, `smalldatetime`, `datetime2`, `datetimeoffset`,
`uniqueidentifier`, `char`/`varchar`/`nchar`/`nvarchar` (any collation and code page, including UTF-8), `binary`/`varbinary`,
`sql_variant`, `rowversion`, `hierarchyid` (and other CLR types as binary), and in-row `varchar(max)`/`nvarchar(max)`/`varbinary(max)`.
Tables can be heaps or clustered (unique or not), and they can have dropped or later-added columns.

Known limitations:

* Values stored **off-row** (large `(max)` values, `text`/`ntext`/`image`, row-overflow data) are shown as *not available*. The scripts mark them with a warning.
* **Compressed** tables (ROW/PAGE), columnstore indexes, sparse columns and `xml` values are not decoded.
* `TRUNCATE TABLE` and minimally-logged bulk operations do not log individual rows. They appear only in the transaction list.
* If the history of an updated row is not available at all (neither in the log nor in the current data), only the
  fixed-length columns inside the changed bytes are decoded. Such changes are marked as incomplete (`*`).

## How it works

1. `SchemaLoader` reads the physical layout of every table: `sys.system_internals_partition_columns` gives
   column offsets, null-bitmap positions and dropped columns, keyed by allocation unit.
2. `SqlLogSource` reads the log records with `fn_dblog` / `fn_dump_dblog`, including the complete binary `[Log Record]`.
   `LogRecordPayload` splits each record into its elements: the row image for inserts and deletes, and the changed byte ranges plus the clustered key for updates.
3. `RowDecoder` parses SQL Server's FixedVar row format (status bits, fixed data, null bitmap, variable-length offsets) and `ValueDecoder` converts the stored bytes of every type.
4. `LogAnalyzer` groups records into transactions. It chains every row's operations by clustered key (or RID for heaps), pairs rollback (compensation) records with the operations they undo, and propagates known row images forwards and backwards through the updates (`RowPatch`). If needed, it anchors the chain on the current page image (`SqlPageRowSource`, `DBCC PAGE`).
5. `LdfFileReader` reads `.ldf` files without SQL Server. It walks the virtual log file headers and validates every
   log block (parity bits, sequence and offset). It restores the sector bytes that SQL Server replaces with
   parity bits, and decodes records with the same header layout `fn_dblog` uses: operation, context, transaction,
   page/slot, allocation unit, begin/commit times, transaction names and login SIDs. Tests check it record for
   record against `fn_dblog` on SQL Server 2017, 2019 and 2022.
6. `ScriptGenerator` writes undo/redo T-SQL that identifies rows by primary key (or all comparable columns, with `TOP (1)`), and `ChangeExporter` writes CSV/JSON.

## Building and testing

```bash
dotnet build src/SqlLogReader.Cli            # library + CLI (any OS)
dotnet build SqlServerLogReader.sln          # everything, including the Windows GUI (*)
dotnet test tests/SqlLogReader.Tests         # unit tests
```

(*) Building the GUI on Linux/macOS needs Microsoft's .NET SDK build. Some distribution packages leave out the Windows Desktop targets.

The integration tests create throw-away databases, run DML against them and check the decoded results against the actual table contents.
That includes every data type, updates rebuilt from history and from current pages, rollbacks, savepoints, heap row forwarding, log backups, and undo scripts restoring the original data.
The offline `.ldf` reader is tested against a captured log file (`tests/SqlLogReader.Tests/Fixtures`), with no server needed.
They run when `SQLLOGREADER_TEST_CONNECTION` is set to a sysadmin connection string, for example with SQL Server in Docker:

```bash
docker run -d --name mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Your#Passw0rd' -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
export SQLLOGREADER_TEST_CONNECTION='Server=localhost;User ID=sa;Password=Your#Passw0rd;TrustServerCertificate=True'
dotnet test tests/SqlLogReader.Tests
```

## Safety notes

* Reading the log is read-only. `--include-inactive` enables trace flag 2537 for the reading session only.
* SQL Server keeps the files of an attached database open. To read the `.ldf` of a running database, use a copy
  taken from a backup, a detached database, or a stopped instance. Records still in the log buffer are not in the file yet.
* Review the generated scripts before running them. They run inside one transaction (`SET XACT_ABORT ON`).
* Attaching database files runs crash recovery, which modifies the files. Always attach **copies**.
