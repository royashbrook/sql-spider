# AdventureWorks — a stepped demo of the spider loop

Northwind (in `examples/northwind/`) is the simple case: one file, already closed,
`extract` reports one component and zero orphans in a single pass. This folder is the
*interesting* case — it shows the **loop** itself, the thing the tool is actually for:
point it at **one** object and let it spider outward, one ring of dependencies at a time.

There is **no live database here.** The DDL was split out of Microsoft's public
[AdventureWorks OLTP install script](https://github.com/microsoft/sql-server-samples/tree/master/samples/databases/adventure-works)
(MIT-licensed) into per-object files, and each step folder stages the next ring by hand —
**simulating** what a real pull would drop in. That is the point: you do **not** fix
everything at once. You follow the dependency edges out, one pull at a time, and watch the
frontier shrink to nothing.

Run it:

```sh
bash examples/adventureworks/demo.sh
```

## The seed

We seed on `Sales.SalesOrderHeader` — a table with a clear, branching dependency chain. Its
declared foreign keys point at seven other tables, and those fan out again, so it makes the
loop visible without dragging in all 123 objects.

## The three passes

Each step folder is a **superset** of the one before it. Re-running `extract` on each shows
the frontier shrinking:

### `step1-seed/` — just the seed

One file: `SalesOrderHeader`'s `CREATE TABLE` plus its declared `FOREIGN KEY` constraints.
That is everything we have on the first pass.

```
FRONTIER: 0 undefined procs, 7 undefined tables (the spider's next pull)
```

The **frontier** is those seven tables — `address`, `creditcard`, `currencyrate`,
`customer`, `salesperson`, `shipmethod`, `salesterritory`. They are *referenced* (the seed's
FKs point at them) but *not yet defined* (we don't have their DDL). That list **is** the
next pull.

To get that DDL, `generate` emits the read-only pull queries for the current frontier:

```sh
dotnet run -- generate step1-frontier.json stage/
```

It writes a `*-TableSchema.sql` / `*-FkEdges.sql` query file carrying a no-lock, deadlock-victim
safety preamble, and prints the generic *"run each query against your database, save the result
as CSV, then `absorb` it back"* instruction. **sql-spider never connects to a database itself** —
you run those queries with whatever adapter you like and feed the CSVs back. (The demo doesn't
run them; it just shows you what it would emit, then jumps to the result.)

### `step2-pulled/` — the first pull, folded in

We "ran" those queries and got the seven tables' DDL back, so `step2-pulled/` adds them. They
are now **defined**, so they leave the frontier — but their *own* foreign keys introduce the
next ring:

```
FRONTIER: 0 undefined procs, 6 undefined tables (the spider's next pull)
```

The frontier shrank from 7 to 6 (`stateprovince`, `currency`, `person`, `store`, `employee`,
`countryregion`). This is the loop working: each pull defines a ring and reveals a smaller one.

### `step3-closed/` — the second pull, folded in → closure

We pull those six the same way. Their FKs only point at tables we already have, or at
`businessentity` (the last leaf). Once `businessentity` is in, nothing is
referenced-but-undefined:

```
FRONTIER: 0 undefined procs, 0 undefined tables (the spider's next pull)
  components: 1   degree-0 nodes: 0
  OK: single connected component, no orphans. Closure holds.
```

The frontier is **empty** and the graph is one connected component with zero orphans. The
spider is **done**. Frontier `7 → 6 → 0` across three passes.

## The two things the tool reports each pass

At every pass the audit distinguishes two very different kinds of "not closed":

1. **The FRONTIER** — objects *referenced but not yet defined*. These are not errors; they
   are the **work queue**. `generate` turns them into the exact read-only queries you'd run
   to pull their definitions. The loop ends when this list is empty.

2. **Genuinely-standalone objects** — objects *referenced nowhere*. These show up as their
   own tiny component (or a degree-0 node) and are reported **with the reason**, not as a
   failure to fix. On the full AdventureWorks script (all 123 objects, not this slice) the
   residual is real and honest:

   - the `ufnGet*StatusText` scalar functions (`ufnGetDocumentStatusText`,
     `ufnGetPurchaseOrderStatusText`, `ufnGetSalesOrderStatusText`) — **app-only** lookups
     called by the application layer, never by other SQL in the script;
   - `awbuildversion` — a single-row **version table**, read by nothing in the DDL;
   - `transactionhistoryarchive` — an **archive table** populated only by `BULK INSERT`, so
     no statement in the script references it;
   - a view or two with the same shape.

   The full script lands at roughly seven components with about six of these standalone
   objects. They are **not bugs** — they are genuinely disconnected in the SQL, and the tool
   names them and says why rather than pretending the graph failed.

**Closure = the frontier is empty.** A standalone residual that the tool can explain is a
finding, not a failure. This slice closes completely because every object in it is reachable
through declared foreign keys from the seed.
