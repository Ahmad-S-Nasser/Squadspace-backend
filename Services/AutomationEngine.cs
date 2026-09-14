using System.Text;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>What a run is given to work with.</summary>
    public sealed class AutomationContext
    {
        public AutomationRule Rule { get; init; }

        /// <summary>The task that fired an event trigger. Null for scheduled rules.</summary>
        public NoteTask Task { get; init; }

        /// <summary>Status before the change, for status-change triggers.</summary>
        public string PreviousStatus { get; init; }
    }

    public sealed class AutomationOutcome
    {
        public string State { get; init; }
        public string Message { get; init; }

        public static AutomationOutcome Ok(string message) =>
            new() { State = AutomationRunStates.Succeeded, Message = message };

        public static AutomationOutcome Skip(string message) =>
            new() { State = AutomationRunStates.Skipped, Message = message };

        public static AutomationOutcome Fail(string message) =>
            new() { State = AutomationRunStates.Failed, Message = message };
    }

    public interface IAutomationEngine
    {
        /// <summary>Executes one rule, records the run, and returns what happened.</summary>
        Task<AutomationOutcome> ExecuteAsync(AutomationContext context);

        /// <summary>Runs every enabled rule matching an event trigger.</summary>
        Task DispatchAsync(string organizationId, string trigger, NoteTask task, string previousStatus);

        /// <summary>The next UTC instant a scheduled rule is due after <paramref name="after"/>.</summary>
        DateTime ComputeNextRun(AutomationRule rule, DateTime after);
    }

    /// <summary>
    /// Runs automation rules.
    /// </summary>
    /// <remarks>
    /// One engine for both tracks. Manager workflows (scheduled, org-scoped, delivered by email)
    /// and task automation (event-driven, project-scoped, acting in the workspace) share a trigger
    /// vocabulary, an action registry, one run history and one meter - so building them separately
    /// would mean building this plumbing twice.
    ///
    /// Metering is enforced here rather than at the call site, for the same reason quota checks
    /// live in the entitlement guard: a limit applied somewhere else does not honour the
    /// log/enforce rollout switch, and would silently block while the switch said "observe".
    /// </remarks>
    public class AutomationEngine : IAutomationEngine
    {
        private readonly IAutomationRepository _automation;
        private readonly IReportService _reports;
        private readonly ISavedReportRepository _savedReports;
        private readonly ITaskStatusService _taskStatuses;
        private readonly INoteTaskRepository _tasks;
        private readonly INotificationRepository _notifications;
        private readonly IOrganizationRepository _organizations;
        private readonly IEntitlementService _entitlements;
        private readonly ILogger<AutomationEngine> _logger;

        public AutomationEngine(
            IAutomationRepository automation,
            IReportService reports,
            ISavedReportRepository savedReports,
            ITaskStatusService taskStatuses,
            INoteTaskRepository tasks,
            INotificationRepository notifications,
            IOrganizationRepository organizations,
            IEntitlementService entitlements,
            ILogger<AutomationEngine> logger)
        {
            _automation = automation;
            _reports = reports;
            _savedReports = savedReports;
            _taskStatuses = taskStatuses;
            _tasks = tasks;
            _notifications = notifications;
            _organizations = organizations;
            _entitlements = entitlements;
            _logger = logger;
        }

        public async Task DispatchAsync(
            string organizationId, string trigger, NoteTask task, string previousStatus)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return;

            try
            {
                var rules = await _automation.GetEnabledByTriggerAsync(organizationId, trigger);

                foreach (var rule in rules)
                {
                    // A project-scoped rule only sees its own project's tasks.
                    if (!string.IsNullOrWhiteSpace(rule.ProjectId)
                        && !string.Equals(rule.ProjectId, task?.ProjectId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    await ExecuteAsync(new AutomationContext
                    {
                        Rule = rule,
                        Task = task,
                        PreviousStatus = previousStatus,
                    });
                }
            }
            catch (Exception ex)
            {
                // Automation must never break the write that triggered it. Someone moving a task
                // should not see an error because a rule they did not write is misconfigured.
                _logger.LogWarning(ex, "Automation dispatch failed for trigger {Trigger}.", trigger);
            }
        }

        public async Task<AutomationOutcome> ExecuteAsync(AutomationContext context)
        {
            var rule = context.Rule;
            var startedAt = DateTime.UtcNow;

            var run = new AutomationRun
            {
                RuleId = rule.Id,
                OrganizationId = rule.OrganizationId,
                RuleName = rule.Name,
                Trigger = rule.Trigger,
                Action = rule.Action,
                StartedAt = startedAt,
            };

            AutomationOutcome outcome;

            try
            {
                var quota = await CheckQuotaAsync(rule.OrganizationId);
                outcome = quota ?? await RunActionAsync(context);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Automation rule {RuleId} failed.", rule.Id);
                outcome = AutomationOutcome.Fail(ex.Message);
            }

            run.State = outcome.State;
            run.Message = Truncate(outcome.Message, 500);
            run.CompletedAt = DateTime.UtcNow;
            run.DurationMs = (long)(run.CompletedAt.Value - startedAt).TotalMilliseconds;

            await _automation.RecordRunAsync(run);
            return outcome;
        }

        /// <summary>
        /// Refuses a run once the organization is over its monthly allowance.
        /// </summary>
        /// <remarks>
        /// Both named competitors meter this - a free plan gets 100 rule runs a month - which
        /// makes it a paid feature and a free-tier lever at once: everyone gets automation, the
        /// volume is what sells.
        ///
        /// A refusal is RECORDED as a run of its own so the customer can see why nothing happened,
        /// but is not itself counted against the allowance.
        /// </remarks>
        private async Task<AutomationOutcome> CheckQuotaAsync(string organizationId)
        {
            var entitlements = await _entitlements.ForOrganizationAsync(organizationId);
            var limit = entitlements?.Limit(Entitlement.AutomationRuns) ?? 0;

            if (limit == long.MaxValue) return null;

            var since = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var used = await _automation.CountRunsSinceAsync(organizationId, since);

            if (used < limit) return null;

            return new AutomationOutcome
            {
                State = AutomationRunStates.QuotaExceeded,
                Message = $"Monthly automation allowance of {limit} runs reached.",
            };
        }

        private async Task<AutomationOutcome> RunActionAsync(AutomationContext context)
        {
            var rule = context.Rule;

            // Event rules can carry a condition; a rule whose condition does not match is a skip,
            // not a failure, and must not consume the meter.
            var gate = await EvaluateConditionAsync(context);
            if (gate != null) return gate;

            return rule.Action switch
            {
                AutomationActions.DeliverReport => await DeliverReportAsync(rule),
                AutomationActions.SendEmail => await SendEmailAsync(rule, context),
                AutomationActions.Notify => await NotifyAsync(rule, context),
                AutomationActions.AssignTask => await AssignTaskAsync(rule, context),
                AutomationActions.SetTaskStatus => await SetTaskStatusAsync(rule, context),
                AutomationActions.CommentOnTask => await CommentAsync(rule, context),
                _ => AutomationOutcome.Fail($"Unknown action '{rule.Action}'."),
            };
        }

        /// <summary>Checks the rule's optional trigger condition.</summary>
        private async Task<AutomationOutcome> EvaluateConditionAsync(AutomationContext context)
        {
            var rule = context.Rule;
            if (context.Task == null) return null;

            // "Only when the task moves INTO this status." Without it a rule for "done" would
            // fire on every subsequent edit of an already-done task.
            if (rule.Config.TryGetValue("toStatus", out var toStatus) && !string.IsNullOrWhiteSpace(toStatus))
            {
                var statuses = await _taskStatuses.ForOrganizationAsync(rule.OrganizationId);
                var target = statuses.Resolve(toStatus);
                var current = statuses.Resolve(context.Task.Status);

                if (!string.Equals(current, target, StringComparison.Ordinal))
                {
                    return AutomationOutcome.Skip("Status did not match the rule's condition.");
                }

                if (string.Equals(statuses.Resolve(context.PreviousStatus), target, StringComparison.Ordinal))
                {
                    return AutomationOutcome.Skip("Task was already in that status.");
                }
            }

            if (rule.Config.TryGetValue("priority", out var priority) && !string.IsNullOrWhiteSpace(priority)
                && !string.Equals(context.Task.Priority, priority, StringComparison.OrdinalIgnoreCase))
            {
                return AutomationOutcome.Skip("Priority did not match the rule's condition.");
            }

            return null;
        }

        // ------------------------------------------------------------ actions

        /// <summary>
        /// Runs a saved report and emails it. The first slice, and the one that proves the runtime.
        /// </summary>
        private async Task<AutomationOutcome> DeliverReportAsync(AutomationRule rule)
        {
            if (!rule.Config.TryGetValue("reportId", out var reportId) || string.IsNullOrWhiteSpace(reportId))
            {
                return AutomationOutcome.Fail("No report is configured.");
            }

            var report = await _savedReports.GetByIdAsync(reportId);
            if (report == null) return AutomationOutcome.Fail("That report no longer exists.");

            // The report's OWN organization, re-checked against the rule's. A rule must not become
            // a way to read a report belonging to another tenant.
            if (!string.Equals(report.OrganizationId, rule.OrganizationId, StringComparison.Ordinal))
            {
                return AutomationOutcome.Fail("That report belongs to another organization.");
            }

            var statuses = await _taskStatuses.ForOrganizationAsync(rule.OrganizationId);
            var result = await _reports.RunAsync(rule.OrganizationId, report.Definition, statuses);

            var recipients = Recipients(rule);
            if (recipients.Count == 0) return AutomationOutcome.Fail("No recipients are configured.");

            var response = await _notifications.SendEmailNotificationAsync(new NotificationRequest
            {
                Type = "automation_report",
                TaskTitle = report.Name,
                RecipientEmails = recipients,
                Metadata = new Dictionary<string, string>
                {
                    { "ReportName", report.Name },
                    { "Summary", Summarize(report.Name, result) },
                },
            });

            return response is { Success: true }
                ? AutomationOutcome.Ok($"Sent \"{report.Name}\" to {recipients.Count} recipient(s).")
                : AutomationOutcome.Fail(response?.Message ?? "Delivery failed.");
        }

        private async Task<AutomationOutcome> SendEmailAsync(AutomationRule rule, AutomationContext context)
        {
            var recipients = Recipients(rule);
            if (recipients.Count == 0) return AutomationOutcome.Fail("No recipients are configured.");

            rule.Config.TryGetValue("subject", out var subject);
            rule.Config.TryGetValue("body", out var body);

            var response = await _notifications.SendEmailNotificationAsync(new NotificationRequest
            {
                Type = "automation_notice",
                TaskId = context.Task?.Id,
                TaskTitle = Render(subject, context) ?? rule.Name,
                RecipientEmails = recipients,
                Metadata = new Dictionary<string, string>
                {
                    { "Summary", Render(body, context) ?? string.Empty },
                },
            });

            return response is { Success: true }
                ? AutomationOutcome.Ok($"Emailed {recipients.Count} recipient(s).")
                : AutomationOutcome.Fail(response?.Message ?? "Delivery failed.");
        }

        private async Task<AutomationOutcome> NotifyAsync(AutomationRule rule, AutomationContext context)
        {
            var userIds = Split(rule.Config.GetValueOrDefault("userIds"));

            // "Whoever it is assigned to" is the common case and saves naming people twice.
            if (userIds.Count == 0 && context.Task?.AssignedTo != null)
            {
                userIds = context.Task.AssignedTo.Select(a => a?.Id).Where(i => i != null).ToList();
            }

            if (userIds.Count == 0) return AutomationOutcome.Skip("Nobody to notify.");

            rule.Config.TryGetValue("message", out var message);

            foreach (var userId in userIds)
            {
                await _notifications.CreateUserNotificationAsync(new Notification
                {
                    UserId = userId,
                    Type = "automation",
                    Title = rule.Name,
                    Message = Render(message, context) ?? rule.Name,
                    Link = context.Task != null ? "/tasks" : "/dashboard",
                    CreatedAt = DateTime.UtcNow,
                });
            }

            return AutomationOutcome.Ok($"Notified {userIds.Count} user(s).");
        }

        private async Task<AutomationOutcome> AssignTaskAsync(AutomationRule rule, AutomationContext context)
        {
            if (context.Task == null) return AutomationOutcome.Skip("No task in context.");
            if (!rule.Config.TryGetValue("assigneeId", out var assigneeId) || string.IsNullOrWhiteSpace(assigneeId))
            {
                return AutomationOutcome.Fail("No assignee is configured.");
            }

            var org = _organizations.FetchOrganizationById(rule.OrganizationId);
            var member = org?.Members?.FirstOrDefault(m => m.UserId == assigneeId);

            // Only someone already in the organization. An automation must not be a way to attach
            // an outsider to a task.
            if (member == null) return AutomationOutcome.Fail("That assignee is not in this organization.");

            var task = await _tasks.GetByIdAsync(context.Task.Id);
            if (task == null) return AutomationOutcome.Skip("Task no longer exists.");

            task.AssignedTo ??= new List<Contributor>();
            if (task.AssignedTo.Any(a => a?.Id == assigneeId))
            {
                return AutomationOutcome.Skip("Already assigned.");
            }

            task.AssignedTo.Add(member.User ?? new Contributor { Id = assigneeId });
            await _tasks.UpdateAsync(task);

            return AutomationOutcome.Ok("Assigned the task.");
        }

        private async Task<AutomationOutcome> SetTaskStatusAsync(AutomationRule rule, AutomationContext context)
        {
            if (context.Task == null) return AutomationOutcome.Skip("No task in context.");
            if (!rule.Config.TryGetValue("status", out var wanted) || string.IsNullOrWhiteSpace(wanted))
            {
                return AutomationOutcome.Fail("No status is configured.");
            }

            var statuses = await _taskStatuses.ForOrganizationAsync(rule.OrganizationId);
            var slug = statuses.Find(wanted)?.Slug;

            // Resolved through the org's registry, so a rule cannot invent a status that no
            // board renders.
            if (slug == null) return AutomationOutcome.Fail($"'{wanted}' is not a status in this organization.");

            var task = await _tasks.GetByIdAsync(context.Task.Id);
            if (task == null) return AutomationOutcome.Skip("Task no longer exists.");

            if (string.Equals(statuses.Resolve(task.Status), slug, StringComparison.Ordinal))
            {
                // Also the loop guard: a rule that sets the status it triggers on would otherwise
                // re-fire itself forever.
                return AutomationOutcome.Skip("Already in that status.");
            }

            task.Status = slug;
            await _tasks.UpdateAsync(task);

            return AutomationOutcome.Ok($"Moved the task to {slug}.");
        }

        private async Task<AutomationOutcome> CommentAsync(AutomationRule rule, AutomationContext context)
        {
            if (context.Task == null) return AutomationOutcome.Skip("No task in context.");
            if (!rule.Config.TryGetValue("comment", out var text) || string.IsNullOrWhiteSpace(text))
            {
                return AutomationOutcome.Fail("No comment text is configured.");
            }

            var task = await _tasks.GetByIdAsync(context.Task.Id);
            if (task == null) return AutomationOutcome.Skip("Task no longer exists.");

            task.Comments ??= new List<TaskComment>();
            task.Comments.Add(new TaskComment
            {
                Id = Guid.NewGuid().ToString(),
                TaskId = task.Id,
                Author = new TaskCommentAuthor { Id = "automation", Name = rule.Name, Email = string.Empty },
                Content = Render(text, context),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
            });

            await _tasks.UpdateAsync(task);
            return AutomationOutcome.Ok("Added a comment.");
        }

        // ------------------------------------------------------------ scheduling

        /// <summary>
        /// The next due instant, in UTC, honouring the rule's own time zone.
        /// </summary>
        /// <remarks>
        /// "Every Monday at 9am" is a wall-clock statement, so it is computed in the rule's zone
        /// and converted back. Doing the arithmetic in UTC would drift by an hour across a DST
        /// boundary - the report would start arriving at 8am or 10am with nobody having changed
        /// anything.
        /// </remarks>
        public DateTime ComputeNextRun(AutomationRule rule, DateTime after)
        {
            var zone = ResolveZone(rule.TimeZone);
            var local = TimeZoneInfo.ConvertTimeFromUtc(after, zone);

            var hour = Math.Clamp(rule.HourOfDay, 0, 23);

            DateTime next = rule.Cadence?.ToLowerInvariant() switch
            {
                AutomationCadence.Hourly => local.Date.AddHours(local.Hour + 1),

                AutomationCadence.Weekly => NextWeekly(local, hour, Math.Clamp(rule.DayOfWeek, 1, 7)),

                AutomationCadence.Monthly => NextMonthly(local, hour, Math.Clamp(rule.DayOfMonth, 1, 28)),

                // Daily is the default: today at the hour if it is still ahead, else tomorrow.
                _ => local.Date.AddHours(hour) > local
                    ? local.Date.AddHours(hour)
                    : local.Date.AddDays(1).AddHours(hour),
            };

            // A local time can be invalid on the spring-forward day; nudging forward an hour is
            // the conventional resolution and keeps the rule firing.
            if (zone.IsInvalidTime(next)) next = next.AddHours(1);

            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(next, DateTimeKind.Unspecified), zone);
        }

        private static DateTime NextWeekly(DateTime local, int hour, int isoDayOfWeek)
        {
            // .NET counts Sunday as 0; the rule uses ISO, where Monday is 1.
            var currentIso = ((int)local.DayOfWeek + 6) % 7 + 1;
            var delta = (isoDayOfWeek - currentIso + 7) % 7;

            var candidate = local.Date.AddDays(delta).AddHours(hour);
            return candidate > local ? candidate : candidate.AddDays(7);
        }

        private static DateTime NextMonthly(DateTime local, int hour, int dayOfMonth)
        {
            var candidate = new DateTime(local.Year, local.Month, dayOfMonth, hour, 0, 0);
            return candidate > local ? candidate : candidate.AddMonths(1);
        }

        /// <summary>Falls back to UTC rather than throwing on an unknown zone id.</summary>
        private static TimeZoneInfo ResolveZone(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;

            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch { return TimeZoneInfo.Utc; }
        }

        // ------------------------------------------------------------ helpers

        private static List<string> Recipients(AutomationRule rule) =>
            Split(rule.Config.GetValueOrDefault("recipients"));

        private static List<string> Split(string value) =>
            string.IsNullOrWhiteSpace(value)
                ? new List<string>()
                : value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(v => v.Trim())
                    .Where(v => v.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

        /// <summary>Substitutes the few placeholders a rule may use in its text.</summary>
        private static string Render(string template, AutomationContext context)
        {
            if (string.IsNullOrWhiteSpace(template)) return template;

            return template
                .Replace("{{task.title}}", context.Task?.Title ?? string.Empty)
                .Replace("{{task.status}}", context.Task?.Status ?? string.Empty)
                .Replace("{{task.priority}}", context.Task?.Priority ?? string.Empty)
                .Replace("{{rule.name}}", context.Rule?.Name ?? string.Empty);
        }

        /// <summary>A plain-text summary of a report result, for the email body.</summary>
        private static string Summarize(string name, ReportResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{name} — {result.matched} task(s) matched.");
            sb.AppendLine();

            foreach (var row in result.rows.Take(20))
            {
                sb.AppendLine($"{row.label}: {row.value}");
            }

            if (result.totals != null && result.totals.TryGetValue("count", out var total))
            {
                sb.AppendLine();
                sb.AppendLine($"Total: {total}");
            }

            return sb.ToString();
        }

        private static string Truncate(string value, int max) =>
            string.IsNullOrEmpty(value) || value.Length <= max ? value : value.Substring(0, max);
    }
}
