using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Models;
using System.Collections.Generic;
using System.Linq;

namespace RafeeqyNotes.Api.Repositories
{
    public class OrganizationInvitationRepository : IOrganizationInvitationRepository
    {
        private readonly IMongoCollection<OrganizationInvitation> _invitations;

        public OrganizationInvitationRepository(MongoDbSettings settings)
        {
            var client = new MongoClient(settings.ConnectionString);
            var database = client.GetDatabase(settings.DatabaseName);
            _invitations = database.GetCollection<OrganizationInvitation>("OrganizationInvitations");
        }

        public OrganizationInvitation CreateInvitation(OrganizationInvitation invitation)
        {
            _invitations.InsertOne(invitation);
            return invitation;
        }

        // Needed to authorize invitation deletion: the caller must manage the
        // organization the invitation belongs to.
        public OrganizationInvitation GetInvitationById(string id)
        {
            return _invitations.Find(i => i.Id == id).FirstOrDefault();
        }

        public OrganizationInvitation GetInvitationByToken(string token)
        {
            return _invitations.Find(i => i.InvitationToken == token).FirstOrDefault();
        }

        public List<OrganizationInvitation> GetPendingInvitationsByOrganization(string organizationId)
        {
            var filter = Builders<OrganizationInvitation>.Filter.And(
                Builders<OrganizationInvitation>.Filter.Eq(i => i.OrganizationId, organizationId),
                Builders<OrganizationInvitation>.Filter.Eq(i => i.IsUsed, false),
                Builders<OrganizationInvitation>.Filter.Gt(i => i.ExpiresAt, System.DateTime.UtcNow)
            );
            return _invitations.Find(filter).ToList();
        }

        public void MarkInvitationAsUsed(string id)
        {
            var update = Builders<OrganizationInvitation>.Update.Set(i => i.IsUsed, true);
            _invitations.UpdateOne(i => i.Id == id, update);
        }

        public void DeleteInvitation(string id)
        {
            _invitations.DeleteOne(i => i.Id == id);
        }
    }
}
