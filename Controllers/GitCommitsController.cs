using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/repositories/{repoId}/commits")]
    // SECURITY: routed as api/Git/repositories/{repoId}/... and previously had neither
    // [Authorize] nor any ownership check, so any caller could read or modify another
    // organization's source code by supplying a repository id. Every action now
    // resolves the repository's project and requires organization membership.
    [Authorize]
    public class GitCommitsController : ControllerBase
    {
        private readonly IGitRepositoryService _service;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;

        public GitCommitsController(IGitRepositoryService service, IOrganizationRepository organizations, IProjectRepository projects)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
        }

        /// <summary>Authorizes against the organization owning this repository.</summary>
        private Task<OrgAuth> AuthorizeRepoAsync(string repoId) =>
            this.AuthorizeRepoAsync(_organizations, _projects, _service, repoId, OrgAccess.AnyMember);

        [HttpGet]
        public async Task<IActionResult> GetAll(string repoId, [FromQuery] string branch, [FromQuery] int page = 1, [FromQuery] int perPage = 20)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var (commits, totalCount) = _service.GetCommits(repoId, branch, page, perPage);
            var items = commits.Select(GitMappers.ToCommitResponse).ToList();

            return Ok(new GitCommitsResponse
            {
                Commits = items,
                TotalCount = totalCount,
                Page = page <= 0 ? 1 : page,
                PerPage = perPage <= 0 ? 20 : perPage,
                HasMore = (page <= 0 ? 1 : page) * (perPage <= 0 ? 20 : perPage) < totalCount,
            });
        }

        [HttpGet("{sha}")]
        public async Task<IActionResult> Get(string repoId, string sha)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var commit = _service.GetCommit(repoId, sha);
            if (commit == null) return NotFound();

            var (files, additions, deletions) = _service.GetCommitDiff(repoId, sha);
            var response = GitMappers.ToCommitResponse(commit);

            return Ok(new GitCommitDetailResponse
            {
                Sha = response.Sha,
                ShortSha = response.ShortSha,
                Message = response.Message,
                Author = response.Author,
                Committer = response.Committer,
                ParentShas = response.ParentShas,
                Additions = additions,
                Deletions = deletions,
                ChangedFiles = files.Count,
                Files = files.Select(GitMappers.ToFileResponse).ToList(),
                Stats = new GitCommitStatsResponse { Additions = additions, Deletions = deletions, Total = additions + deletions },
            });
        }

        /// <summary>Raw unified diff for the whole commit, as plain text. The in-app commit
        /// view (CommitDiff.tsx) actually renders per-file patches from Get() above; this
        /// exists for the dedicated raw-diff endpoint the frontend API client also declares.</summary>
        [HttpGet("{sha}/diff")]
        public async Task<IActionResult> GetDiff(string repoId, string sha)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var commit = _service.GetCommit(repoId, sha);
            if (commit == null) return NotFound();

            var (files, _, _) = _service.GetCommitDiff(repoId, sha);
            var text = string.Join("\n", files.Select(f => f.Patch ?? string.Empty));
            return Content(text, "text/plain");
        }
    }
}
