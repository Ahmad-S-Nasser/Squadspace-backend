using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/Git/repositories/{repoId}/pulls")]
    // SECURITY: routed as api/Git/repositories/{repoId}/... and previously had neither
    // [Authorize] nor any ownership check, so any caller could read or modify another
    // organization's source code by supplying a repository id. Every action now
    // resolves the repository's project and requires organization membership.
    [Authorize]
    public class GitPullRequestsController : ControllerBase
    {
        private readonly IGitRepositoryService _service;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly IContributorRepository _contributors;

        public GitPullRequestsController(
            IGitRepositoryService service,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            IContributorRepository contributors)
        {
            _service = service;
            _organizations = organizations;
            _projects = projects;
            _contributors = contributors;
        }

        /// <summary>Authorizes against the organization owning this repository.</summary>
        private Task<OrgAuth> AuthorizeRepoAsync(string repoId) =>
            this.AuthorizeRepoAsync(_organizations, _projects, _service, repoId, OrgAccess.AnyMember);

        private async Task<GitPersonResponse> ToPersonAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            var user = await _contributors.GetByIdAsync(userId);
            return new GitPersonResponse { Id = userId, Name = user?.Name ?? "Unknown user" };
        }

        private async Task<PullRequestResponse> ToResponseAsync(string repoId, PullRequest pr)
        {
            var (additions, deletions, changedFiles, commits) = _service.GetPullRequestStats(repoId, pr);

            return new PullRequestResponse
            {
                Number = pr.Number,
                Title = pr.Title,
                Body = pr.Body,
                // The frontend's "closed" bucket covers both a plain-closed PR and a merged
                // one; MergedAt is what tells them apart, matching how PullRequestList filters.
                State = pr.Status == "open" ? "open" : "closed",
                IsDraft = pr.IsDraft,
                HtmlUrl = "",
                SourceBranch = pr.SourceBranch,
                TargetBranch = pr.TargetBranch,
                Author = await ToPersonAsync(pr.AuthorId),
                Additions = additions,
                Deletions = deletions,
                ChangedFiles = changedFiles,
                Commits = commits,
                // No mergeability precheck exists — left unknown (null) rather than a guess;
                // the merge action itself reports a real conflict if the attempt fails.
                Mergeable = null,
                MergedAt = pr.MergedAt,
                MergedBy = await ToPersonAsync(pr.MergedById),
                CreatedAt = pr.CreatedAt,
                UpdatedAt = pr.UpdatedAt,
                ClosedAt = pr.ClosedAt,
            };
        }

        private async Task<PullRequestDetailResponse> ToDetailResponseAsync(string repoId, PullRequest pr)
        {
            var (additions, deletions, changedFiles, commits) = _service.GetPullRequestStats(repoId, pr);
            var files = _service.GetPullRequestFiles(repoId, pr).Select(GitMappers.ToFileResponse).ToList();

            var comments = new List<PRCommentResponse>();
            foreach (var c in _service.GetPullRequestComments(repoId, pr.Number))
            {
                comments.Add(new PRCommentResponse
                {
                    Id = c.Id,
                    Body = c.Body,
                    Author = await ToPersonAsync(c.AuthorId),
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Path = c.Path,
                    Line = c.Line,
                    CommitSha = c.CommitSha,
                });
            }

            return new PullRequestDetailResponse
            {
                Number = pr.Number,
                Title = pr.Title,
                Body = pr.Body,
                State = pr.Status == "open" ? "open" : "closed",
                IsDraft = pr.IsDraft,
                HtmlUrl = "",
                SourceBranch = pr.SourceBranch,
                TargetBranch = pr.TargetBranch,
                Author = await ToPersonAsync(pr.AuthorId),
                Additions = additions,
                Deletions = deletions,
                ChangedFiles = changedFiles,
                Commits = commits,
                Mergeable = null,
                MergedAt = pr.MergedAt,
                MergedBy = await ToPersonAsync(pr.MergedById),
                CreatedAt = pr.CreatedAt,
                UpdatedAt = pr.UpdatedAt,
                ClosedAt = pr.ClosedAt,
                Files = files,
                Comments = comments,
            };
        }

        [HttpGet]
        public async Task<IActionResult> GetAll(string repoId, [FromQuery] string state, [FromQuery] int page = 1, [FromQuery] int perPage = 20)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var (prs, totalCount) = _service.GetPullRequests(repoId, state, page, perPage);
            var items = new List<PullRequestResponse>();
            foreach (var pr in prs) items.Add(await ToResponseAsync(repoId, pr));

            return Ok(new PullRequestsResponse
            {
                PullRequests = items,
                TotalCount = totalCount,
                Page = page <= 0 ? 1 : page,
                PerPage = perPage <= 0 ? 20 : perPage,
            });
        }

        [HttpPost]
        public async Task<IActionResult> Create(string repoId, [FromBody] CreatePullRequestRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Title) ||
                string.IsNullOrWhiteSpace(request.SourceBranch) || string.IsNullOrWhiteSpace(request.TargetBranch))
                return BadRequest("Title, sourceBranch and targetBranch are required.");

            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var pr = _service.CreatePullRequest(repoId, request.Title.Trim(), request.Body, request.SourceBranch, request.TargetBranch, request.IsDraft, auth.UserId);
            return Ok(await ToResponseAsync(repoId, pr));
        }

        [HttpGet("{number}")]
        public async Task<IActionResult> Get(string repoId, int number)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var pr = _service.GetPullRequest(repoId, number);
            if (pr == null) return NotFound();
            return Ok(await ToDetailResponseAsync(repoId, pr));
        }

        [HttpPut("{number}")]
        public async Task<IActionResult> Update(string repoId, int number, [FromBody] UpdatePullRequestRequest request)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var success = _service.UpdatePullRequest(repoId, number, request ?? new UpdatePullRequestRequest());
            if (!success) return NotFound();

            var pr = _service.GetPullRequest(repoId, number);
            return Ok(await ToResponseAsync(repoId, pr));
        }

        [HttpPost("{number}/merge")]
        public async Task<IActionResult> Merge(string repoId, int number, [FromBody] MergePullRequestRequest request)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var (authorName, authorEmail) = GitAuthorHelper.FromClaims(User);
            var (success, conflict) = _service.MergePullRequest(
                repoId, number, request?.MergeMethod, request?.CommitMessage ?? request?.CommitTitle, auth.UserId, authorName, authorEmail);

            if (conflict) return Conflict(new { message = "Merge conflict — this pull request has changes that can't be merged automatically." });
            return success ? Ok() : BadRequest();
        }

        [HttpGet("{number}/comments")]
        public async Task<IActionResult> GetComments(string repoId, int number)
        {
            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var pr = _service.GetPullRequest(repoId, number);
            if (pr == null) return NotFound();

            var comments = new List<PRCommentResponse>();
            foreach (var c in _service.GetPullRequestComments(repoId, number))
            {
                comments.Add(new PRCommentResponse
                {
                    Id = c.Id,
                    Body = c.Body,
                    Author = await ToPersonAsync(c.AuthorId),
                    CreatedAt = c.CreatedAt,
                    UpdatedAt = c.UpdatedAt,
                    Path = c.Path,
                    Line = c.Line,
                    CommitSha = c.CommitSha,
                });
            }
            return Ok(comments);
        }

        [HttpPost("{number}/comments")]
        public async Task<IActionResult> AddComment(string repoId, int number, [FromBody] AddPRCommentRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Body))
                return BadRequest("Comment body is required.");

            var auth = await AuthorizeRepoAsync(repoId);
            if (!auth.Allowed) return auth.Error;

            var pr = _service.GetPullRequest(repoId, number);
            if (pr == null) return NotFound();

            var comment = _service.AddPullRequestComment(repoId, number, request.Body.Trim(), auth.UserId, request.Path, request.Line, request.CommitSha);
            return Ok(new PRCommentResponse
            {
                Id = comment.Id,
                Body = comment.Body,
                Author = await ToPersonAsync(comment.AuthorId),
                CreatedAt = comment.CreatedAt,
                UpdatedAt = comment.UpdatedAt,
                Path = comment.Path,
                Line = comment.Line,
                CommitSha = comment.CommitSha,
            });
        }
    }
}
