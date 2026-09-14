using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Api.Services;

[ApiController]
[Route("api/[controller]")]
// SECURITY: this controller had no [Authorize] and no ownership checks. GetAllMeetings
// returned every meeting in the system, and any meeting could be read, edited or deleted
// by id. Each action now resolves the owning organization from the meeting's project.
[Authorize]
public class MeetingController : ControllerBase
{
    private readonly IMeetingRepository _repo;
    private readonly INotificationRepository _notificationRepo;

    private readonly IOrganizationRepository _organizations;
    private readonly IProjectRepository _projects;

    private readonly IIcsBuilder _ics;
    private readonly IEntitlementService _entitlements;
    private readonly IConfiguration _config;
    private readonly IContributorRepository _contributors;

    public MeetingController(
        IMeetingRepository repo,
        INotificationRepository notificationRepo,
        IOrganizationRepository organizations,
        IProjectRepository projects,
        IIcsBuilder ics,
        IEntitlementService entitlements,
        IConfiguration config,
        IContributorRepository contributors)
    {
        _repo = repo;
        _notificationRepo = notificationRepo;
        _organizations = organizations;
        _projects = projects;
        _ics = ics;
        _entitlements = entitlements;
        _config = config;
        _contributors = contributors;
    }

    /// <summary>
    /// Emails a calendar invite to every attendee with an address.
    /// </summary>
    /// <remarks>
    /// Best-effort by design. A meeting that was saved must not fail because SMTP is down or one
    /// address bounces - the in-app notification already landed, and losing the meeting to protect
    /// the invite would be the wrong trade.
    ///
    /// Gated on integrations.calendar, which honours Billing:EntitlementGuardMode, so it observes
    /// before it enforces like every other gate here.
    /// </remarks>
    private async Task SendInviteAsync(Meeting meeting, string orgId, string method, int sequence)
    {
        try
        {
            var entitlements = await _entitlements.ForOrganizationAsync(orgId);
            var allowed = this.RequireEntitlement(entitlements, Entitlement.CalendarIntegration);
            if (!allowed.Allowed) return;

            var recipients = (meeting.Attendees ?? new List<MeetingAttendee>())
                .Select(a => a?.Email)
                .Where(e => !string.IsNullOrWhiteSpace(e))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (recipients.Count == 0) return;

            await _notificationRepo.SendEmailNotificationAsync(new NotificationRequest
            {
                Type = method == IcsMethods.Cancel ? "meeting_cancelled" : "meeting_invite",
                TaskId = meeting.Id,
                TaskTitle = meeting.Title,
                RecipientEmails = recipients,
                IcsContent = _ics.Build(meeting, method, sequence),
                IcsMethod = method,
                Metadata = new Dictionary<string, string>
                {
                    { "MeetingId", meeting.Id },
                    { "ScheduledAt", meeting.ScheduledAt.ToString("o") },
                    { "TimeZone", meeting.TimeZone ?? "UTC" },
                },
            });
        }
        catch
        {
            // Never let invite delivery take down the write that already succeeded.
        }
    }

    /// <summary>Downloads the meeting as an .ics file.</summary>
    /// <remarks>
    /// PUBLISH rather than REQUEST: a file someone downloaded is a copy for their own calendar,
    /// not an invitation to reply to. Offering Accept/Decline on it would send an RSVP nobody
    /// asked for.
    /// </remarks>
    [HttpGet("{id}/calendar.ics")]
    public async Task<IActionResult> DownloadIcs(string id)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        var ics = _ics.Build(meeting, IcsMethods.Publish, SequenceFor(meeting));
        var bytes = System.Text.Encoding.UTF8.GetBytes(ics);

