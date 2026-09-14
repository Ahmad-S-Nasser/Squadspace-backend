using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Automation rules and their run history.
    /// </summary>
    /// <remarks>
    /// Writing rules is MANAGERS ONLY. A rule can email people, reassign work and move tasks
    /// between statuses on its own, so it is a delegation of authority rather than a preference -
    /// any member being able to write one would let them act as the organization on a schedule.
    /// Reading rules and history stays open to members, because an automation nobody can inspect
    /// is one nobody trusts.
    /// </remarks>
    [Route("api/organization/{orgId}/automation")]
    [ApiController]
    [Authorize]
    public class AutomationController : ControllerBase
    {
        private readonly IAutomationRepository _automation;
        private readonly IOrganizationRepository _organizations;
        private readonly ISavedReportRepository _savedReports;
        private readonly IAutomationEngine _engine;
        private readonly IEntitlementService _entitlements;

        public AutomationController(
            IAutomationRepository automation,
            IOrganizationRepository organizations,
            ISavedReportRepository savedReports,
            IAutomationEngine engine,
            IEntitlementService entitlements)
        {
            _automation = automation;
            _organizations = organizations;
            _savedReports = savedReports;
            _engine = engine;
            _entitlements = entitlements;
        }

        /// <summary>The trigger and action vocabulary, plus this month's usage.</summary>
        [HttpGet("catalog")]
        public async Task<IActionResult> Catalog(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var limit = entitlements?.Limit(Entitlement.AutomationRuns) ?? 0;

            var since = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
            var used = await _automation.CountRunsSinceAsync(orgId, since);

            return Ok(new
            {
                triggers = AutomationTriggers.All.Select(t => new { key = t, label = TriggerLabel(t) }),
                actions = AutomationActions.All.Select(a => new { key = a, label = ActionLabel(a) }),
                cadences = AutomationCadence.All,
                usage = new
                {
                    used,
                    limit = limit == long.MaxValue ? (long?)null : limit,
                    resetsAt = since.AddMonths(1),
                },
            });
        }

        [HttpGet]
        public async Task<IActionResult> List(string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _automation.GetForOrganizationAsync(orgId));
        }

        [HttpGet("runs")]
        public async Task<IActionResult> Runs(string orgId, [FromQuery] string ruleId = null, [FromQuery] int limit = 50)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _automation.GetRunsAsync(orgId, ruleId, limit));
        }

        [HttpPost]
        public async Task<IActionResult> Create(string orgId, [FromBody] AutomationRule rule)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var invalid = await ValidateAsync(rule, orgId);
            if (invalid != null) return BadRequest(new { message = invalid });

            // Identity and tenancy from the authorized context, never the body.
            rule.Id = Guid.NewGuid().ToString();
            rule.OrganizationId = orgId;
            rule.CreatedBy = auth.UserId;
            rule.CreatedAt = DateTime.UtcNow;
            rule.ConsecutiveFailures = 0;
            rule.LockedUntil = null;

            rule.NextRunAt = AutomationTriggers.IsScheduled(rule.Trigger)
                ? _engine.ComputeNextRun(rule, DateTime.UtcNow)
                : null;

            await _automation.UpsertAsync(rule);
            return Ok(rule);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string orgId, string id, [FromBody] AutomationRule rule)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var stored = await _automation.GetByIdAsync(id);
            if (stored == null || stored.OrganizationId != orgId) return NotFound();

            var invalid = await ValidateAsync(rule, orgId);
            if (invalid != null) return BadRequest(new { message = invalid });

            stored.Name = rule.Name;
            stored.Description = rule.Description;
            stored.Enabled = rule.Enabled;
            stored.Trigger = rule.Trigger;
            stored.Action = rule.Action;
            stored.Config = rule.Config ?? new Dictionary<string, string>();
            stored.ProjectId = rule.ProjectId;
            stored.Cadence = rule.Cadence;
            stored.HourOfDay = rule.HourOfDay;
            stored.DayOfWeek = rule.DayOfWeek;
            stored.DayOfMonth = rule.DayOfMonth;
            stored.TimeZone = rule.TimeZone;

            // Re-enabling clears the failure count, so a fixed rule is not disabled again on its
            // first stumble.
            if (stored.Enabled) stored.ConsecutiveFailures = 0;

            stored.NextRunAt = AutomationTriggers.IsScheduled(stored.Trigger)
                ? _engine.ComputeNextRun(stored, DateTime.UtcNow)
                : null;

            await _automation.UpsertAsync(stored);
            return Ok(stored);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string orgId, string id)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var stored = await _automation.GetByIdAsync(id);
            if (stored == null || stored.OrganizationId != orgId) return NotFound();

            await _automation.DeleteAsync(id);
            return NoContent();
        }

        /// <summary>Runs a rule immediately, without waiting for its schedule.</summary>
        /// <remarks>
        /// The thing that makes a scheduled rule testable. Without it, checking a Monday-morning
        /// report means waiting until Monday. It counts against the meter like any other run,
        /// because it does the same work.
        /// </remarks>
        [HttpPost("{id}/run")]
        public async Task<IActionResult> RunNow(string orgId, string id)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var rule = await _automation.GetByIdAsync(id);
            if (rule == null || rule.OrganizationId != orgId) return NotFound();

            var outcome = await _engine.ExecuteAsync(new AutomationContext { Rule = rule });
            return Ok(new { state = outcome.State, message = outcome.Message });
        }

        private async Task<string> ValidateAsync(AutomationRule rule, string orgId)
        {
            if (rule == null) return "A rule is required.";
            if (string.IsNullOrWhiteSpace(rule.Name)) return "A name is required.";

            if (!AutomationTriggers.All.Contains(rule.Trigger))
                return $"Unknown trigger '{rule.Trigger}'.";

            if (!AutomationActions.All.Contains(rule.Action))
                return $"Unknown action '{rule.Action}'.";

            if (AutomationTriggers.IsScheduled(rule.Trigger)
                && !AutomationCadence.All.Contains(rule.Cadence ?? string.Empty))
            {
                return "A scheduled rule needs a cadence.";
            }

            // Checked here rather than at run time so a broken rule is refused when someone is
            // watching, instead of failing quietly at 9am on Monday.
            if (rule.Action == AutomationActions.DeliverReport)
            {
                var reportId = rule.Config?.GetValueOrDefault("reportId");
                if (string.IsNullOrWhiteSpace(reportId)) return "Choose a report to deliver.";

                var report = await _savedReports.GetByIdAsync(reportId);
                if (report == null || report.OrganizationId != orgId)
                {
                    return "That report does not exist in this organization.";
                }
            }

            return null;
        }

        private static string TriggerLabel(string key) => key switch
        {
            AutomationTriggers.Schedule => "On a schedule",
            AutomationTriggers.TaskStatusChanged => "When a task changes status",
            AutomationTriggers.TaskCreated => "When a task is created",
            _ => key,
        };

        private static string ActionLabel(string key) => key switch
        {
            AutomationActions.DeliverReport => "Email a saved report",
            AutomationActions.SendEmail => "Send an email",
            AutomationActions.Notify => "Send an in-app notification",
            AutomationActions.AssignTask => "Assign the task",
            AutomationActions.SetTaskStatus => "Move the task to a status",
            AutomationActions.CommentOnTask => "Comment on the task",
            _ => key,
        };
    }
}
