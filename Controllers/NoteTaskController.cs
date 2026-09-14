using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    // SECURITY: this controller had no [Authorize] and no ownership checks over the core
    // task data. Any caller could read, edit, comment on or delete any task in any
    // organization by id, and the per-contributor list endpoints returned that person's
    // tasks across every tenant. Every action now resolves the owning organization.
    [Authorize]
    public class NoteTaskController : ControllerBase
    {
        private readonly INoteTaskRepository _repo;
        private readonly INotificationRepository _notificationRepo;

        private readonly IOrganizationRepository _organizations;
        private readonly IProjectRepository _projects;
        private readonly INoteRepository _notes;
        private readonly ISprintRepository _sprints;
        private readonly ITaskStatusService _taskStatuses;
        private readonly ITaskTimerService _timers;
        private readonly IAutomationEngine _automation;

        public NoteTaskController(
            INoteTaskRepository repo,
            INotificationRepository notificationRepo,
            IOrganizationRepository organizations,
            IProjectRepository projects,
            INoteRepository notes,
            ISprintRepository sprints,
            ITaskStatusService taskStatuses,
            ITaskTimerService timers,
            IAutomationEngine automation)
        {
            _repo = repo;
            _notificationRepo = notificationRepo;
            _organizations = organizations;
            _projects = projects;
            _notes = notes;
            _sprints = sprints;
            _taskStatuses = taskStatuses;
            _timers = timers;
            _automation = automation;
        }

        /// <summary>
        /// Authorizes against the organization owning a stored task, using the embedded
        /// Note -> Board -> Project chain and falling back to a project read.
        /// </summary>
        private async Task<OrgAuth> AuthorizeTaskAsync(string taskId)
        {
            var task = await _repo.GetByIdAsync(taskId);
            var projectId = OrgScope.ProjectIdOfTask(task);
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
        }

        /// <summary>Authorizes against the organization owning a note, via its board's project.</summary>
        private async Task<OrgAuth> AuthorizeNoteAsync(string noteId)
        {
            var note = await _notes.GetByIdAsync(noteId);
            return await this.AuthorizeProjectAsync(
                _organizations, _projects, note?.Board?.Project?.Id, OrgAccess.AnyMember);
        }

        /// <summary>Organization ids the caller belongs to, for filtering cross-tenant lists.</summary>
        private HashSet<string> MyOrganizationIds()
        {
            var orgs = _organizations.FetchOrganizationsByUserId(OrgAccess.UserId(User))
                       ?? new List<RafeeqyNotes.Api.Models.Organization>();
            return new HashSet<string>(
                orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Keeps only tasks in the caller's organizations. Used by the per-contributor list
        /// endpoints, which are legitimate "my work" queries but must not span tenants.
        /// </summary>
        private List<NoteTask> VisibleToCaller(List<NoteTask> tasks)
        {
            var mine = MyOrganizationIds();
            return (tasks ?? new List<NoteTask>())
                .Where(t => OrgScope.OrgIdOfTask(t) is string o && mine.Contains(o))
                .ToList();
        }

        // GET /api/notetask/byNote/{noteId}
        [HttpGet("byNote/{noteId}")]
        public async Task<ActionResult<List<NoteTask>>> GetByNoteId(string noteId)
        {
            var auth = await AuthorizeNoteAsync(noteId);
            if (!auth.Allowed) return auth.Error;

            var tasks = await _repo.GetByNoteIdAsync(noteId);
            return Ok(tasks);
        }
        // GET /api/notetask/byNote/{noteId}
        [HttpGet("byCreator/{contributorId}")]
        public async Task<ActionResult<List<NoteTask>>> GetByCreatorId(string contributorId)
        {
            // Filtered to the caller's organizations: these are "my work" queries,
            // but the raw repository call spans every tenant.
            var tasks = await _repo.GetByCreatorId(contributorId);
            return Ok(VisibleToCaller(tasks));
        }
        // GET /api/notetask/byNote/{noteId}
        [HttpGet("byReviewer/{contributorId}")]
        public async Task<ActionResult<List<NoteTask>>> GetByReviewerId(string contributorId)
        {
            // Filtered to the caller's organizations: these are "my work" queries,
            // but the raw repository call spans every tenant.
            var tasks = await _repo.GetByReviewerId(contributorId);
            return Ok(VisibleToCaller(tasks));
        }
        // GET /api/notetask/byNote/{noteId}
        [HttpGet("byAssignedTo/{contributorId}")]
        public async Task<ActionResult<List<NoteTask>>> GetByAssignedToID(string contributorId)
        {
            // Filtered to the caller's organizations: these are "my work" queries,
            // but the raw repository call spans every tenant.
            var tasks = await _repo.GetByAssignedToID(contributorId);
            return Ok(VisibleToCaller(tasks));
        }
       
        [HttpGet("byProject/{projectId}")]
        public async Task<ActionResult<List<NoteTask>>> GetByProjectID(string projectId)
        {
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, projectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var tasks = await _repo.GetByProjectIdAsync(projectId);
            return Ok(tasks);
        }
        // GET /api/notetask/bySprint/{sprintId}
        [HttpGet("bySprint/{sprintId}")]
        public async Task<ActionResult<List<NoteTask>>> GetBySprintID(string sprintId)
        {
            var sprint = _sprints.FetchSprintsById(sprintId);
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, sprint?.ProjectId, OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var tasks = await _repo.GetBySprintID(sprintId);
            return Ok(tasks);
        }
        // POST /api/notetask
        [HttpPost]
        public async Task<ActionResult<NoteTask>> Create([FromBody] NoteTask task)
        {
            if (task == null) return BadRequest("Request body is required.");

            // Parent note comes from the body, so it is attacker-controlled.
            var auth = await this.AuthorizeProjectAsync(
                _organizations, _projects, OrgScope.ProjectIdOfTask(task), OrgAccess.AnyMember);
            if (!auth.Allowed) return auth.Error;

            var badKeys = CustomFieldKeys.Rejected(
                task.CustomFields, await _taskStatuses.CustomFieldSlugsAsync(auth.Org?.Id, "task"));
            if (badKeys.Count > 0)
            {
                return BadRequest(new
                {
                    message = "Unknown or malformed custom field keys.",
                    keys = badKeys,
                    expected = CustomFieldKeys.Pattern,
                });
            }

            // CreatedAt was never set server-side, so a client that omitted it stored
            // DateTime.MinValue - which made every time-based report bucket that task under
            // year 1. Trusting the caller's clock for it is also how you get tasks created
            // "tomorrow" skewing a burndown.
            if (task.CreatedAt == default) task.CreatedAt = DateTime.UtcNow;

            // Same rule on create: time is measured here, never asserted by the caller.
            task.TimeEntries = new List<TaskTimeEntry>();
            task.RealDuration = 0;

            // A status the organization does not recognise is stored as its canonical slug
            // rather than as a fourth spelling, and a missing one takes the default.
            var statuses = await _taskStatuses.ForOrganizationAsync(auth.Org?.Id);
            task.Status = string.IsNullOrWhiteSpace(task.Status)
                ? statuses.DefaultSlug
                : statuses.Resolve(task.Status);

            if (task.Activities == null) task.Activities = new List<TaskActivity>();
            
            task.Activities.Add(new TaskActivity
            {
                Id = Guid.NewGuid().ToString(),
                TaskId = task.Id,
                Type = "task_created",
                Actor = new TaskActivityActor { Id = task.Creator?.Id ?? "system", Name = task.Creator?.Name ?? "System", Email = task.Creator?.Email ?? "" },
                Message = $"{task.Creator?.Name ?? "System"} created this task",
                CreatedAt = DateTime.UtcNow
            });

            try
            {
                await _repo.CreateAsync(task);

            await _automation.DispatchAsync(
                auth.Org?.Id, AutomationTriggers.TaskCreated, task, null);
            }
            catch (DuplicateEntityException ex)
            {
                // 409, not 500: an importer re-running a batch needs to tell "already there"
                // apart from "the server broke".
                return Conflict(new { message = "Already exists.", id = ex.EntityId });
            }


            // Notify assigned contributors
            if (task.AssignedTo != null)
            {
                foreach (var contributor in task.AssignedTo)
                {
                        await _notificationRepo.CreateUserNotificationAsync(new Notification
                        {
                            UserId = contributor.Id,
                            Type = "task_assigned",
                            Title = "Task Assigned",
                            Message = $"You have been assigned a new task: {task.Title}",
                            Link = $"/tasks?taskId={task.Id}",
                            CreatedAt = DateTime.UtcNow,
                            Metadata = new Dictionary<string, string> { { "taskId", task.Id }, { "noteId", task.Note?.Id ?? "" } }
                        });
                }
            }

            if (string.IsNullOrEmpty(task.Note?.Id))
            {
                return Ok(task);
            }

            return CreatedAtAction(nameof(GetByNoteId), new { noteId = task.Note.Id }, task);
        }

        // PUT /api/notetask/{id}
        [HttpPut("{id}")]
        public async Task<ActionResult<NoteTask>> Update(string id, [FromBody] NoteTask task)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            task.Id = id;
            var oldTask = await _repo.GetByIdAsync(id);
            string statusChangedFrom = null;

            // Preserve Comments and Activities
            task.Comments = oldTask?.Comments ?? new List<TaskComment>();
            task.Activities = oldTask?.Activities ?? new List<TaskActivity>();

            // Dependencies follow the same rule as everything else on this endpoint: a PUT
            // replaces the whole document, so anything the body does not carry would be erased.
            //
            // The distinction that matters is null versus empty. `dependencies: []` is a real
            // edit - someone removed the last link - while omitting the field entirely means the
            // caller is changing something else, and several call sites do exactly that when
            // they drag a card between columns. Treating those the same would silently drop a
            // task's dependencies every time its status changed.
            if (task.Dependencies == null)
            {
                task.DependencyIds = oldTask?.DependencyIds ?? new List<string>();
            }

            // Measured time is server-owned and is NOT accepted from the request body.
            //
            // This endpoint replaces the whole document, so without this a client could PUT
            // realDuration: 9999 with timeEntries: [] and the "measured" total would be whatever
            // it felt like - which is strictly worse than the self-reported number the timer
            // replaced, because this one looks measured. The stored segments win, and the total
            // is recomputed from them.
            task.TimeEntries = oldTask?.TimeEntries ?? new List<TaskTimeEntry>();
            _timers.RecomputeTotal(task);

            if (oldTask != null)
            {
                var actor = new TaskActivityActor { Id = "system", Name = "System", Email = "" };
                var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!string.IsNullOrEmpty(userId)) {
                    actor.Id = userId;
                    actor.Name = "A user";
                }

                statusChangedFrom = oldTask.Status != task.Status ? oldTask.Status : null;

                if (oldTask.Status != task.Status)
                {
                    task.Activities.Add(new TaskActivity
                    {
                        Id = Guid.NewGuid().ToString(),
                        TaskId = task.Id,
                        Type = "status_changed",
                        Actor = actor,
                        Message = $"Status changed from {oldTask.Status ?? "None"} to {task.Status}",
                        Metadata = new TaskActivityMetadata { OldValue = oldTask.Status, NewValue = task.Status },
                        CreatedAt = DateTime.UtcNow
                    });
                }
                
                if (oldTask.Priority != task.Priority)
                {
                    task.Activities.Add(new TaskActivity
                    {
                        Id = Guid.NewGuid().ToString(),
                        TaskId = task.Id,
                        Type = "priority_changed",
                        Actor = actor,
                        Message = $"Priority changed from {oldTask.Priority ?? "None"} to {task.Priority}",
                        Metadata = new TaskActivityMetadata { OldValue = oldTask.Priority, NewValue = task.Priority },
                        CreatedAt = DateTime.UtcNow
                    });
                }

                var oldAssigneeIds = oldTask.AssignedTo?.Select(a => a.Id).ToList() ?? new List<string>();
                var newAssigneeIds = task.AssignedTo?.Select(a => a.Id).ToList() ?? new List<string>();
                
                var addedAssignees = task.AssignedTo?.Where(a => !oldAssigneeIds.Contains(a.Id)).ToList() ?? new List<Contributor>();
                var removedAssignees = oldTask.AssignedTo?.Where(a => !newAssigneeIds.Contains(a.Id)).ToList() ?? new List<Contributor>();

                foreach(var added in addedAssignees)
                {
                    task.Activities.Add(new TaskActivity
                    {
                        Id = Guid.NewGuid().ToString(),
                        TaskId = task.Id,
                        Type = "assignee_added",
                        Actor = actor,
                        Message = $"{added.Name} was assigned to the task",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                foreach(var removed in removedAssignees)
                {
                    task.Activities.Add(new TaskActivity
                    {
                        Id = Guid.NewGuid().ToString(),
                        TaskId = task.Id,
                        Type = "assignee_removed",
                        Actor = actor,
                        Message = $"{removed.Name} was removed from the task",
                        CreatedAt = DateTime.UtcNow
                    });
                }
            }

            await _repo.UpdateAsync(task);

            // Track B: event-driven rules, fired AFTER the write succeeds so an automation can
            // never roll back or block the user's own edit. Dispatch swallows its own failures.
            if (statusChangedFrom != null)
            {
                await _automation.DispatchAsync(
                    auth.Org?.Id, AutomationTriggers.TaskStatusChanged, task, statusChangedFrom);
            }

            // Notify new assignees
            if (task.AssignedTo != null)
            {
                var oldAssigneeIds = oldTask?.AssignedTo?.Select(a => a.Id).ToList() ?? new List<string>();
                foreach (var contributor in task.AssignedTo)
                {
                    if (!oldAssigneeIds.Contains(contributor.Id))
                    {
                        await _notificationRepo.CreateUserNotificationAsync(new Notification
                        {
                            UserId = contributor.Id,
                            Type = "task_assigned",
                            Title = "Task Assigned",
                            Message = $"You have been assigned a task: {task.Title}",
                            Link = $"/tasks?taskId={task.Id}",
                            CreatedAt = DateTime.UtcNow,
                            Metadata = new Dictionary<string, string> { { "taskId", task.Id }, { "noteId", task.Note?.Id ?? "" } }
                        });
                    }
                }
            }

            return Ok(task);
        }

        // DELETE /api/notetask/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(string id)
        {
            var auth = await AuthorizeTaskAsync(id);
            if (!auth.Allowed) return auth.Error;

            await _repo.DeleteAsync(id);
            return NoContent();
        }

        [HttpGet("{taskId}/activities")]
        public async Task<ActionResult<List<TaskActivity>>> GetActivities(string taskId)
        {
            var auth = await AuthorizeTaskAsync(taskId);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(taskId);
            if (task == null) return NotFound();
            return Ok(task.Activities ?? new List<TaskActivity>());
        }

        [HttpGet("{taskId}/comments")]
        public async Task<ActionResult<List<TaskComment>>> GetComments(string taskId)
        {
            var auth = await AuthorizeTaskAsync(taskId);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(taskId);
            if (task == null) return NotFound();
            return Ok(task.Comments ?? new List<TaskComment>());
        }

        [HttpPost("{taskId}/comments")]
        public async Task<ActionResult<TaskComment>> AddComment(string taskId, [FromBody] TaskComment comment)
        {
            var auth = await AuthorizeTaskAsync(taskId);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(taskId);
            if (task == null) return NotFound();

            comment.CreatedAt = DateTime.UtcNow;
            comment.UpdatedAt = DateTime.UtcNow;
            if (string.IsNullOrEmpty(comment.Id)) comment.Id = Guid.NewGuid().ToString();
            comment.TaskId = taskId;

            if (task.Comments == null) task.Comments = new List<TaskComment>();
            task.Comments.Add(comment);

            await _repo.UpdateAsync(task);
            return Ok(comment);
        }

        [HttpDelete("{taskId}/comments/{commentId}")]
        public async Task<IActionResult> DeleteComment(string taskId, string commentId)
        {
            var auth = await AuthorizeTaskAsync(taskId);
            if (!auth.Allowed) return auth.Error;

            var task = await _repo.GetByIdAsync(taskId);
            if (task == null) return NotFound();

            if (task.Comments != null)
            {
                task.Comments.RemoveAll(c => c.Id == commentId);
                await _repo.UpdateAsync(task);
            }
            return NoContent();
        }
    }
}