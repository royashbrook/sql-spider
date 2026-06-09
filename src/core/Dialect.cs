// ---------------------------------------------------------------------------
// Dialect layer: the ONE seam where sql-spider is SQL-dialect-specific.
// ---------------------------------------------------------------------------
// Everything else in the tool -- graph assembly, the frontier, the spider loop
// (seed/generate/reverse/absorb), the connected-components audit, lineage roll-up,
// and viz -- is dialect-NEUTRAL: it works on the facts below, never on SQL text.
//
// A dialect's only job is to turn a corpus of *.sql files into those facts. Add a
// dialect by implementing IDialectExtractor (or by enabling another SqlParserCS
// dialect in GenericSqlExtractor); the rest of the pipeline is untouched.


// the dependency facts the neutral core needs out of one parsed .sql file.
// these are exactly the per-file facts the original T-SQL extractor computed inline;
// pulling them behind a record makes the SQL->facts step swappable per dialect.
namespace SqlSpider;

public sealed class FileFacts
{
    public string File = "";   // source-file label (used as each node's source_file)

    // every object DEFINED in this file (we hold its definition): name -> kind
    // (table/view/proc/function/trigger). a file may define many objects.
    public List<Container> Containers = new();

    // declared FOREIGN KEY edges (owner-table -> referenced-table), file-scoped, so
    // ALTER-TABLE-style FKs are caught too. keeps a pure-DDL extract connected.
    public List<(string from, string to)> FkEdges = new();

    // join-key / lineage layer is only emitted by dialects that resolve columns
    // (T-SQL). dialects without that fidelity (SQLite via SqlParserCS) leave these
    // empty -- that is correct, not a bug; the rest of the pipeline copes.
    public List<(string tbl, string col)> ColumnHits = new();                                  // real-table column usages (weight)
    public List<(string srcTbl, string srcCol, string tgtTbl, string tgtCol)> Lineage = new(); // update-set col->col flow
}

// one defined object plus the dependencies extracted from ITS subtree only, so a
// table read inside proc A is attributed to A, not to proc B in the same file.
public sealed class Container
{
    public string Name = "";
    public string Kind = "";                 // table | view | proc | function | trigger | script
    public bool   Defined;                   // true => we HOLD this object's definition (a real create-statement,
                                             // not a fallback bare-script). drives the frontier: referenced-but-
                                             // -undefined objects are the spider's next pull.
    public List<string> Reads  = new();      // tables this container reads
    public List<string> Writes = new();      // tables this container writes
    public List<string> Calls  = new();      // procs this container executes
    public List<string> Funcs  = new();      // user functions this container calls
}

// the corpus-level result. per-file facts plus the two cross-file sets the neutral
// lineage/column logic keys on (which names are REAL declared tables vs CTE/derived
// phantoms), and the running parse-failure count.
public sealed class CorpusFacts
{
    public List<FileFacts> Files = new();
    public HashSet<string> RealTables = new();   // names we have CREATE TABLE DDL for
    public HashSet<string> CteNames   = new();   // named CTEs (legit subquery sources)
    public int ParseFail;                        // files still erroring after any dialect fallback
}

// the dialect seam. given the corpus's *.sql files (already discovered + ordered by
// the neutral core), return the dependency facts. taking the whole file list -- not
// one file -- lets a dialect do cross-file resolution passes (T-SQL builds a
// column-owner index across all files before attributing unqualified columns).
public interface IDialectExtractor
{
    CorpusFacts Extract(IReadOnlyList<string> files);
}
