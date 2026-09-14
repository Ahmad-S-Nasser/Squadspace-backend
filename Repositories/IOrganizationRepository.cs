using RafeeqyNotes.Api.Models;

namespace RafeeqyNotes.Api.Repositories
{
    public interface IOrganizationRepository
    {
        Organization FetchOrganizationById(string id);
        List<Project> FetchProjectsByOrganizationId(string id);
        List<Organization> FetchAllOrganizations();
        List<Organization> FetchOrganizationsByUserId(string userId);
        Organization InsertOrganization(Organization org);
        Organization UpdateOrganization(string id, Organization org);
        void DeleteOrganization(string id);

        OrganizationMember AddMember(string orgId, OrganizationMember member);
        void RemoveMember(string orgId, string memberId);
        bool UpdateMemberRole(string orgId, string memberId, string role);
        List<OrganizationMember> GetMembers(string orgId);
    }
}
