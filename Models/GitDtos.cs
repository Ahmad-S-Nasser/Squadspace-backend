namespace RafeeqyNotes.Api.Models
{
    // Request/response shapes for the Git controllers, kept separate from the storage
    // models above so the wire format (what the frontend's src/types/git.ts expects) can
    // evolve independently of what's persisted, and so computed-per-request fields (like
    // a repository's UserRole for the calling user) never leak into storage.

    // ===== Repositories =====

    public class GitRepositoryResponse
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public string ProjectId { get; set; }
        public string DefaultBranch { get; set; }
        public bool IsPrivate { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? PushedAt { get; set; }

        // This is a standalone, self-hosted git host, not a GitHub mirror — there is no
        // real GitHub URL for a repository created here. Left blank rather than fabricated;
        // the frontend hides the "Open in GitHub" affordance when this is empty.
        public string GithubUrl { get; set; } = "";
        public string CloneUrl { get; set; } = "";

        public string? UserRole { get; set; } // admin | write | read, for the calling user
    }

    public class CreateRepositoryRequest
    {
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public bool IsPrivate { get; set; } = true;

        // Present only from the "Link Existing GitHub repo" tab. Currently informational
        // only — no clone/sync happens; the repository is created as a new empty repo. See
        // GitRepositoriesController.Create.
        public string? GithubRepoId { get; set; }
        public string? GithubUrl { get; set; }
    }

    public class UpdateRepositoryRequest
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? DefaultBranch { get; set; }
        public bool? IsPrivate { get; set; }
    }

    // ===== Branches =====

    public class GitBranchResponse
    {
        public string Name { get; set; }
        public bool IsDefault { get; set; }
        public bool IsProtected { get; set; } = false; // no branch-protection feature exists yet
        public GitCommitResponse? LastCommit { get; set; }
    }

    public class CreateBranchRequest
    {
        public string Name { get; set; }
        public string SourceBranch { get; set; }
    }

    public class MergeBranchRequest
    {
        public string? TargetBranch { get; set; }
        public string? CommitMessage { get; set; }
    }

    // ===== Commits =====

    public class GitCommitAuthorResponse
    {
        public string Name { get; set; }
        public string Email { get; set; }
        public string? AvatarUrl { get; set; }
        public DateTimeOffset Date { get; set; }
    }

    public class GitCommitResponse
    {
        public string Sha { get; set; }
        public string ShortSha { get; set; }
        public string Message { get; set; }
        public GitCommitAuthorResponse Author { get; set; }
        public GitCommitAuthorResponse Committer { get; set; }
        public List<string> ParentShas { get; set; } = new();
        public int? Additions { get; set; }
        public int? Deletions { get; set; }
        public int? ChangedFiles { get; set; }
    }

    public class GitCommitFileResponse
    {
        public string Filename { get; set; }
        public string Status { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public string? Patch { get; set; }
        public string? PreviousFilename { get; set; }
    }

    public class GitCommitStatsResponse
    {
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public int Total { get; set; }
    }

    public class GitCommitDetailResponse : GitCommitResponse
    {
        public List<GitCommitFileResponse> Files { get; set; } = new();
        public GitCommitStatsResponse Stats { get; set; }
    }

    public class GitCommitsResponse
    {
        public List<GitCommitResponse> Commits { get; set; }
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PerPage { get; set; }
        public bool HasMore { get; set; }
    }

    // ===== Files & Tree =====

    public class GitTreeItemResponse
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; } // file | dir
        public long? Size { get; set; }
        public string? Sha { get; set; }
    }

    public class GitTreeResponse
    {
        public List<GitTreeItemResponse> Items { get; set; }
        public bool Truncated { get; set; } = false;
    }

    public class GitFileContentResponse
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Sha { get; set; }
        public long Size { get; set; }
        public string Content { get; set; } // base64
        public string Encoding { get; set; } = "base64";
    }

    public class CreateFileRequest
    {
        public string? Path { get; set; }
        public string Content { get; set; } // base64
        public string Message { get; set; }
        public string? Branch { get; set; }
        public string? Sha { get; set; }
    }

    public class DeleteFileRequest
    {
        public string? Path { get; set; }
        public string Message { get; set; }
        public string? Branch { get; set; }
        public string? Sha { get; set; }
    }

    // ===== Pull Requests =====

    public class GitPersonResponse
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string? AvatarUrl { get; set; }
    }

    public class PullRequestResponse
    {
        public int Number { get; set; }
        public string Title { get; set; }
        public string? Body { get; set; }
        public string State { get; set; } // open | closed (merged PRs report "closed" + MergedAt set)
        public bool IsDraft { get; set; }
        public string HtmlUrl { get; set; } = "";
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public GitPersonResponse Author { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public int ChangedFiles { get; set; }
        public int Commits { get; set; }
        public bool? Mergeable { get; set; }
        public DateTime? MergedAt { get; set; }
        public GitPersonResponse? MergedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
    }

    public class PullRequestDetailResponse : PullRequestResponse
    {
        public List<GitCommitFileResponse> Files { get; set; } = new();
        public List<PRCommentResponse> Comments { get; set; } = new();
    }

    public class PullRequestsResponse
    {
        public List<PullRequestResponse> PullRequests { get; set; }
        public int TotalCount { get; set; }
        public int Page { get; set; }
        public int PerPage { get; set; }
    }

    public class CreatePullRequestRequest
    {
        public string Title { get; set; }
        public string? Body { get; set; }
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }
        public bool IsDraft { get; set; }
    }

    public class UpdatePullRequestRequest
    {
        public string? Title { get; set; }
        public string? Body { get; set; }
        public string? State { get; set; } // open | closed
    }

    public class MergePullRequestRequest
    {
        public string? MergeMethod { get; set; } // merge | squash | rebase
        public string? CommitTitle { get; set; }
        public string? CommitMessage { get; set; }
    }

    public class PRCommentResponse
    {
        public string Id { get; set; }
        public string Body { get; set; }
        public GitPersonResponse Author { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? Path { get; set; }
        public int? Line { get; set; }
        public string? CommitSha { get; set; }
    }

    public class AddPRCommentRequest
    {
        public string Body { get; set; }
        public string? Path { get; set; }
        public int? Line { get; set; }
        public string? CommitSha { get; set; }
    }

    // ===== Collaborators =====

    public class GitPersonWithEmailResponse
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
        public string? AvatarUrl { get; set; }
    }

    public class RepoCollaboratorResponse
    {
        public string Id { get; set; }
        public string UserId { get; set; }
        public GitPersonWithEmailResponse User { get; set; }
        public string RepositoryId { get; set; }
        public string Role { get; set; }
        public DateTime AddedAt { get; set; }
        public string? AddedBy { get; set; }
    }

    public class AddCollaboratorRequest
    {
        // The dialog collects an email; UserId is accepted too so future callers that
        // already know the id (e.g. a picker over org members) aren't forced through email.
        public string? UserId { get; set; }
        public string? Email { get; set; }
        public string Role { get; set; } = "read";
    }

    public class UpdateCollaboratorRequest
    {
        public string Role { get; set; }
    }

    // ===== GitHub Integration (browsing the user's own GitHub repos only — no clone/sync) =====

    public class GitHubOwnerResponse
    {
        public string Login { get; set; }
        public string AvatarUrl { get; set; }
    }

    public class GitHubRepoResponse
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string FullName { get; set; }
        public string? Description { get; set; }
        public string HtmlUrl { get; set; }
        public string CloneUrl { get; set; }
        public string SshUrl { get; set; }
        public bool IsPrivate { get; set; }
        public string DefaultBranch { get; set; }
        public string? Language { get; set; }
        public int Stars { get; set; }
        public int Forks { get; set; }
        public GitHubOwnerResponse Owner { get; set; }
    }
}
