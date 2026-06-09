# Adapters — getting query results back into sql-spider

sql-spider never opens a database connection. It **emits** read-only `.sql` query files and
**ingests** their CSV results. The piece in between — running the queries — is the *adapter*, and
it is yours to provide. This is what keeps the tool driver-agnostic and safe to point at anything:
it cannot write, cannot lock, cannot even connect.

When you run this as a skill, **the agent is the adapter.** Pick whichever of the patterns below
matches how you can reach the target database, and use it for the whole run. The contract is always
the same: an emitted `.sql` goes in, a CSV with the expected columns comes out.

## The CSV contract

Each emitted query already names its output columns; an adapter just has to preserve them. The
shapes you will see:

- **Module definitions** — `object_name` plus `d00`, `d01`, … `dNN`. Long definitions are pulled in
  `substring` chunks across the `dNN` columns (a driver may cap a single column at ~4000–8000 chars);
  `absorb` reassembles them in order. If a body is still truncated, re-pull it with more chunks.
- **Table schema** — `table_name`, `column_name`, `data_type`, nullability, ordinal, etc.
- **Foreign-key edges** — `fk_table`, `fk_column`, `ref_table`, `ref_column`.
- **Reverse dependencies** — `referencing`, `referenced`.

Any adapter that produces those columns works. Save one CSV per emitted `.sql`, into the same
directory the queries were written to.

## Pattern A — a connected SQL tool / MCP

You have a tool (an MCP, a database client the agent can call) that runs a query and returns rows.
For each emitted `.sql`: read the file, run it through your tool, and write the returned rows as a
CSV next to it (same basename, `.csv`). This is the fastest path — no human in the loop — and the
right one when the agent can reach the database directly with read access.

Keep the emitted query *verbatim*. The safety preamble at the top (`read uncommitted` /
`deadlock_priority -10` / `set nocount on`) is there so your reads never block live writers; do not
strip it.

## Pattern B — a read-only query bridge (a job with DB access)

The original setup: a CI job (or a skill wrapping one) where you drop a `.sql` file, it runs against
the database, and a CSV is committed/returned. The agent dispatches each emitted query through the
bridge and collects the CSV.

This is the right pattern when the database is reachable only indirectly — behind a network
boundary, through a service account, via a vendor system you can't connect to directly. The whole
spider design came out of exactly this constraint. A bridge like this typically wants:

- a read-only connection (the bridge enforces "select only", not sql-spider),
- a traceability handle (an issue/ticket id) so each run is auditable,
- a place the CSV lands that the agent can read back.

Dispatch one emitted query per run, wait for the CSV, drop it where `absorb` expects it, continue.

## Pattern C — the user runs them (human in the loop)

You can't reach the database at all. Print each emitted query and ask the user to run it in their
client (SSMS, Azure Data Studio, `sqlcmd`, DBeaver, …) and paste back the CSV — or have them save it
to the staging directory directly.

This is the correct, safe path against a **locked-down production database**: the user (or their DBA)
runs the read-only queries on their own schedule, in batches if the volume is large, and you only
ever see the results. Default to one round at a time here so there is a natural review point.

## Direct CLI recipes (Patterns A/C, by hand)

If the agent's host — or the user — can reach SQL Server directly, two common one-liners turn an
emitted `.sql` into the CSV `absorb` wants:

```sh
# sqlcmd (cross-platform), comma-separated, trimmed
sqlcmd -S <server> -d <db> -i stage/20240101-ProcDefs.sql -s "," -W -o stage/20240101-ProcDefs.csv
```

```powershell
# PowerShell / Invoke-Sqlcmd
Invoke-Sqlcmd -ServerInstance <server> -Database <db> -InputFile stage\20240101-ProcDefs.sql |
  Export-Csv -NoTypeInformation -Path stage\20240101-ProcDefs.csv
```

> `Invoke-Sqlcmd` can truncate very long `nvarchar` columns (~4000 chars), which is exactly why
> definitions are pulled in `dNN` chunks rather than as one column — the chunking sidesteps the cap.

## Then absorb

However the CSVs were produced, fold them back in the same way:

```sh
spider absorb stage/*.csv --corpus corpus/     # or corpus/_next/*.csv on later rounds
spider round corpus/
```

The adapter changes; the loop does not.
