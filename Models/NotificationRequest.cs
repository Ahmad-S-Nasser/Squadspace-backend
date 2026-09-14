namespace RafeeqyNotes.Api.Models
{
    public class NotificationRequest
    {
        public string Type { get; set; } // e.g. "task_assigned"
        public string TaskId { get; set; }
        public string TaskTitle { get; set; }
        public List<string> RecipientEmails { get; set; }
        public List<string>? CcEmails { get; set; }
        public List<string>? AssigneeNames { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }

        /// <summary>
        /// Raw VCALENDAR text, attached to the mail as a calendar part.
        /// </summary>
        /// <remarks>
        /// Carried on the request rather than built inside the mailer, so the notification layer
        /// stays ignorant of meetings and the calendar rules live in one place (IcsBuilder).
        /// </remarks>
        public string? IcsContent { get; set; }

        /// <summary>"REQUEST" or "CANCEL". Decides how a client treats the attachment.</summary>
        public string? IcsMethod { get; set; }
    }
    public class NotificationResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; }
        public string NotificationId { get; set; }
    }
}
