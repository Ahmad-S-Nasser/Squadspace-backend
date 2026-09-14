namespace RafeeqyNotes.Api.Models
{
    public class CreateMeetingDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime ScheduledAt { get; set; }
        public int? Duration { get; set; }
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public List<string> AttendeeIds { get; set; } = new();
        public List<CreateAttachmentDto>? Attachments { get; set; }
    }
}
