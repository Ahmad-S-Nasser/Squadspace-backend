namespace RafeeqyNotes.Api.Models
{
    public class Whiteboard
    {
        public string Id { get; set; }
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public List<WhiteboardElement> Elements { get; set; }
        public LinkedItem? LinkedItem { get; set; }
        public List<string>? SharedWith { get; set; }
        public string CreatedBy { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CreateWhiteboardInput
    {
        public string ProjectId { get; set; }
        public string Name { get; set; }
        public string? Description { get; set; }
        public LinkedItem? LinkedItem { get; set; }
        public List<string>? SharedWith { get; set; }
    }

    public class UpdateWhiteboardInput
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class LinkedItem
    {
        public string Id { get; set; }
        public string Type { get; set; }  // "board" | "note" | "task"
        public string Name { get; set; }
    }
    public class WhiteboardPresence
    {
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
        public bool IsEditing { get; set; }
        public DateTime LastActive { get; set; } = DateTime.UtcNow;
    }
}
