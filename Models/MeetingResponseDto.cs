namespace RafeeqyNotes.Api.Models
{
    public class MeetingResponseDto
    {
        public string Id { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTime ScheduledAt { get; set; }
        public int? Duration { get; set; }
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public List<AttendeeDto> Attendees { get; set; } = new();
        public List<AttachmentDto> Attachments { get; set; } = new();
        public AttendeeDto CreatedBy { get; set; } = new();
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
