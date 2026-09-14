using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// Fields a condition can test.
    /// </summary>
    /// <remarks>
    /// A closed vocabulary, for the same reason the dimension list is closed: an arbitrary field
    /// path would let a caller filter on - and by inference read - parts of the embedded parent
    /// graph that belong to another tenant. Every entry here resolves to one known, safe value.
    ///
    /// <c>custom:&lt;slug&gt;</c> reaches the organization's own fields.
    /// </remarks>
    public static class ReportFields
    {
        public const string Title = "title";
        public const string Description = "description";
        public const string Status = "status";
        public const string Priority = "priority";
        public const string Category = "category";
        public const string Project = "project";
        public const string Sprint = "sprint";
        public const string Assignee = "assignee";
        public const string Creator = "creator";
        public const string DueDate = "dueDate";
        public const string StartDate = "startDate";
        public const string CreatedAt = "createdAt";
        public const string EstimatedMinutes = "estimatedMinutes";
        public const string ActualMinutes = "actualMinutes";

        /// <summary>Derived: the status is terminal in this organization's registry.</summary>
        public const string IsCompleted = "isCompleted";

        /// <summary>Derived: past its due date and not finished.</summary>
        public const string IsOverdue = "isOverdue";

        public const string CustomPrefix = "custom:";

        public static readonly string[] BuiltIn =
        {
            Title, Description, Status, Priority, Category, Project, Sprint, Assignee, Creator,
            DueDate, StartDate, CreatedAt, EstimatedMinutes, ActualMinutes, IsCompleted, IsOverdue,
        };
    }

    public static class ReportOperators
    {
        public const string Is = "is";
        public const string IsNot = "isNot";
        public const string Contains = "contains";
        public const string NotContains = "notContains";
        public const string In = "in";
        public const string NotIn = "notIn";
        public const string GreaterThan = "gt";
        public const string LessThan = "lt";
        public const string OnOrAfter = "gte";
        public const string OnOrBefore = "lte";
        public const string Between = "between";
        public const string IsEmpty = "isEmpty";
        public const string IsNotEmpty = "isNotEmpty";

        /// <summary>Relative to now, in days. Negative looks backwards.</summary>
        public const string WithinDays = "withinDays";

        public static readonly string[] All =
        {
            Is, IsNot, Contains, NotContains, In, NotIn, GreaterThan, LessThan,
            OnOrAfter, OnOrBefore, Between, IsEmpty, IsNotEmpty, WithinDays,
        };
    }

    [BsonIgnoreExtraElements]
    public class ReportCondition
    {
        /// <summary>A value from <see cref="ReportFields"/>, or <c>custom:&lt;slug&gt;</c>.</summary>
        public string Field { get; set; }

        public string Operator { get; set; } = ReportOperators.Is;

        /// <summary>Single operand. For <c>between</c> this is the lower bound.</summary>
        public string Value { get; set; }

        /// <summary>Upper bound for <c>between</c>.</summary>
        public string Value2 { get; set; }

        /// <summary>Operands for <c>in</c> / <c>notIn</c>.</summary>
        public List<string> Values { get; set; } = new();
    }

    /// <summary>A set of conditions combined with a single operator.</summary>
    [BsonIgnoreExtraElements]
    public class ReportConditionGroup
    {
        /// <summary>"and" or "or", applied between the conditions in this group.</summary>
        public string Join { get; set; } = "and";

        public List<ReportCondition> Conditions { get; set; } = new();
    }

    /// <summary>
    /// The condition tree: groups combined by one operator, each group internally combined by its
    /// own.
    /// </summary>
    /// <remarks>
    /// Exactly ONE level of nesting, deliberately. That is enough for the questions people
    /// actually ask - "open work, where it is high priority OR overdue" - while staying something
    /// a UI can render as a readable list and a reader can check at a glance. An arbitrarily
    /// nested boolean tree is more expressive and, in a report builder, reliably produces filters
    /// nobody can reason about afterwards.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class ReportConditions
    {
        /// <summary>"and" or "or", applied between the groups.</summary>
        public string Join { get; set; } = "and";

        public List<ReportConditionGroup> Groups { get; set; } = new();

        public bool IsEmpty => Groups == null || Groups.Count == 0
                               || Groups.All(g => g.Conditions == null || g.Conditions.Count == 0);
    }

    /// <summary>Columns a detail-mode report can show.</summary>
    /// <remarks>
    /// Same closed-vocabulary rule as everywhere else here. Detail mode returns TASKS rather than
    /// aggregates, so an open-ended column list would be a direct read of whatever the caller
    /// named.
    /// </remarks>
    public static class ReportColumns
    {
        public static readonly string[] All =
        {
            ReportFields.Title, ReportFields.Status, ReportFields.Priority, ReportFields.Category,
            ReportFields.Project, ReportFields.Sprint, ReportFields.Assignee, ReportFields.Creator,
            ReportFields.DueDate, ReportFields.StartDate, ReportFields.CreatedAt,
            ReportFields.EstimatedMinutes, ReportFields.ActualMinutes,
        };

        public static readonly string[] Default =
        {
            ReportFields.Title, ReportFields.Status, ReportFields.Assignee,
            ReportFields.DueDate, ReportFields.EstimatedMinutes, ReportFields.ActualMinutes,
        };
    }

    public static class ReportModes
    {
        /// <summary>Grouped rows and a measure. The original behaviour.</summary>
        public const string Aggregate = "aggregate";

        /// <summary>The tasks themselves, with chosen columns.</summary>
        public const string Detail = "detail";
    }

    /// <summary>One block of a multi-part report.</summary>
    /// <remarks>
    /// A saved report with no sections behaves exactly as it always did - a single definition.
    /// Sections turn it into a short document: a chart, the table behind it, and the detail rows
    /// that make it actionable, in one saved thing rather than three.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class ReportSection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Title { get; set; }
        public string Description { get; set; }
        public ReportDefinition Definition { get; set; } = new();
        public int SortOrder { get; set; }
    }
}
