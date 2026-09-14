namespace RafeeqyNotes.Api.Models
{
    // ===== Storage models (persisted in MongoDB) =====

    public class GitRepository
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public string ProjectId { get; set; }
        public string DefaultBranch { get; set; } = "main";
        public bool IsPrivate { get; set; } = true;
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public DateTime? PushedAt { get; set; }
    }

    public class Collaborator
    {
        public string Id { get; set; }
        public string RepoId { get; set; }
        public string UserId { get; set; }
        public string Role { get; set; } // admin | write | read
        public string? AddedBy { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    }

    public class PullRequest
    {
        public string Id { get; set; }
        public string RepoId { get; set; }
        public int Number { get; set; }
        public string Title { get; set; }
        public string? Body { get; set; }
        public string SourceBranch { get; set; }
        public string TargetBranch { get; set; }

        // Internal tracking keeps "merged" distinct from "closed"; the API response
        // collapses this to the frontend's open/closed state and uses MergedAt to
        // distinguish a merged PR from a plain closed one within "closed".
        public string Status { get; set; } = "open"; // open | closed | merged
        public bool IsDraft { get; set; }
        public string AuthorId { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? ClosedAt { get; set; }
        public DateTime? MergedAt { get; set; }
        public string? MergedById { get; set; }
    }

    public class PRComment
    {
        public string Id { get; set; }
        public string RepoId { get; set; }
        public int PrNumber { get; set; }
        public string Body { get; set; }
        public string AuthorId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        public string? Path { get; set; }
        public int? Line { get; set; }
        public string? CommitSha { get; set; }
    }

    // ===== Internal working types (not serialized directly to clients) =====

    public class GitCommit
    {
        public string Sha { get; set; }
        public string AuthorName { get; set; }
        public string AuthorEmail { get; set; }
        public DateTimeOffset AuthorWhen { get; set; }
        public string CommitterName { get; set; }
        public string CommitterEmail { get; set; }
        public DateTimeOffset CommitterWhen { get; set; }
        public string Message { get; set; }
        public List<string> ParentShas { get; set; } = new();
    }

    public class GitFileEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Type { get; set; } // file | dir
        public long? Size { get; set; }
        public string? Sha { get; set; }
    }

    public class GitFileChange
    {
        public string Filename { get; set; }
        public string Status { get; set; } // added | removed | modified | renamed
        public int Additions { get; set; }
        public int Deletions { get; set; }
        public string? Patch { get; set; }
        public string? PreviousFilename { get; set; }
    }
}
