using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Git repositories, hosted on this server and owned by a project.
    /// </summary>
    /// <remarks>
    /// SECURITY: this controller had no [Authorize] and no ownership checks, over source code.
    /// GetAll returned every repository in the system, Get exposed any repository's metadata
    /// by id, and Delete destroyed any repository — including its working directory on disk —
    /// for any caller who could name one.
    ///
    /// Create additionally took the creator from <c>User.Identity.Name</c>, which is not the
    /// user id claim this application issues; it now comes from the same JWT claim everything
    /// else uses.
    /// </remarks>
    [ApiController]
    [Route("api/Git/repositories")]
    [Authorize]
    public class GitRepositoriesController : ControllerBase
    {
        private readonly IGitRepositoryService _service;
        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        private readonly IEntitlementService _entitlements;

        public GitRepositoriesController(
            IGitRepositoryService service,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IEntitlementService entitlements)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
            _entitlements = entitlements;
        }

        private GitRepositoryResponse ToResponse(GitRepository repo, string userId) => new GitRepositoryResponse
        {
            Id = repo.Id,
            Name = repo.Name,
            Description = repo.Description,
            ProjectId = repo.ProjectId,
            DefaultBranch = repo.DefaultBranch,
            IsPrivate = repo.IsPrivate,
            CreatedBy = repo.CreatedBy,
            CreatedAt = repo.CreatedAt,
            UpdatedAt = repo.UpdatedAt,
            PushedAt = repo.PushedAt,
            UserRole = string.IsNullOrEmpty(userId) ? null : _service.GetUserRole(repo.Id, userId),
        };

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest request)
        {
            if (request == null) return BadRequest("Request body is required.");
            if (string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Repository name is required.");

            // The project is supplied in the body, so it is attacker-controlled.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, request.ProjectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            // Plan gate. Distinct from the membership check above: that decides WHETHER YOU
            // BELONG, this decides whether the organization's plan includes repositories at
            // all. Returns 402 rather than 403 so the client can offer an upgrade instead of
            // an access error.
            var entitlements = await _entitlements.ForOrganizationAsync(auth.Org?.Id);
            var allowed = this.RequireEntitlement(entitlements, Entitlement.GitRepositories);
            if (!allowed.Allowed) return allowed.Error;

            // Quota, not just capability: a plan may include repositories but cap how many.
            // Routed through RequireQuota so it honours the same log/enforce switch — checking
            // the limit inline here would re-block requests that "log" mode just allowed.
            var used = (_service.GetAllRepositories() ?? Enumerable.Empty<GitRepository>())
                .Count(r => r != null && r.ProjectId == request.ProjectId);

            var withinQuota = this.RequireQuota(entitlements, Entitlement.GitRepositories, used);
            if (!withinQuota.Allowed) return withinQuota.Error;

            var repo = new GitRepository
            {
                Name = request.Name.Trim(),
                Description = request.Description,
                ProjectId = request.ProjectId,
                IsPrivate = request.IsPrivate,
            };

            // "Link Existing" only ever reaches here with githubRepoId/githubUrl set — there is
            // no clone/import behind it (see CreateRepositoryRequest), so it always creates a
            // new empty local repository, same as "Create New".
            var created = _service.CreateRepository(repo, auth.UserId);
            return Ok(ToResponse(created, auth.UserId));
        }

        /// <remarks>
        /// Returns only repositories in the caller's organizations. This previously returned
        /// every repository on the server, disclosing other tenants' project structure.
        /// </remarks>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var userId = OrgAccess.UserId(User);
            if (string.IsNullOrEmpty(userId)) return Unauthorized();

            var orgs = _organizations.FetchOrganizationsByUserId(userId) ?? new List<Organization>();
            var myOrgIds = new HashSet<string>(
                orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
                StringComparer.OrdinalIgnoreCase);

            var repos = _service.GetAllRepositories() ?? Enumerable.Empty<GitRepository>();

            var visible = new List<GitRepositoryResponse>();
            foreach (var r in repos.Where(r => r != null))
            {
                var orgId = await OrgScope.OrgIdForProjectAsync(_projects, r.ProjectId);
                if (orgId != null && myOrgIds.Contains(orgId)) visible.Add(ToResponse(r, userId));
            }

            return Ok(visible);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> Get(string id)
        {
            var auth = await this.AuthorizeRepoAsync(
                _organizations, _projects, _service, id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var repo = _service.GetRepository(id);
            if (repo == null) return NotFound();
            return Ok(ToResponse(repo, auth.UserId));
        }

        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] UpdateRepositoryRequest request)
        {
            // Authorized against the STORED repository, not the submitted one.
            var auth = await this.AuthorizeRepoAsync(
                _organizations, _projects, _service, id, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var success = _service.UpdateRepository(id, request ?? new UpdateRepositoryRequest());
            if (!success) return NotFound();

            var repo = _service.GetRepository(id);
            return Ok(ToResponse(repo, auth.UserId));
        }

        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            // Deleting a repository removes its working directory from disk. Managers only.
            var auth = await this.AuthorizeRepoAsync(
                _organizations, _projects, _service, id, OrgAccess.Managers);
            if (!auth.Allowed) return auth.Error;

            var success = _service.DeleteRepository(id);
            return success ? Ok() : NotFound();
        }

        // Note: the frontend also declares a singular getRepositoryByProject() against this
        // same URL, expecting one object instead of an array — it is unused anywhere in the
        // app (grepped: declared, never called), so this endpoint matches the one that's
        // actually used (ProjectRepositories.tsx, via getRepositories() filtered client-side)
        // rather than servicing dead code with an incompatible shape.
        [HttpGet("project/{projectId}")]
        public async Task<IActionResult> GetByProject(string projectId)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var repos = (_service.GetAllRepositories() ?? Enumerable.Empty<GitRepository>())
                .Where(r => r.ProjectId == projectId)
                .Select(r => ToResponse(r, auth.UserId));
            return Ok(repos);
        }
    }
}
