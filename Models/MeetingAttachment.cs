using MongoDB.Driver;

namespace RafeeqyNotes.Api.Models
{
    public class MeetingAttachment
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Type { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
    }
}
