using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Hubs;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>
    /// Start / stop / correct the measured time on a task.
    /// </summary>
    /// <remarks>
    /// Split out of NoteTaskController, which is already ~420 lines, rather than added to it.
    /// Routed under the same <c>api/NoteTask</c> prefix, so this is an internal organization only -
    /// no client sees a difference.
    ///
    /// SECURITY: every action resolves the owning organization and checks membership, exactly like
    /// the rest of the task surface. A timer endpoint reachable without that would be worse than
    /// having no timer at all, because it silently corrupts the dataset that velocity, workload and
    /// the eventual calibration engine are all computed from - and a wrong number that looks
    /// measured is more dangerous than an obviously missing one.
    ///
    /// Identity always comes from the JWT. The caller never names whose time this is.
    /// </remarks>
    [Route("api/NoteTask")]
    [ApiController]
    [Authorize]
    public class NoteTaskTimerController : ControllerBase
    {
        private readonly INoteTaskRepository _repo;
        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly ITaskTimerService _timers;
        private readonly IHubContext<TasksHub> _hub;

        public NoteTaskTimerController(
            INoteTaskRepository repo,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            ITaskTimerService timers,
            IHubContext<TasksHub> hub)
        {
            _repo = repo;
            _organizations = organizations;
            _projects = projects;
            _timers = timers;
            _hub = hub;
        }

        private async Task<OrgAuth> AuthorizeTaskAsync(string taskId)
        {
            var task = await _repo.GetByIdAsync(taskId);
            var projectId = OrgScope.ProjectIdOfTask(task);
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
        }

        /// <summary>Caller's display name, from the organization the guard already fetched.</summary>
        private static string NameOf(OrgAuth auth) =>
            auth?.Org?.Members?.FirstOrDefault(m => m.UserId == auth.UserId)?.User?.Name ?? "A user";

        /// <summary>Current timer state for a task, including the live running total.</summary>
        /// <remarks>
        /// <c>totalMinutes</c> is the stored, closed-segment total. <c>runningSince</c> lets the
        /// client tick the extra seconds itself, computed from the timestamp on every render rather
        /// than by incrementing a counter - a counter drifts whenever the tab sleeps, and a sleeping
        /// tab is the normal case for a timer left running.
        /// </remarks>
        [HttpGet("{id}/timer")]
        public async Task<IActionResult> GetTimer(string id)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(id);
            if (task == null) return NotFound();

            // Reading is enough to retire a runaway; persist if it changed anything.
            if (_timers.ApplyRunawayCap(task, DateTime.UtcNow))
            {
                _timers.RecomputeTotal(task);
                await _repo.UpdateAsync(task);
            }

            return Ok(TimerState(task, auth.UserId));
        }

        [HttpPost("{id}/timer/start")]
        public async Task<IActionResult> Start(string id)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(id);
            if (task == null) return NotFound();

            var now = DateTime.UtcNow;

            // One running timer per person, across every task in every project. Starting here
            // closes whatever else they had ticking and says so, rather than silently accumulating
            // three parallel timers whose totals are all wrong by morning.
            var stoppedElsewhere = new List<object>();
            foreach (var other in await _repo.GetWithRunningTimerAsync(auth.UserId))
            {
                if (other.Id == id) continue;

                var closed = _timers.Stop(other, auth.UserId, now);
                if (!closed.Changed) continue;

                _timers.RecomputeTotal(other);
                AddActivity(other, auth, "timer_stopped",
                    $"{NameOf(auth)} {closed.Message} (auto-stopped by starting another task)");

                await _repo.UpdateAsync(other);
                await BroadcastAsync(other, auth.UserId);

                stoppedElsewhere.Add(new { taskId = other.Id, title = other.Title });
            }

            var result = _timers.Start(task, auth.UserId, NameOf(auth), now);
            if (result.Changed)
            {
                AddActivity(task, auth, "timer_started", $"{NameOf(auth)} {result.Message}");
                await _repo.UpdateAsync(task);
                await BroadcastAsync(task, auth.UserId);
            }

            var state = TimerState(task, auth.UserId);
            return Ok(new { state.running, state.runningSince, state.totalMinutes, state.entries, stoppedElsewhere });
        }

        /// <summary>
        /// Stops the caller's timer on this task.
        /// </summary>
        /// <remarks>
        /// Pause and stop are the same operation on the data: both close the open segment. The
        /// difference is only what the UI offers next, and modelling them separately would mean a
        /// "paused" state to keep consistent across tabs and devices for no gain.
        /// </remarks>
        [HttpPost("{id}/timer/stop")]
        public async Task<IActionResult> Stop(string id)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            return await CloseRunningAsync(id, auth);
        }

        /// <summary>Alias for stop; see the remarks there for why they are one operation.</summary>
        /// <remarks>
        /// The guard is repeated in both actions rather than hidden behind the shared helper.
        /// tools/audit_guards.py checks each action body for it and cannot follow a delegation, and
        /// a guard the audit cannot see is one nobody will notice going missing.
        /// </remarks>
        [HttpPost("{id}/timer/pause")]
        public async Task<IActionResult> Pause(string id)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            return await CloseRunningAsync(id, auth);
        }

        private async Task<IActionResult> CloseRunningAsync(string id, OrgAuth auth)
        {
            var task = await _repo.GetByIdAsync(id);
            if (task == null) return NotFound();

            var result = _timers.Stop(task, auth.UserId, DateTime.UtcNow);
            if (result.Changed)
            {
                AddActivity(task, auth, "timer_stopped", $"{NameOf(auth)} {result.Message}");
                await _repo.UpdateAsync(task);
                await BroadcastAsync(task, auth.UserId);
            }

            return Ok(TimerState(task, auth.UserId));
        }

        /// <summary>Logs a stretch of work by hand, for when someone forgot to press start.</summary>
        /// <remarks>
        /// Always attributed to the caller. Logging time on someone else's behalf is a different
        /// feature with different trust implications, and this is not it.
        /// </remarks>
        [HttpPost("{id}/timer/entries")]
        public async Task<IActionResult> AddEntry(string id, [FromBody] ManualTimeEntryRequest request)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            if (request == null) return BadRequest(new { message = "Request body is required." });

            var task = await _repo.GetByIdAsync(id);
            if (task == null) return NotFound();

            var startedAt = request.StartedAt.ToUniversalTime();
            var endedAt = request.EndedAt.ToUniversalTime();

            if (endedAt <= startedAt)
                return BadRequest(new { message = "End must be after start." });

            if (endedAt > DateTime.UtcNow.AddMinutes(1))
                return BadRequest(new { message = "Cannot log time in the future." });

            if (endedAt - startedAt > _timers.MaxSegment)
                return BadRequest(new
                {
                    message = $"A single entry cannot exceed {_timers.MaxSegment.TotalHours} hours.",
                });

            var result = _timers.AddManualEntry(
                task, auth.UserId, NameOf(auth), startedAt, endedAt, request.Note);

            if (!result.Changed) return BadRequest(new { message = result.Message });

            AddActivity(task, auth, "time_logged", $"{NameOf(auth)} {result.Message}");
            await _repo.UpdateAsync(task);
            await BroadcastAsync(task, auth.UserId);

            return Ok(TimerState(task, auth.UserId));
        }

        /// <summary>Removes a time entry. Own entries only, unless the caller manages the org.</summary>
        [HttpDelete("{id}/timer/entries/{entryId}")]
        public async Task<IActionResult> DeleteEntry(string id, string entryId)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(id);
            if (task == null) return NotFound();

            var entry = task.TimeEntries?.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return NotFound();

            var isManager = OrgAccess.Managers.Contains(auth.Role ?? string.Empty);
            if (!string.Equals(entry.UserId, auth.UserId, StringComparison.Ordinal) && !isManager)
            {
                return Forbid();
            }

            var result = _timers.RemoveEntry(task, entryId);
            if (result.Changed)
            {
                AddActivity(task, auth, "time_entry_removed", $"{NameOf(auth)} {result.Message}");
                await _repo.UpdateAsync(task);
                await BroadcastAsync(task, auth.UserId);
            }

            return Ok(TimerState(task, auth.UserId));
        }

        private static void AddActivity(NoteTask task, OrgAuth auth, string type, string message)
        {
            task.Activities ??= new List<TaskActivity>();
            task.Activities.Add(new TaskActivity
            {
                Id = Guid.NewGuid().ToString(),
                TaskId = task.Id,
                Type = type,
                Actor = new TaskActivityActor { Id = auth.UserId, Name = NameOf(auth), Email = string.Empty },
                Message = message,
                CreatedAt = DateTime.UtcNow,
            });
        }

        /// <summary>
        /// Tells other viewers the task is being worked on, and the same user's other tabs that
        /// their timer state moved.
        /// </summary>
        private async Task BroadcastAsync(NoteTask task, string actorUserId)
        {
            try
            {
                await _hub.Clients.Group(task.Id).SendAsync("ReceiveTaskUpdate", "timer", new
                {
                    taskId = task.Id,
                    actorUserId,
                    totalMinutes = task.RealDuration ?? 0,
                    running = _timers.RunningEntries(task)
                        .Select(e => new { e.UserId, e.UserName, startedAt = e.StartedAt }),
                });
            }
            catch
            {
                // A broadcast failure must never fail the write that already succeeded. The client
                // re-reads timer state on focus anyway.
            }
        }

        private TimerStateDto TimerState(NoteTask task, string userId)
        {
            var mine = _timers.RunningEntry(task, userId);

            return new TimerStateDto
            {
                taskId = task.Id,
                running = mine != null,
                runningSince = mine?.StartedAt,
                totalMinutes = task.RealDuration ?? 0,
                runningOthers = _timers.RunningEntries(task)
                    .Where(e => e.UserId != userId)
                    .Select(e => new RunningUserDto { userId = e.UserId, userName = e.UserName, startedAt = e.StartedAt })
                    .ToList(),
                entries = (task.TimeEntries ?? new List<TaskTimeEntry>())
                    .OrderByDescending(e => e.StartedAt)
                    .Select(e => new TimeEntryDto
                    {
                        id = e.Id,
                        userId = e.UserId,
                        userName = e.UserName,
                        startedAt = e.StartedAt,
                        endedAt = e.EndedAt,
                        source = e.Source,
                        note = e.Note,
                        autoClosed = e.AutoClosed,
                        minutes = e.EndedAt == null
                            ? 0
                            : (int)Math.Round((e.EndedAt.Value - e.StartedAt).TotalMinutes, MidpointRounding.AwayFromZero),
                    })
                    .ToList(),
            };
        }
    }

    public class ManualTimeEntryRequest
    {
        public DateTime StartedAt { get; set; }
        public DateTime EndedAt { get; set; }
        public string Note { get; set; }
    }

    public class TimerStateDto
    {
        public string taskId { get; set; }
        public bool running { get; set; }
        public DateTime? runningSince { get; set; }
        public int totalMinutes { get; set; }
        public List<RunningUserDto> runningOthers { get; set; } = new();
        public List<TimeEntryDto> entries { get; set; } = new();
    }

    public class RunningUserDto
    {
        public string userId { get; set; }
        public string userName { get; set; }
        public DateTime startedAt { get; set; }
    }

    public class TimeEntryDto
    {
        public string id { get; set; }
        public string userId { get; set; }
        public string userName { get; set; }
        public DateTime startedAt { get; set; }
        public DateTime? endedAt { get; set; }
        public string source { get; set; }
        public string note { get; set; }
        public bool autoClosed { get; set; }
        public int minutes { get; set; }
    }
}
