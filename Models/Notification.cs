using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    public class Notification
    {
        [BsonId]
        public string Id { get; set; }

        public string UserId { get; set; }
        public string Type { get; set; } // new_message, task_assigned, etc.
        public string Title { get; set; }
        public string Message { get; set; }
        public string Link { get; set; }
        public bool IsRead { get; set; }
        public Dictionary<string, string> Metadata { get; set; }
        public DateTime CreatedAt { get; set; }
    }
}