        return File(bytes, "text/calendar", $"{Slug(meeting.Title)}.ics");
    }

    /// <summary>
    /// Mints short-lived TURN credentials for this meeting's in-app call, per the standard
    /// coturn REST API long-term-credential convention: username = "&lt;expiry&gt;:&lt;userId&gt;",
    /// credential = base64(HMAC-SHA1(sharedSecret, username)). The shared secret itself never
    /// leaves the server — only the derived, time-limited credential is returned.
    /// </summary>
    [HttpGet("{id}/call-credentials")]
    public async Task<ActionResult<TurnCredentials>> GetCallCredentials(string id)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var secret = _config["Turn:SharedSecret"];
        if (string.IsNullOrWhiteSpace(secret))
            return StatusCode(503, new { message = "In-app calling is not configured on this server." });

        var ttl = _config.GetValue<int?>("Turn:CredentialTtlSeconds") ?? 3600;
        var urls = _config.GetSection("Turn:Urls").Get<string[]>() ?? Array.Empty<string>();

        var expiry = DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds();
        var username = $"{expiry}:{auth.UserId}";

        using var mac = new System.Security.Cryptography.HMACSHA1(System.Text.Encoding.UTF8.GetBytes(secret));
        var credential = Convert.ToBase64String(mac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(username)));

        return Ok(new TurnCredentials
        {
            Username = username,
            Credential = credential,
            Urls = urls.ToList(),
            Ttl = ttl,
        });
    }

    /// <summary>
    /// A monotonic SEQUENCE derived from the last update.
    /// </summary>
    /// <remarks>
    /// RFC 5545 needs this to increase for a client to accept a revision. There is no counter on
    /// the model, so time since a fixed epoch stands in: it always rises, it is stable for a given
    /// meeting state, and it survives the app restarting.
    ///
    /// SECONDS, not minutes. At minute resolution two edits inside the same minute produce the
    /// same SEQUENCE and the client silently ignores the second one - which is precisely the case
    /// of correcting a time you just got wrong. Seconds from 2020 stay inside int32 until 2088.
    /// </remarks>
    private static int SequenceFor(Meeting meeting)
    {
        var since = meeting.UpdatedAt - new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return (int)Math.Max(0, Math.Min(int.MaxValue, since.TotalSeconds));
    }

    private static string Slug(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "meeting";

        var cleaned = new string(value
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray());

        return cleaned.Trim('-').Replace("--", "-") is { Length: > 0 } s ? s : "meeting";
    }

    /// <summary>Authorizes against the organization owning the meeting's project.</summary>
    private async Task<OrgAuth> AuthorizeMeetingAsync(string meetingId)
    {
        var meeting = await _repo.GetByIdAsync(meetingId);
        return await this.AuthorizeProjectAsync(
            _organizations, _projects, meeting?.ProjectId, OrgAccess.AnyMember);
    }

    /// <summary>Organization ids the caller belongs to, for list filtering.</summary>
    private HashSet<string> MyOrganizationIds()
    {
        var orgs = _organizations.FetchOrganizationsByUserId(OrgAccess.UserId(User))
                   ?? new List<Organization>();
        return new HashSet<string>(
            orgs.Where(o => !string.IsNullOrEmpty(o?.Id)).Select(o => o.Id),
            StringComparer.OrdinalIgnoreCase);
    }

    [HttpGet("rooms")]
    public async Task<ActionResult<List<Meeting>>> GetAllMeetings()
    {
        // Was every meeting in the database. Now only those in the caller's organizations.
        var meetings = await _repo.GetAllMeetings() ?? new List<Meeting>();
        var myOrgIds = MyOrganizationIds();

        var visible = new List<Meeting>();
        foreach (var m in meetings.Where(m => m != null))
        {
            var orgId = await OrgScope.OrgIdForProjectAsync(_projects, m.ProjectId);
            if (orgId != null && myOrgIds.Contains(orgId)) visible.Add(m);
        }
        return Ok(visible);
    }
    // GET /api/Meeting/project/{projectId}
    [HttpGet("byproject/{projectId}")]
    public async Task<ActionResult<List<Meeting>>> GetByProjectId(string projectId)
    {
        var auth = await this.AuthorizeProjectAsync(
            _organizations, _projects, projectId, OrgAccess.AnyMember);
        if (!auth.Allowed) return auth.Error;

        var meetings = await _repo.GetByProjectIdAsync(projectId);
        return Ok(meetings);
    }

    // GET /api/Meeting/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<Meeting>> GetById(string id)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();
        return Ok(meeting);
    }

    // POST /api/Meeting
    [HttpPost]
    public async Task<ActionResult<Meeting>> Create([FromBody] Meeting meeting)
    {
        if (meeting == null) return BadRequest("Request body is required.");

        // Project comes from the body, so it is attacker-controlled.
        var auth = await this.AuthorizeProjectAsync(
            _organizations, _projects, meeting.ProjectId, OrgAccess.AnyMember);
        if (!auth.Allowed) return auth.Error;

        meeting.CreatedAt = DateTime.UtcNow;
        meeting.UpdatedAt = DateTime.UtcNow;

        // The create form only sends attendee ids (AttendeeIds), never full MeetingAttendee
        // objects - it doesn't know each contributor's name/email client-side. Without this,
        // meeting.Attendees stayed permanently empty for every meeting ever created here, which
        // silently skipped both the in-app "meeting_created" notification below and the email
        // invite in SendInviteAsync (its `recipients.Count == 0` check returns immediately) -
        // nobody was ever actually told about a meeting they were "invited" to.
        if (meeting.AttendeeIds is { Count: > 0 })
        {
            foreach (var contributorId in meeting.AttendeeIds.Distinct())
            {
                var contributor = await _contributors.GetByIdAsync(contributorId);
                if (contributor == null) continue;

                meeting.Attendees.Add(new MeetingAttendee
                {
                    MeetingId = meeting.Id,
                    ContributorId = contributorId,
                    Name = contributor.Name,
                    Email = contributor.Email,
                });
            }
        }

        await _repo.CreateAsync(meeting);

        // Notify all attendees about the new meeting
        foreach (var attendee in meeting.Attendees)
        {
            await _notificationRepo.CreateUserNotificationAsync(new Notification
            {
                UserId = attendee.ContributorId,
                Type = "meeting_created",
                Title = "New Meeting",
                Message = $"You have a new meeting: {meeting.Title}",
                Link = "/meetings",
                CreatedAt = DateTime.UtcNow,
                Metadata = new Dictionary<string, string> { { "meetingId", meeting.Id } }
            });
        }

        await SendInviteAsync(meeting, auth.Org?.Id, IcsMethods.Request, sequence: 0);

        return CreatedAtAction(nameof(GetById), new { id = meeting.Id }, meeting);
    }

    // PUT /api/Meeting/{id}
    [HttpPut("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] Meeting meeting)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var existing = await _repo.GetByIdAsync(id);
        if (existing == null) return NotFound();

        // _repo.UpdateAsync does a full ReplaceOne, and the edit form only ever sends the fields
        // below - never ProjectId, CreatedById, CreatedBy, CreatedAt, Attendees or Attachments.
        // Saving the incoming body as-is (as this used to) silently wiped all of those back to
        // their type defaults on every edit - including ProjectId, which every subsequent
        // request on this meeting needs to resolve authorization. Merge onto the existing
        // document instead, matching the fix already applied to Organization updates for the
        // same reason.
        var wasScheduledAt = existing.ScheduledAt;
        var wasDuration = existing.Duration;
        var wasTitle = existing.Title;
        var wasLocation = existing.Location;
        var wasMeetingLink = existing.MeetingLink;
        var wasTimeZone = existing.TimeZone;

        existing.Title = meeting.Title;
        existing.Description = meeting.Description;
        existing.ScheduledAt = meeting.ScheduledAt;
        existing.TimeZone = meeting.TimeZone;
        existing.Duration = meeting.Duration;
        existing.Location = meeting.Location;
        existing.MeetingLink = meeting.MeetingLink;
        existing.UpdatedAt = DateTime.UtcNow;

        // A time change means the "starting soon" reminder needs to fire again for the new
        // time - otherwise a meeting moved after its reminder already went out (or moved later
        // so the reminder window hasn't opened yet) would never remind anyone a second time.
        if (existing.ScheduledAt != wasScheduledAt)
        {
            existing.ReminderSentAt = null;
        }

        await _repo.UpdateAsync(existing);

        // Only re-invite when something a calendar entry actually shows has moved. Re-sending on
        // every save trains people to ignore the invites.
        var rescheduled = wasScheduledAt != existing.ScheduledAt
                          || wasDuration != existing.Duration
                          || !string.Equals(wasTitle, existing.Title, StringComparison.Ordinal)
                          || !string.Equals(wasLocation, existing.Location, StringComparison.Ordinal)
                          || !string.Equals(wasMeetingLink, existing.MeetingLink, StringComparison.Ordinal)
                          || !string.Equals(wasTimeZone, existing.TimeZone, StringComparison.Ordinal);

        if (rescheduled)
        {
            // Same UID, higher SEQUENCE: clients update the existing entry rather than adding a
            // second one the attendee then has to work out which of is real.
            await SendInviteAsync(existing, auth.Org?.Id, IcsMethods.Request, SequenceFor(existing));
        }

        return NoContent();
    }

    // DELETE /api/Meeting/{id}
    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        // Cancelled BEFORE deletion, while the attendee list is still readable. A CANCEL with the
        // meeting's UID is what clears it from a calendar; deleting our row alone leaves it sitting
        // in everyone else's.
        await SendInviteAsync(meeting, auth.Org?.Id, IcsMethods.Cancel, SequenceFor(meeting) + 1);

        await _repo.DeleteAsync(id);
        return NoContent();
    }

    // POST /api/Meeting/{id}/attachments
    [HttpPost("{id}/attachments")]
    public async Task<ActionResult<MeetingAttachment>> AddAttachment(string id, [FromBody] MeetingAttachment attachment)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        //attachment.MeetingId = meeting.Id;
        meeting.Attachments.Add(attachment);
        await _repo.UpdateAsync(meeting);

        return Ok(attachment);
    }

    // DELETE /api/Meeting/{id}/attachments/{attachmentId}
    [HttpDelete("{id}/attachments/{attachmentId}")]
    public async Task<IActionResult> RemoveAttachment(string id, string attachmentId)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        var attachment = meeting.Attachments.FirstOrDefault(a => a.Id == attachmentId);
        if (attachment == null) return NotFound();

        meeting.Attachments.Remove(attachment);
        await _repo.UpdateAsync(meeting);

        return NoContent();
    }

    // GET /api/Meeting/{id}/attendees
    [HttpGet("{id}/attendees")]
    public async Task<ActionResult<List<MeetingAttendee>>> GetAttendees(string id)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        return Ok(meeting.Attendees);
    }

    // POST /api/Meeting/{id}/attendees
    [HttpPost("{id}/attendees")]
    public async Task<ActionResult<MeetingAttendee>> AddAttendee(string id, [FromBody] MeetingAttendee attendee)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        attendee.MeetingId = meeting.Id;
        meeting.Attendees.Add(attendee);
        await _repo.UpdateAsync(meeting);

        // Notify the added attendee
        await _notificationRepo.CreateUserNotificationAsync(new Notification
        {
            UserId = attendee.ContributorId,
            Type = "meeting_invitation",
            Title = "Meeting Invitation",
            Message = $"You have been invited to a meeting: {meeting.Title}",
            Link = "/meetings",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string> { { "meetingId", meeting.Id } }
        });

        return Ok(attendee);
    }

    // DELETE /api/Meeting/{id}/attendees/{attendeeId}
    [HttpDelete("{id}/attendees/{attendeeId}")]
    public async Task<IActionResult> RemoveAttendee(string id, string attendeeId)
    {
        var auth = await AuthorizeMeetingAsync(id);
        if (!auth.Allowed) return auth.Error;

        var meeting = await _repo.GetByIdAsync(id);
        if (meeting == null) return NotFound();

        var attendee = meeting.Attendees.FirstOrDefault(a => a.Id == attendeeId);
        if (attendee == null) return NotFound();

        meeting.Attendees.Remove(attendee);
        await _repo.UpdateAsync(meeting);

        return NoContent();
    }
}