using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// One task status in an organization's workflow.
    /// </summary>
    [BsonIgnoreExtraElements]
    public class TaskStatusDefinition
    {
        /// <summary>
        /// Stable machine identity, and the value physically stored in NoteTask.Status.
        /// </summary>
        /// <remarks>
        /// Immutable once created. Renaming a status changes <see cref="Label"/>, never this -
        /// changing it would orphan every task already carrying the old value, and there is no
        /// transaction available to rewrite them atomically.
        ///
        /// The seeded slugs are byte-identical to the frontend's canonical union in
        /// src/types/tasks.ts. That is not cosmetic: live documents already contain those exact
        /// strings, so any other choice would strand every existing task on day one.
        /// </remarks>
        public string Slug { get; set; }

        /// <summary>Display text. Free to change at any time.</summary>
        public string Label { get; set; }

        /// <summary>Tailwind badge classes, mirroring statusLabels in src/types/tasks.ts.</summary>
        public string Color { get; set; }

        /// <summary>Column position, ascending.</summary>
        public int Order { get; set; }

        /// <summary>
        /// The work is finished. This is what replaces the ~30 hardcoded <c>== "done"</c> checks.
        /// </summary>
        /// <remarks>
        /// Deliberately a property of the status rather than a boolean denormalized onto each
        /// task: a task-level copy would have to be rewritten across the whole organization
        /// whenever an admin marks another status terminal.
        /// </remarks>
        public bool IsTerminal { get; set; }

        /// <summary>Work has begun. Used for cycle-time, and for "not started" filters.</summary>
        public bool IsStarted { get; set; }

        /// <summary>Applied to a task created without a status.</summary>
        public bool IsDefault { get; set; }

        /// <summary>Retired: still resolves for existing tasks, but is not offered for new ones.</summary>
        public bool IsArchived { get; set; }

        /// <summary>
        /// Legacy and third-party spellings that mean this status.
        /// </summary>
        /// <remarks>
        /// The data already disagrees with itself - the backend model defaulted to "To-do", the
        /// frontend writes "todo", and analytics compared against both "done" and "Done". Aliases
        /// let the resolver treat them as one before any data is rewritten, which is what makes
        /// the later normalization migration a semantic no-op rather than a cutover.
        ///
        /// Kept permanently: integrations and importers will keep sending unknown spellings long
        /// after the stored data is clean.
        /// </remarks>
        public List<string> Aliases { get; set; } = new();
    }

    /// <summary>
    /// One organization-defined field on a task, note, board or project.
    /// </summary>
    /// <remarks>
    /// Deliberately a small, closed set of types. Relation and rollup fields would reintroduce
    /// exactly the cross-entity graph this phase spent its effort untangling, so they are out of
    /// scope until there is a reason beyond completeness.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class CustomFieldDefinition
    {
        /// <summary>
        /// Stable machine key, and the key used in the entity's CustomFields bag.
        /// </summary>
        /// <remarks>
        /// Constrained to ^[a-z][a-z0-9_]{0,39}$. This is not stylistic: a BSON key containing a
        /// "." or starting with "$" produces a document that cannot be queried or updated through
        /// normal driver calls. Validated before any write.
        /// </remarks>
        public string Slug { get; set; }

        /// <summary>Display name. Free to change.</summary>
        public string Label { get; set; }

        /// <summary>"string" | "number" | "date" | "select" | "bool".</summary>
        public string Type { get; set; } = "string";

        /// <summary>"task" | "note" | "board" | "project".</summary>
        public string AppliesTo { get; set; } = "task";

        /// <summary>Allowed values, for Type = "select".</summary>
        public List<string> Options { get; set; } = new();

        public int Order { get; set; }

        /// <summary>Retired: existing values still read, not offered on new records.</summary>
        public bool IsArchived { get; set; }
    }

    /// <summary>
    /// Per-organization workspace vocabulary: task statuses and custom fields.
    /// </summary>
    /// <remarks>
    /// One document per organization rather than one per status, for three reasons:
    ///
    /// - It is always read whole. You cannot render a board from a subset of its columns.
    /// - Reordering is atomic. StartSession is used nowhere in this codebase, so with per-status
    ///   documents a drag-reorder would be N unordered UpdateOne calls, and a half-applied column
    ///   order would be a permanent inconsistency with no way to roll back.
    /// - Slug uniqueness within an organization is then a code check on a list, instead of a
    ///   compound unique index that could later conflict.
    ///
    /// It is emphatically NOT stored on Models/Organization.cs. That object is embedded in every
    /// Project, which is embedded in every Board, Note and Task - configuration hung off it would
    /// be copied across the entire database.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class OrgWorkspaceConfig
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Owning organization. Unique.</summary>
        public string OrganizationId { get; set; }

        public List<TaskStatusDefinition> Statuses { get; set; } = new();

        /// <summary>
        /// Custom field definitions. Shares this document with Statuses deliberately: same
        /// lifecycle, same cache entry, same endpoint, one round trip on first render.
        /// </summary>
        public List<CustomFieldDefinition> CustomFields { get; set; } = new();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
