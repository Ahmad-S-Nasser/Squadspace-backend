using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IAttachmentRepository
    {
        Task<Attachment> SaveAttachment(IFormFile file, string entityType, string entityId, string uploaderId, string organizationId);

        /// <summary>Total bytes stored by an organization, for the storage quota.</summary>
        Task<long> SumSizeByOrganizationAsync(string organizationId);
        List<Attachment> GetAttachments(string entityType, string entityId);
        Attachment GetAttachmentById(string id);
        NotificationResponse DeleteAttachment(string id);
    }
}
