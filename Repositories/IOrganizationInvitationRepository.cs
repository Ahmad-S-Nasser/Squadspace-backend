using RafeeqyNotes.Api.Models;
using System.Collections.Generic;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IOrganizationInvitationRepository
    {
        OrganizationInvitation CreateInvitation(OrganizationInvitation invitation);
        OrganizationInvitation GetInvitationById(string id);
        OrganizationInvitation GetInvitationByToken(string token);
        List<OrganizationInvitation> GetPendingInvitationsByOrganization(string organizationId);
        void MarkInvitationAsUsed(string id);
        void DeleteInvitation(string id);
    }
}
