namespace SqlLogReader.Cli;

internal static class Help
{
    public const string Text = """
        SQL Server Log Reader - decode INSERT / UPDATE / DELETE operations from a SQL Server transaction log.

        Usage: sqllogreader <command> [options]

        Commands
          databases       List the databases on the server (recovery model, log size, ...)
          read            Decode row changes from the transaction log of a database
          transactions    List the transactions found in the log (who, when, which tables)
          backups         List transaction log backups recorded in msdb for a database
          vlfs            Show the virtual log files of a database
          raw             Dump raw log records (fn_dblog)
          attach          Attach a database from .mdf/.ldf files so its log can be read
          ldf             Read an .ldf file directly (no SQL Server needed to read the file)

        Connection
          -S, --server <name>            Server or server\instance (default: localhost)
          -U, --user <login>             SQL Server login (default: Windows authentication)
          -P, --password <password>      Password (or set SQLLOGREADER_PASSWORD)
          --connection-string <string>   Use a full connection string instead
          --encrypt                      Require an encrypted connection
          --no-trust-cert                Validate the server certificate
          -d, --database <name>          Database whose log is read

        read / transactions
          --from <time>  --to <time>     Transactions that started in this range (server local time)
          -t, --table <name>             Only these tables (repeat or comma-separate; "Patients" or "dbo.Patients")
          --op <insert,update,delete>    Only these operations
          --login <name>                 Only transactions started by this login
          --transaction <id>             Only this transaction (e.g. 0000:0000037f)
          --include-rolled-back          Also show changes that were rolled back
          --all-transactions             (transactions) also list internal system transactions
          --backup <file>                Also read a transaction log backup (.trn) - path on the server; repeatable.
                                         Use "a.trn|b.trn" for a backup striped over several files.
          --no-online-log                Read only the --backup files
          --include-inactive             Also read the inactive part of the log (trace flag 2537)
          --no-current-data              Do not read current rows (DBCC PAGE) to rebuild old update values
          --start-lsn / --end-lsn <lsn>  LSN range, e.g. 0000002A:00000168:0001
          --timeout <seconds>            Command timeout (default: none)

        read output
          -f, --format <format>          table (default), details, csv, json, undo, redo
          -o, --output <file>            Write to a file instead of the console

        attach
          -d, --database <name> --mdf <path> --ldf <path>    Paths as seen by the SQL Server service

        raw
          --top <n>                      Number of records (default 1000, 0 = all)

        ldf
          --file <path.ldf>              The log file (a copy, or the file of a detached database);
                                         repeat for a database with several log files
          -S <server> -d <database>      A database with the same tables (the database itself or an attached
                                         copy) to decode row values; without it only transactions are listed
          --transactions                 List transactions instead of row changes
          --raw [--top <n>]              Dump the raw log records
          --vlfs                         Show the virtual log files found in the file
          plus the read filters and output options above

        Examples
          sqllogreader databases -S .\SQLEXPRESS
          sqllogreader read -S . -d "Care&Cure" --from "2026-10-01" --table Patients
          sqllogreader read -S . -d "Care&Cure" --op delete -f undo -o restore_deleted_rows.sql
          sqllogreader read -S . -U sa -d "Care&Cure" --backup "D:\Backup\CareCure_log.trn" -f details
          sqllogreader attach -S . -d CareCure_Copy --mdf "D:\Data\Care&Cure.mdf" --ldf "D:\Data\Care&Cure_log.ldf"
          sqllogreader ldf --file "D:\Copy\Care&Cure_log.ldf" -S . -d "Care&Cure" -f details

        Reading the log requires membership in the sysadmin server role.
        """;
}
