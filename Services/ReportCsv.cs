using System.Globalization;
using System.Text;
using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Renders a report result as CSV.
    /// </summary>
    /// <remarks>
    /// Emits whichever shape the report produced: aggregate mode gives one row per group with a
    /// column per measure, detail mode one row per task with the chosen columns.
    ///
    /// The totals row is included when the report asked for it. A spreadsheet that disagrees with
    /// the screen it was exported from is worse than one with no totals at all - and because the
    /// service computes totals over every matching task rather than the returned rows, a truncated
    /// report's total is still the real one.
    /// </remarks>
    public static class ReportCsv
    {
        public static string Build(ReportResult result)
        {
            var sb = new StringBuilder();
            if (result == null) return sb.ToString();

            if (string.Equals(result.mode, ReportModes.Detail, StringComparison.OrdinalIgnoreCase))
            {
                BuildDetail(sb, result);
            }
            else
            {
                BuildAggregate(sb, result);
            }

            return sb.ToString();
        }

        private static void BuildAggregate(StringBuilder sb, ReportResult result)
        {
            var withPercent = result.rows.Any(r => r.percentOfTotal.HasValue);

            var header = new List<string> { Label(result.groupBy) };
            if (!string.IsNullOrWhiteSpace(result.splitBy)) header.Add(Label(result.splitBy));
            header.AddRange(result.measures);
            header.Add("tasks");
            if (withPercent) header.Add("percentOfTotal");

            sb.AppendLine(Join(header));

            foreach (var row in result.rows)
            {
                var cells = new List<string> { row.label ?? row.key };
                if (!string.IsNullOrWhiteSpace(result.splitBy)) cells.Add(row.splitLabel ?? row.splitKey);

                cells.AddRange(result.measures.Select(m =>
                    Num(row.values.TryGetValue(m, out var v) ? v : row.value)));

                cells.Add(row.count.ToString(CultureInfo.InvariantCulture));
                if (withPercent) cells.Add(row.percentOfTotal.HasValue ? Num(row.percentOfTotal.Value) : string.Empty);

                sb.AppendLine(Join(cells));
            }

            if (result.totals != null)
            {
                var cells = new List<string> { "Total" };
                if (!string.IsNullOrWhiteSpace(result.splitBy)) cells.Add(string.Empty);

                cells.AddRange(result.measures.Select(m =>
                    Num(result.totals.TryGetValue(m, out var v) ? v : 0)));

                cells.Add(Num(result.totals.TryGetValue("count", out var c) ? c : 0));
                if (withPercent) cells.Add("100");

                sb.AppendLine(Join(cells));
            }
        }

        private static void BuildDetail(StringBuilder sb, ReportResult result)
        {
            sb.AppendLine(Join(result.columns.Select(Label)));

            foreach (var row in result.detail)
            {
                sb.AppendLine(Join(result.columns.Select(c =>
                    row.cells.TryGetValue(c, out var v) ? v : null)));
            }
        }

        private static string Num(double value) =>
            value == Math.Floor(value)
                ? ((long)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString(CultureInfo.InvariantCulture);

        private static string Label(string key) =>
            string.IsNullOrWhiteSpace(key) ? string.Empty : key.Replace("custom:", string.Empty);

        private static string Join(IEnumerable<string> cells) => string.Join(",", cells.Select(Escape));

        /// <summary>RFC 4180. Task titles routinely contain commas and quotes.</summary>
        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;

            var needsQuotes = value.Contains(',') || value.Contains('"')
                              || value.Contains('\n') || value.Contains('\r');

            return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }
    }
}
