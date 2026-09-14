using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Config
{
    /// <summary>
    /// The default task-status workflow seeded for every organization.
    /// </summary>
    /// <remarks>
    /// Mirrors PlanCatalogue: code is the source of truth, the database holds a per-organization
    /// copy that may later diverge.
    ///
    /// THE SLUGS MUST NOT CHANGE. They are byte-identical to the frontend's canonical union in
    /// src/types/tasks.ts, and that union's values are what live task documents already contain.
    /// A different slug here does not "rename" anything - it strands every existing task on a
    /// status the registry no longer knows.
    ///
    /// The alias lists come from a census of the live data plus every spelling found hardcoded in
    /// the codebase: the NoteTask model defaulted to "To-do", AnalyticsRepository grouped nulls
    /// into "To Do" and compared completion against both "done" and "Done", and the frontend
    /// writes snake_case. All of it resolves to one slug now.
    /// </remarks>
    public static class StatusCatalogue
    {
        /// <summary>Slug applied to a task created without one. Matches CreateTaskDialog.</summary>
        public const string DefaultSlug = "todo";

        public static List<TaskStatusDefinition> Defaults() => new()
        {
            new TaskStatusDefinition
            {
                Slug = "backlog",
                Label = "Backlog",
                Color = "bg-slate-500/20 text-slate-600",
                Order = 0,
                IsStarted = false,
                IsTerminal = false,
                Aliases = new List<string> { "Backlog", "back-log", "back_log" },
            },
            new TaskStatusDefinition
            {
                Slug = "todo",
                Label = "To Do",
                Color = "bg-muted text-muted-foreground",
                Order = 1,
                IsStarted = false,
                IsTerminal = false,
                IsDefault = true,
                // "To-do" was the NoteTask model default; "To Do" is what AnalyticsRepository
                // substituted for a null status when grouping.
                Aliases = new List<string> { "To-do", "To Do", "to-do", "to do", "TODO", "open", "new" },
            },
            new TaskStatusDefinition
            {
                Slug = "in_progress",
                Label = "In Progress",
                Color = "bg-blue-500/20 text-blue-600",
                Order = 2,
                IsStarted = true,
                IsTerminal = false,
                Aliases = new List<string> { "In-progress", "In Progress", "in-progress", "in progress", "doing", "active" },
            },
            new TaskStatusDefinition
            {
                Slug = "in_review",
                Label = "In Review",
                Color = "bg-yellow-500/20 text-yellow-600",
                Order = 3,
                IsStarted = true,
                IsTerminal = false,
                Aliases = new List<string> { "In-review", "In Review", "in-review", "in review", "review" },
            },
            new TaskStatusDefinition
            {
                Slug = "done",
                Label = "Done",
                Color = "bg-green-500/20 text-green-600",
                Order = 4,
                IsStarted = true,
                IsTerminal = true,
                Aliases = new List<string> { "Done", "DONE", "complete", "completed", "Completed", "closed" },
            },
        };
    }
}
