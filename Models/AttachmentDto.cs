namespace RafeeqyNotes.Api.Models
{
    public class AttachmentDto
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // "board", "note", "task"
        public string ItemId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
    }
}
