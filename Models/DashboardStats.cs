using System.Collections.Generic;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Models
{
    /*
     * 1. DashboardStats
    json
    {
      "totalProjects": 8,
      "totalBoards": 24,
      "totalNotes": 156,
      "totalTasks": 89,
      "totalMeetings": 32,
      "totalContributors": 12,
      "tasksCompletedThisWeek": 15,
      "meetingsThisWeek": 5
    }
    2. TaskAnalytics
    json
    {
      "statusBreakdown": [
        { "status": "todo", "label": "To Do", "count": 28 },
        { "status": "in_progress", "label": "In Progress", "count": 24 },
        { "status": "in_review", "label": "In Review", "count": 15 },
        { "status": "done", "label": "Done", "count": 22 }
      ],
      NOTE: "status" is the canonical slug from the organization's status registry
      (GET /api/Organization/{id}/task-statuses), not free text. Colour is no longer
      invented here - it belongs to the status definition. Buckets are grouped on the
      resolved slug, so legacy spellings no longer split one status into several.
      [ 
      ],
      "priorityBreakdown": [
        { "priority": "Low", "count": 20, "color": "hsl(220, 14%, 60%)" },
        { "priority": "Medium", "count": 35, "color": "hsl(48, 96%, 53%)" },
        { "priority": "High", "count": 25, "color": "hsl(25, 95%, 53%)" },
        { "priority": "Urgent", "count": 9, "color": "hsl(0, 72%, 51%)" }
      ],
      "completionTrend": [
        { "date": "Mon", "completed": 5, "created": 8 },
        { "date": "Tue", "completed": 8, "created": 6 },
        { "date": "Wed", "completed": 6, "created": 10 }
      ],
      "averageCompletionTime": 24,
      "overdueCount": 7
    }
    3. MeetingAnalytics
    json
    {
      "upcomingCount": 8,
      "pastCount": 24,
      "thisWeekCount": 5,
      "averageAttendees": 4.5,
      "meetingTrend": [
        { "date": "Week 1", "scheduled": 6, "completed": 5 },
        { "date": "Week 2", "scheduled": 8, "completed": 8 },
        { "date": "Week 3", "scheduled": 5, "completed": 4 }
      ]
    }
    4. TeamActivity
    json
    {
      "activities": [
        {
          "id": "1",
          "type": "task_completed",
          "userId": "1",
          "userName": "Sarah Chen",
          "userAvatar": "https://...",
          "description": "completed a task",
          "timestamp": "2025-12-28T01:30:00Z",
          "metadata": {
            "itemId": "task-123",
            "itemName": "Update dashboard UI",
            "projectName": "Team Board"
          }
        }
      ],
      "totalCount": 5
    }
    Activity Types: task_created, task_completed, note_created, meeting_scheduled, board_created, project_created, comment_added
     * */

    public class DashboardStats
    {
        public int TotalProjects { get; set; }
        public int TotalTasks { get; set; }
        public int CompletedTasks { get; set; }
        public int UpcomingMeetings { get; set; }
        public int ActiveMembers { get; set; }
    }
    public class TaskAnalytics
    {
        public List<StatusCount> ByStatus { get; set; }
        public List<PriorityCount> ByPriority { get; set; }
        public double CompletionRate { get; set; }
        public List<NoteTask> Tasks { get; set; }
    }

    public class StatusCount
    {
        /// <summary>Canonical slug, resolved through the organization's status registry.</summary>
        public string Status { get; set; }

        /// <summary>Display text for that slug. Previously the client had to guess it.</summary>
        public string Label { get; set; }

        public int Count { get; set; }
    }

    public class PriorityCount
    {
        public string Priority { get; set; }
        public int Count { get; set; }
    }
    public class TeamActivity
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Description { get; set; }
        public string UserId { get; set; }
        public string UserName { get; set; }
        public DateTime CreatedAt { get; set; }
    }
    public class MeetingAnalytics
    {
        public int TotalMeetings { get; set; }
        public int UpcomingMeetings { get; set; }
        public double AverageDurationMinutes { get; set; }
    }
}
