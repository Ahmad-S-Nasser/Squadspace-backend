using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>Outcome of a timer transition, for the caller to persist and report.</summary>
    public sealed class TimerResult
    {
        public bool Changed { get; init; }

        /// <summary>Human-readable summary, used as the activity message.</summary>
        public string Message { get; init; }

        /// <summary>The segment started or closed by this transition, if any.</summary>
        public TaskTimeEntry Entry { get; init; }
    }

    /// <summary>
    /// All task-timer arithmetic and state transitions, in one place.
    /// </summary>
    /// <remarks>
    /// Pure with respect to the database: it mutates the task object it is handed and reports what
    /// changed. Persistence, authorization and the activity trail belong to the controller. That
    /// split is what makes the rules here checkable in isolation - which matters in a repository
    /// with no test project, because these are the rules that decide whether a number a customer
    /// is billed against is right.
    ///
    /// Every timestamp comes from <see cref="DateTime.UtcNow"/> on the server. The client says
    /// "start" and "stop"; it never says "for how long".
    /// </remarks>
    public class TaskTimerService : ITaskTimerService
    {
        /// <summary>
        /// A single segment may not exceed this before the cap closes it.
        /// </summary>
        /// <remarks>
        /// Someone who starts a timer on Friday and remembers on Monday would otherwise book 72
        /// hours to a task. A forgotten timer poisons the dataset worse than the guess the timer
        /// replaced, so the segment is truncated to this and flagged AutoClosed rather than
        /// discarded - discarding it would lose real work, and keeping it whole would invent a
        /// number nobody worked.
        /// </remarks>
        public TimeSpan MaxSegment { get; }

        public TaskTimerService(IConfiguration configuration)
        {
            var hours = configuration?.GetValue<double?>("Timers:MaxSegmentHours") ?? 12d;
            MaxSegment = TimeSpan.FromHours(hours > 0 ? hours : 12d);
        }

        public TaskTimeEntry RunningEntry(NoteTask task, string userId) =>
            task?.TimeEntries?.FirstOrDefault(
                e => e.EndedAt == null && string.Equals(e.UserId, userId, StringComparison.Ordinal));

        public IReadOnlyList<TaskTimeEntry> RunningEntries(NoteTask task) =>
            task?.TimeEntries?.Where(e => e.EndedAt == null).ToList() ?? new List<TaskTimeEntry>();

        /// <summary>
        /// Closes any segment that has outrun the cap. Call before reading or transitioning.
        /// </summary>
        /// <remarks>
        /// Enforced lazily, on touch, rather than by a background sweep. A capped segment on a task
        /// nobody opens is harmless: the stored total counts closed segments only, so an
        /// unattended runaway contributes nothing until it is closed here.
        /// </remarks>
        public bool ApplyRunawayCap(NoteTask task, DateTime now)
        {
            var capped = false;

            foreach (var entry in RunningEntries(task))
            {
                if (now - entry.StartedAt <= MaxSegment) continue;

                entry.EndedAt = entry.StartedAt + MaxSegment;
                entry.AutoClosed = true;
                capped = true;
            }

            return capped;
        }

        public int ClosedMinutes(NoteTask task)
        {
            if (task?.TimeEntries == null) return 0;

            var total = task.TimeEntries
                .Where(e => e.EndedAt != null && e.EndedAt > e.StartedAt)
                .Sum(e => (e.EndedAt.Value - e.StartedAt).TotalMinutes);

            return (int)Math.Round(total, MidpointRounding.AwayFromZero);
        }

        /// <summary>Recomputes the denormalized total. The client's value is never used.</summary>
        public void RecomputeTotal(NoteTask task)
        {
            if (task == null) return;
            task.RealDuration = ClosedMinutes(task);
        }

        public TimerResult Start(NoteTask task, string userId, string userName, DateTime now)
        {
            if (task == null) return new TimerResult { Changed = false, Message = "No task." };

            task.TimeEntries ??= new List<TaskTimeEntry>();
            ApplyRunawayCap(task, now);

            var running = RunningEntry(task, userId);
            if (running != null)
            {
                // Idempotent: pressing start twice, or a second tab replaying it, must not open a
                // second segment for the same person - that would double-count every minute.
                return new TimerResult { Changed = false, Message = "Timer already running.", Entry = running };
            }

            var entry = new TaskTimeEntry
            {
                UserId = userId,
                UserName = userName,
                StartedAt = now,
                Source = TaskTimeEntrySources.Timer,
            };

            task.TimeEntries.Add(entry);
            RecomputeTotal(task);

            return new TimerResult { Changed = true, Message = "started the timer", Entry = entry };
        }

        public TimerResult Stop(NoteTask task, string userId, DateTime now)
        {
            if (task == null) return new TimerResult { Changed = false, Message = "No task." };

            ApplyRunawayCap(task, now);

            var running = RunningEntry(task, userId);
            if (running == null)
            {
                return new TimerResult { Changed = false, Message = "No running timer." };
            }

            // A segment shorter than a second is a mis-click, not work. Closing it at StartedAt
            // keeps the arithmetic honest instead of rounding a stray tap up to a minute.
            running.EndedAt = now > running.StartedAt ? now : running.StartedAt;
            RecomputeTotal(task);

            var minutes = (int)Math.Round((running.EndedAt.Value - running.StartedAt).TotalMinutes,
                MidpointRounding.AwayFromZero);

            return new TimerResult
            {
                Changed = true,
                Message = $"stopped the timer after {minutes} min",
                Entry = running,
            };
        }

        public TimerResult AddManualEntry(
            NoteTask task, string userId, string userName,
            DateTime startedAt, DateTime endedAt, string note)
        {
            if (task == null) return new TimerResult { Changed = false, Message = "No task." };
            if (endedAt <= startedAt) return new TimerResult { Changed = false, Message = "End must be after start." };

            task.TimeEntries ??= new List<TaskTimeEntry>();

            var entry = new TaskTimeEntry
            {
                UserId = userId,
                UserName = userName,
                StartedAt = DateTime.SpecifyKind(startedAt, DateTimeKind.Utc),
                EndedAt = DateTime.SpecifyKind(endedAt, DateTimeKind.Utc),
                // Flagged so reports can tell measured time from remembered time. That distinction
                // is exactly what the estimate-vs-actual calibration will need.
                Source = TaskTimeEntrySources.Manual,
                Note = note,
            };

            task.TimeEntries.Add(entry);
            RecomputeTotal(task);

            var minutes = (int)Math.Round((endedAt - startedAt).TotalMinutes, MidpointRounding.AwayFromZero);
            return new TimerResult { Changed = true, Message = $"logged {minutes} min manually", Entry = entry };
        }

        public TimerResult RemoveEntry(NoteTask task, string entryId)
        {
            var entry = task?.TimeEntries?.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return new TimerResult { Changed = false, Message = "No such entry." };

            task.TimeEntries.Remove(entry);
            RecomputeTotal(task);

            return new TimerResult { Changed = true, Message = "removed a time entry", Entry = entry };
        }
    }

    public interface ITaskTimerService
    {
        TimeSpan MaxSegment { get; }
        TaskTimeEntry RunningEntry(NoteTask task, string userId);
        IReadOnlyList<TaskTimeEntry> RunningEntries(NoteTask task);
        bool ApplyRunawayCap(NoteTask task, DateTime now);
        int ClosedMinutes(NoteTask task);
        void RecomputeTotal(NoteTask task);
        TimerResult Start(NoteTask task, string userId, string userName, DateTime now);
        TimerResult Stop(NoteTask task, string userId, DateTime now);
        TimerResult AddManualEntry(NoteTask task, string userId, string userName, DateTime startedAt, DateTime endedAt, string note);
        TimerResult RemoveEntry(NoteTask task, string entryId);
    }
}
