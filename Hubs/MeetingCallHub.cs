using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Repositories;

namespace RafeeqyNotes.Api.Hubs
{
    /// <summary>One connection's identity within a call room, as sent to a newly-joining peer.</summary>
    public class CallParticipant
    {
        public string ConnectionId { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public string UserName { get; set; } = string.Empty;
    }

    /// <summary>
    /// WebRTC signaling for in-app meeting calls. This hub relays SDP offers/answers and ICE
    /// candidates between participants — it never touches audio/video itself, that flows
    /// peer-to-peer (or via TURN) once negotiation completes.
    ///
    /// The room is the meeting's own id: one meeting, one project, one organization is already
    /// the natural authorized unit at this scale (small mesh calls), so there is no separate
    /// call-session concept.
    /// </summary>
    /// <remarks>
    /// [Authorize] from the first commit, mirroring ChatHub rather than WhiteboardHub (the
    /// latter has no [Authorize] — a known gap, not one to repeat for a feature that relays
    /// live audio/video).
    /// </remarks>
    [Authorize]
    public class MeetingCallHub : Hub
    {
        private readonly IMeetingRepository _meetings;
        private readonly IProjectRepository _projects;
        private readonly IOrganizationRepository _organizations;

        // SignalR groups don't expose their own membership, so call rooms are tracked here:
        // meetingId -> (connectionId -> participant). Needed both to hand a joining peer the
        // list of who's already in the room, and to notify the room on a dropped connection
        // (OnDisconnectedAsync) without the client having called LeaveCall first.
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, CallParticipant>> Rooms = new();

        // connectionId -> meetingId, so OnDisconnectedAsync knows which room to clean up without
        // the caller having to pass it in (a disconnect carries no arguments).
        private static readonly ConcurrentDictionary<string, string> ConnectionMeetingIds = new();

        public MeetingCallHub(
            IMeetingRepository meetings,
            IProjectRepository projects,
            IOrganizationRepository organizations)
        {
            _meetings = meetings;
            _projects = projects;
            _organizations = organizations;
        }

        /// <summary>
        /// Hub-safe equivalent of MeetingController.AuthorizeMeetingAsync: a Hub does not derive
        /// from ControllerBase, so the AuthorizeProjectAsync extension method isn't callable here
        /// — this replicates it from the same primitives (OrgScope.OrgIdForProjectAsync,
        /// OrgAccess.RoleIn) instead of reinventing the resolution logic. Called on every public
        /// method, not just JoinCall, so a connection whose meeting access is revoked mid-call
        /// can't keep relaying signaling.
        /// </summary>
        private async Task<bool> IsAuthorizedAsync(string meetingId)
        {
            if (string.IsNullOrWhiteSpace(meetingId)) return false;

            var meeting = await _meetings.GetByIdAsync(meetingId);
            if (meeting == null) return false;

            var orgId = await OrgScope.OrgIdForProjectAsync(_projects, meeting.ProjectId);
            if (string.IsNullOrWhiteSpace(orgId)) return false;

            var org = _organizations.FetchOrganizationById(orgId);
            var userId = OrgAccess.UserId(Context.User);
            var role = OrgAccess.RoleIn(org, userId);

            return role != null && OrgAccess.AnyMember.Contains(role, StringComparer.OrdinalIgnoreCase);
        }

        public async Task JoinCall(string meetingId)
        {
            if (!await IsAuthorizedAsync(meetingId))
                throw new HubException("Not authorized for this meeting.");

            var userId = OrgAccess.UserId(Context.User);
            var userName = Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "Participant";
            var self = new CallParticipant { ConnectionId = Context.ConnectionId, UserId = userId ?? "", UserName = userName };

            var room = Rooms.GetOrAdd(meetingId, _ => new ConcurrentDictionary<string, CallParticipant>());

            // Snapshot taken BEFORE adding this connection, so the new joiner doesn't see itself
            // in its own "who's already here" list.
            var existingParticipants = room.Values.ToList();

            room[Context.ConnectionId] = self;
            ConnectionMeetingIds[Context.ConnectionId] = meetingId;
            await Groups.AddToGroupAsync(Context.ConnectionId, meetingId);

            // Convention: the new joiner always initiates the WebRTC offer to each existing
            // member, never the reverse — avoids a double-offer glare condition between two
            // peers negotiating at once, with no tie-breaker needed.
            await Clients.Caller.SendAsync("ExistingParticipants", existingParticipants);
            await Clients.OthersInGroup(meetingId).SendAsync("ParticipantJoined", self.ConnectionId, self.UserId, self.UserName);
        }

        public async Task LeaveCall(string meetingId)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, meetingId);
            ConnectionMeetingIds.TryRemove(Context.ConnectionId, out _);

            if (Rooms.TryGetValue(meetingId, out var room))
            {
                room.TryRemove(Context.ConnectionId, out _);
                if (room.IsEmpty) Rooms.TryRemove(meetingId, out _);
            }

            await Clients.Group(meetingId).SendAsync("ParticipantLeft", Context.ConnectionId);
        }

        public async Task SendOffer(string meetingId, string toConnectionId, string sdp)
        {
            if (!await IsAuthorizedAsync(meetingId)) throw new HubException("Not authorized for this meeting.");
            await Clients.Client(toConnectionId).SendAsync("ReceiveOffer", Context.ConnectionId, sdp);
        }

        public async Task SendAnswer(string meetingId, string toConnectionId, string sdp)
        {
            if (!await IsAuthorizedAsync(meetingId)) throw new HubException("Not authorized for this meeting.");
            await Clients.Client(toConnectionId).SendAsync("ReceiveAnswer", Context.ConnectionId, sdp);
        }

        public async Task SendIceCandidate(string meetingId, string toConnectionId, string candidate)
        {
            if (!await IsAuthorizedAsync(meetingId)) throw new HubException("Not authorized for this meeting.");
            await Clients.Client(toConnectionId).SendAsync("ReceiveIceCandidate", Context.ConnectionId, candidate);
        }

        public async Task UpdateMediaState(string meetingId, bool micOn, bool cameraOn)
        {
            if (!await IsAuthorizedAsync(meetingId)) throw new HubException("Not authorized for this meeting.");
            await Clients.OthersInGroup(meetingId).SendAsync("MediaStateChanged", Context.ConnectionId, micOn, cameraOn);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            // A dropped tab must still tell the room to tear down that one peer connection —
            // unlike Chat/Whiteboard, a stale call peer leaves the other participants staring at
            // a frozen video tile rather than just missing a harmless future broadcast.
            if (ConnectionMeetingIds.TryRemove(Context.ConnectionId, out var meetingId))
            {
                if (Rooms.TryGetValue(meetingId, out var room))
                {
                    room.TryRemove(Context.ConnectionId, out _);
                    if (room.IsEmpty) Rooms.TryRemove(meetingId, out _);
                }

                await Clients.Group(meetingId).SendAsync("ParticipantLeft", Context.ConnectionId);
            }

            await base.OnDisconnectedAsync(exception);
        }
    }
}
