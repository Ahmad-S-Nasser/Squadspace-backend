using RafeeqyNotes.Api.Helpers;

namespace RafeeqyNotes.Api.Config
{
    /// <summary>One thing the product does.</summary>
    public sealed class FeatureDescriptor
    {
        public string Key { get; set; }
        public string Category { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }

        /// <summary>
        /// Entitlement that gates it, or null when every plan includes it.
        /// </summary>
        /// <remarks>
        /// This is what keeps the marketing site honest. The pricing page reads the gate from
        /// here and the plan's own entitlement bag, so a feature cannot be advertised as included
        /// on a tier that does not actually grant it.
        /// </remarks>
        public string EntitlementKey { get; set; }

        public int SortOrder { get; set; }
    }

    /// <summary>
    /// Everything the product does, in one place.
    /// </summary>
    /// <remarks>
    /// THE SINGLE SOURCE OF TRUTH for what SquadSpace offers. Both front ends and the marketing
    /// site read it from the API rather than keeping their own lists.
    ///
    /// This exists because the alternative already failed once. The pricing page used to carry a
    /// hand-written feature list that drifted from the product - it claimed "nothing is locked
    /// behind a higher plan" while the entitlement engine gated repositories, advanced analytics
    /// and export. A list maintained by hand in the place most likely to be read by a prospect is
    /// the one guaranteed to go stale.
    ///
    /// Each entry names the entitlement that gates it, or null for "every plan". A tier's feature
    /// list is then DERIVED by asking whether that plan grants the key, so the site cannot
    /// advertise something a plan does not include.
    ///
    /// When a feature ships, it is added here. That is the whole maintenance rule.
    /// </remarks>
    public static class FeatureCatalogue
    {
        public const string CategoryCore = "Core";
        public const string CategoryPlanning = "Planning";
        public const string CategoryTracking = "Time & tracking";
        public const string CategoryReporting = "Reporting";
        public const string CategoryCollaboration = "Collaboration";
        public const string CategoryCode = "Code";
        public const string CategoryAutomation = "Automation";
        public const string CategoryData = "Data & migration";
        public const string CategorySecurity = "Security & administration";

