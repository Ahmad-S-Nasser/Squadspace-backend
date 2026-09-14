using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Getting data out, and getting it in from elsewhere.
    /// </summary>
    /// <remarks>
    /// Export ships first and stands alone: it is what the Starter plan has been advertising, it
    /// is small, and a round trip through it is the cheapest proof that the entity model survives
    /// leaving the product and coming back. Import is measured against that round trip.
    ///
    /// Both are gated on export.data, which honours Billing:EntitlementGuardMode - so they ship
    /// observing-only and are flipped to enforce once the logs are quiet.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class MigrationController : ControllerBase
    {
        /// <summary>Refuses a file larger than this rather than reading it into memory.</summary>
        private const int MaxCsvBytes = 8 * 1024 * 1024;

        private readonly IProjectExportService _export;
        private readonly IImportService _import;
        private readonly IImportJobRepository _jobs;
        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly ITaskStatusService _taskStatuses;
        private readonly IEntitlementService _entitlements;

        public MigrationController(
            IProjectExportService export,
            IImportService import,
            IImportJobRepository jobs,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            ITaskStatusService taskStatuses,
            IEntitlementService entitlements)
        {
            _export = export;
            _import = import;
            _jobs = jobs;
            _organizations = organizations;
            _projects = projects;
            _taskStatuses = taskStatuses;
            _entitlements = entitlements;
        }

        /// <summary>Exports the organization, or one project, as CSV.</summary>
        [HttpGet("export")]
        public async Task<IActionResult> Export([FromQuery] string orgId, [FromQuery] string projectId = null)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireExportAsync(orgId);
            if (gate != null) return gate;

            // A projectId is a narrowing WITHIN the authorized organization. Checked here so a
            // project id from another tenant cannot be used to read across.
            if (!string.IsNullOrWhiteSpace(projectId))
            {
                var project = await _projects.GetByIdAsync(projectId);
                if (project == null || project.OrganizationId != orgId) return NotFound();
            }

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            var csv = await _export.ExportCsvAsync(orgId, projectId, statuses);

            var name = $"squadspace-export-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";

            // UTF-8 BOM: without it Excel opens the file as the local ANSI codepage and mangles
            // every non-ASCII title, which for this product's users is most of them.
            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv)).ToArray();
            return File(bytes, "text/csv", name);
        }

        /// <summary>Parses a file and reports what it contains, without writing anything.</summary>
        /// <remarks>
        /// A dry run exists because an import is hard to undo here - there are no transactions, so
        /// "look before you leap" is the only rollback story worth offering.
        /// </remarks>
        [HttpPost("import/preview")]
        public async Task<IActionResult> Preview([FromQuery] string orgId, [FromBody] ImportRequest request)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireExportAsync(orgId);
            if (gate != null) return gate;

            var invalid = ValidateRequest(request);
            if (invalid != null) return BadRequest(new { message = invalid });

            var rows = _import.Parse(request.Csv, request.Source);
            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);

            // Which incoming status names this organization does NOT recognise. Surfacing them
            // before the run is the difference between mapping them and discovering afterwards
            // that 300 tasks all landed in the default column.
            var unmapped = rows
                .Where(r => r.Level == "task" && !string.IsNullOrWhiteSpace(r.Status))
                .Select(r => request.StatusMap != null && request.StatusMap.TryGetValue(r.Status, out var m) ? m : r.Status)
                .Where(s => statuses.Find(s) == null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(50)
                .ToList();

            return Ok(new
            {
                totalRows = rows.Count,
                projects = rows.Count(r => r.Level == "project"),
                boards = rows.Count(r => r.Level == "board"),
                notes = rows.Count(r => r.Level == "note"),
                tasks = rows.Count(r => r.Level == "task"),
                unmappedStatuses = unmapped,
                knownStatuses = statuses.Statuses.Select(s => new { s.Slug, s.Label }),
                sample = rows.Take(10).Select(r => new { r.Number, r.Level, r.Name, r.Status, r.Priority }),
            });
        }

        /// <summary>Runs an import and returns the job record.</summary>
        [HttpPost("import")]
        public async Task<IActionResult> Import([FromQuery] string orgId, [FromBody] ImportRequest request)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var gate = await RequireExportAsync(orgId);
            if (gate != null) return gate;

            var invalid = ValidateRequest(request);
            if (invalid != null) return BadRequest(new { message = invalid });

            if (!string.IsNullOrWhiteSpace(request.TargetProjectId))
            {
                var project = await _projects.GetByIdAsync(request.TargetProjectId);
                if (project == null || project.OrganizationId != orgId) return NotFound();
            }

            request.ExternalSource = string.IsNullOrWhiteSpace(request.ExternalSource)
                ? request.Source
                : request.ExternalSource;

            var job = new ImportJob
            {
                OrganizationId = orgId,
                OwnerId = auth.UserId,
                OwnerName = NameOf(auth),
                Source = request.Source,
                ExternalSource = request.ExternalSource,
                FileName = request.FileName,
            };

            await _jobs.CreateAsync(job);

            var statuses = await _taskStatuses.ForOrganizationAsync(orgId);
            await _import.RunAsync(job, request, orgId, statuses);

            return Ok(job);
        }

        /// <summary>Import history for the organization.</summary>
        [HttpGet("import/jobs")]
        public async Task<IActionResult> Jobs([FromQuery] string orgId)
        {
            var auth = this.AuthorizeOrg(_organizations, orgId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(await _jobs.GetForOrganizationAsync(orgId));
        }

        /// <summary>One job, for polling progress.</summary>
        [HttpGet("import/jobs/{id}")]
        public async Task<IActionResult> Job(string id)
        {
            var job = await _jobs.GetByIdAsync(id);
            if (job == null) return NotFound();

            var auth = this.AuthorizeOrg(_organizations, job.OrganizationId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            return Ok(job);
        }

        private static string ValidateRequest(ImportRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Csv))
                return "A CSV payload is required.";

            if (Encoding.UTF8.GetByteCount(request.Csv) > MaxCsvBytes)
                return $"File is larger than {MaxCsvBytes / (1024 * 1024)} MB.";

            if (!string.IsNullOrWhiteSpace(request.Source)
                && request.Source is not ("csv" or "jira"))
                return $"Unknown import source '{request.Source}'.";

            return null;
        }

        private async Task<IActionResult> RequireExportAsync(string orgId)
        {
            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var allowed = this.RequireEntitlement(entitlements, Entitlement.DataExport);
            return allowed.Allowed ? null : allowed.Error;
        }

        private static string NameOf(OrgAuth auth) =>
            auth?.Org?.Members?.FirstOrDefault(m => m.UserId == auth.UserId)?.User?.Name ?? "A user";
    }
}
