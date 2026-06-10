using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace SqlSpider;


// ---------------------------------------------------------------------------
// T-SQL dialect: Microsoft.SqlServer.TransactSql.ScriptDom (the parser SQL Server
// itself uses). Full-fidelity -- procs, functions, triggers, column lineage, the
// works. This is a pure move of the original inline extractor behind IDialectExtractor;
// the facts it emits are exactly what the neutral core consumed before.
// ---------------------------------------------------------------------------
public sealed class TSqlExtractor : IDialectExtractor
{
    public CorpusFacts Extract(IReadOnlyList<string> files)
    {
        var result = new CorpusFacts();

        // pass A: build column -> owning-tables index from the absorbed create-table DDL.
        // lets pass B resolve UNqualified column refs (just `quantity`, no table prefix) to a table
        // when exactly one in-scope table owns that column name.
        var colOwners  = new Dictionary<string, HashSet<string>>();
        var realTables = result.RealTables;       // tables we have create-table DDL for
        var cteNames   = result.CteNames;         // named CTEs = legit subquery sources (keep), vs bare per-query aliases (drop)
        foreach (var f in files)
        {
            var p0 = new TSql160Parser(true);
            using var r0 = new StreamReader(f);
            var fr0 = p0.Parse(r0, out _);
            if (fr0 == null) continue;
            var tc = new TableColFinder(); fr0.Accept(tc);
            foreach (var (tbl, col) in tc.Cols)
            {
                realTables.Add(tbl);
                (colOwners.TryGetValue(col, out var s) ? s : colOwners[col] = new HashSet<string>()).Add(tbl);
            }
            var cnf = new CteNameFinder(); fr0.Accept(cnf);
            foreach (var n in cnf.Names) cteNames.Add(n);
        }

        // pass B: per file, scope dependency/column/lineage extraction to each defined object's subtree.
        foreach (var f in files)
        {
            var fname = Path.GetFileName(f);
            var ff = new FileFacts { File = fname };
            var sql = File.ReadAllText(f);
            var frag = new TSql160Parser(true).Parse(new StringReader(sql), out var errs);
            if (errs.Count > 0)
            {
                // legacy vendor procs (double-quote strings, *= joins) need the old parser with quoted-identifier OFF
                var f80 = new TSql80Parser(false).Parse(new StringReader(sql), out var e80);
                if (e80.Count < errs.Count) { frag = f80; errs = e80; }
            }
            if (errs.Count > 0) result.ParseFail++;
            if (frag == null) { result.Files.Add(ff); continue; }

            // A single .sql file can define MANY objects (a schema install script, a migration, etc).
            // Find every top-level create-statement and treat each as its own container, scoping the
            // dependency/column/lineage visitors to THAT statement's subtree only -- so a table read
            // inside proc A is attributed to A and one inside proc B in the same file is attributed to B.
            // A file with no create-statement (a bare script) is still one container over the whole file.
            var cc = new ContainerCollector(); frag.Accept(cc);
            var containers = cc.Containers.Count > 0
                ? cc.Containers
                : new List<(string name, string kind, TSqlFragment node)>
                  {
                      (Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), "script", (TSqlFragment)frag)
                  };
            // a found create-statement IS a definition we hold; a fallback bare-script is not.
            bool defined = cc.Containers.Count > 0;

            // declared FOREIGN KEY constraints (inline in CREATE TABLE or via ALTER TABLE ADD CONSTRAINT)
            // are real table->table dependencies the dependency visitor never sees (the ref table lives in
            // the constraint, not a FROM/JOIN). Scanned at FILE scope so ALTER-TABLE FKs (Northwind's style)
            // are caught too -- keeps a pure-DDL extract connected instead of stranding FK-only lookup tables.
            var fkPass = new FileFkVisitor(); frag.Accept(fkPass);
            foreach (var (from, to) in fkPass.Edges) ff.FkEdges.Add((from, to));

            foreach (var (name, ckind, node) in containers)
            {
                string container = name;
                var c = new Container { Name = container, Kind = ckind, Defined = defined };

                var v = new DepVisitor(container); node.Accept(v);
                c.Reads.AddRange(v.Reads);
                c.Writes.AddRange(v.Writes);
                c.Calls.AddRange(v.Calls);
                c.Funcs.AddRange(v.Funcs);
                ff.Containers.Add(c);

                var cv = new ColumnVisitor(); node.Accept(cv);
                void Hit(string tbl, string col)
                {
                    // attribute columns ONLY to real (DDL'd) tables, so join_key edges connect real nodes,
                    // not CTE/derived phantoms.
                    if (Noise(tbl) || !realTables.Contains(tbl)) return;
                    ff.ColumnHits.Add((tbl, col));
                }
                foreach (var (qual, col) in cv.Cols)
                {
                    if (!cv.Alias.TryGetValue(qual, out var tbl)) continue;   // only alias-resolved -> real table
                    Hit(tbl, col);
                }
                // unqualified columns: attribute to the lone in-scope table that owns that column name
                var scopeTables = new HashSet<string>(cv.Alias.Values);
                foreach (var col in cv.Unqual)
                {
                    if (!colOwners.TryGetValue(col, out var owners)) continue;
                    var inScope = scopeTables.Where(t => owners.Contains(t)).ToList();
                    if (inScope.Count == 1) Hit(inScope[0], col);
                }

                var lv = new LineageVisitor(); node.Accept(lv); ff.Lineage.AddRange(lv.Edges);
            }

            result.Files.Add(ff);
        }

