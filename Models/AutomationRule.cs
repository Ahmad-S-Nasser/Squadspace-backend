using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>What starts a rule.</summary>
    public static class AutomationTriggers
    {
        /// <summary>Fires on a recurring schedule. Track A — manager workflows.</summary>
        public const string Schedule = "schedule";

        /// <summary>Fires when a task's status changes. Track B — task automation.</summary>
        public const string TaskStatusChanged = "task.status_changed";

        /// <summary>Fires when a task is created.</summary>
        public const string TaskCreated = "task.created";

        public static readonly string[] All = { Schedule, TaskStatusChanged, TaskCreated };

        /// <summary>Triggers the scheduler polls for, rather than ones raised by an event.</summary>
        public static bool IsScheduled(string trigger) =>
            string.Equals(trigger, Schedule, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a rule does.</summary>
    public static class AutomationActions
    {
        /// <summary>Runs a SavedReport and emails it. The first slice.</summary>
        public const string DeliverReport = "report.deliver";

        /// <summary>Sends an email to named recipients.</summary>
        public const string SendEmail = "notify.email";

        /// <summary>Raises an in-app notification.</summary>
        public const string Notify = "notify.inapp";

        /// <summary>Assigns the triggering task to someone.</summary>
        public const string AssignTask = "task.assign";

        /// <summary>Moves the triggering task to a status.</summary>
        public const string SetTaskStatus = "task.set_status";

        /// <summary>Comments on the triggering task.</summary>
        public const string CommentOnTask = "task.comment";

        public static readonly string[] All =
        {
            DeliverReport, SendEmail, Notify, AssignTask, SetTaskStatus, CommentOnTask,
        };
    }

    /// <summary>How often a scheduled rule runs.</summary>
    public static class AutomationCadence
    {
        public const string Hourly = "hourly";
        public const string Daily = "daily";
        public const string Weekly = "weekly";
        public const string Monthly = "monthly";

        public static readonly string[] All = { Hourly, Daily, Weekly, Monthly };
    }

    /// <summary>
    /// One automation rule: a trigger, a condition, and an action.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT a graph. The surveyed prototype that specified this feature was a
    /// high-fidelity builder over a diagram model with no runtime behind it - its schema had
    /// nodes and edges but no Workflow, Trigger, Action or Run type, and node behaviour was
    /// dispatched on the display label, so renaming a box changed what it did.
    ///
    /// The lesson that prototype already paid for is that a working runtime beats a beautiful
    /// builder. So this is a trigger, an optional filter and one action - the smallest shape that
    /// runs, meters and audits correctly. A graph is a later presentation over the same registry,
    /// which is why triggers and actions are named registry keys with a config bag rather than
    /// anything structural.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class AutomationRule
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string OrganizationId { get; set; }

        /// <summary>Optional narrowing for event triggers. Null means the whole organization.</summary>
        public string ProjectId { get; set; }

        public string Name { get; set; }
        public string Description { get; set; }

        public bool Enabled { get; set; } = true;

        /// <summary>A key from <see cref="AutomationTriggers"/>.</summary>
        public string Trigger { get; set; }

        /// <summary>A key from <see cref="AutomationActions"/>.</summary>
        public string Action { get; set; }

        /// <summary>
        /// Trigger and action parameters, as strings.
        /// </summary>
        /// <remarks>
        /// The same string-bag idiom as SubscriptionPlan.Entitlements and the custom-field bag:
        /// the registry declares what a key means and each edge parses it. One concept the
        /// codebase already has, rather than a third way of describing configuration.
        /// </remarks>
        public Dictionary<string, string> Config { get; set; } = new();

        // ---- scheduling ----

        /// <summary>A value from <see cref="AutomationCadence"/>, for scheduled triggers.</summary>
        public string Cadence { get; set; }

        /// <summary>Hour of day, 0-23, in <see cref="TimeZone"/>.</summary>
        public int HourOfDay { get; set; } = 9;

        /// <summary>Day of week for weekly cadence. 1 = Monday.</summary>
        public int DayOfWeek { get; set; } = 1;

        /// <summary>Day of month for monthly cadence.</summary>
        public int DayOfMonth { get; set; } = 1;

        /// <summary>IANA zone the schedule is expressed in. "9am Monday" is a local statement.</summary>
        public string TimeZone { get; set; }

        /// <summary>When this rule is next due. The scheduler's only query.</summary>
        public DateTime? NextRunAt { get; set; }

        /// <summary>
        /// Claim held by whichever instance is running it.
        /// </summary>
        /// <remarks>
        /// There is no distributed lock available here, so a rule is claimed by an atomic
        /// find-and-update that sets this forward. Without it every app instance would fire the
        /// same rule at the same minute and a customer would get four copies of one report.
        /// </remarks>
        public DateTime? LockedUntil { get; set; }

        public DateTime? LastRunAt { get; set; }
        public string LastRunState { get; set; }
        public int ConsecutiveFailures { get; set; }

        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public static class AutomationRunStates
    {
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";

        /// <summary>The rule fired but its condition did not match, so nothing was done.</summary>
        public const string Skipped = "skipped";

        /// <summary>Blocked by the organization's automation-run quota.</summary>
        public const string QuotaExceeded = "quota_exceeded";
    }

    /// <summary>
    /// One execution of a rule.
    /// </summary>
    /// <remarks>
    /// Written for every attempt, including skips and quota refusals. An automation nobody can
    /// audit is one nobody trusts - "did it run?" has to be answerable without reading logs, and
    /// this is also the meter the plan's automation.runs quota counts.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class AutomationRun
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        public string RuleId { get; set; }
        public string OrganizationId { get; set; }
        public string RuleName { get; set; }
        public string Trigger { get; set; }
        public string Action { get; set; }

        public string State { get; set; }
        public string Message { get; set; }

        /// <summary>1 for the first attempt. Retries reuse the rule, not this record.</summary>
        public int Attempt { get; set; } = 1;

        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public long DurationMs { get; set; }
    }
}
