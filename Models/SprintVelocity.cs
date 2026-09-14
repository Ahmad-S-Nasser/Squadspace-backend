namespace RafeeqyNotes.Api.Models
{
    public class SprintVelocity
    {
        public string SprintId { get; set; }
        public string SprintName { get; set; }
        public int PlannedPoints { get; set; }     // Based on task estimatedDuration
        public int CompletedPoints { get; set; }   // Tasks marked "done"
        public int TaskCount { get; set; }
        public int CompletedTasks { get; set; }
    }
}
