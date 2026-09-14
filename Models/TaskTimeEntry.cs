using MongoDB.Bson.Serialization.Attributes;

namespace RafeeqyNotes.Api.Models
{
    /// <summary>
    /// One continuous stretch of work by one person on one task.
    /// </summary>
    /// <remarks>
    /// SEGMENTS, NOT A COUNTER. A single accumulating integer cannot survive a refresh, a crash, a
    /// closed laptop, or the same person working from two devices - each of those either loses
    /// time or double-counts it, and neither failure is visible afterwards. A list of closed
    /// intervals can be recomputed from scratch at any point, which is what makes the total
    /// auditable rather than merely plausible.
    ///
    /// Both timestamps are UTC and both are stamped by the SERVER. This is billing-adjacent data
    /// and the first thing anyone would fudge, so a client-supplied duration or clock is never
    /// trusted - the client says "start" and "stop", the server decides when that was.
    ///
    /// <see cref="Source"/> is what makes the data honest later: the estimate-vs-actual
    /// calibration this roadmap builds toward is only meaningful if measured time can be told
    /// apart from time somebody typed in from memory.
    /// </remarks>
    [BsonIgnoreExtraElements]
    public class TaskTimeEntry
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Who logged it. From the JWT, never from the request body.</summary>
        public string UserId { get; set; }

        /// <summary>
        /// Display name at the time of logging, denormalized for reports.
        /// </summary>
        /// <remarks>
        /// Same trade-off the existing TaskActivityActor already makes here: a report over a year
        /// of segments should not need a join per row, and a name that has since changed is not
        /// worth a lookup.
        /// </remarks>
        public string UserName { get; set; }

        /// <summary>When the stretch began. Server clock, UTC.</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>When it ended. Null means still running.</summary>
        public DateTime? EndedAt { get; set; }

        /// <summary>"timer" (measured) or "manual" (entered by hand).</summary>
        public string Source { get; set; } = TaskTimeEntrySources.Timer;

        /// <summary>Optional note, mainly for manual entries explaining what they cover.</summary>
        public string Note { get; set; }

        /// <summary>
        /// Set when the runaway cap closed this segment rather than a person doing so.
        /// </summary>
        /// <remarks>
        /// A timer left running overnight poisons the dataset worse than the guess it replaced, so
        /// it is capped. Flagging which segments were capped keeps that visible instead of
        /// silently inventing a plausible-looking number.
        /// </remarks>
        public bool AutoClosed { get; set; }
    }

    public static class TaskTimeEntrySources
    {
        /// <summary>Measured by the timer.</summary>
        public const string Timer = "timer";

        /// <summary>Entered by hand, because people forget to press start.</summary>
        public const string Manual = "manual";
    }
}
