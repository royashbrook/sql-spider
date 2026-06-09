using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using System.Text.RegularExpressions;

namespace SqlSpider;


// ---------------------------------------------------------------------------
// Generic SQL dialect: SqlParserCS (https://www.nuget.org/packages/SqlParserCS/),
// a .NET port of sqlparser-rs. One code path covers every dialect SqlParserCS
// understands (SQLite, MySQL, Postgres, ...); pick the dialect at construction.
// ---------------------------------------------------------------------------
// Wired here for SQLite. SQLite has NO stored procedures and NO SQL functions, so
// the calls/funcs/proc-def facts simply never appear -- that is correct for the
// dialect, not a gap. We emit:
//   CREATE TABLE   -> a table node + its FOREIGN KEY edges (table-level and inline
//                     column-level) as fk edges to the referenced table.
//   CREATE VIEW    -> a view node + `references` edges to every table its query reads.
//   CREATE INDEX   -> a `references` edge from the index's table back to itself is
//                     pointless, so an index just reinforces its table exists (the
//                     table node is created/kept); no extra edge needed.
//   CREATE TRIGGER -> a trigger node + read/write edges to the tables it touches.
//
// SqlParserCS API friction worth knowing: its SQLite dialect does NOT parse SQLite's
// CREATE TRIGGER (it routes CREATE TRIGGER to a constraint-trigger parser and throws).
// So triggers are handled by a small regex fallback below, and a statement that fails
// to parse is isolated (we re-parse the file statement-by-statement) so one bad
// statement never sinks the whole file.
public sealed class GenericSqlExtractor : IDialectExtractor
{
    readonly Dialect _dialect;
    public GenericSqlExtractor(Dialect dialect) { _dialect = dialect; }

    // bracketed/quoted identifiers -> bare lowercase name; schema-qualified -> last part.
    static string Last(ObjectName o) => o.Values[^1].Value.ToLowerInvariant();

    public CorpusFacts Extract(IReadOnlyList<string> files)
    {
        var result = new CorpusFacts();
        foreach (var f in files)
        {
            var fname = Path.GetFileName(f);
            var ff = new FileFacts { File = fname };
            var sql = File.ReadAllText(f);

            // try the whole file first; if any statement is unparseable (e.g. a SQLite
            // trigger), fall back to statement-by-statement so the rest still lands.
            var statements = ParseResilient(sql, ff, result, out var triggerChunks);
            foreach (var s in statements) Handle(s, ff);
            foreach (var chunk in triggerChunks) HandleTrigger(chunk, ff);

            result.Files.Add(ff);
        }
        return result;
    }

    // parse the file; on failure, split on top-level `;` and parse each statement alone so
    // one bad statement is isolated. statements we still can't parse but that look like a
    // CREATE TRIGGER are handed back as raw text for the regex fallback; anything else that
    // won't parse increments ParseFail.
    List<Statement> ParseResilient(string sql, FileFacts ff, CorpusFacts result, out List<string> triggerChunks)
    {
        triggerChunks = new List<string>();
        try { return new Parser().ParseSql(sql, _dialect).ToList(); }
        catch { /* fall through to per-statement */ }

        var ok = new List<Statement>();
        foreach (var stmt in SplitStatements(sql))
        {
            var text = stmt.Trim();
            if (text.Length == 0) continue;
            try { ok.AddRange(new Parser().ParseSql(text + ";", _dialect)); }
            catch
            {
                if (Regex.IsMatch(text, @"^\s*create\s+(temp(orary)?\s+)?trigger", RegexOptions.IgnoreCase))
                    triggerChunks.Add(text);     // library can't parse SQLite triggers -> regex fallback
                else
                    result.ParseFail++;          // genuinely unparseable statement
            }
        }
        return ok;
    }

