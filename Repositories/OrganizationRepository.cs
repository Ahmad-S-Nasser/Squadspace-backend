using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Models;
using System.Threading.Tasks;

namespace RafeeqyNotes.Api.Repositories
{
    public class OrganizationRepository : IOrganizationRepository
    {
        private readonly IMongoCollection<Organization> _organizations;
        private readonly IMongoCollection<Project> _projects;

        public OrganizationRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _organizations = database.GetCollection<Organization>("Organizations");
            _projects= database.GetCollection<Project>("Projects");
        }

        public Organization FetchOrganizationById(string id) =>
            _organizations.Find(o => o.Id == id).FirstOrDefault();

        public List<Project> FetchProjectsByOrganizationId(string id)=>
            // Flat id rather than the embedded path; see the note in ProjectRepository.
            _projects.Find(p => p.OrganizationId == id).ToList();
        public List<Organization> FetchAllOrganizations() =>
            _organizations.Find(_ => true).ToList();

        public List<Organization> FetchOrganizationsByUserId(string userId)
        {
            var filter = Builders<Organization>.Filter.Or(
                Builders<Organization>.Filter.Eq(o => o.OwnerId, userId),
                Builders<Organization>.Filter.ElemMatch(o => o.Members, m => m.UserId == userId)
            );
            return _organizations.Find(filter).ToList();
        }

        public Organization InsertOrganization(Organization org)
        {
            org.CreatedAt = DateTime.UtcNow;
            org.UpdatedAt = DateTime.UtcNow;
            _organizations.InsertOne(org);
            return org;
        }

        public Organization UpdateOrganization(string id, Organization org)
        {
            org.UpdatedAt = DateTime.UtcNow;

            // A targeted $set on just the editable fields, not a whole-document ReplaceOne:
            // the caller (OrganizationController.UpdateOrganization) only ever changes
            // Name/Description/Logo, but a ReplaceOne of the full document would silently
            // overwrite Members with whatever was in this snapshot if a role change lands
            // on the same document between this read and this write.
            var update = Builders<Organization>.Update
                .Set(o => o.Name, org.Name)
                .Set(o => o.Description, org.Description)
                .Set(o => o.Logo, org.Logo)
                .Set(o => o.UpdatedAt, org.UpdatedAt);
            _organizations.UpdateOne(o => o.Id == id, update);
            return org;
        }

        public void DeleteOrganization(string id) =>
            _organizations.DeleteOne(o => o.Id == id);

        public OrganizationMember AddMember(string orgId, OrganizationMember member)
        {
            member.JoinedAt = DateTime.UtcNow;
            var update = Builders<Organization>.Update.Push(o => o.Members, member);
            _organizations.UpdateOne(o => o.Id == orgId, update);
            return member;
        }

        public void RemoveMember(string orgId, string memberId)
        {
            // Robust removal: Try to match by either m.Id OR m.UserId to account for potential frontend ID mismatches
            var update = Builders<Organization>.Update.PullFilter(o => o.Members, 
                m => m.Id == memberId || m.UserId == memberId);
            _organizations.UpdateOne(o => o.Id == orgId, update);
        }

        public bool UpdateMemberRole(string orgId, string memberId, string role)
        {
            var filter = Builders<Organization>.Filter.And(
                Builders<Organization>.Filter.Eq(o => o.Id, orgId),
                Builders<Organization>.Filter.ElemMatch(o => o.Members, m => m.Id == memberId || m.UserId == memberId)
            );

            var update = Builders<Organization>.Update.Set("Members.$.Role", role);
            var result = _organizations.UpdateOne(filter, update);
            // MatchedCount == 0 means the $elemMatch found no member row for this id — a
            // stale/mismatched memberId — and UpdateOne succeeds silently in that case rather
            // than throwing, so the caller must check this instead of assuming success.
            return result.MatchedCount > 0;
        }

        public List<OrganizationMember> GetMembers(string orgId)
        {
            var org = _organizations.Find(o => o.Id == orgId).FirstOrDefault();
            return org?.Members ?? new List<OrganizationMember>();
        }
    }
}
