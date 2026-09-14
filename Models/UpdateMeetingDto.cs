namespace RafeeqyNotes.Api.Models
{
    public class UpdateMeetingDto
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public DateTime? ScheduledAt { get; set; }
        public int? Duration { get; set; }
        public string? Location { get; set; }
        public string? MeetingLink { get; set; }
        public List<string>? AttendeeIds { get; set; }

    }
}
