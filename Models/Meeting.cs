using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    public class Meeting
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProjectId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        /// <summary>Start time, stored in UTC.</summary>
        public DateTime ScheduledAt { get; set; }

        /// <summary>
        /// IANA time zone the meeting was scheduled in - "Africa/Cairo", "Europe/London".
        /// </summary>
        /// <remarks>
        /// Added BEFORE calendar invites, not after, because without it every invite is wrong for
        /// anyone outside the organizer's zone. ScheduledAt is UTC, which is enough to place the
        /// meeting on a timeline but not enough to describe it: "our 10am standup" is a wall-clock
        /// commitment, and across a DST boundary the correct UTC instant changes while the wall
        /// clock does not.
        ///
        /// Null means the organizer's zone was never captured. Invites then fall back to plain
        /// UTC, which is unambiguous even when it is not what anyone typed.
        /// </remarks>
        public string? TimeZone { get; set; }
        public int? Duration { get; set; } // minutes
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public string CreatedById { get; set; } = string.Empty;
        public Contributor? CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// When the "starting soon" reminder was sent, or null if it hasn't been yet. Also
        /// doubles as the claim marker MeetingReminderHostedService uses to avoid sending the
        /// same reminder twice if the API runs as more than one instance.
        /// </summary>
        public DateTime? ReminderSentAt { get; set; }

        // Navigation
        public List<MeetingAttendee> Attendees { get; set; } = new();
        public List<MeetingAttachment> Attachments { get; set; } = new();

        /// <summary>
        /// Wire-only, never persisted. The create form only knows contributor ids, not full
        /// name/email - the controller resolves each id into a real MeetingAttendee (populating
        /// Attendees itself) before saving, rather than trusting client-supplied names/emails.
        /// </summary>
        [BsonIgnore]
        public List<string>? AttendeeIds { get; set; }
    }
}