        return result;
    }

    // pseudo-tables / temp noise to drop (trigger inserted/deleted, #temp, @tablevars, system procs).
    // mirrors the neutral core's Noise() so column attribution matches exactly.
    static bool Noise(string t) => t == "inserted" || t == "deleted" || t == "sp_executesql"
        || t.StartsWith("#") || t.StartsWith("@") || t.StartsWith("xp_");
}

// ---------------------------------------------------------------------------
// AST visitors (Microsoft.SqlServer.TransactSql.ScriptDom)
// ---------------------------------------------------------------------------
// finds EVERY create-statement in a file (not just the first) and keeps the statement node
// itself, so each object's dependency/column/lineage extraction can be scoped to its own subtree.
// real schema scripts define many objects in one file; one node per defined object.
class ContainerCollector : TSqlFragmentVisitor
{
    // (defined-object name, kind, the statement subtree to scope visitors to)
    public List<(string name, string kind, TSqlFragment node)> Containers = new();
    static string L(SchemaObjectName s) => (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value).ToLowerInvariant();
    // temp tables (create table #t) are scratch, not defined objects -> skip.
    static bool Temp(string n) => n.StartsWith("#");
    void Add(string name, string kind, TSqlFragment node) { if (!Temp(name)) Containers.Add((name, kind, node)); }
    // visit the BASE statement-body classes, not the concrete Create* ones: ScriptDom's generated
    // Visit(CreateProcedureStatement) chains up to Visit(ProcedureStatementBody), and the same base
    // also covers CreateOrAlter*/Alter* -- so `create or alter procedure` (the standard modern idiom)
    // and plain `alter` redefinitions register as defined objects instead of silently vanishing.
    public override void Visit(ProcedureStatementBody n)   => Add(L(n.ProcedureReference.Name), "proc", n);
    public override void Visit(ViewStatementBody n)        => Add(L(n.SchemaObjectName), "view", n);
    public override void Visit(FunctionStatementBody n)    => Add(L(n.Name), "function", n);
    public override void Visit(CreateTableStatement n)     => Add(L(n.SchemaObjectName), "table", n);
    public override void Visit(TriggerStatementBody n)     => Add(L(n.Name), "trigger", n);
}

