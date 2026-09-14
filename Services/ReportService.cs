using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    public sealed class ReportRow
    {
        /// <summary>Raw group key, as stored.</summary>
        public string key { get; set; }

        /// <summary>Display label, resolved where the dimension has one.</summary>
        public string label { get; set; }

        /// <summary>Second-level key when SplitBy is set.</summary>
        public string splitKey { get; set; }
        public string splitLabel { get; set; }

        /// <summary>The first measure, kept so existing chart code and clients keep working.</summary>
        public double value { get; set; }

        /// <summary>Every requested measure, keyed by measure name.</summary>
        public Dictionary<string, double> values { get; set; } = new();

        /// <summary>Share of the report total for the first measure, when asked for.</summary>
        public double? percentOfTotal { get; set; }

        /// <summary>Task count behind the row, so a rate can show what it is a rate of.</summary>
        public int count { get; set; }
    }

    /// <summary>One task, for detail mode.</summary>
    public sealed class ReportDetailRow
    {
        public string id { get; set; }

        /// <summary>Column key to display value. Only the requested columns are populated.</summary>
        public Dictionary<string, string> cells { get; set; } = new();
    }

    public sealed class ReportResult
    {
        public string groupBy { get; set; }
        public string splitBy { get; set; }
        public string measure { get; set; }
        public string chartType { get; set; }

        /// <summary>Tasks matching the filters, before grouping.</summary>
        public int matched { get; set; }

        /// <summary>True when groups were dropped by the limit, so the UI can say so.</summary>
        public bool truncated { get; set; }

        public List<ReportRow> rows { get; set; } = new();

        /// <summary>Measures actually computed, in order. The first is the one a chart draws.</summary>
        public List<string> measures { get; set; } = new();

        /// <summary>"aggregate" or "detail".</summary>
        public string mode { get; set; } = ReportModes.Aggregate;

        /// <summary>Totals across every group. Null when not requested.</summary>
        public Dictionary<string, double> totals { get; set; }

        /// <summary>Populated in detail mode instead of <see cref="rows"/>.</summary>
        public List<ReportDetailRow> detail { get; set; } = new();

        /// <summary>Columns present on each detail row, in order.</summary>
        public List<string> columns { get; set; } = new();
    }

    public interface IReportService
    {
        Task<ReportResult> RunAsync(string organizationId, ReportDefinition definition, TaskStatusSet statuses);
    }

    /// <summary>
    /// Runs an ad-hoc aggregation over an organization's tasks.
    /// </summary>
    /// <remarks>
    /// Replaces bolting one more fixed shape onto DashboardStats every time someone wants a
    /// different cut. Management teams could not previously answer their own questions: the
    /// dashboard's chart set is fixed in JSX and the backend returned only what a DTO declared.
    ///
    /// TENANCY IS NOT NEGOTIABLE HERE. Every query starts from OrganizationId, taken from the
    /// authorized caller's context and never from the report definition - a definition is
    /// user-authored data, including on a shared report someone else wrote. A report builder
    /// widens what one endpoint can expose, so the org filter is applied first and separately
    /// from anything the definition asks for.
    ///
    /// Grouping happens in memory rather than in a Mongo $group. That is a deliberate trade: the
    /// dimension set includes array fields (assignees) and a per-organization custom-field bag,
    /// which need unwinding and key resolution that the aggregation framework makes awkward and
    /// unreadable. Volumes here are per-organization task counts, and the org filter is indexed.
    /// If an organization's task count ever makes this slow, the fix is a $group pipeline for the
    /// scalar dimensions - not a different shape of answer.
    /// </remarks>
    public class ReportService : IReportService
    {
        /// <summary>Hard ceiling on documents pulled into memory for one report.</summary>
        private const int MaxScan = 20000;

        /// <summary>Hard ceiling on returned groups, whatever the definition asks for.</summary>
        private const int MaxGroups = 200;

        private readonly IMongoCollection<NoteTask> _tasks;
        private readonly ILogger<ReportService> _logger;

        public ReportService(MongoDbSettings settings, ILogger<ReportService> logger)
        {
            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _tasks = database.GetCollection<NoteTask>("NoteTasks");
            _logger = logger;
        }

        public async Task<ReportResult> RunAsync(
            string organizationId, ReportDefinition definition, TaskStatusSet statuses)
        {
            definition ??= new ReportDefinition();
            var filters = definition.Filters ?? new ReportFilters();

            var result = new ReportResult
            {
                groupBy = definition.GroupBy,
                splitBy = definition.SplitBy,
                measure = definition.Measure,
                chartType = definition.ChartType,
            };

            if (string.IsNullOrWhiteSpace(organizationId)) return result;

            var tasks = await _tasks.Find(BuildFilter(organizationId, filters))
                .Limit(MaxScan)
                .ToListAsync();

            // Terminal-status filters run here rather than in Mongo: "terminal" is a per-org
            // registry answer including legacy aliases, not a value the database can evaluate.
            if (filters.OnlyCompleted.HasValue)
            {
                tasks = tasks
                    .Where(t => statuses.IsTerminal(t.Status) == filters.OnlyCompleted.Value)
                    .ToList();
            }

            if (filters.StatusSlugs is { Count: > 0 })
            {
                var wanted = filters.StatusSlugs.Select(statuses.Resolve).ToHashSet(StringComparer.Ordinal);
                tasks = tasks.Where(t => wanted.Contains(statuses.Resolve(t.Status))).ToList();
            }

            // Conditions run last, after the indexed org/project narrowing. They can only ever
            // reduce what was already fetched for this organization.
            if (definition.Conditions is { IsEmpty: false })
            {
                tasks = tasks
                    .Where(t => ReportConditionEvaluator.Matches(t, definition.Conditions, statuses))
                    .ToList();
            }

            result.matched = tasks.Count;

            var measures = ResolveMeasures(definition);
            result.measures = measures;
            result.mode = string.Equals(definition.Mode, ReportModes.Detail, StringComparison.OrdinalIgnoreCase)
                ? ReportModes.Detail
                : ReportModes.Aggregate;

            if (result.mode == ReportModes.Detail)
            {
                BuildDetail(result, tasks, definition, statuses);
                return result;
            }

            var groups = new Dictionary<(string, string), List<NoteTask>>();
            foreach (var task in tasks)
            {
                // A task with several assignees belongs to each of their groups, so per-person
                // workload adds up the way a manager expects. Every other dimension yields one key.
                foreach (var key in KeysFor(task, definition.GroupBy, statuses))
                {
                    foreach (var split in KeysFor(task, definition.SplitBy, statuses))
                    {
                        var bucket = (key, split);
                        if (!groups.TryGetValue(bucket, out var list))
                        {
                            groups[bucket] = list = new List<NoteTask>();
                        }
                        list.Add(task);
                    }
                }
            }

            var limit = Math.Clamp(definition.Limit <= 0 ? 50 : definition.Limit, 1, MaxGroups);

            var rows = groups
                .Select(g => new ReportRow
                {
                    key = g.Key.Item1,
                    label = LabelFor(g.Key.Item1, definition.GroupBy, statuses),
                    splitKey = g.Key.Item2,
                    splitLabel = g.Key.Item2 == null ? null : LabelFor(g.Key.Item2, definition.SplitBy, statuses),
                    value = Measure(g.Value, measures[0], statuses),
                    values = measures.ToDictionary(m => m, m => Measure(g.Value, m, statuses)),
                    count = g.Value.Count,
                })
                .ToList();

            rows = Sort(rows, definition, measures);

            result.truncated = rows.Count > limit;
            result.rows = rows.Take(limit).ToList();

            // Time buckets read as a series, not a ranking - unless a sort was asked for
            // explicitly, in which case the reader meant it.
            if (IsTimeDimension(definition.GroupBy) && string.IsNullOrWhiteSpace(definition.SortBy))
            {
                result.rows = result.rows.OrderBy(r => r.key, StringComparer.Ordinal).ToList();
            }

            if (definition.ShowTotals || definition.ShowPercentOfTotal)
            {
                // Totals are computed over EVERY matching task, not over the returned rows. A
                // truncated report whose total only adds up the visible rows is worse than no
                // total, because it looks authoritative.
                result.totals = measures.ToDictionary(m => m, m => Measure(tasks, m, statuses));
                result.totals["count"] = tasks.Count;
            }

            if (definition.ShowPercentOfTotal && result.totals != null)
            {
                var total = result.totals.TryGetValue(measures[0], out var t) ? t : 0;
                foreach (var row in result.rows)
                {
                    row.percentOfTotal = total == 0 ? 0 : Math.Round(100d * row.value / total, 2);
                }
            }

            if (!definition.ShowTotals) result.totals = null;

            if (tasks.Count >= MaxScan)
            {
                _logger.LogWarning(
                    "Report for {OrganizationId} hit the {MaxScan}-document scan cap; results are partial.",
                    organizationId, MaxScan);
            }

            return result;
        }

        /// <summary>Measures to compute: the list, or the legacy single measure.</summary>
        private static List<string> ResolveMeasures(ReportDefinition definition)
        {
            var measures = (definition.Measures ?? new List<string>())
                .Where(m => ReportMeasures.All.Contains(m))
                .Distinct()
                .ToList();

            if (measures.Count == 0)
            {
                measures.Add(ReportMeasures.All.Contains(definition.Measure)
                    ? definition.Measure
                    : ReportMeasures.Count);
            }

            return measures;
        }

        /// <summary>
        /// Applies the requested sort.
        /// </summary>
        /// <remarks>
        /// Sorting by a measure the report also computes is the common case, so SortBy accepts a
        /// measure key as well as "label" and "value". An unrecognised key falls back to the first
        /// measure rather than throwing - a saved report whose measure was later removed should
        /// still render.
        /// </remarks>
        private static List<ReportRow> Sort(
            List<ReportRow> rows, ReportDefinition definition, List<string> measures)
        {
            var ascending = string.Equals(definition.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            var sortBy = definition.SortBy;

            if (string.Equals(sortBy, "label", StringComparison.OrdinalIgnoreCase))
            {
                return (ascending
                        ? rows.OrderBy(r => r.label, StringComparer.OrdinalIgnoreCase)
                        : rows.OrderByDescending(r => r.label, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            var measure = !string.IsNullOrWhiteSpace(sortBy) && measures.Contains(sortBy)
                ? sortBy
                : measures[0];

            double Key(ReportRow r) => r.values.TryGetValue(measure, out var v) ? v : r.value;

            return (ascending
                    ? rows.OrderBy(Key).ThenBy(r => r.label, StringComparer.OrdinalIgnoreCase)
                    : rows.OrderByDescending(Key).ThenBy(r => r.label, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Detail mode: the tasks themselves, with the requested columns.
        /// </summary>
        /// <remarks>
        /// Capped by the same Limit as grouped rows. Detail mode returns one row per TASK rather
        /// than per group, so an uncapped one on a busy organization is a very different payload
        /// from an uncapped aggregate - the cap matters more here, not less.
        /// </remarks>
        private static void BuildDetail(
            ReportResult result, List<NoteTask> tasks, ReportDefinition definition, TaskStatusSet statuses)
        {
            var columns = (definition.Columns ?? new List<string>())
                .Where(c => ReportColumns.All.Contains(c)
                            || c.StartsWith(ReportFields.CustomPrefix, StringComparison.Ordinal))
                .Distinct()
                .ToList();

            if (columns.Count == 0) columns = ReportColumns.Default.ToList();
            result.columns = columns;

            var limit = Math.Clamp(definition.Limit <= 0 ? 100 : definition.Limit, 1, MaxDetailRows);

            var ordered = SortDetail(tasks, definition, statuses);
            result.truncated = ordered.Count > limit;

            result.detail = ordered.Take(limit).Select(task => new ReportDetailRow
            {
                id = task.Id,
                cells = columns.ToDictionary(c => c, c => CellFor(task, c, statuses)),
            }).ToList();

            if (definition.ShowTotals)
            {
                // Only the additive columns have a meaningful total; a sum of statuses does not.
                result.totals = new Dictionary<string, double>
                {
                    ["count"] = tasks.Count,
                    [ReportMeasures.EstimatedMinutes] = tasks.Sum(t => t.EstimatedDuration ?? 0),
                    [ReportMeasures.ActualMinutes] = tasks.Sum(t => t.RealDuration ?? 0),
                };
            }
        }

        private static List<NoteTask> SortDetail(
            List<NoteTask> tasks, ReportDefinition definition, TaskStatusSet statuses)
        {
            var ascending = string.Equals(definition.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
            var by = string.IsNullOrWhiteSpace(definition.SortBy) ? ReportFields.DueDate : definition.SortBy;

            // Cells are strings, but sorting them as strings puts 960 above 240 and 60 above
            // both. Numbers sort as numbers and dates as dates; everything else falls back to
            // text. ISO dates would sort correctly either way - the numeric columns would not.
            string Text(NoteTask t) => CellFor(t, by, statuses) ?? string.Empty;

            double? Numeric(NoteTask t) =>
                double.TryParse(Text(t), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;

            var allNumeric = tasks.Count > 0 && tasks.All(t => Numeric(t).HasValue);

            if (allNumeric)
            {
                return (ascending
                        ? tasks.OrderBy(t => Numeric(t)!.Value)
                        : tasks.OrderByDescending(t => Numeric(t)!.Value))
                    .ToList();
            }

            return (ascending
                    ? tasks.OrderBy(Text, StringComparer.OrdinalIgnoreCase)
                    : tasks.OrderByDescending(Text, StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>Display value for one detail cell. Ids stay ids; the client resolves names.</summary>
        private static string CellFor(NoteTask task, string column, TaskStatusSet statuses)
        {
            if (column.StartsWith(ReportFields.CustomPrefix, StringComparison.Ordinal))
            {
                var slug = column.Substring(ReportFields.CustomPrefix.Length);
                return task.CustomFields != null && task.CustomFields.TryGetValue(slug, out var v) ? v : null;
            }

            return column switch
            {
                ReportFields.Title => task.Title,
                ReportFields.Status => statuses.Resolve(task.Status),
                ReportFields.Priority => task.Priority,
                ReportFields.Category => task.Category,
                ReportFields.Project => task.ProjectId,
                ReportFields.Sprint => task.SprintId,
                ReportFields.Creator => task.Creator?.Id,
                ReportFields.Assignee => task.AssignedTo == null || task.AssignedTo.Count == 0
                    ? null
                    : string.Join(";", task.AssignedTo.Select(a => a?.Id).Where(i => i != null)),
                ReportFields.DueDate => Iso(task.DueDate),
                ReportFields.StartDate => Iso(task.StartDate),
                ReportFields.CreatedAt => task.CreatedAt.Year > 1 ? Iso(task.CreatedAt) : null,
                ReportFields.EstimatedMinutes => (task.EstimatedDuration ?? 0).ToString(),
                ReportFields.ActualMinutes => (task.RealDuration ?? 0).ToString(),
                _ => null,
            };
        }

        private static string Iso(DateTime? value) =>
            value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");

        /// <summary>Detail mode returns tasks, not groups, so it carries its own ceiling.</summary>
        private const int MaxDetailRows = 2000;

        /// <summary>Bucket for tasks with no usable creation date.</summary>
        private const string UnknownDate = "(no date)";

        private static bool HasDate(NoteTask task) => task.CreatedAt.Year > 1;

        private static bool IsTimeDimension(string dimension) =>
            dimension is ReportDimensions.Day or ReportDimensions.Week or ReportDimensions.Month;

        /// <summary>
        /// The Mongo-side filter. Organization first, always.
        /// </summary>
        private static FilterDefinition<NoteTask> BuildFilter(string organizationId, ReportFilters f)
        {
            var b = Builders<NoteTask>.Filter;

            // The flat OrganizationId from Phase 2: one indexed equality instead of a four-level
            // embedded walk that could not be indexed at all.
            var filter = b.Eq(t => t.OrganizationId, organizationId);

            if (f.ProjectIds is { Count: > 0 }) filter &= b.In(t => t.ProjectId, f.ProjectIds);
            if (f.SprintIds is { Count: > 0 }) filter &= b.In(t => t.SprintId, f.SprintIds);
            if (f.Priorities is { Count: > 0 }) filter &= b.In(t => t.Priority, f.Priorities);
            if (f.StartDate.HasValue) filter &= b.Gte(t => t.CreatedAt, f.StartDate.Value);
            if (f.EndDate.HasValue) filter &= b.Lte(t => t.CreatedAt, f.EndDate.Value);

            if (f.AssigneeIds is { Count: > 0 })
            {
                filter &= b.ElemMatch(t => t.AssignedTo,
                    Builders<Contributor>.Filter.In(c => c.Id, f.AssigneeIds));
            }

            return filter;
        }

        /// <summary>Group keys a task contributes to for one dimension.</summary>
        private static IEnumerable<string> KeysFor(NoteTask task, string dimension, TaskStatusSet statuses)
        {
            // No split dimension: one pass with a null key.
            if (string.IsNullOrWhiteSpace(dimension)) return new string[] { null };

            if (dimension.StartsWith(ReportDimensions.CustomPrefix, StringComparison.Ordinal))
            {
                var slug = dimension.Substring(ReportDimensions.CustomPrefix.Length);
                var value = task.CustomFields != null && task.CustomFields.TryGetValue(slug, out var v) ? v : null;
                return new[] { string.IsNullOrWhiteSpace(value) ? "(none)" : value };
            }

            switch (dimension)
            {
                case ReportDimensions.Status:
                    return new[] { statuses.Resolve(task.Status) ?? "(none)" };

                case ReportDimensions.Priority:
                    return new[] { Or(task.Priority, "(none)") };

                case ReportDimensions.Category:
                    return new[] { Or(task.Category, "(none)") };

                case ReportDimensions.Project:
                    return new[] { Or(task.ProjectId, "(none)") };

                case ReportDimensions.Sprint:
                    return new[] { Or(task.SprintId, "(no sprint)") };

                case ReportDimensions.Creator:
                    return new[] { Or(task.Creator?.Id, "(none)") };

                case ReportDimensions.Assignee:
                    // Unassigned work is a real answer to "who is loaded", not a row to drop.
                    if (task.AssignedTo == null || task.AssignedTo.Count == 0) return new[] { "(unassigned)" };
                    return task.AssignedTo.Select(a => Or(a?.Id, "(unassigned)")).Distinct();

                // Tasks written before CreatedAt was set server-side carry DateTime.MinValue.
                // Bucketing those under "0001-01" is worse than useless - it looks like a real
                // date. They get their own bucket and say what they are.
                case ReportDimensions.Day:
                    return new[] { HasDate(task) ? task.CreatedAt.ToString("yyyy-MM-dd") : UnknownDate };

                case ReportDimensions.Week:
                    if (!HasDate(task)) return new[] { UnknownDate };
                    // ISO-style week start (Monday), so weeks are comparable across months.
                    var monday = task.CreatedAt.Date.AddDays(
                        -(((int)task.CreatedAt.DayOfWeek + 6) % 7));
                    return new[] { monday.ToString("yyyy-MM-dd") };

                case ReportDimensions.Month:
                    return new[] { HasDate(task) ? task.CreatedAt.ToString("yyyy-MM") : UnknownDate };

                default:
                    // An unknown dimension collapses to one bucket rather than throwing. A report
                    // saved against a since-removed custom field should degrade, not 500.
                    return new[] { "(all)" };
            }
        }

        private static string LabelFor(string key, string dimension, TaskStatusSet statuses)
        {
            if (key == null) return null;

            // Ids are resolved to names client-side, where the project and member lists already
            // live. Sending them back through here would mean a lookup per row.
            if (dimension == ReportDimensions.Status) return statuses.Find(key)?.Label ?? key;

            return key;
        }

        private static double Measure(List<NoteTask> tasks, string measure, TaskStatusSet statuses)
        {
            switch (measure)
            {
                case ReportMeasures.CompletedCount:
                    return tasks.Count(t => statuses.IsTerminal(t.Status));

                case ReportMeasures.CompletionRate:
                    return tasks.Count == 0
                        ? 0
                        : Math.Round(100d * tasks.Count(t => statuses.IsTerminal(t.Status)) / tasks.Count, 2);

                case ReportMeasures.EstimatedMinutes:
                    return tasks.Sum(t => t.EstimatedDuration ?? 0);

                case ReportMeasures.ActualMinutes:
                    return tasks.Sum(t => t.RealDuration ?? 0);

                case ReportMeasures.EstimateVariance:
                    // Only over tasks that have BOTH numbers. Counting a task with no estimate as
                    // "came in under by its whole actual" would make every team look reckless.
                    var comparable = tasks
                        .Where(t => (t.EstimatedDuration ?? 0) > 0 && (t.RealDuration ?? 0) > 0)
                        .ToList();
                    return comparable.Sum(t => (t.RealDuration ?? 0) - (t.EstimatedDuration ?? 0));

                case ReportMeasures.Count:
                default:
                    return tasks.Count;
            }
        }

        private static string Or(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
