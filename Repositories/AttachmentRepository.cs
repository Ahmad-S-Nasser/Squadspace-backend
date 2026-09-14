using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public class AttachmentRepository : IAttachmentRepository
    {
        private readonly IWebHostEnvironment _env;
        private readonly IMongoCollection<Attachment> _attachments;

        public AttachmentRepository(IWebHostEnvironment env, MongoDbSettings settings)
        {
            _env = env;
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _attachments = database.GetCollection<Attachment>("Attachments");
        }

        public async Task<Attachment> SaveAttachment(IFormFile file, string entityType, string entityId, string uploaderId, string organizationId)
        {
            var uploadsPath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "attachments");
            if (!Directory.Exists(uploadsPath))
                Directory.CreateDirectory(uploadsPath);

            var fileName = $"{Guid.NewGuid()}_{file.FileName}";
            var filePath = Path.Combine(uploadsPath, fileName);

            using (var stream = new FileStream(filePath, FileMode.Create))
            {
                await file.CopyToAsync(stream);
            }

            var attachment = new Attachment
            {
                Id = Guid.NewGuid().ToString(),
                FileName = file.FileName,
                FileUrl = $"/attachments/{fileName}",
                ContentType = file.ContentType,
                FileSize = file.Length,
                EntityType = entityType,
                EntityId = entityId,
                OrganizationId = organizationId,
                UploaderId = uploaderId,
                UploadedAt = DateTime.UtcNow
            };

            _attachments.InsertOne(attachment);
            return attachment;
        }

        public async Task<long> SumSizeByOrganizationAsync(string organizationId)
        {
            if (string.IsNullOrWhiteSpace(organizationId)) return 0;

            // Aggregation rather than loading the documents: an organization with thousands
            // of files should not pull them all into memory to add up one field.
            var result = await _attachments
                .Aggregate()
                .Match(a => a.OrganizationId == organizationId)
                .Group(a => a.OrganizationId, g => new { Total = g.Sum(x => x.FileSize) })
                .FirstOrDefaultAsync();

            return result?.Total ?? 0;
        }

        public List<Attachment> GetAttachments(string entityType, string entityId) =>
            _attachments.Find(a => a.EntityType == entityType && a.EntityId == entityId).ToList();

        public Attachment GetAttachmentById(string id) =>
            _attachments.Find(a => a.Id == id).FirstOrDefault();

        public NotificationResponse DeleteAttachment(string id)
        {
            var attachment = GetAttachmentById(id);
            if (attachment == null)
            {
                return new NotificationResponse
                {
                    Success = false,
                    Message = "Attachment not found",
                    NotificationId = null
                };
            }

            var filePath = Path.Combine(_env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"), "attachments", Path.GetFileName(attachment.FileUrl));
            if (System.IO.File.Exists(filePath))
            {
                System.IO.File.Delete(filePath);
            }

            _attachments.DeleteOne(a => a.Id == id);

            return new NotificationResponse
            {
                Success = true,
                Message = "Attachment deleted successfully",
                NotificationId = id
            };
        }
    }
}