class DepVisitor : TSqlFragmentVisitor
{
    // CTE names + derived-table aliases are NOT real tables -> drop. update/delete targets are often
    // the ALIAS (update a ... from realtable a) -> resolve to base via the alias map.
    public HashSet<string> Calls = new();
    public HashSet<string> Funcs = new();               // user-defined function calls (scalar in expressions + TVF in FROM)
    readonly HashSet<string> drop = new();              // cte names + derived-table aliases
    readonly Dictionary<string, string> alias = new();  // alias -> base table
    readonly List<string> rawReads = new(), rawWrites = new();
    readonly string self;
    public DepVisitor(string s) { self = s; }
    static string? L(SchemaObjectName? s) => s == null ? null : (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value)?.ToLowerInvariant();
    public override void Visit(CommonTableExpression n) { if (n.ExpressionName != null) drop.Add(n.ExpressionName.Value.ToLowerInvariant()); }
    public override void Visit(QueryDerivedTable n)     { if (n.Alias != null) drop.Add(n.Alias.Value.ToLowerInvariant()); }
    public override void Visit(NamedTableReference n)   { var t = L(n.SchemaObject); if (t == null) return; if (n.Alias != null) alias[n.Alias.Value.ToLowerInvariant()] = t; rawReads.Add(t); }
    public override void Visit(InsertStatement n) { T(n.InsertSpecification?.Target); }
    public override void Visit(UpdateStatement n) { T(n.UpdateSpecification?.Target); }
    public override void Visit(DeleteStatement n) { T(n.DeleteSpecification?.Target); }
    public override void Visit(MergeStatement n)  { T(n.MergeSpecification?.Target); }
    // `select ... into <tbl>` creates/fills the target; it's a SchemaObjectName, never a
    // NamedTableReference, so the generic table-reference path can't see it.
    public override void Visit(SelectStatement n) { var t = L(n.Into); if (t != null) rawWrites.Add(t); }
    public override void Visit(TruncateTableStatement n) { var t = L(n.TableName); if (t != null) rawWrites.Add(t); }
    // the table a trigger fires ON lives in TriggerObject (not a table reference); without this
    // edge a trigger never connects to its own host table. matches the sqlite dialect (a read).
    public override void Visit(TriggerStatementBody n) { var t = L(n.TriggerObject?.Name); if (t != null && t != self) rawReads.Add(t); }
    // visit the SPECIFICATION, not just ExecuteStatement: `insert into #t exec proc ...`
    // (INSERT...EXEC) wraps the execute inside the insert's source, so a statement-level
    // visit never sees the call. ExecuteSpecification lives inside both shapes.
    public override void Visit(ExecuteSpecification n)
    {
        if (n.ExecutableEntity is ExecutableProcedureReference e)
        {
            var p = L(e.ProcedureReference?.ProcedureReference?.Name);
            if (p != null && p != self) Calls.Add(p);
        }
    }
    // scalar UDF calls in expressions (e.g. dbo.ufnGetStatus(x)) parse as a FunctionCall with a
    // schema-qualified CallTarget; built-ins (getdate(), count()) are unqualified (CallTarget == null) -> skipped.
    // method calls on hierarchyid/xml/CLR values (OrganizationNode.GetLevel(), payload.value(...))
    // parse IDENTICALLY to a schema-qualified UDF, so the well-known built-in method names are
    // filtered -- otherwise each becomes a phantom function node on the frontier.
    static readonly HashSet<string> BuiltinMethods = new()
    {
        "getlevel", "getancestor", "getdescendant", "getreparentedvalue", "getroot", "isdescendantof",
        "tostring", "parse", "read", "write",
        "value", "nodes", "exist", "query", "modify"
    };
    public override void Visit(FunctionCall n)
    {
        if (n.CallTarget != null && n.FunctionName != null)
        {
            var fn = n.FunctionName.Value.ToLowerInvariant();
            if (fn != self && !BuiltinMethods.Contains(fn)) Funcs.Add(fn);
        }
    }
    // table-valued functions invoked in a FROM / APPLY (e.g. from dbo.ufnTableValued(x))
    public override void Visit(SchemaObjectFunctionTableReference n)
    {
        var fn = L(n.SchemaObject);
        if (fn != null && fn != self) Funcs.Add(fn);
    }
    void T(TableReference? tr) { if (tr is NamedTableReference ntr) { var t = L(ntr.SchemaObject); if (t != null) rawWrites.Add(t); } }
    string Resolve(string t) => alias.TryGetValue(t, out var b) ? b : t;
    public IEnumerable<string> Reads  => rawReads.Select(Resolve).Where(t => t != self && !drop.Contains(t)).Distinct();
    public IEnumerable<string> Writes => rawWrites.Select(Resolve).Where(t => t != self && !drop.Contains(t)).Distinct();
}

// declared foreign keys, owner-aware: both `create table` (inline `col .. references T` + table-level
// `foreign key (..) references T`) AND `alter table .. add constraint .. foreign key .. references T`.
// emits (owning-table -> referenced-table) so FK-only relationships survive a pure-DDL parse.
class FileFkVisitor : TSqlFragmentVisitor
{
    public List<(string from, string to)> Edges = new();
    static string? L(SchemaObjectName? s) => s == null ? null : (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value)?.ToLowerInvariant();
    void Scan(string? owner, TableDefinition? d)
    {
        if (owner == null || d == null) return;
        if (d.TableConstraints != null)
            foreach (var c in d.TableConstraints)
                if (c is ForeignKeyConstraintDefinition fk) { var to = L(fk.ReferenceTableName); if (to != null && to != owner) Edges.Add((owner, to)); }
        if (d.ColumnDefinitions != null)
            foreach (var col in d.ColumnDefinitions)
                foreach (var c in col.Constraints)
                    if (c is ForeignKeyConstraintDefinition fk) { var to = L(fk.ReferenceTableName); if (to != null && to != owner) Edges.Add((owner, to)); }
    }
    public override void Visit(CreateTableStatement n) => Scan(L(n.SchemaObjectName), n.Definition);
    public override void Visit(AlterTableAddTableElementStatement n) => Scan(L(n.SchemaObjectName), n.Definition);
}

