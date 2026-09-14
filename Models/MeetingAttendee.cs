using MongoDB.Driver;

namespace RafeeqyNotes.Api.Models
{
    public class MeetingAttendee
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string MeetingId { get; set; } = string.Empty;
        public string ContributorId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;

        // Navigation
        public Meeting? Meeting { get; set; }
        public Contributor? Contributor { get; set; }
    }
}
