namespace RafeeqyNotes.Api.Models
{
    public enum AttachmentType : byte
    {
        board,
        note,
        task
    };
    public class CreateAttachmentDto
    {
        public AttachmentType Type { get; set; }
        public string ItemId { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
    }
}
