using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    public static class ImportJobStates
    {
        public const string Pending = "pending";
        public const string Running = "running";
        public const string Completed = "completed";

        /// <summary>Finished, but some rows failed. The successful ones are still there.</summary>
        public const string Partial = "partial";

        public const string Failed = "failed";
    }

    [BsonIgnoreExtraElements]
    public class ImportRowError
    {
        public int Row { get; set; }
        public string Message { get; set; }
    }

    /// <summary>
    /// One import run: what was asked for, how far it got, and what went wrong.
    /// </summary>
    /// <remarks>
    /// The existing CSV wizard has no job model at all - it fires a serial loop of single-entity
    /// POSTs from the browser, so failing at item 400 of 2000 leaves half a project behind with no
    /// record of what landed and no way to continue.
    ///
    /// RESUME IS NOT A SEPARATE MECHANISM. Every imported entity carries
    /// {ExternalSource, ExternalId} under a partial unique index, so re-running the same file
    /// collides on what already exists instead of duplicating it. Re-submitting IS the resume, and
    /// it works whether the previous run was interrupted by an error, a timeout, or the app
    /// restarting mid-run. That is what Phase 2's external identity was for.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class ImportJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string OrganizationId { get; set; }
        public string OwnerId { get; set; }
        public string OwnerName { get; set; }

        /// <summary>"csv" | "jira" — which mapping was applied.</summary>
        public string Source { get; set; } = "csv";

        /// <summary>Namespaces the external ids, so two sources cannot collide on the same key.</summary>
        public string ExternalSource { get; set; }

        public string FileName { get; set; }
        public string State { get; set; } = ImportJobStates.Pending;

        public int TotalRows { get; set; }
        public int ProcessedRows { get; set; }

        public int ProjectsCreated { get; set; }
        public int BoardsCreated { get; set; }
        public int NotesCreated { get; set; }
        public int TasksCreated { get; set; }

        /// <summary>Rows whose entity already existed. On a resume this is most of them.</summary>
        public int SkippedExisting { get; set; }

        public int DependenciesLinked { get; set; }

        /// <summary>Capped; see ImportService for why a failing file is not allowed to fill Mongo.</summary>
        public List<ImportRowError> Errors { get; set; } = new();
        public int ErrorCount { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
    }
}
