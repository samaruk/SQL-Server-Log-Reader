using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SqlLogReader.Analysis;
using SqlLogReader.Decoding;

namespace SqlLogReader.Export;

/// <summary>Writes decoded changes to CSV or JSON.</summary>
public static class ChangeExporter
{
    private static readonly string[] CsvHeader =
    {
        "LSN", "TransactionId", "TransactionName", "BeginTime", "EndTime", "User", "State",
        "Operation", "Table", "Key", "Column", "OldValue", "NewValue", "Complete",
    };

    /// <summary>One CSV line per changed column (inserts/deletes: one line per column of the row).</summary>
    public static void WriteCsv(TextWriter writer, IEnumerable<RowChange> changes)
    {
        writer.WriteLine(string.Join(",", CsvHeader));
        foreach (var c in changes)
        {
            var columns = c.ChangedColumns.ToList();
            if (columns.Count == 0)
            {
                WriteCsvLine(writer, c, null);
                continue;
            }
            foreach (var column in columns)
                WriteCsvLine(writer, c, column);
        }
    }

    private static void WriteCsvLine(TextWriter writer, RowChange c, ColumnChange? column)
    {
        var fields = new[]
        {
            c.Lsn.ToString(),
            c.Transaction.TransactionId,
            c.Transaction.Name ?? "",
            FormatTime(c.Transaction.BeginTime),
            FormatTime(c.Transaction.EndTime),
            c.UserName,
            c.Transaction.State.ToString(),
            c.Operation.ToString().ToUpperInvariant(),
            c.TableName,
            c.KeyText,
            column?.Name ?? "",
            column?.Before?.DisplayText ?? "",
            column?.After?.DisplayText ?? "",
            c.IsComplete ? "yes" : "no",
        };
        writer.WriteLine(string.Join(",", fields.Select(Csv)));
    }

    public static void WriteJson(Stream stream, IEnumerable<RowChange> changes)
    {
        var options = new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var json = new Utf8JsonWriter(stream, options);
        json.WriteStartArray();
        foreach (var c in changes)
        {
            json.WriteStartObject();
            json.WriteString("lsn", c.Lsn.ToString());
            json.WriteString("operation", c.Operation.ToString().ToUpperInvariant());
            json.WriteString("table", c.TableName);
            json.WriteStartObject("transaction");
            json.WriteString("id", c.Transaction.TransactionId);
            json.WriteString("name", c.Transaction.Name);
            json.WriteString("beginTime", FormatTime(c.Transaction.BeginTime));
            json.WriteString("endTime", FormatTime(c.Transaction.EndTime));
            json.WriteString("user", c.UserName);
            json.WriteString("state", c.Transaction.State.ToString());
            json.WriteEndObject();
            json.WriteString("pageId", c.Record.PageId);
            if (c.Record.SlotId.HasValue) json.WriteNumber("slotId", c.Record.SlotId.Value);
            json.WriteBoolean("complete", c.IsComplete);
            json.WriteString("key", c.KeyText);
            json.WriteStartArray("columns");
            foreach (var column in c.Columns)
            {
                json.WriteStartObject();
                json.WriteString("name", column.Name);
                json.WriteString("type", column.Column.FullTypeName);
                json.WriteBoolean("changed", column.IsChanged);
                if (column.Before != null) WriteValue(json, "oldValue", column.Before);
                if (column.After != null) WriteValue(json, "newValue", column.After);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            if (c.Notes.Count > 0)
            {
                json.WriteStartArray("notes");
                foreach (var note in c.Notes) json.WriteStringValue(note);
                json.WriteEndArray();
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    private static void WriteValue(Utf8JsonWriter json, string name, ColumnValue value)
    {
        switch (value.State)
        {
            case ValueState.Null:
                json.WriteNull(name);
                break;
            case ValueState.Value:
                switch (value.Value)
                {
                    case bool b: json.WriteBoolean(name, b); break;
                    case byte or short or int or long: json.WriteNumber(name, Convert.ToInt64(value.Value, CultureInfo.InvariantCulture)); break;
                    case decimal m: json.WriteNumber(name, m); break;
                    case double d when double.IsFinite(d): json.WriteNumber(name, d); break;
                    case float f when float.IsFinite(f): json.WriteNumber(name, f); break;
                    default: json.WriteString(name, value.DisplayText); break;
                }
                break;
            default:
                json.WriteString(name, value.DisplayText);
                break;
        }
    }

    private static string FormatTime(DateTime? time) =>
        time?.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "";

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Exports to a file; the format is chosen from the extension (.csv or .json).</summary>
    public static void ExportToFile(string path, IEnumerable<RowChange> changes)
    {
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.Create(path);
            WriteJson(stream, changes);
            return;
        }
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        WriteCsv(writer, changes);
    }
}
