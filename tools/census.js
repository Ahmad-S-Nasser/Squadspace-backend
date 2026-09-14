// Schema census — READ ONLY. Writes nothing, ever.
//
// Run:  mongosh "mongodb://localhost:27017/RafeeqyNotesDb" --quiet --file tools/census.js
//
// Purpose (Phase 2, Stage 0):
//   1. The complete live task-status vocabulary. Every distinct value has to map to exactly
//      one slug in the Stage 8 alias table, or normalization is not safe to run.
//   2. The ACTUAL BSON key names. The driver maps a C# member named `Id` onto `_id`, so the
//      nested paths are `Note.Board.Project._id`, not `.Id`. A backfill written against the
//      wrong path matches zero documents and looks like it succeeded.
//   3. How many documents cannot resolve an organization today. Those are already invisible
//      to the caller-visibility filter in NoteTaskController.cs:81 — this counts the damage.
//   4. avgObjSize, as the baseline for the eventual removal of the embedded snapshots.
//
// Re-run this after Stage 8 to confirm the vocabulary collapsed to the five seeded slugs.

function rule(title) {
    print("");
    print("=".repeat(72));
    print("  " + title);
    print("=".repeat(72));
}

function countOrZero(coll, filter) {
    try { return db.getCollection(coll).countDocuments(filter); }
    catch (e) { return "ERR: " + e.message; }
}

rule("1. Task status vocabulary  (NoteTasks.Status)");

const statuses = db.NoteTasks.aggregate([
    { $group: { _id: "$Status", n: { $sum: 1 } } },
    { $sort: { n: -1 } }
]).toArray();

if (statuses.length === 0) {
    print("  (no task documents)");
} else {
    const total = statuses.reduce((a, s) => a + s.n, 0);
    statuses.forEach(s => {
        const label = s._id === null ? "<null>" : s._id === undefined ? "<missing>" : JSON.stringify(s._id);
        print("  " + String(s.n).padStart(7) + "   " + label);
    });
    print("  " + "-".repeat(50));
    print("  " + String(total).padStart(7) + "   total, in " + statuses.length + " distinct value(s)");
}

rule("1b. Sprint status vocabulary  (Sprints.Status) — SEPARATE, do not normalize");

db.Sprints.aggregate([
    { $group: { _id: "$Status", n: { $sum: 1 } } },
    { $sort: { n: -1 } }
]).toArray().forEach(s => {
    print("  " + String(s.n).padStart(7) + "   " + JSON.stringify(s._id));
});

rule("1c. Task priority and category");

db.NoteTasks.aggregate([
    { $group: { _id: "$Priority", n: { $sum: 1 } } }, { $sort: { n: -1 } }
]).toArray().forEach(s => print("  Priority " + String(s.n).padStart(6) + "   " + JSON.stringify(s._id)));

db.NoteTasks.aggregate([
    { $group: { _id: "$Category", n: { $sum: 1 } } }, { $sort: { n: -1 } }
]).toArray().forEach(s => print("  Category " + String(s.n).padStart(6) + "   " + JSON.stringify(s._id)));

rule("2. ACTUAL BSON key names — the backfill depends on these");

["Projects", "Boards", "Notes", "NoteTasks"].forEach(name => {
    const doc = db.getCollection(name).findOne();
    print("");
    print("  " + name + ":");
    if (!doc) { print("    (empty collection)"); return; }
    print("    top level: " + Object.keys(doc).join(", "));
});

print("");
print("  Nested parent-chain probe on one NoteTask:");
const t = db.NoteTasks.findOne({ Note: { $exists: true } });
if (!t) {
    print("    (no task carries an embedded Note)");
} else {
    print("    Note keys:                    " + (t.Note ? Object.keys(t.Note).join(", ") : "<null>"));
    print("    Note.Board keys:              " + (t.Note && t.Note.Board ? Object.keys(t.Note.Board).join(", ") : "<null>"));
    print("    Note.Board.Project keys:      " + (t.Note && t.Note.Board && t.Note.Board.Project ? Object.keys(t.Note.Board.Project).join(", ") : "<null>"));
    const org = t.Note && t.Note.Board && t.Note.Board.Project && t.Note.Board.Project.Organization;
    print("    ...Project.Organization keys: " + (org ? Object.keys(org).join(", ") : "<null>"));
}

rule("3. Documents that cannot resolve an organization today");

print("");
print("  Projects");
print("    total                                  " + countOrZero("Projects", {}));
print("    missing Organization                   " + countOrZero("Projects", { Organization: null }));
print("    missing Organization._id               " + countOrZero("Projects", { "Organization._id": { $exists: false } }));

print("");
print("  Boards  (OrgScope falls back to a project read when the snapshot is stale)");
print("    total                                  " + countOrZero("Boards", {}));
print("    missing Project                        " + countOrZero("Boards", { Project: null }));
print("    missing Project._id  (no fallback key) " + countOrZero("Boards", { "Project._id": { $exists: false } }));
print("    missing Project.Organization._id       " + countOrZero("Boards", { "Project.Organization._id": { $exists: false } }));

print("");
print("  Notes");
print("    total                                  " + countOrZero("Notes", {}));
print("    missing Board._id                      " + countOrZero("Notes", { "Board._id": { $exists: false } }));
print("    missing Board.Project._id              " + countOrZero("Notes", { "Board.Project._id": { $exists: false } }));

print("");
print("  NoteTasks  (OrgIdOfTask has NO fallback — these are invisible in list endpoints)");
print("    total                                  " + countOrZero("NoteTasks", {}));
print("    missing Note                           " + countOrZero("NoteTasks", { Note: null }));
print("    missing Note.Board._id                 " + countOrZero("NoteTasks", { "Note.Board._id": { $exists: false } }));
print("    missing Note.Board.Project._id         " + countOrZero("NoteTasks", { "Note.Board.Project._id": { $exists: false } }));
print("    missing ...Project.Organization._id    " + countOrZero("NoteTasks", { "Note.Board.Project.Organization._id": { $exists: false } }));

rule("4. Size baseline — for the eventual snapshot removal");

["Projects", "Boards", "Notes", "NoteTasks"].forEach(name => {
    try {
        const s = db.getCollection(name).stats();
        print("  " + name.padEnd(12) + " count=" + String(s.count).padStart(7) +
              "  avgObjSize=" + String(s.avgObjSize || 0).padStart(8) + " B" +
              "  size=" + (Math.round((s.size || 0) / 1024)) + " KB");
    } catch (e) {
        print("  " + name.padEnd(12) + " ERR: " + e.message);
    }
});

rule("5. Existing indexes on the four collections");

["Projects", "Boards", "Notes", "NoteTasks"].forEach(name => {
    print("");
    print("  " + name + ":");
    try {
        db.getCollection(name).getIndexes().forEach(ix =>
            print("    " + ix.name + "  " + JSON.stringify(ix.key) +
                  (ix.unique ? "  UNIQUE" : "") +
                  (ix.partialFilterExpression ? "  PARTIAL " + JSON.stringify(ix.partialFilterExpression) : "")));
    } catch (e) { print("    ERR: " + e.message); }
});

print("");
print("Census complete. Nothing was written.");
print("");
