namespace RafeeqyNotes.Api.Models
{
    public class Attachment
    {
        public string Id { get; set; }
        public string FileName { get; set; }
        public string FileUrl { get; set; }
        public string ContentType { get; set; }
        public long FileSize { get; set; }
        public string EntityType { get; set; } // task | project | chat | sprint
        public string EntityId { get; set; }

        /// <summary>Organization this file counts against, for the storage quota.</summary>
        /// <remarks>
        /// Denormalized on write, matching the rest of the data model. The alternative is
        /// resolving every attachment's entity up to its project on every quota check, which
        /// is a read per file — unusable on the upload path.
        ///
        /// Attachments written before this field existed have it empty and therefore do not
        /// count toward any quota. That under-counts rather than over-counts, which is the
        /// safe direction: it lets legitimate uploads through instead of blocking a customer
        /// on data we cannot attribute. Backfill it if storage billing ever depends on it.
        /// </remarks>
        public string OrganizationId { get; set; }
        public string UploaderId { get; set; }
        public DateTime UploadedAt { get; set; }
    }
}