    void Handle(Statement s, FileFacts ff)
    {
        switch (s)
        {
            case Statement.CreateTable ct:
            {
                var owner = Last(ct.Element.Name);
                ff.Containers.Add(new Container { Name = owner, Kind = "table", Defined = true });
                // table-level FOREIGN KEY constraints
                if (ct.Element.Constraints != null)
                    foreach (var c in ct.Element.Constraints)
                        if (c is TableConstraint.ForeignKey fk)
                        {
                            var to = Last(fk.ForeignTable);
                            if (to != owner) ff.FkEdges.Add((owner, to));
                        }
                // inline column-level FOREIGN KEY (col INTEGER REFERENCES other(id))
                if (ct.Element.Columns != null)
                    foreach (var col in ct.Element.Columns)
                        if (col.Options != null)
                            foreach (var o in col.Options)
                                if (o.Option is ColumnOption.ForeignKey cfk)
                                {
                                    var to = Last(cfk.Name);
                                    if (to != owner) ff.FkEdges.Add((owner, to));
                                }
                break;
            }
            case Statement.CreateView cv:
            {
                var name = Last(cv.Name);
                var c = new Container { Name = name, Kind = "view", Defined = true };
                // every table the view's query reads = a `references` edge
                var rels = new RelationCollector();
                ((IElement)cv.Query).Visit(rels);
                foreach (var t in rels.Tables) if (t != name) c.Reads.Add(t);
                ff.Containers.Add(c);
                break;
            }
            case Statement.CreateIndex ci:
            {
                // an index just confirms its table exists. emit the table as a (referenced,
                // not defined) container so a lone index keeps its table in the graph; the
                // table's own CREATE TABLE (if present) wins the "defined" merge in the core.
                var tbl = Last(ci.Element.TableName);
                ff.Containers.Add(new Container { Name = tbl, Kind = "table", Defined = false });
                break;
            }
        }
    }

    // CREATE TRIGGER regex fallback (SqlParserCS can't parse SQLite triggers). Pull the trigger
    // name, the table it fires ON (a read of that table), and any INSERT/UPDATE/DELETE/FROM/JOIN
    // tables in the body (writes for I/U/D targets, reads for FROM/JOIN sources).
    static void HandleTrigger(string text, FileFacts ff)
    {
        var nameM = Regex.Match(text, @"create\s+(?:temp(?:orary)?\s+)?trigger\s+(?:if\s+not\s+exists\s+)?[\[""`]?(?<n>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        var onM   = Regex.Match(text, @"\son\s+[\[""`]?(?<t>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase);
        if (!nameM.Success) return;
        var c = new Container { Name = nameM.Groups["n"].Value.ToLowerInvariant(), Kind = "trigger", Defined = true };
        if (onM.Success) c.Reads.Add(onM.Groups["t"].Value.ToLowerInvariant());   // the table it watches
        foreach (Match m in Regex.Matches(text, @"\b(?:insert\s+into|update|delete\s+from)\s+[\[""`]?(?<t>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase))
            c.Writes.Add(m.Groups["t"].Value.ToLowerInvariant());
        foreach (Match m in Regex.Matches(text, @"\b(?:from|join)\s+[\[""`]?(?<t>[A-Za-z0-9_]+)", RegexOptions.IgnoreCase))
            c.Reads.Add(m.Groups["t"].Value.ToLowerInvariant());
        ff.Containers.Add(c);
    }

    // split SQL on top-level semicolons, respecting single/double/backtick/bracket quoting and
    // -- / /* */ comments, so a `;` inside a string or a trigger body BEGIN..END doesn't split.
    static IEnumerable<string> SplitStatements(string sql)
    {
        var sb = new System.Text.StringBuilder();
        char quote = '\0'; bool lineComment = false, blockComment = false;
        for (int i = 0; i < sql.Length; i++)
        {
            char ch = sql[i];
            char nx = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (lineComment) { sb.Append(ch); if (ch == '\n') lineComment = false; continue; }
            if (blockComment) { sb.Append(ch); if (ch == '*' && nx == '/') { sb.Append(nx); i++; blockComment = false; } continue; }
            if (quote != '\0')
            {
                sb.Append(ch);
                if ((quote == ']' && ch == ']') || (quote != ']' && ch == quote)) quote = '\0';
                continue;
            }
            if (ch == '-' && nx == '-') { lineComment = true; sb.Append(ch); continue; }
            if (ch == '/' && nx == '*') { blockComment = true; sb.Append(ch); continue; }
            if (ch is '\'' or '"' or '`') { quote = ch; sb.Append(ch); continue; }
            if (ch == '[') { quote = ']'; sb.Append(ch); continue; }
            if (ch == ';') { yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(ch);
        }
        if (sb.ToString().Trim().Length > 0) yield return sb.ToString();
    }
}

// collects every table referenced in a query subtree (FROM/JOIN/subquery relations).
sealed class RelationCollector : Visitor
{
    public List<string> Tables = new();
    public override ControlFlow PreVisitRelation(ObjectName relation)
    {
        Tables.Add(relation.Values[^1].Value.ToLowerInvariant());
        return ControlFlow.Continue;
    }
}
