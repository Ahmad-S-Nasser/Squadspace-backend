using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System.Text.Json.Serialization;

namespace RafeeqyNotes.Api.Models
{
    [BsonIgnoreExtraElements]
    public class ProjectSchedule
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [JsonIgnore]
        public string? InternalId { get; set; }
        
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string ProjectId { get; set; } = string.Empty;

        // Audit Fields
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;
        public string ModifiedByUserId { get; set; } = string.Empty;
        public string ModifiedByUserName { get; set; } = string.Empty;

        // Schedule Data
        public List<int> WorkingDays { get; set; } = new List<int> { 1, 2, 3, 4, 5 }; // Sunday=0, Monday=1, etc.
        public WorkingHours WorkingHours { get; set; } = new WorkingHours();
        public List<string> Holidays { get; set; } = new List<string>(); // "YYYY-MM-DD"
    }

    public class WorkingHours
    {
        public string Start { get; set; } = "09:00";
        public string End { get; set; } = "17:00";
    }
}
