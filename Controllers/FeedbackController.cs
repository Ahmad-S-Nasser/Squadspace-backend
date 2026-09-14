using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Controllers
{
    /// <summary>What a user sends when something is broken or missing.</summary>
    public class FeedbackRequest
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string OrganizationId { get; set; }
        public string Page { get; set; }
        public string ScreenSize { get; set; }
    }

    /// <summary>
    /// In-product bug reports and ideas.
    /// </summary>
    /// <remarks>
    /// SUBMITTING is open to any signed-in user - gating feedback behind a role is how you stop
    /// hearing from the people who actually hit the bugs.
    ///
    /// READING is platform-admin only, and that is a real boundary rather than a formality: this
    /// is vendor feedback, not an organization's data. One customer's admin must never be able to
    /// read another customer's bug reports, and a report often quotes what the reporter was
    /// looking at when it broke.
    /// </remarks>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FeedbackController : ControllerBase
    {
        /// <summary>Longer than a tweet, shorter than a bug tracker. Enough to be useful.</summary>
        private const int MaxMessage = 2000;

        /// <summary>Per user, per hour. Generous for a person, uninteresting for a script.</summary>
        private const int HourlyLimit = 10;

        private readonly IFeedbackRepository _feedback;
        private readonly IContributorRepository _contributors;
        private readonly INotificationRepository _notifications;
        private readonly IConfiguration _config;
        private readonly ILogger<FeedbackController> _logger;

        public FeedbackController(
            IFeedbackRepository feedback,
            IContributorRepository contributors,
            INotificationRepository notifications,
            IConfiguration config,
            ILogger<FeedbackController> logger)
        {
            _feedback = feedback;
            _contributors = contributors;
            _notifications = notifications;
            _config = config;
            _logger = logger;
        }

        [HttpPost]
        public async Task<IActionResult> Submit([FromBody] FeedbackRequest request)
        {
            var userId = OrgAccess.UserId(User);
            if (string.IsNullOrWhiteSpace(userId)) return Unauthorized();

            if (request == null || string.IsNullOrWhiteSpace(request.Message))
            {
                return BadRequest(new { message = "Tell us what happened." });
            }

            var type = (request.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (!FeedbackTypes.All.Contains(type)) type = FeedbackTypes.Idea;

            var recent = await _feedback.CountByUserSinceAsync(userId, DateTime.UtcNow.AddHours(-1));
            if (recent >= HourlyLimit)
            {
                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    message = "Thanks — that is a lot of feedback in one hour. Please try again shortly.",
                });
            }

            // Identity from the token, never the body: a report has to be traceable to a real
            // account, and a form that lets you name someone else is a way to put words in their
            // mouth.
            var contributor = await _contributors.GetByIdAsync(userId);

            var feedback = new Feedback
            {
                Type = type,
                Message = request.Message.Trim().Substring(0, Math.Min(request.Message.Trim().Length, MaxMessage)),
                UserId = userId,
                UserName = contributor?.Name,
                UserEmail = contributor?.Email,
                OrganizationId = request.OrganizationId,

                // Context is captured, not asked for. Truncated because a user agent is
                // attacker-controlled text and there is no reason to store an essay.
                Page = Clip(request.Page, 300),
                ScreenSize = Clip(request.ScreenSize, 40),
                UserAgent = Clip(Request.Headers["User-Agent"].ToString(), 400),
            };

            await _feedback.CreateAsync(feedback);

            await NotifyAsync(feedback);

            return Ok(new { received = true });
        }

        /// <summary>
        /// Emails the report to the product inbox.
        /// </summary>
        /// <remarks>
        /// Deliberately AFTER the write and wrapped: the report is already saved and visible in
        /// the triage screen, so a mail failure must not lose it or show the user an error for
        /// something that did work. The inbox is configurable rather than hardcoded, because a
        /// destination baked into a controller is one nobody can change without a deploy.
        /// </remarks>
        private async Task NotifyAsync(Feedback feedback)
        {
            try
            {
                var inbox = _config["Feedback:NotifyEmail"];
                if (string.IsNullOrWhiteSpace(inbox)) return;

                var response = await _notifications.SendEmailNotificationAsync(new NotificationRequest
                {
                    Type = "product_feedback",
                    TaskTitle = feedback.Type,
                    RecipientEmails = new List<string> { inbox },
                    Metadata = new Dictionary<string, string>
                    {
                        { "Kind", feedback.Type ?? string.Empty },
                        { "Message", feedback.Message ?? string.Empty },
                        { "From", $"{feedback.UserName} <{feedback.UserEmail}>" },
                        { "Page", feedback.Page ?? string.Empty },
                        { "ScreenSize", feedback.ScreenSize ?? string.Empty },
                        { "UserAgent", feedback.UserAgent ?? string.Empty },
                        { "OrganizationId", feedback.OrganizationId ?? string.Empty },
                    },
                });

                // SendEmailNotificationAsync catches its own exceptions and reports failure in
                // the RESULT, so without this check a broken inbox fails silently forever - the
                // reports keep saving and nobody notices the notifications stopped.
                if (response is not { Success: true })
                {
                    _logger.LogWarning(
                        "Feedback {FeedbackId} saved but the notification to {Inbox} failed: {Reason}",
                        feedback.Id, inbox, response?.Message ?? "no response");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Feedback saved but could not be emailed.");
            }
        }

        /// <summary>Whether the caller may read the inbox, so the app can show the link.</summary>
        [HttpGet("access")]
        public IActionResult Access() => Ok(new { canRead = OrgAccess.IsPlatformAdmin(this) });

        /// <summary>Everything sent, newest first. Platform admins only.</summary>
        [HttpGet]
        public async Task<IActionResult> List(
            [FromQuery] string type = null, [FromQuery] string state = null, [FromQuery] int limit = 100)
        {
            if (!OrgAccess.IsPlatformAdmin(this)) return NotFound();

            return Ok(await _feedback.GetRecentAsync(type, state, limit));
        }

        /// <summary>Moves an item through triage. Platform admins only.</summary>
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(string id, [FromBody] Feedback update)
        {
            // 404 rather than 403: whether a feedback inbox exists here is not something an
            // ordinary user needs confirmed.
            if (!OrgAccess.IsPlatformAdmin(this)) return NotFound();

            var stored = await _feedback.GetByIdAsync(id);
            if (stored == null) return NotFound();

            if (update?.State != null && FeedbackStates.All.Contains(update.State))
            {
                stored.State = update.State;
            }

            stored.Note = Clip(update?.Note, 1000);

            await _feedback.UpdateAsync(stored);
            return Ok(stored);
        }

        private static string Clip(string value, int max) =>
            string.IsNullOrEmpty(value) ? value : value.Substring(0, Math.Min(value.Length, max));
    }
}
