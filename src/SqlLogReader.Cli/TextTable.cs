using System.Text;

namespace SqlLogReader.Cli;

/// <summary>Renders rows as a fixed-width text table.</summary>
internal static class TextTable
{
    public static string Render(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows, IReadOnlyList<int>? maxWidths = null)
    {
        var data = rows.Select(r => r.Select((cell, i) => Clip(Clean(cell), maxWidths != null && i < maxWidths.Count ? maxWidths[i] : 0)).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, data.Count == 0 ? 0 : data.Max(r => i < r.Length ? r[i].Length : 0))).ToArray();

        var sb = new StringBuilder();
        AppendRow(sb, headers, widths);
        sb.AppendLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in data)
            AppendRow(sb, row, widths);
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, IReadOnlyList<string> cells, int[] widths)
    {
        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0) sb.Append(" | ");
            var cell = i < cells.Count ? cells[i] : "";
            sb.Append(i == widths.Length - 1 ? cell : cell.PadRight(widths[i]));
        }
        sb.AppendLine();
    }

    private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

    private static string Clip(string s, int max) => max <= 3 || s.Length <= max ? s : s[..(max - 3)] + "...";
}
