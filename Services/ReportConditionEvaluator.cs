using System.Globalization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Decides whether a task satisfies a report's conditions.
    /// </summary>
    /// <remarks>
    /// Evaluated in memory, after the organization filter has already run in Mongo. That ordering
    /// is not an implementation detail: conditions are user-authored, and several of them
    /// (terminal status, overdue) depend on the per-organization status registry, which the
    /// database cannot evaluate. Keeping tenancy in the query and expressiveness here means a
    /// condition can never widen what was fetched - only narrow it.
    ///
    /// Every comparison is null-tolerant and type-tolerant. A report is a question, not a
    /// contract: a condition against a field a task has not filled in should exclude that task,
    /// never throw and abandon the run.
    /// </remarks>
    public static class ReportConditionEvaluator
    {
        public static bool Matches(NoteTask task, ReportConditions conditions, TaskStatusSet statuses)
        {
            if (conditions == null || conditions.IsEmpty) return true;

            var groups = conditions.Groups
                .Where(g => g?.Conditions is { Count: > 0 })
                .ToList();

            if (groups.Count == 0) return true;

            var orGroups = string.Equals(conditions.Join, "or", StringComparison.OrdinalIgnoreCase);

            foreach (var group in groups)
            {
                var groupResult = EvaluateGroup(task, group, statuses);

                // Short-circuits: with OR between groups one match is enough; with AND one miss
                // settles it.
                if (orGroups && groupResult) return true;
                if (!orGroups && !groupResult) return false;
            }

            return !orGroups;
        }

        private static bool EvaluateGroup(NoteTask task, ReportConditionGroup group, TaskStatusSet statuses)
        {
            var orConditions = string.Equals(group.Join, "or", StringComparison.OrdinalIgnoreCase);

            foreach (var condition in group.Conditions)
            {
                if (condition == null || string.IsNullOrWhiteSpace(condition.Field)) continue;

                var result = Evaluate(task, condition, statuses);
                if (orConditions && result) return true;
                if (!orConditions && !result) return false;
            }

            return !orConditions;
        }

        private static bool Evaluate(NoteTask task, ReportCondition condition, TaskStatusSet statuses)
        {
            // Multi-valued fields (assignees) satisfy a condition when ANY value does, which is
            // what "assignee is Sam" means to the person asking.
            var actual = ValuesFor(task, condition.Field, statuses);

            switch (condition.Operator)
            {
                case ReportOperators.IsEmpty:
                    return actual.Count == 0 || actual.All(string.IsNullOrWhiteSpace);

                case ReportOperators.IsNotEmpty:
                    return actual.Any(v => !string.IsNullOrWhiteSpace(v));

                case ReportOperators.Is:
                    return actual.Any(v => Same(v, condition.Value));

                case ReportOperators.IsNot:
                    return !actual.Any(v => Same(v, condition.Value));

                case ReportOperators.Contains:
                    return actual.Any(v => v != null && condition.Value != null
                        && v.Contains(condition.Value, StringComparison.OrdinalIgnoreCase));

                case ReportOperators.NotContains:
                    return !actual.Any(v => v != null && condition.Value != null
                        && v.Contains(condition.Value, StringComparison.OrdinalIgnoreCase));

                case ReportOperators.In:
                    return actual.Any(v => condition.Values != null
                        && condition.Values.Any(candidate => Same(v, candidate)));

                case ReportOperators.NotIn:
                    return !actual.Any(v => condition.Values != null
                        && condition.Values.Any(candidate => Same(v, candidate)));

                case ReportOperators.GreaterThan:
                    return Compare(actual, condition.Value, (a, b) => a > b);

                case ReportOperators.LessThan:
                    return Compare(actual, condition.Value, (a, b) => a < b);

                case ReportOperators.OnOrAfter:
                    return Compare(actual, condition.Value, (a, b) => a >= b);

                case ReportOperators.OnOrBefore:
                    return Compare(actual, condition.Value, (a, b) => a <= b);

                case ReportOperators.Between:
                    return Compare(actual, condition.Value, (a, b) => a >= b)
                           && Compare(actual, condition.Value2, (a, b) => a <= b);

                case ReportOperators.WithinDays:
                {
                    // "Due in the next 7 days", or with a negative value, "created in the last 7".
                    if (!double.TryParse(condition.Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var days))
                        return false;

                    var now = DateTime.UtcNow;
                    var bound = now.AddDays(days);

                    return actual.Any(v =>
                    {
                        if (!TryDate(v, out var date)) return false;
                        return days >= 0 ? date >= now && date <= bound : date <= now && date >= bound;
                    });
                }

                default:
                    // An unknown operator excludes rather than admits. A condition nobody can
                    // evaluate must not silently widen the result set.
                    return false;
            }
        }

        private static bool Same(string a, string b) =>
            string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Numeric comparison first, then date. Anything else fails the test.</summary>
        private static bool Compare(List<string> actual, string operand, Func<double, double, bool> numeric)
        {
            if (string.IsNullOrWhiteSpace(operand)) return false;

            if (double.TryParse(operand, NumberStyles.Any, CultureInfo.InvariantCulture, out var target))
            {
                return actual.Any(v =>
                    double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
                    && numeric(value, target));
            }

            if (TryDate(operand, out var targetDate))
            {
                return actual.Any(v => TryDate(v, out var value)
                    && numeric(value.Ticks, targetDate.Ticks));
            }

            return false;
        }

        private static bool TryDate(string value, out DateTime date) =>
            DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);

        /// <summary>
        /// The task's value(s) for a field, as strings.
        /// </summary>
        /// <remarks>
        /// Everything is compared as a string and parsed on demand. A single representation keeps
        /// the operator set from multiplying by field type, and the parse attempts above make
        /// numeric and date comparisons work anyway.
        /// </remarks>
        private static List<string> ValuesFor(NoteTask task, string field, TaskStatusSet statuses)
        {
            if (field.StartsWith(ReportFields.CustomPrefix, StringComparison.Ordinal))
            {
                var slug = field.Substring(ReportFields.CustomPrefix.Length);
                var value = task.CustomFields != null && task.CustomFields.TryGetValue(slug, out var v) ? v : null;
                return new List<string> { value };
            }

            return field switch
            {
                ReportFields.Title => One(task.Title),
                ReportFields.Description => One(task.Description),
                ReportFields.Status => One(statuses.Resolve(task.Status)),
                ReportFields.Priority => One(task.Priority),
                ReportFields.Category => One(task.Category),
                ReportFields.Project => One(task.ProjectId),
                ReportFields.Sprint => One(task.SprintId),
                ReportFields.Creator => One(task.Creator?.Id),
                ReportFields.Assignee => task.AssignedTo == null || task.AssignedTo.Count == 0
                    ? new List<string>()
                    : task.AssignedTo.Select(a => a?.Id).ToList(),
                ReportFields.DueDate => One(Iso(task.DueDate)),
                ReportFields.StartDate => One(Iso(task.StartDate)),
                ReportFields.CreatedAt => One(task.CreatedAt.Year > 1 ? Iso(task.CreatedAt) : null),
                ReportFields.EstimatedMinutes => One((task.EstimatedDuration ?? 0).ToString(CultureInfo.InvariantCulture)),
                ReportFields.ActualMinutes => One((task.RealDuration ?? 0).ToString(CultureInfo.InvariantCulture)),
                ReportFields.IsCompleted => One(statuses.IsTerminal(task.Status) ? "true" : "false"),
                ReportFields.IsOverdue => One(
                    task.DueDate.HasValue
                    && task.DueDate.Value < DateTime.UtcNow
                    && !statuses.IsTerminal(task.Status)
                        ? "true" : "false"),
                _ => new List<string>(),
            };
        }

        private static List<string> One(string value) => new() { value };

        private static string Iso(DateTime? value) =>
            value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
    }
}
