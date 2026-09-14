using MongoDB.Bson.Serialization.Attributes;
using RafeeqyNotes.Api.Models;
using System;
using System.Collections.Generic;

namespace RafeeqyNotes.Models
{
    [BsonIgnoreExtraElements]
    public class TaskActivityActor
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class TaskActivityMetadata
    {
        public string? OldValue { get; set; }
        public string? NewValue { get; set; }
        public string? CommentText { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class TaskActivity
    {
        public string Id { get; set; }
        public string TaskId { get; set; }
        public string Type { get; set; }
        public TaskActivityActor Actor { get; set; }
        public string Message { get; set; }
        public TaskActivityMetadata? Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class TaskCommentAuthor
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Email { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class TaskComment
    {
        public string Id { get; set; }
        public string TaskId { get; set; }
        public TaskCommentAuthor Author { get; set; }
        public string Content { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [BsonIgnoreExtraElements]
    public class NoteTask
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        // No default here. This one initializer is why the data holds three spellings of the
        // same status: it wrote "To-do" while the frontend writes "todo" and analytics compared
        // against "done"/"Done". A task created without a status now takes the organization's
        // configured default slug, applied in NoteTaskController.
        public string Status { get; set; }
        public string Priority { get; set; } = "Medium";// "High", "Medium", "Low"

        // "bug", "feature", "improvement", "documentation", "research", "other".
        // Mirrors TaskCategory in the app's src/types/tasks.ts. The client has always sent
        // this; until now there was no property to bind it to and every value was dropped.
        public string Category { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? DueDate { get; set; }
        public Contributor Creator { get; set; }
        public List<Contributor> AssignedTo { get; set; } = new List<Contributor>();
        public Contributor Reviewer { get; set; }
        public string ReviewerComment { get; set; }
        public Note Note { get; set; }

        // Denormalized parent ids, populated SERVER-SIDE by EntityGraphService on every write.
        // Never trust the incoming value: the client already sends fields of these names
        // alongside the embedded snapshot, so binding them and then treating them as
        // authoritative for tenant resolution would let a caller name someone else's
        // organization. Hydration clears them and rewrites them from what it read.
        public string NoteId { get; set; }
        public string BoardId { get; set; }
        public string ProjectId { get; set; }
        public string OrganizationId { get; set; }

        // Where this record came from, when it was imported rather than created here.
        // ExternalSource names the system ("jira", "clickup", "azure-devops"); ExternalId is
        // that system's own key. Together they carry a partial unique index, which is what
        // makes an import idempotent and resumable: re-running it collides instead of
        // duplicating, and the collision is reported as 409 rather than a 500.
        //
        // The index is partial because these are absent on everything created in the product
        // itself. A plain unique compound index would treat every one of those as the same
        // (null, null) key and reject the second document written.
        public string ExternalId { get; set; }
        public string ExternalSource { get; set; }

        // Per-organization custom fields, keyed by the field slug from OrgWorkspaceConfig.
        //
        // Values are strings even when the field is declared numeric or a date, exactly as
        // SubscriptionPlan.Entitlements stores "25" and "unlimited" side by side - the registry
        // declares the type and each edge parses. That idiom already exists on both sides of the
        // wire here, so this adds one concept rather than three.
        //
        // Keys are validated against ^[a-z][a-z0-9_]{0,39}$ on write: a BSON key containing "."
        // or starting with "$" produces a document that cannot be queried or updated normally.
        public Dictionary<string, string> CustomFields { get; set; } = new();
        public string SprintId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? EstimatedDuration { get; set; } = 0; // in minutes
        /// <summary>
        /// Measured time on this task, in minutes: the sum of CLOSED segments in
        /// <see cref="TimeEntries"/>, across every person who worked on it.
        /// </summary>
        /// <remarks>
        /// DERIVED, not entered. It used to be typed in by hand when a task moved to Done, and the
        /// dialog pre-filled it with the ESTIMATE - so a distracted user accepting the default
        /// silently made the "actual" a copy of the guess. Velocity, workload, cycle time and the
        /// eventual estimate-vs-actual calibration all inherited that, and the whole value of
        /// calibration is that the two numbers are independent measurements.
        ///
        /// Kept persisted rather than computed on read, so existing consumers (Dashboard's
        /// workload chart, analytics) keep working unchanged. Recomputed server-side on every
        /// timer transition; a client-supplied value is ignored.
        ///
        /// CLOSED segments only. A running segment is deliberately excluded, because a stored
        /// total that includes one is stale the moment it is written. The live figure - stored
        /// total plus whatever is currently running - is computed for display, never persisted.
        /// </remarks>
        public int? RealDuration { get; set; } = 0; // in minutes

        /// <summary>Per-person stretches of work. See TaskTimeEntry for why these are segments.</summary>
        public List<TaskTimeEntry> TimeEntries { get; set; } = new();
        /// <summary>
        /// Ids of the tasks this one waits on. THIS is what is stored.
        /// </summary>
        /// <remarks>
        /// Dependencies used to be persisted as full <see cref="NoteTask"/> objects, each with
        /// its own Note - Board - Project chain and its own three embedded people. On the largest
        /// task in dev that was 3,635 bytes, 46% of the document, for what is conceptually a
        /// list of ids.
        ///
        /// Worse than the size, the copy was a SNAPSHOT: a dependency's title and status were
        /// frozen at the moment the link was made, so "Blocked" could keep showing after the
        /// blocker was finished. Storing ids and resolving them on read makes that impossible.
        /// </remarks>
        public List<string> DependencyIds { get; set; } = new List<string>();

        /// <summary>
        /// Dependency summaries - id, title, status, priority. Computed for display, never persisted.
        /// </summary>
        /// <remarks>
        /// <see cref="BsonIgnoreAttribute"/> is what stops the old fat shape coming back: this is
        /// filled in on the way out and dropped on the way in, so no write path can persist it.
        /// The property stays a <c>List&lt;NoteTask&gt;</c> rather than becoming a new DTO type
        /// because the response shape must not change - clients read <c>dependencies[].id</c>,
        /// <c>.title</c> and <c>.status</c>, and every one of those still arrives.
        ///
        /// It also still DESERIALIZES, which is deliberate: existing clients send whole task
        /// objects here and the server maps them down to ids. Nothing had to change on the client
        /// for this to be safe.
        ///
        /// Null means "not supplied" and empty means "no dependencies" - a distinction the update
        /// path depends on to tell a caller clearing the list from one that never mentioned it.
        /// Hence no initializer.
        /// </remarks>
        [BsonIgnore]
        public List<TaskDependencySummary> Dependencies { get; set; }
        public List<TaskComment> Comments { get; set; } = new List<TaskComment>();
        public List<TaskActivity> Activities { get; set; } = new List<TaskActivity>();
    }

    /// <summary>
    /// A dependency, as much of it as anything actually needs.
    /// </summary>
    /// <remarks>
    /// Four fields, chosen from what the clients read: the dependency graph uses the id to draw
    /// edges, and the kanban card and detail dialog use the status to decide whether a task is
    /// blocked. Title and priority are there so a summary can be rendered without a second
    /// lookup.
    ///
    /// A type of its own rather than a sparsely-filled <see cref="NoteTask"/>. That was the first
    /// attempt and it serialized every default on the model - CreatedAt, RealDuration,
    /// CustomFields, empty collections - so each dependency still cost 567 bytes to express four
    /// values. This costs what it says it costs.
    ///
    /// It also deserializes, which is what keeps older clients working: they post whole task
    /// objects here, and the extra properties are simply ignored.
    /// </remarks>
    public class TaskDependencySummary
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
    }
}
