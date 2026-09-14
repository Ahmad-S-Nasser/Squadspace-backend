using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// What a report groups by.
    /// </summary>
    /// <remarks>
    /// A closed vocabulary of built-in dimensions, plus <c>custom:&lt;slug&gt;</c> for the
    /// organization's own fields. That escape hatch is the whole point of sequencing this after
    /// the schema work: a report builder that cannot group by the fields a team imported from
    /// Jira is not much of a builder.
    ///
    /// Deliberately NOT "any field path the client names". An arbitrary path lets a caller group
    /// by <c>Note.Board.Project.Organization.Members</c> and read another tenant's member list out
    /// of the group keys - the aggregation is org-filtered, but the keys themselves would leak.
    /// Every dimension here resolves to a known, safe field.
    /// </remarks>
    public static class ReportDimensions
    {
        public const string Status = "status";
        public const string Priority = "priority";
        public const string Category = "category";
        public const string Project = "project";
        public const string Assignee = "assignee";
        public const string Sprint = "sprint";
        public const string Creator = "creator";

        /// <summary>Calendar buckets over CreatedAt.</summary>
        public const string Day = "day";
        public const string Week = "week";
        public const string Month = "month";

        /// <summary>Prefix for an organization-defined field: <c>custom:story_points</c>.</summary>
        public const string CustomPrefix = "custom:";

        public static readonly string[] BuiltIn =
        {
            Status, Priority, Category, Project, Assignee, Sprint, Creator, Day, Week, Month,
        };
    }

    /// <summary>What a report counts or sums.</summary>
    public static class ReportMeasures
    {
        /// <summary>Number of tasks.</summary>
        public const string Count = "count";

        /// <summary>Tasks in a terminal status.</summary>
        public const string CompletedCount = "completed";

        /// <summary>Percentage of tasks in a terminal status.</summary>
        public const string CompletionRate = "completionRate";

        /// <summary>Sum of EstimatedDuration, in minutes.</summary>
        public const string EstimatedMinutes = "estimatedMinutes";

        /// <summary>Sum of RealDuration - measured time, from Phase 3's segments.</summary>
        public const string ActualMinutes = "actualMinutes";

        /// <summary>Actual minus estimated, in minutes. Negative means it came in under.</summary>
        public const string EstimateVariance = "estimateVariance";

        public static readonly string[] All =
        {
            Count, CompletedCount, CompletionRate, EstimatedMinutes, ActualMinutes, EstimateVariance,
        };
    }

    /// <summary>The filters a report applies before grouping.</summary>
    [BsonIgnoreExtraElements]
    public class ReportFilters
    {
        /// <summary>Empty or null means every project in the organization.</summary>
        public List<string> ProjectIds { get; set; } = new();

        public List<string> StatusSlugs { get; set; } = new();
        public List<string> Priorities { get; set; } = new();
        public List<string> AssigneeIds { get; set; } = new();
        public List<string> SprintIds { get; set; } = new();

        /// <summary>Filters on CreatedAt.</summary>
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }

        /// <summary>Restrict to tasks in a terminal status, or to those not in one.</summary>
        public bool? OnlyCompleted { get; set; }
    }

    /// <summary>A runnable report: what to filter, how to group, what to measure.</summary>
    [BsonIgnoreExtraElements]
    public class ReportDefinition
    {
        public ReportFilters Filters { get; set; } = new();

        /// <summary>A value from <see cref="ReportDimensions"/>, or <c>custom:&lt;slug&gt;</c>.</summary>
        public string GroupBy { get; set; } = ReportDimensions.Status;

        /// <summary>Optional second grouping, for stacked and grouped charts.</summary>
        public string SplitBy { get; set; }

        /// <summary>
        /// A value from <see cref="ReportMeasures"/>.
        /// </summary>
        /// <remarks>
        /// Kept for the single-measure reports saved before <see cref="Measures"/> existed. When
        /// Measures is empty this is the one that runs, so an older saved report keeps working
        /// untouched.
        /// </remarks>
        public string Measure { get; set; } = ReportMeasures.Count;

        /// <summary>
        /// Several measures side by side. Falls back to <see cref="Measure"/> when empty.
        /// </summary>
        /// <remarks>
        /// A chart still draws the FIRST measure only - plotting count and minutes on one axis
        /// would need two y-scales, and a dual-axis chart is the single most misread thing in
        /// data visualization. The table shows them all, which is where comparing them belongs.
        /// </remarks>
        public List<string> Measures { get; set; } = new();

        /// <summary>Conditions applied on top of <see cref="Filters"/>.</summary>
        public ReportConditions Conditions { get; set; } = new();

        /// <summary>"aggregate" (grouped rows) or "detail" (the tasks themselves).</summary>
        public string Mode { get; set; } = ReportModes.Aggregate;

        /// <summary>Columns for detail mode. Defaults to <see cref="ReportColumns.Default"/>.</summary>
        public List<string> Columns { get; set; } = new();

        /// <summary>"value" | "label" | a measure key. Defaults to the first measure.</summary>
        public string SortBy { get; set; }

        /// <summary>"asc" or "desc".</summary>
        public string SortDirection { get; set; } = "desc";

        /// <summary>Adds a totals row across every group.</summary>
        public bool ShowTotals { get; set; } = true;

        /// <summary>Adds each row's share of the total.</summary>
        public bool ShowPercentOfTotal { get; set; }

        /// <summary>"bar" | "line" | "pie" | "table" — presentation only, never affects the data.</summary>
        public string ChartType { get; set; } = "bar";

        /// <summary>Cap on returned groups. Server-clamped; see ReportService.</summary>
        public int Limit { get; set; } = 50;
    }

    /// <summary>
    /// A report someone saved and may share with their organization.
    /// </summary>
    /// <remarks>
    /// The first genuinely new entity in this roadmap. Owner-scoped by default and shared only
    /// deliberately, because a report is a question someone asked - a half-finished one appearing
    /// on a colleague's dashboard is worse than not having saved views at all.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class SavedReport
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string OrganizationId { get; set; }

        /// <summary>Creator's user id, from the JWT. Only they may edit or delete it.</summary>
        public string OwnerId { get; set; }
        public string OwnerName { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }

        public ReportDefinition Definition { get; set; } = new();

        /// <summary>
        /// Additional blocks. Empty means a single-definition report, exactly as before.
        /// </summary>
        /// <remarks>
        /// Additive rather than a replacement for Definition: every report saved before sections
        /// existed keeps rendering, and a reader of the model can see which one is the primary
        /// block without consulting a length check.
        /// </remarks>
        public List<ReportSection> Sections { get; set; } = new();

        /// <summary>Visible to everyone in the organization when true; to the owner alone otherwise.</summary>
        public bool SharedWithOrganization { get; set; }

        /// <summary>Pinned onto the owner's dashboard, which is what turns fixed JSX into a widget list.</summary>
        public bool PinnedToDashboard { get; set; }

        public int SortOrder { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