class ColumnVisitor : TSqlFragmentVisitor
{
    public Dictionary<string, string> Alias = new();
    public List<(string qual, string col)> Cols = new();
    public List<string> Unqual = new();
    static string? L(SchemaObjectName? s) => s == null ? null : (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value)?.ToLowerInvariant();
    public override void Visit(NamedTableReference n) { var t = L(n.SchemaObject); if (t == null) return; Alias[t] = t; if (n.Alias != null) Alias[n.Alias.Value.ToLowerInvariant()] = t; }
    public override void Visit(ColumnReferenceExpression c)
    {
        var ids = c.MultiPartIdentifier?.Identifiers;
        if (ids == null || ids.Count == 0) return;
        if (ids.Count >= 2) Cols.Add((ids[ids.Count - 2].Value.ToLowerInvariant(), ids[ids.Count - 1].Value.ToLowerInvariant()));
        else Unqual.Add(ids[0].Value.ToLowerInvariant());
    }
}

class TableColFinder : TSqlFragmentVisitor
{
    public List<(string tbl, string col)> Cols = new();
    static string L(SchemaObjectName s) => (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value).ToLowerInvariant();
    public override void Visit(CreateTableStatement n) { var t = L(n.SchemaObjectName); foreach (var c in n.Definition.ColumnDefinitions) Cols.Add((t, c.ColumnIdentifier.Value.ToLowerInvariant())); }
}

class CteNameFinder : TSqlFragmentVisitor
{
    public List<string> Names = new();
    public override void Visit(CommonTableExpression n) { if (n.ExpressionName != null) Names.Add(n.ExpressionName.Value.ToLowerInvariant()); }
}

// column lineage: `update <tgt> set tgt.col = src.col from ... join src` -> (src.col -> tgt.col).
// surfaces cross-table column flow that shared-column-name matching can't, when columns are
// prefixed per table (common in legacy vendor schemas).
class AliasCollector : TSqlFragmentVisitor
{
    public Dictionary<string, string> Map = new();
    static string? L(SchemaObjectName? s) => s == null ? null : (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value)?.ToLowerInvariant();
    public override void Visit(NamedTableReference n) { var t = L(n.SchemaObject); if (t == null) return; Map[t] = t; if (n.Alias != null) Map[n.Alias.Value.ToLowerInvariant()] = t; }
}

class ColRefCollector : TSqlFragmentVisitor
{
    public List<(string qual, string col)> Refs = new();
    public override void Visit(ColumnReferenceExpression c)
    {
        var ids = c.MultiPartIdentifier?.Identifiers;
        if (ids == null || ids.Count < 2) return;
        Refs.Add((ids[ids.Count - 2].Value.ToLowerInvariant(), ids[ids.Count - 1].Value.ToLowerInvariant()));
    }
}

class LineageVisitor : TSqlFragmentVisitor
{
    public List<(string srcTbl, string srcCol, string tgtTbl, string tgtCol)> Edges = new();
    static string? L(SchemaObjectName? s) => s == null ? null : (s.BaseIdentifier?.Value ?? s.Identifiers[s.Identifiers.Count - 1].Value)?.ToLowerInvariant();
    public override void Visit(UpdateStatement u)
    {
        var spec = u.UpdateSpecification; if (spec == null) return;
        var ac = new AliasCollector(); u.Accept(ac); var map = ac.Map;
        string? R(string? q) => q != null && map.TryGetValue(q, out var b) ? b : q;
        string? tgt = spec.Target is NamedTableReference nt ? R(L(nt.SchemaObject)) : null;
        foreach (var sc in spec.SetClauses)
        {
            if (sc is not AssignmentSetClause asc || asc.Column == null || asc.NewValue == null) continue;
            var tids = asc.Column.MultiPartIdentifier?.Identifiers; if (tids == null || tids.Count == 0) continue;
            string tgtCol = tids[tids.Count - 1].Value.ToLowerInvariant();
            string? tgtTbl = tids.Count >= 2 ? R(tids[tids.Count - 2].Value.ToLowerInvariant()) : tgt;
            var crc = new ColRefCollector(); asc.NewValue.Accept(crc);
            foreach (var (q, col) in crc.Refs) { var srcTbl = R(q); if (srcTbl != null && tgtTbl != null) Edges.Add((srcTbl, col, tgtTbl, tgtCol)); }
        }
    }
}
