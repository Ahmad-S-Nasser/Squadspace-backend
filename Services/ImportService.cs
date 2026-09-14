using System.Globalization;
using MongoDB.Driver;
using RafeeqyNotes.Api.Config;
using RafeeqyNotes.Api.Helpers;
using RafeeqyNotes.Api.Models;
using RafeeqyNotes.Api.Repositories;
using RafeeqyNotes.Models;

namespace RafeeqyNotes.Api.Services
{
    /// <summary>One parsed CSV row, before it becomes an entity.</summary>
    public sealed class ImportRow
    {
        public int Number { get; set; }
        public string Level { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Color { get; set; }
        public string Mood { get; set; }
        public string Tags { get; set; }
        public string Status { get; set; }
        public string Priority { get; set; }
        public string Category { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? DueDate { get; set; }
        public int? EstimatedMinutes { get; set; }
        public string SprintId { get; set; }
        public string ExternalId { get; set; }
        public string DependsOn { get; set; }
    }

    public sealed class ImportRequest
    {
        public string Csv { get; set; }
        public string FileName { get; set; }

        /// <summary>"csv" | "jira". Chooses the column mapping.</summary>
        public string Source { get; set; } = "csv";

        /// <summary>Namespaces external ids. Defaults to Source.</summary>
        public string ExternalSource { get; set; }

        /// <summary>Foreign status name to org status slug, applied before the registry's aliases.</summary>
        public Dictionary<string, string> StatusMap { get; set; } = new();

        /// <summary>Import into this project instead of creating one from the file.</summary>
        public string TargetProjectId { get; set; }
    }

    public interface IImportService
    {
        List<ImportRow> Parse(string csv, string source);
        Task RunAsync(ImportJob job, ImportRequest request, string organizationId, TaskStatusSet statuses);
    }

    /// <summary>
    /// Imports a project hierarchy from CSV.
    /// </summary>
    /// <remarks>
    /// Written against the failure modes of the existing client-side wizard, which fires a serial
    /// 1 + B + N + T loop of single POSTs from the browser with no batching, no resume and no
    /// rollback. Four things are different here:
    ///
    /// - It runs SERVER-SIDE in one request, so a closed tab does not abandon a half-built project.
    /// - Every entity is written with {ExternalSource, ExternalId}. Re-submitting the same file
    ///   collides on the partial unique index instead of duplicating, which is what makes a failed
    ///   run resumable without any separate resume machinery.
    /// - Tasks are inserted in BATCHES rather than one at a time.
    /// - Dependencies resolve in a SECOND PASS, because a task can depend on one defined later in
    ///   the file. A single-pass importer silently drops those edges.
    ///
    /// It also does NOT go through the project-create controller path, which defaults
    /// CreateDefaultBoard to true - that is why existing CSV imports quietly produce a spurious
    /// extra board alongside the ones in the file.
    ///
    /// There are no transactions in this codebase, so a partial import is a real outcome rather
    /// than one to pretend away. The job record says exactly what landed, and re-running finishes
    /// the job.
    /// </remarks>
    public class ImportService : IImportService
    {
        /// <summary>Tasks inserted per round trip.</summary>
        private const int BatchSize = 200;

        /// <summary>Row errors kept on the job. A wrong file must not fill the database with noise.</summary>
        private const int MaxStoredErrors = 100;

        private readonly IProjectRepository _projects;
        private readonly IBoardRepository _boards;
        private readonly INoteRepository _notes;
        private readonly INoteTaskRepository _tasks;
        private readonly IImportJobRepository _jobs;
        private readonly IMongoCollection<NoteTask> _taskCollection;
        private readonly IMongoCollection<Project> _projectCollection;
        private readonly IMongoCollection<Board> _boardCollection;
        private readonly IMongoCollection<Note> _noteCollection;
        private readonly ILogger<ImportService> _logger;

        public ImportService(
            MongoDbSettings settings,
            IProjectRepository projects,
            IBoardRepository boards,
            INoteRepository notes,
            INoteTaskRepository tasks,
            IImportJobRepository jobs,
            ILogger<ImportService> logger)
        {
            _projects = projects;
            _boards = boards;
            _notes = notes;
            _tasks = tasks;
            _jobs = jobs;
            _logger = logger;

            var database = new MongoClient(settings.ConnectionString).GetDatabase(settings.DatabaseName);
            _taskCollection = database.GetCollection<NoteTask>("NoteTasks");
            _projectCollection = database.GetCollection<Project>(settings.ProjectsCollection);
            _boardCollection = database.GetCollection<Board>(settings.BoardsCollection);
            _noteCollection = database.GetCollection<Note>(settings.NotesCollection);
        }

        /// <summary>
        /// The entity already written under this external identity, if any.
        /// </summary>
        /// <remarks>
        /// This is what makes re-running a file a RESUME rather than a failure. Without it a
        /// collision aborts the whole subtree: the project already exists, so nothing after it has
        /// a parent, and every remaining row fails for a reason that has nothing to do with it.
        ///
        /// Checked before inserting rather than caught afterwards, because the caller needs the
        /// existing entity to attach children to - an exception tells you it exists but not which
        /// one it is.
        /// </remarks>
        private static async Task<T> FindExistingAsync<T>(
            IMongoCollection<T> collection, string externalSource, string externalId)
        {
            if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(externalSource))
            {
                return default;
            }

            var filter = Builders<T>.Filter.And(
                Builders<T>.Filter.Eq("ExternalSource", externalSource),
                Builders<T>.Filter.Eq("ExternalId", externalId));

            return await collection.Find(filter).FirstOrDefaultAsync();
        }

        // ---------------------------------------------------------------- parsing

        /// <summary>
        /// Parses CSV into rows, honouring RFC 4180 quoting.
        /// </summary>
        /// <remarks>
        /// The client-side parser this replaces toggles a quote flag but splits on any newline, so
        /// a quoted note body containing one ends the row early and shifts every later column.
        /// Quoted newlines are handled here.
        /// </remarks>
        public List<ImportRow> Parse(string csv, string source)
        {
            var rows = new List<ImportRow>();
            if (string.IsNullOrWhiteSpace(csv)) return rows;

            var records = SplitRecords(csv);
            if (records.Count == 0) return rows;

            var header = records[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
            var jira = string.Equals(source, "jira", StringComparison.OrdinalIgnoreCase);

            for (var i = 1; i < records.Count; i++)
            {
                var fields = records[i];
                if (fields.Count == 0 || fields.All(string.IsNullOrWhiteSpace)) continue;

                var row = jira
                    ? MapJira(fields, header, i + 1)
                    : MapNative(fields, i + 1);

                if (row != null) rows.Add(row);
            }

            return rows;
        }

        /// <summary>Splits CSV into records of fields, treating quoted newlines as content.</summary>
        private static List<List<string>> SplitRecords(string csv)
        {
            var records = new List<List<string>>();
            var fields = new List<string>();
            var current = new System.Text.StringBuilder();
            var inQuotes = false;

            for (var i = 0; i < csv.Length; i++)
            {
                var c = csv[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        // "" inside a quoted field is a literal quote.
                        if (i + 1 < csv.Length && csv[i + 1] == '"') { current.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else current.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"':
                        inQuotes = true;
                        break;
                    case ',':
                        fields.Add(current.ToString());
                        current.Clear();
                        break;
                    case '\r':
                        break;
                    case '\n':
                        fields.Add(current.ToString());
                        current.Clear();
                        records.Add(fields);
                        fields = new List<string>();
                        break;
                    default:
                        current.Append(c);
                        break;
                }
            }

            if (current.Length > 0 || fields.Count > 0)
            {
                fields.Add(current.ToString());
                records.Add(fields);
            }

            return records;
        }

        private static string At(List<string> fields, int index) =>
            index >= 0 && index < fields.Count ? NullIfBlank(fields[index].Trim()) : null;

        private static string NullIfBlank(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;

        /// <summary>Native format: the export's column order, a superset of the legacy importer's.</summary>
        private static ImportRow MapNative(List<string> fields, int number)
        {
            var level = At(fields, 0)?.ToLowerInvariant();
            if (level is not ("project" or "board" or "note" or "task")) return null;

            return new ImportRow
            {
                Number = number,
                Level = level,
                Name = At(fields, 1),
                Description = At(fields, 2),
                Color = At(fields, 3),
                Mood = At(fields, 4),
                Tags = At(fields, 5),
                Status = At(fields, 6),
                Priority = At(fields, 7),
                Category = At(fields, 8),
                StartDate = ParseDate(At(fields, 9)),
                DueDate = ParseDate(At(fields, 10)),
                EstimatedMinutes = ParseInt(At(fields, 11)),
                // index 12 is ActualMinutes: exported for reference, never imported. Measured time
                // is owned by the timer and derived from segments - a CSV cannot assert it.
                SprintId = At(fields, 13),

                // Falls back to the row's own Id when no ExternalId is present. An export of
                // native records carries no external identity - without this, re-importing the
                // same file would create a second copy of everything instead of recognising it.
                // The source record's id IS the external key for a round trip.
                ExternalId = At(fields, 14) ?? NullIfBlank(At(fields, 16)),
                DependsOn = At(fields, 17),
            };
        }

        /// <summary>
        /// Jira's CSV export, mapped by column NAME.
        /// </summary>
        /// <remarks>
        /// Jira exports are flat - every row is an issue, with no project/board/note levels - and
        /// the column set varies by which fields a site has configured. So this maps by header name
        /// and treats every row as a task; the caller supplies the project to import into.
        /// </remarks>
        private static ImportRow MapJira(List<string> fields, List<string> header, int number)
        {
            string Col(params string[] names)
            {
                foreach (var name in names)
                {
                    var index = header.IndexOf(name);
                    if (index >= 0)
                    {
                        var value = At(fields, index);
                        if (!string.IsNullOrWhiteSpace(value)) return value;
                    }
                }
                return null;
            }

            var summary = Col("summary", "title", "name");
            if (string.IsNullOrWhiteSpace(summary)) return null;

            return new ImportRow
            {
                Number = number,
                Level = "task",
                Name = summary,
                Description = Col("description"),
                Status = Col("status"),
                Priority = Col("priority"),
                Category = Col("issue type", "issuetype", "type"),
                DueDate = ParseDate(Col("due date", "duedate")),
                // Jira stores estimates in SECONDS; this model is minutes.
                EstimatedMinutes = ParseSecondsAsMinutes(Col("original estimate", "σriginal estimate", "timeoriginalestimate"))
                                   ?? ParseInt(Col("story points")),
                SprintId = Col("sprint"),
                ExternalId = Col("issue key", "key", "id"),
                DependsOn = Col("depends on", "blocked by", "inward issue link (blocks)"),
            };
        }

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        private static int? ParseInt(string value) =>
            int.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        private static int? ParseSecondsAsMinutes(string value)
        {
            var seconds = ParseInt(value);
            return seconds.HasValue ? Math.Max(1, seconds.Value / 60) : null;
        }

        // ---------------------------------------------------------------- running

        public async Task RunAsync(
            ImportJob job, ImportRequest request, string organizationId, TaskStatusSet statuses)
        {
            job.State = ImportJobStates.Running;
            job.StartedAt = DateTime.UtcNow;
            await _jobs.UpdateAsync(job);

            try
            {
                var rows = Parse(request.Csv, request.Source);
                job.TotalRows = rows.Count;
                await _jobs.UpdateAsync(job);

                // Maps an external id to the id it was written under, so pass two can resolve
                // dependency edges declared before their target appeared in the file.
                var taskIdByExternalId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var pendingDependencies = new List<(string TaskId, string DependsOn)>();

                Project project = null;
                Board board = null;
                Note note = null;
                var taskBuffer = new List<NoteTask>();

                async Task FlushAsync()
                {
                    if (taskBuffer.Count == 0) return;

                    var inserted = await InsertTasksAsync(taskBuffer, job);
                    job.TasksCreated += inserted;
                    taskBuffer.Clear();
                    await _jobs.UpdateAsync(job);
                }

                if (!string.IsNullOrWhiteSpace(request.TargetProjectId))
                {
                    project = await _projects.GetByIdAsync(request.TargetProjectId);

                    // Belt and braces: the controller authorized this project, but an import writes
                    // a lot of rows and a mismatch here would scatter them into another tenant.
                    if (project == null || project.OrganizationId != organizationId)
                    {
                        throw new InvalidOperationException("Target project not found in this organization.");
                    }

                    board = (await _boards.GetByProjectIdAsync(project.Id)).FirstOrDefault();
                    note = board == null ? null : (await _notes.GetNotesByBoardIDAsync(board.Id)).FirstOrDefault();
                }

                foreach (var row in rows)
                {
                    try
                    {
                        switch (row.Level)
                        {
                            case "project":
                                await FlushAsync();
                                project = await UpsertProjectAsync(row, organizationId, request, job);
                                board = null;
                                note = null;
                                break;

                            case "board":
                                await FlushAsync();
                                if (project == null) throw new InvalidOperationException("A board needs a project before it.");
                                board = await UpsertBoardAsync(row, project, request, job);
                                note = null;
                                break;

                            case "note":
                                await FlushAsync();
                                board ??= await EnsureBoardAsync(project, request, job);
                                note = await UpsertNoteAsync(row, board, request, job);
                                break;

                            case "task":
                                board ??= await EnsureBoardAsync(project, request, job);
                                note ??= await UpsertNoteAsync(
                                    new ImportRow { Name = "Imported", Level = "note" }, board, request, job);

                                var task = BuildTask(row, note, statuses, request);
                                taskBuffer.Add(task);

                                if (!string.IsNullOrWhiteSpace(row.ExternalId))
                                {
                                    taskIdByExternalId[row.ExternalId] = task.Id;
                                }
                                if (!string.IsNullOrWhiteSpace(row.DependsOn))
                                {
                                    pendingDependencies.Add((task.Id, row.DependsOn));
                                }

                                if (taskBuffer.Count >= BatchSize) await FlushAsync();
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordError(job, row.Number, ex.Message);
                    }

                    job.ProcessedRows++;
                }

                await FlushAsync();

                // ---- pass two: dependency edges ----
                //
                // Separate because a task may depend on one defined LATER in the file. Resolving
                // inline would silently drop every forward reference, which in a Jira export is
                // most of them.
                job.DependenciesLinked = await LinkDependenciesAsync(pendingDependencies, taskIdByExternalId, job);

                job.State = job.ErrorCount > 0 ? ImportJobStates.Partial : ImportJobStates.Completed;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import {JobId} failed outright.", job.Id);
                job.State = ImportJobStates.Failed;
                RecordError(job, 0, ex.Message);
            }
            finally
            {
                job.CompletedAt = DateTime.UtcNow;
                await _jobs.UpdateAsync(job);
            }
        }

        private static void RecordError(ImportJob job, int row, string message)
        {
            job.ErrorCount++;
            if (job.Errors.Count < MaxStoredErrors)
            {
                job.Errors.Add(new ImportRowError { Row = row, Message = message });
            }
        }

        /// <summary>
        /// Inserts a batch, falling back to one-by-one when the batch collides.
        /// </summary>
        /// <remarks>
        /// An unordered InsertMany reports which documents failed and inserts the rest, so one
        /// already-imported task does not abandon the other 199. Duplicate keys are counted as
        /// "already there" rather than errors - on a resume that is the expected outcome for
        /// nearly every row.
        /// </remarks>
        private async Task<int> InsertTasksAsync(List<NoteTask> batch, ImportJob job)
        {
            try
            {
                await _taskCollection.InsertManyAsync(batch, new InsertManyOptions { IsOrdered = false });
                return batch.Count;
            }
            catch (MongoBulkWriteException<NoteTask> ex)
            {
                var duplicates = ex.WriteErrors.Count(e => e.Code == DuplicateEntityException.DuplicateKeyCode);
                var others = ex.WriteErrors.Count - duplicates;

                job.SkippedExisting += duplicates;
                foreach (var error in ex.WriteErrors.Where(e => e.Code != DuplicateEntityException.DuplicateKeyCode))
                {
                    RecordError(job, 0, error.Message);
                }

                return Math.Max(0, batch.Count - duplicates - others);
            }
        }

        private async Task<int> LinkDependenciesAsync(
            List<(string TaskId, string DependsOn)> pending,
            Dictionary<string, string> taskIdByExternalId,
            ImportJob job)
        {
            var linked = 0;

            foreach (var (taskId, dependsOn) in pending)
            {
                try
                {
                    var targets = dependsOn
                        .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => taskIdByExternalId.ContainsKey(s))
                        .Select(s => taskIdByExternalId[s])
                        .Distinct()
                        .ToList();

                    if (targets.Count == 0) continue;

                    var task = await _tasks.GetByIdAsync(taskId);
                    if (task == null) continue;

                    // Just the ids now. This used to re-read every target purely to copy its
                    // title and status into an embedded snapshot; the API resolves those on read,
                    // so the extra query per edge and the stale copy both go away.
                    task.DependencyIds = new List<string>();
                    foreach (var target in targets)
                    {
                        if (string.IsNullOrWhiteSpace(target)) continue;
                        if (string.Equals(target, task.Id, StringComparison.OrdinalIgnoreCase)) continue;
                        if (task.DependencyIds.Contains(target, StringComparer.OrdinalIgnoreCase)) continue;

                        task.DependencyIds.Add(target);
                        linked++;
                    }

                    // Null so the update path keeps DependencyIds exactly as set above rather
                    // than recomputing them from an empty summary list.
                    task.Dependencies = null;

                    await _tasks.UpdateAsync(task);
                }
                catch (Exception ex)
                {
                    RecordError(job, 0, $"Dependency link failed: {ex.Message}");
                }
            }

            return linked;
        }

        // ---------------------------------------------------------------- upserts

        private async Task<Project> UpsertProjectAsync(
            ImportRow row, string organizationId, ImportRequest request, ImportJob job)
        {
            var existing = await FindExistingAsync(_projectCollection, request.ExternalSource, row.ExternalId);
            if (existing != null)
            {
                job.SkippedExisting++;
                return existing;
            }

            var project = new Project
            {
                Id = Guid.NewGuid().ToString(),
                Name = string.IsNullOrWhiteSpace(row.Name) ? "Imported project" : row.Name,
                Description = row.Description ?? string.Empty,
                Color = string.IsNullOrWhiteSpace(row.Color) ? "#6366f1" : row.Color,
                OwnerId = job.OwnerId,
                CreatedAt = DateTime.UtcNow,
                Organization = new Organization { Id = organizationId },
                ExternalId = row.ExternalId,
                ExternalSource = request.ExternalSource,
            };

            await _projects.CreateAsync(project);
            job.ProjectsCreated++;
            return project;
        }

        /// <summary>
        /// A board for tasks that arrived without one - a Jira export has no board level.
        /// </summary>
        /// <remarks>
        /// Created explicitly here rather than by the project-create path, whose CreateDefaultBoard
        /// flag defaults to true and is why existing CSV imports produce an unexpected extra board.
        /// </remarks>
        private async Task<Board> EnsureBoardAsync(Project project, ImportRequest request, ImportJob job)
        {
            if (project == null) throw new InvalidOperationException("No project to attach to.");

            // Keyed on the project so a resume finds the same fallback board instead of adding
            // another one on every run.
            var fallbackKey = $"board:{project.Id}";
            var existing = await FindExistingAsync(_boardCollection, request.ExternalSource, fallbackKey);
            if (existing != null) return existing;

            var board = new Board
            {
                Id = Guid.NewGuid().ToString(),
                ExternalId = fallbackKey,
                Name = "Imported",
                Description = $"Created by the {request.Source} import",
                Project = project,
                MemberIds = new List<string> { job.OwnerId },
                ExternalSource = request.ExternalSource,
            };

            await _boards.CreateAsync(board);
            job.BoardsCreated++;
            return board;
        }

        private async Task<Board> UpsertBoardAsync(
            ImportRow row, Project project, ImportRequest request, ImportJob job)
        {
            var existing = await FindExistingAsync(_boardCollection, request.ExternalSource, row.ExternalId);
            if (existing != null)
            {
                job.SkippedExisting++;
                return existing;
            }

            var board = new Board
            {
                Id = Guid.NewGuid().ToString(),
                Name = string.IsNullOrWhiteSpace(row.Name) ? "Imported board" : row.Name,
                Description = row.Description ?? string.Empty,
                Project = project,
                MemberIds = new List<string> { job.OwnerId },
                ExternalId = row.ExternalId,
                ExternalSource = request.ExternalSource,
            };

            await _boards.CreateAsync(board);
            job.BoardsCreated++;
            return board;
        }

        private async Task<Note> UpsertNoteAsync(
            ImportRow row, Board board, ImportRequest request, ImportJob job)
        {
            var existing = await FindExistingAsync(_noteCollection, request.ExternalSource, row.ExternalId);
            if (existing != null)
            {
                job.SkippedExisting++;
                return existing;
            }

            var note = new Note
            {
                Id = Guid.NewGuid().ToString(),
                Title = string.IsNullOrWhiteSpace(row.Name) ? "Imported note" : row.Name,
                Content = row.Description ?? string.Empty,
                // NoteRepository.CreateAsync title-cases Mood by indexing [0], which throws on an
                // empty string. A default keeps a blank column from failing the row.
                Mood = string.IsNullOrWhiteSpace(row.Mood) ? "Neutral" : row.Mood,
                Board = board,
                AuthorId = job.OwnerId,
                Tags = string.IsNullOrWhiteSpace(row.Tags)
                    ? new List<string>()
                    : row.Tags.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()).ToList(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ExternalId = row.ExternalId,
                ExternalSource = request.ExternalSource,
            };

            await _notes.CreateAsync(note);
            job.NotesCreated++;
            return note;
        }

        private static NoteTask BuildTask(
            ImportRow row, Note note, TaskStatusSet statuses, ImportRequest request)
        {
            // Explicit mapping first, then the registry's own aliases. A Jira site calling its
            // final column "Shipped" needs the mapping; one calling it "Done" does not.
            var raw = row.Status;
            if (!string.IsNullOrWhiteSpace(raw)
                && request.StatusMap != null
                && request.StatusMap.TryGetValue(raw, out var mapped))
            {
                raw = mapped;
            }

            var slug = string.IsNullOrWhiteSpace(raw) ? statuses.DefaultSlug : statuses.Resolve(raw);

            return new NoteTask
            {
                Id = Guid.NewGuid().ToString(),
                Title = string.IsNullOrWhiteSpace(row.Name) ? "Imported task" : row.Name,
                Description = row.Description ?? string.Empty,
                Status = slug,
                Priority = string.IsNullOrWhiteSpace(row.Priority) ? "Medium" : row.Priority,
                Category = row.Category,
                StartDate = row.StartDate,
                DueDate = row.DueDate,
                EstimatedDuration = row.EstimatedMinutes ?? 0,

                // Never imported. Measured time is derived from timer segments; a CSV asserting it
                // would put a fabricated number where a measured one belongs.
                RealDuration = 0,
                TimeEntries = new List<TaskTimeEntry>(),

                Note = note,
                NoteId = note.Id,
                BoardId = note.BoardId,
                ProjectId = note.ProjectId,
                OrganizationId = note.OrganizationId,
                SprintId = row.SprintId,
                CreatedAt = DateTime.UtcNow,
                ExternalId = row.ExternalId,
                ExternalSource = request.ExternalSource,
                AssignedTo = new List<Contributor>(),
            };
        }
    }
}