        public static List<FeatureDescriptor> All() => new()
        {
            // ---- core ----
            F(CategoryCore, "projects", "Projects and boards",
                "Organize work into projects, boards and notes, with members per project.", null, 0),
            F(CategoryCore, "tasks", "Tasks",
                "Assignees, reviewers, priorities, categories, due dates, estimates and dependencies.", null, 1),
            F(CategoryCore, "kanban", "Kanban board",
                "Drag work between columns, with a live view of what everyone is on.", null, 2),
            F(CategoryCore, "custom-statuses", "Configurable statuses",
                "Define your own workflow columns per organization, including which count as finished.", null, 3),
            F(CategoryCore, "custom-fields", "Custom fields",
                "Add your own fields to tasks and report on them.", null, 4),
            F(CategoryCore, "search", "Search",
                "Full-text search across notes and tasks in your organization.", null, 5),

            // ---- planning ----
            F(CategoryPlanning, "sprints", "Sprints",
                "Plan in sprints with velocity tracking and burndown.", Entitlement.Sprints, 10),
            F(CategoryPlanning, "gantt", "Gantt and timeline",
                "See schedules, overlaps and dependencies across a project.", null, 11),
            F(CategoryPlanning, "dependencies", "Task dependencies",
                "Link blocking work and see what is waiting on what.", null, 12),
            F(CategoryPlanning, "meetings", "Meetings",
                "Schedule meetings with attendees, agendas and attachments.", Entitlement.Meetings, 13),
            F(CategoryPlanning, "calendar", "Calendar invites (.ics)",
                "Meetings arrive as real calendar invitations in Outlook, Gmail and Apple Calendar.",
                Entitlement.CalendarIntegration, 14),

            // ---- time ----
            F(CategoryTracking, "timer", "Task timer",
                "Start and stop a timer on a task; time is measured on the server, not typed in from memory.", null, 20),
            F(CategoryTracking, "manual-time", "Manual time entry",
                "Log time you forgot to track, flagged as entered rather than measured.", null, 21),
            F(CategoryTracking, "estimates", "Estimates versus actuals",
                "Compare what work was expected to take against what it did.", null, 22),

            // ---- reporting ----
            F(CategoryReporting, "dashboard", "Dashboard",
                "Workload, completion trend, burndown and a kanban overview across projects.", null, 30),
            F(CategoryReporting, "report-builder", "Report builder",
                "Build your own reports: filter with conditions, group, measure, and chart or tabulate.",
                Entitlement.AnalyticsAdvanced, 31),
            F(CategoryReporting, "saved-reports", "Saved and shared reports",
                "Keep a report, share it with your organization, and pin it to your dashboard.",
                Entitlement.AnalyticsAdvanced, 32),
            F(CategoryReporting, "report-export", "Report export",
                "Download any report as CSV.", Entitlement.DataExport, 33),

            // ---- collaboration ----
            F(CategoryCollaboration, "notes", "Rich notes",
                "Formatted notes with tags, contributors and public sharing.", null, 40),
            F(CategoryCollaboration, "comments", "Comments and activity",
                "Discuss on the task, with a full audit trail of what changed and who changed it.", null, 41),
            F(CategoryCollaboration, "chat", "Team chat",
                "Direct and group messaging alongside the work.", null, 42),
            F(CategoryCollaboration, "whiteboard", "Whiteboard",
                "A shared canvas for sketching, with live collaboration.", Entitlement.Whiteboard, 43),
            F(CategoryCollaboration, "notifications", "Notifications",
                "In-app and email notifications for assignments, mentions and changes.", null, 44),

            // ---- code ----
            F(CategoryCode, "git", "Git repositories",
                "Self-hosted repositories with branches, commits, file browsing and pull requests.",
                Entitlement.GitRepositories, 50),
            F(CategoryCode, "github", "GitHub connection",
                "Connect a GitHub account to work with your existing repositories.", Entitlement.GitRepositories, 51),

            // ---- automation ----
            F(CategoryAutomation, "automation-rules", "Automation rules",
                "When something happens, do something: assign, move, comment or notify.",
                Entitlement.AutomationRuns, 60),
            F(CategoryAutomation, "scheduled-reports", "Scheduled reports",
                "Have a report emailed to you every morning, week or month without anyone running it.",
                Entitlement.AutomationRuns, 61),

            // ---- data ----
            F(CategoryData, "export", "Data export",
                "Export your projects, boards, notes and tasks as CSV, any time.", Entitlement.DataExport, 70),
            F(CategoryData, "import", "Import from Jira and CSV",
                "Bring a workspace across, with status mapping, a dry run and a resumable import.",
                Entitlement.DataExport, 71),
            F(CategoryData, "attachments", "Attachments",
                "Attach files to tasks, notes and meetings.", Entitlement.StorageGb, 72),

            // ---- security ----
            F(CategorySecurity, "organizations", "Organizations and roles",
                "Owner, admin, member and viewer roles, with every action scoped to the organization.", null, 80),
            F(CategorySecurity, "invitations", "Invitations",
                "Invite teammates by email with a role.", null, 81),
            F(CategorySecurity, "sso", "Single sign-on",
                "Sign in with your company's Google Workspace or Microsoft Entra directory, with the option to require it.",
                Entitlement.Sso, 82),
            F(CategorySecurity, "audit", "Activity history",
                "Who changed what, and when, on every task.", null, 83),
        };

        private static FeatureDescriptor F(
            string category, string key, string name, string description, string entitlementKey, int sortOrder) =>
            new()
            {
                Key = key,
                Category = category,
                Name = name,
                Description = description,
                EntitlementKey = entitlementKey,
                SortOrder = sortOrder,
            };
    }
}
