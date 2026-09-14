using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Hubs;
using System.Security.Claims;

namespace RafeeqyNotes.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Microsoft.AspNetCore.Authorization.Authorize]
    public class NotificationsController : ControllerBase
    {
        private readonly INotificationRepository _repo;
        private readonly IHubContext<NotificationHub> _notificationHubContext;
        private readonly INoteTaskRepository _tasks;

        public NotificationsController(
            INotificationRepository repo,
            IHubContext<NotificationHub> notificationHubContext,
            INoteTaskRepository tasks)
        {
            _repo = repo;
            _notificationHubContext = notificationHubContext;
            _tasks = tasks;
        }

        /// <summary>
        /// The caller's id, from the JWT.
        /// </summary>
        /// <remarks>
        /// This previously fell back to the literal string "current" when there was no token,
        /// which put every anonymous caller into one shared pseudo-user's inbox and
        /// preferences. There is no fallback now — the class requires authentication.
        /// </remarks>
        private string CallerId => User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        /// <summary>Task-related email notifications. Recipients are resolved server-side.</summary>
        /// <remarks>
        /// This was anonymous and passed the caller's <c>RecipientEmails</c> list straight to
        /// SmtpClient, making it an open relay: anyone could send arbitrary mail to arbitrary
        /// addresses from the product's SMTP identity and burn its domain reputation.
        ///
        /// It still exists because the app genuinely uses it (assignment, status change and
        /// comment notifications in <c>services/notifications.ts</c>), but it is now:
        ///   1. authenticated;
        ///   2. restricted to a fixed set of task notification types;
        ///   3. addressed only to people already attached to the referenced task — the
        ///      client's requested recipients are intersected with that set, never trusted.
        /// The client may narrow the audience but can never widen it.
        /// </remarks>
        [HttpPost("email")]
        public async Task<ActionResult<NotificationResponse>> SendEmail([FromBody] NotificationRequest request)
        {
            var callerId = CallerId;
            if (string.IsNullOrEmpty(callerId)) return Unauthorized(new { message = "Authentication required" });
            if (request == null || string.IsNullOrWhiteSpace(request.TaskId))
                return BadRequest(new { message = "TaskId is required" });

            if (!AllowedTaskNotificationTypes.Contains(request.Type))
            {
                return BadRequest(new
                {
                    message = "Unsupported notification type",
                    detail = $"Allowed types: {string.Join(", ", AllowedTaskNotificationTypes)}."
                });
            }

            var task = await _tasks.GetByIdAsync(request.TaskId);
            if (task == null) return NotFound(new { message = "Task not found" });

            // Everyone attached to the task; the caller must be one of them.
            var participants = new[] { task.Creator, task.Reviewer }
                .Concat(task.AssignedTo ?? new List<Contributor>())
                .Where(c => c != null)
                .ToList();

            if (!participants.Any(c => string.Equals(c.Id, callerId, StringComparison.OrdinalIgnoreCase)))
            {
                return StatusCode(403, new
                {
                    message = "Insufficient permissions",
                    detail = "You are not a participant of this task."
                });
            }

            var allowedEmails = participants
                .Select(c => c.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var requested = request.RecipientEmails ?? new List<string>();
            var recipients = requested.Count == 0
                ? allowedEmails.ToList()
                : requested.Where(e => allowedEmails.Contains(e)).ToList();

            if (recipients.Count == 0)
            {
                return Ok(new NotificationResponse { Success = true, Message = "No eligible recipients." });
            }

            // Rebuild the request from trusted data; never forward the client's copy.
            var safeRequest = new NotificationRequest
            {
                Type = request.Type,
                TaskId = task.Id,
                TaskTitle = task.Title,
                RecipientEmails = recipients,
                CcEmails = null,
                AssigneeNames = (task.AssignedTo ?? new List<Contributor>())
                    .Where(c => c != null).Select(c => c.Name).ToList(),
                Metadata = request.Metadata,
            };

            var result = await _repo.SendEmailNotificationAsync(safeRequest);
            return Ok(result);
        }

        private static readonly HashSet<string> AllowedTaskNotificationTypes =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "task_assigned", "task_status_changed", "task_commented", "task_updated",
            };

        [HttpGet]
        public async Task<ActionResult<List<Notification>>> GetNotifications()
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });

            var notifications = await _repo.GetUserNotificationsAsync(userId);
            return Ok(notifications);
        }

        /// <summary>Creates an in-app notification for the caller.</summary>
        /// <remarks>
        /// The target user is forced to the caller. Previously the whole notification —
        /// including <c>UserId</c> — came from the body, so anyone could write arbitrary
        /// content, with arbitrary links, into any user's notification feed.
        /// </remarks>
        [HttpPost]
        public async Task<ActionResult<Notification>> CreateNotification([FromBody] Notification notification)
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });
            if (notification == null) return BadRequest(new { message = "Notification payload is required" });

            notification.UserId = userId;

            await _repo.CreateUserNotificationAsync(notification);
            return CreatedAtAction(nameof(GetNotifications), new { id = notification.Id }, notification);
        }

        /// <summary>Marks one notification as read.</summary>
        /// <remarks>
        /// The frontend has always called this route, but it did not exist — the request
        /// 404'd, `response.ok` came back false, and because the client returned that value
        /// instead of throwing, React Query still ran its optimistic update. The badge
        /// appeared to clear and then came back on the next refetch, which is why
        /// notifications looked permanently unread.
        /// </remarks>
        [HttpPut("{id}/read")]
        public async Task<IActionResult> MarkAsRead(string id)
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });

            var updated = await _repo.MarkAsReadAsync(id, userId);
            if (!updated) return NotFound(new { message = "Notification not found" });

            return Ok(new { id, isRead = true });
        }

        /// <summary>Marks every unread notification for the caller as read.</summary>
        [HttpPut("read-all")]
        public async Task<IActionResult> MarkAllAsRead()
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });

            var count = await _repo.MarkAllAsReadAsync(userId);
            return Ok(new { updated = count });
        }

        /// <summary>Deletes one of the caller's notifications.</summary>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteNotification(string id)
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });

            var deleted = await _repo.DeleteUserNotificationAsync(id, userId);
            if (!deleted) return NotFound(new { message = "Notification not found" });

            return NoContent();
        }

        [HttpGet("preferences")]
        public async Task<ActionResult<NotificationPreferences>> GetPreferences()
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });

            var prefs = await _repo.GetPreferencesAsync(userId);
            if (prefs == null)
            {
                return Ok(new NotificationPreferences { UserId = userId });
            }
            return Ok(prefs);
        }

        [HttpPut("preferences")]
        public async Task<ActionResult<NotificationPreferences>> UpdatePreferences([FromBody] NotificationPreferences preferences)
        {
            var userId = CallerId;
            if (string.IsNullOrEmpty(userId)) return Unauthorized(new { message = "Authentication required" });
            if (preferences == null) return BadRequest(new { message = "Preferences payload is required" });

            preferences.UserId = userId;
            await _repo.UpdatePreferencesAsync(preferences);
            return Ok(preferences);
        }
    }
}
