using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>
    /// Notifies attendees shortly before a meeting starts. Follows the same poll-loop shape as
    /// <see cref="AutomationHostedService"/> - see its remarks for why this codebase polls a
    /// Mongo query on a timer rather than using a job framework.
    /// </summary>
    /// <remarks>
    /// SAFE ON SEVERAL INSTANCES. Every instance polls, but a meeting is claimed with an atomic
    /// find-and-update (<see cref="IMeetingRepository.ClaimMeetingNeedingReminderAsync"/>) before
    /// its reminder is sent, so two instances can't both notify the same attendees.
    ///
    /// It never throws. An unhandled exception in a BackgroundService stops the host in .NET 8,
    /// so one malformed meeting must not take the whole API down with it.
    /// </remarks>
    public class MeetingReminderHostedService : BackgroundService
    {
        /// <summary>A meeting starting soon is only ever a few minutes away; poll accordingly.</summary>
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

        /// <summary>
        /// How far ahead of the start time to remind - matches the VALARM lead time IcsBuilder
        /// already uses for calendar-invite reminders, so the two stay consistent.
        /// </summary>
        private static readonly TimeSpan ReminderWindow = TimeSpan.FromMinutes(15);

        /// <summary>Ceiling per tick, so one busy minute cannot monopolise the loop.</summary>
        private const int MaxPerTick = 25;

        private readonly IServiceProvider _services;
        private readonly ILogger<MeetingReminderHostedService> _logger;

        public MeetingReminderHostedService(IServiceProvider services, ILogger<MeetingReminderHostedService> logger)
        {
            _services = services;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the app finish starting before competing with it for the database.
            try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
            catch (OperationCanceledException) { return; }

            _logger.LogInformation(
                "Meeting reminder runner started; polling every {Seconds}s for meetings starting within {Minutes}m.",
                PollInterval.TotalSeconds, ReminderWindow.TotalMinutes);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await TickAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // Never propagate: an unhandled exception here would stop the host.
                    _logger.LogError(ex, "Meeting reminder tick failed.");
                }

                try { await Task.Delay(PollInterval, stoppingToken); }
                catch (OperationCanceledException) { break; }
            }

            _logger.LogInformation("Meeting reminder runner stopped.");
        }

        private async Task TickAsync(CancellationToken stoppingToken)
        {
            var meetings = _services.GetService<IMeetingRepository>();
            var notifications = _services.GetService<INotificationRepository>();
            if (meetings == null || notifications == null) return;

            for (var i = 0; i < MaxPerTick && !stoppingToken.IsCancellationRequested; i++)
            {
                var meeting = await meetings.ClaimMeetingNeedingReminderAsync(DateTime.UtcNow, ReminderWindow);
                if (meeting == null) return;

                foreach (var attendee in meeting.Attendees ?? new List<MeetingAttendee>())
                {
                    if (string.IsNullOrWhiteSpace(attendee?.ContributorId)) continue;

                    try
                    {
                        await notifications.CreateUserNotificationAsync(new Notification
                        {
                            UserId = attendee.ContributorId,
                            Type = "meeting_reminder",
                            Title = "Meeting starting soon",
                            Message = $"\"{meeting.Title}\" starts at {meeting.ScheduledAt:t} UTC.",
                            Link = "/meetings",
                            CreatedAt = DateTime.UtcNow,
                            Metadata = new Dictionary<string, string> { { "meetingId", meeting.Id } }
                        });
                    }
                    catch (Exception ex)
                    {
                        // One attendee's notification failing (e.g. a bad Metadata write) must
                        // not stop the rest of the room from being reminded.
                        _logger.LogWarning(ex,
                            "Failed to send meeting reminder for meeting {MeetingId} to attendee {ContributorId}.",
                            meeting.Id, attendee.ContributorId);
                    }
                }
            }
        }
    }
}
