using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Ad-hoc reporting: run an aggregation, and save the ones worth keeping.
    /// </summary>
    /// <remarks>
    /// SECURITY: every action resolves the organization and checks membership before anything
    /// else. This endpoint can express far more shapes than the fixed dashboard DTOs it replaces,
    /// so the guard matters more here, not less - the old AnalyticsController took orgId from the
    /// query string with no membership check at all, and a report builder on that footing would
    /// hand any caller an arbitrary cut of any tenant's data.
    ///
    /// The organization id used for the query comes from the authorized context, never from the
    /// saved report - a shared report is data written by another user.
    ///
    /// ENTITLEMENT: gated on analytics.advanced. The guard honours Billing:EntitlementGuardMode,
    /// so it ships observing-only and is flipped to enforce once the logs are quiet.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class ReportsController : ControllerBase
    {
        private readonly IReportService _reports;
        private readonly ISavedReportRepository _saved;
        private readonly IOrganizationRepository _organizations;
        private readonly ITaskStatusService _taskStatuses;
        private readonly IEntitlementService _entitlements;

        public ReportsController(
            IReportService reports,
            ISavedReportRepository saved,
            IOrganizationRepository organizations,
            ITaskStatusService taskStatuses,
            IEntitlementService entitlements)
        {
            _reports = reports;
            _saved = saved;
            _organizations = organizations;
            _taskStatuses = taskStatuses;
            _entitlements = entitlements;
        }

        /// <summary>The dimensions and measures this organization can report on.</summary>
        /// <remarks>
        /// Served rather than hardcoded in the client, because the custom-field dimensions are
        /// per-organization. A builder that offers a field the org has not defined is worse than
        /// one that offers fewer options.
        /// </remarks>
        [HttpGet("dimensions")]
        public async Task<IActionResult> GetDimensions([FromQuery] string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var customSlugs = await _taskStatuses.CustomFieldSlugsAsync(orgId, "task");

            return Ok(new
            {
                dimensions = ReportDimensions.BuiltIn
                    .Select(d => new { key = d, label = DimensionLabel(d), custom = false })
                    .Concat(customSlugs.Select(s => new
                    {
                        key = ReportDimensions.CustomPrefix + s,
                        label = s.Replace('_', ' '),
                        custom = true,
                    })),
                measures = ReportMeasures.All.Select(m => new { key = m, label = MeasureLabel(m) }),
                fields = ReportFields.BuiltIn
                    .Select(f => new { key = f, label = FieldLabel(f), custom = false })
                    .Concat(customSlugs.Select(sl => new
                    {
                        key = ReportFields.CustomPrefix + sl,
                        label = sl.Replace('_', ' '),
                        custom = true,
                    })),
                operators = ReportOperators.All.Select(o => new { key = o, label = OperatorLabel(o) }),
                columns = ReportColumns.All
                    .Select(c => new { key = c, label = FieldLabel(c), custom = false })
                    .Concat(customSlugs.Select(sl => new
                    {
                        key = ReportFields.CustomPrefix + sl,
                        label = sl.Replace('_', ' '),
                        custom = true,
                    })),
                defaultColumns = ReportColumns.Default,
            });
        }

        /// <summary>Runs a report definition and returns the grouped rows.</summary>
        [HttpPost("run")]
        public async Task<IActionResult> Run([FromQuery] string orgId, [FromBody] ReportDefinition definition)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireAdvancedAsync(orgId);
            if (gate != null) return gate;

            if (definition == null) return BadRequest(new { message = "A report definition is required." });

            var invalid = Validate(definition);
            if (invalid != null) return BadRequest(new { message = invalid });

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            return Ok(await _reports.RunAsync(orgId, definition, statuses));
        }

        /// <summary>Saved reports the caller can see: their own, plus anything shared.</summary>
        [HttpGet]
        public async Task<IActionResult> List([FromQuery] string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _saved.GetVisibleAsync(orgId, auth.UserId));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromQuery] string orgId, [FromBody] SavedReport report)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireAdvancedAsync(orgId);
            if (gate != null) return gate;

            if (report == null || string.IsNullOrWhiteSpace(report.Name))
                return BadRequest(new { message = "A name is required." });

            var invalid = ValidateReport(report);
            if (invalid != null) return BadRequest(new { message = invalid });

            // Ownership and tenancy come from the authorized context, never the body. Otherwise a
            // caller could file a report into another organization, or under someone else's name.
            report.Id = Guid.NewGuid().ToString();
            report.OrganizationId = orgId;
            report.OwnerId = auth.UserId;
            report.OwnerName = NameOf(auth);

            await _saved.CreateAsync(report);
            return CreatedAtAction(nameof(List), new { orgId }, report);
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] SavedReport report)
        {
            var stored = await _saved.GetByIdAsync(id);
            if (stored == null) return NotFound();

            // Authorized against the STORED report's organization, not one named in the body.
            var auth = this.AuthorizeOrg(_organizations, stored.OrganizationId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            // Sharing a report does not hand over the pen. Anyone in the org may read a shared
            // report; only its author may change what it asks.
            if (!string.Equals(stored.OwnerId, auth.UserId, StringComparison.Ordinal)) return Forbid();

            if (report == null || string.IsNullOrWhiteSpace(report.Name))
                return BadRequest(new { message = "A name is required." });

            var invalid = ValidateReport(report);
            if (invalid != null) return BadRequest(new { message = invalid });

            stored.Name = report.Name;
            stored.Description = report.Description;
            stored.Definition = report.Definition ?? new ReportDefinition();
            stored.Sections = report.Sections ?? new List<ReportSection>();
            stored.SharedWithOrganization = report.SharedWithOrganization;
            stored.PinnedToDashboard = report.PinnedToDashboard;
            stored.SortOrder = report.SortOrder;

            await _saved.UpdateAsync(stored);
            return Ok(stored);
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var stored = await _saved.GetByIdAsync(id);
            if (stored == null) return NotFound();

            var auth = this.AuthorizeOrg(_organizations, stored.OrganizationId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            // Owner, or an org manager clearing up after someone who left.
            var isManager = OrgAccess.Managers.Contains(auth.Role ?? string.Empty);
            if (!string.Equals(stored.OwnerId, auth.UserId, StringComparison.Ordinal) && !isManager)
                return Forbid();

            await _saved.DeleteAsync(id);
            return NoContent();
        }

        /// <summary>Runs a saved report by id.</summary>
        [HttpPost("{id}/run")]
        public async Task<IActionResult> RunSaved(string id)
        {
            var stored = await _saved.GetByIdAsync(id);
            if (stored == null) return NotFound();

            var auth = this.AuthorizeOrg(_organizations, stored.OrganizationId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            if (!stored.SharedWithOrganization
                && !string.Equals(stored.OwnerId, auth.UserId, StringComparison.Ordinal))
            {
                // Same 404 as a missing report: whether a private report exists is itself private.
                return NotFound();
            }

            var gate = await RequireAdvancedAsync(stored.OrganizationId);
            if (gate != null) return gate;

            var statuses = await _taskStatuses.ForOrganizationAsync(stored.OrganizationId);

            // The organization comes from the stored report's own tenancy, which the guard above
            // has already checked the caller against - not from anything in the request.
            return Ok(await _reports.RunAsync(stored.OrganizationId, stored.Definition, statuses));
        }

        /// <summary>Runs a saved report and every one of its sections.</summary>
        /// <remarks>
        /// One call rather than a request per block, so a multi-section report renders from a
        /// single round trip and every block sees the same data.
        /// </remarks>
        [HttpPost("{id}/run-all")]
        public async Task<IActionResult> RunAll(string id)
        {
            var stored = await _saved.GetByIdAsync(id);
            if (stored == null) return NotFound();

            var auth = this.AuthorizeOrg(_organizations, stored.OrganizationId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            if (!stored.SharedWithOrganization
                && !string.Equals(stored.OwnerId, auth.UserId, StringComparison.Ordinal))
            {
                return NotFound();
            }

            var gate = await RequireAdvancedAsync(stored.OrganizationId);
            if (gate != null) return gate;

            var statuses = await _taskStatuses.ForOrganizationAsync(stored.OrganizationId);
            var primary = await _reports.RunAsync(stored.OrganizationId, stored.Definition, statuses);

            var sections = new List<object>();
            foreach (var section in (stored.Sections ?? new List<ReportSection>()).OrderBy(x => x.SortOrder))
            {
                sections.Add(new
                {
                    section.Id,
                    section.Title,
                    section.Description,
                    result = await _reports.RunAsync(stored.OrganizationId, section.Definition, statuses),
                });
            }

            return Ok(new { report = stored, primary, sections });
        }

        /// <summary>Runs a report and returns it as CSV.</summary>
        /// <remarks>
        /// Gated on export.data as well as analytics.advanced: this is a report AND an export, and
        /// a customer whose plan excludes taking data out should not get it through the reporting
        /// door. Emits whichever shape the report produced - grouped rows or detail rows.
        /// </remarks>
        [HttpPost("export")]
        public async Task<IActionResult> Export([FromQuery] string orgId, [FromBody] ReportDefinition definition)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireAdvancedAsync(orgId);
            if (gate != null) return gate;

            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var exportAllowed = this.RequireEntitlement(entitlements, Entitlement.DataExport);
            if (!exportAllowed.Allowed) return exportAllowed.Error;

            if (definition == null) return BadRequest(new { message = "A report definition is required." });

            var invalid = Validate(definition);
            if (invalid != null) return BadRequest(new { message = invalid });

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            var result = await _reports.RunAsync(orgId, definition, statuses);

            var csv = ReportCsv.Build(result);
            var bytes = System.Text.Encoding.UTF8.GetPreamble()
                .Concat(System.Text.Encoding.UTF8.GetBytes(csv)).ToArray();

            return File(bytes, "text/csv", $"report-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        }

        /// <summary>Validates a saved report, sections included.</summary>
        private static string ValidateReport(SavedReport report)
        {
            var invalid = Validate(report.Definition);
            if (invalid != null) return invalid;

            foreach (var section in report.Sections ?? new List<ReportSection>())
            {
                var sectionInvalid = Validate(section?.Definition);
                if (sectionInvalid != null)
                {
                    var title = string.IsNullOrWhiteSpace(section?.Title) ? "untitled" : section.Title;
                    return "Section " + title + ": " + sectionInvalid;
                }
            }

            return null;
        }

        /// <summary>Returns a 402 when the plan excludes advanced analytics, else null.</summary>
        private async Task<IActionResult> RequireAdvancedAsync(string orgId)
        {
            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var allowed = this.RequireEntitlement(entitlements, Entitlement.AnalyticsAdvanced);
            return allowed.Allowed ? null : allowed.Error;
        }

        /// <summary>
        /// Rejects a definition naming a dimension or measure outside the known vocabulary.
        /// </summary>
        /// <remarks>
        /// The service already degrades gracefully on an unknown dimension, but failing loudly at
        /// the edge is better than silently returning one meaningless "(all)" bucket and letting
        /// someone build a decision on it.
        /// </remarks>
        private static string Validate(ReportDefinition definition)
        {
            if (definition == null) return "A report definition is required.";

            if (!IsKnownDimension(definition.GroupBy))
                return $"Unknown dimension '{definition.GroupBy}'.";

            if (!string.IsNullOrWhiteSpace(definition.SplitBy) && !IsKnownDimension(definition.SplitBy))
                return $"Unknown dimension '{definition.SplitBy}'.";

            if (!ReportMeasures.All.Contains(definition.Measure))
                return $"Unknown measure '{definition.Measure}'.";

            foreach (var measure in definition.Measures ?? new List<string>())
            {
                if (!ReportMeasures.All.Contains(measure))
                    return $"Unknown measure '{measure}'.";
            }

            foreach (var column in definition.Columns ?? new List<string>())
            {
                if (!ReportColumns.All.Contains(column) && !IsCustomField(column))
                    return $"Unknown column '{column}'.";
            }

            // Same closed-vocabulary rule as dimensions: a condition names a field, and an
            // arbitrary field path would let a caller probe the embedded parent graph.
            foreach (var group in definition.Conditions?.Groups ?? new List<ReportConditionGroup>())
            {
                foreach (var condition in group.Conditions ?? new List<ReportCondition>())
                {
                    if (condition == null || string.IsNullOrWhiteSpace(condition.Field)) continue;

                    if (!ReportFields.BuiltIn.Contains(condition.Field) && !IsCustomField(condition.Field))
                        return $"Unknown condition field '{condition.Field}'.";

                    if (!ReportOperators.All.Contains(condition.Operator))
                        return $"Unknown operator '{condition.Operator}'.";
                }
            }

            return null;
        }

        private static bool IsCustomField(string key) =>
            key.StartsWith(ReportFields.CustomPrefix, StringComparison.Ordinal)
            && CustomFieldKeys.IsValid(key.Substring(ReportFields.CustomPrefix.Length));

        private static bool IsKnownDimension(string dimension) =>
            !string.IsNullOrWhiteSpace(dimension)
            && (ReportDimensions.BuiltIn.Contains(dimension)
                || (dimension.StartsWith(ReportDimensions.CustomPrefix, StringComparison.Ordinal)
                    && CustomFieldKeys.IsValid(dimension.Substring(ReportDimensions.CustomPrefix.Length))));

        private static string NameOf(OrgAuth auth) =>
            auth?.Org?.Members?.FirstOrDefault(m => m.UserId == auth.UserId)?.User?.Name ?? "A user";

        private static string DimensionLabel(string key) => key switch
        {
            ReportDimensions.Status => "Status",
            ReportDimensions.Priority => "Priority",
            ReportDimensions.Category => "Category",
            ReportDimensions.Project => "Project",
            ReportDimensions.Assignee => "Assignee",
            ReportDimensions.Sprint => "Sprint",
            ReportDimensions.Creator => "Created by",
            ReportDimensions.Day => "Day created",
            ReportDimensions.Week => "Week created",
            ReportDimensions.Month => "Month created",
            _ => key,
        };

        private static string FieldLabel(string key) => key switch
        {
            ReportFields.Title => "Title",
            ReportFields.Description => "Description",
            ReportFields.Status => "Status",
            ReportFields.Priority => "Priority",
            ReportFields.Category => "Category",
            ReportFields.Project => "Project",
            ReportFields.Sprint => "Sprint",
            ReportFields.Assignee => "Assignee",
            ReportFields.Creator => "Created by",
            ReportFields.DueDate => "Due date",
            ReportFields.StartDate => "Start date",
            ReportFields.CreatedAt => "Created",
            ReportFields.EstimatedMinutes => "Estimated (min)",
            ReportFields.ActualMinutes => "Actual (min)",
            ReportFields.IsCompleted => "Is completed",
            ReportFields.IsOverdue => "Is overdue",
            _ => key,
        };

        private static string OperatorLabel(string key) => key switch
        {
            ReportOperators.Is => "is",
            ReportOperators.IsNot => "is not",
            ReportOperators.Contains => "contains",
            ReportOperators.NotContains => "does not contain",
            ReportOperators.In => "is any of",
            ReportOperators.NotIn => "is none of",
            ReportOperators.GreaterThan => "is greater than",
            ReportOperators.LessThan => "is less than",
            ReportOperators.OnOrAfter => "is on or after",
            ReportOperators.OnOrBefore => "is on or before",
            ReportOperators.Between => "is between",
            ReportOperators.IsEmpty => "is empty",
            ReportOperators.IsNotEmpty => "is not empty",
            ReportOperators.WithinDays => "is within (days)",
            _ => key,
        };

        private static string MeasureLabel(string key) => key switch
        {
            ReportMeasures.Count => "Task count",
            ReportMeasures.CompletedCount => "Completed tasks",
            ReportMeasures.CompletionRate => "Completion rate (%)",
            ReportMeasures.EstimatedMinutes => "Estimated time (min)",
            ReportMeasures.ActualMinutes => "Actual time (min)",
            ReportMeasures.EstimateVariance => "Actual − estimated (min)",
            _ => key,
        };
    }
}
