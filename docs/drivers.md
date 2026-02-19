---
layout: default
title: Drivers
nav_order: 5
---

# Drivers

Sixth Server uses a driver contract to abstract database access. The framework's `db-json-array` and `db-json-strings` words call the driver contract -- they don't know or care which database is behind it.

## Driver Contract

Every driver implements these 9 words:

```forth
db-init    ( -- )                       \ Initialize driver state
db-path!   ( addr u -- )                \ Set database file path
db-path    ( -- addr u )                \ Get database file path
db-exec    ( db-a db-u sql-a sql-u -- ) \ Execute query, prepare results
db-open    ( -- )                       \ Begin result iteration
db-row?    ( -- addr u flag )           \ Next row (flag=true if row available)
db-close   ( -- )                       \ End iteration
db-ok?     ( -- flag )                  \ Last db-exec succeeded?
db-error   ( -- addr u )               \ Error message if not ok
```

## Row Wire Format

All drivers produce rows in the same format:

- **Field separator**: ASCII 31 (unit separator)
- **Row terminator**: ASCII 30 (record separator)

Example row with 3 fields: `alice[US]42[US]admin[RS]` (where `[US]` = ASCII 31, `[RS]` = ASCII 30).

This is the format that `parse-pipe` in `lib/core.fs` consumes. All drivers normalize to this format regardless of their native output.

## Require Order

Drivers must be required **after** `lib/core.fs` and **before** `lib/server.fs` and `lib/db-json.fs`. This is because `server.fs` and `db-json.fs` reference words defined by the driver.

```forth
require lib/core.fs
require drivers/sqlite.fs     \ ← driver goes here
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

---

## SQLite Driver (`drivers/sqlite.fs`)

### How It Works

The SQLite driver executes queries via the `sqlite3` CLI subprocess:

1. **Command construction**: Builds a shell command in `cmd-buf`:
   ```
   sqlite3 -ascii <db-path> "<sql>" > /tmp/srm-query.txt 2>/tmp/srm-error.txt
   ```
2. **Execution**: Calls `system` to run the command
3. **Error check**: Reads the stderr file; if non-empty, sets `db-ok?` to false
4. **Result reading**: Slurps the output file into memory, iterates by scanning for ASCII 30 record separators

The `-ascii` flag tells `sqlite3` to use ASCII 31/30 as field/row separators natively -- no conversion needed.

### Temp Files

The SQLite driver uses three temp files (paths are configurable):

| Default Path | Purpose |
|-------------|---------|
| `/tmp/srm-query.txt` | Query output (rows) |
| `/tmp/srm-count.txt` | Scalar query output |
| `/tmp/srm-error.txt` | Stderr capture |

Configure with:

```forth
s" /tmp/my-query.txt" sqlite-output-path!
s" /tmp/my-count.txt" sqlite-count-path!
s" /tmp/my-error.txt" sqlite-error-path!
```

### Initialization

```forth
sqlite-init ( -- )   \ Set default temp file paths, clear error state
```

### Additional Words

The SQLite driver provides convenience words beyond the contract:

```forth
sql-exec   ( db$ sql$ -- )              \ Execute query, store results
sql-run    ( db$ sql$ -- )              \ Execute statement (no output capture)
sql-open   ( -- )                       \ Slurp output file, prepare iteration
sql-row?   ( -- addr u flag )           \ Next row from slurped output
sql-close  ( -- )                       \ No-op (results already in memory)
sql-count  ( db$ sql$ -- n )            \ Execute query, return single integer
sql-each   ( db$ sql$ xt -- )           \ Execute query, call xt for each row
sql-dump   ( db$ sql$ -- )              \ Execute query, print rows to stdout
sql-table  ( db$ sql$ -- )              \ Execute query, print formatted table
```

### SRM Compatibility Layer

For backward compatibility with pre-modular code:

```forth
srm-init    ( -- )                      \ sqlite-init + set default db path
srm-db      ( -- addr u )              \ Alias for db-path
srm-exec    ( sql$ -- )                \ sql-run against srm-db
srm-query   ( sql$ -- )                \ sql-exec against srm-db
srm-scalar  ( sql$ -- n )              \ sql-count against srm-db
srm-print   ( sql$ -- )                \ sql-dump against srm-db
srm-each    ( sql$ xt -- )             \ sql-each against srm-db
srm-table   ( sql$ -- )                \ sql-table against srm-db
srm-int     ( addr u n -- n )          \ parse-pipe + s>number?
```

---

## SixthDB CLI Driver (`drivers/sixthdb-cli.fs`)

### How It Works

Similar architecture to the SQLite driver, but calls the `sixthdb` binary:

1. **Command construction**:
   ```
   sixthdb <db-path> sql "<sql>" | tr '|\n' '\037\036' > /tmp/sdb-query.txt 2>/tmp/sdb-error.txt
   ```
2. **Format conversion**: SixthDB uses `|` as field separator and newline as row separator. The `tr` pipeline converts these to ASCII 31/30 so `parse-pipe` works unchanged.
3. **Result reading**: Same slurp-and-scan approach as the SQLite driver.

### Initialization

```forth
sixthdb-cli-init ( -- )   \ Set default temp file paths, clear error state
```

### Temp Files

| Default Path | Purpose |
|-------------|---------|
| `/tmp/sdb-query.txt` | Query output |
| `/tmp/sdb-error.txt` | Stderr capture |

---

## SixthDB Direct-Link Driver (`drivers/sixthdb.fs`)

### How It Works

This driver links SixthDB's Forth modules directly into the server binary. No subprocess, no temp files, no shell commands.

1. **Database access**: Uses mmap -- the database file is memory-mapped. Opening is expensive (done once, lazily), but subsequent queries are fast.
2. **Query execution**: Runs SixthDB's SQL parser and execution engine in-process via `sql-parse-plan` and `exec-op`.
3. **Result formatting**: Iterates the result set (either `rs-buf` for regular results or `grp-buf` for GROUP BY results), formats each row into `str2-buf` with ASCII 31 separators.

### Word Shadowing

This driver requires careful word ordering. SixthDB defines its own `db-open`, `db-close`, `str=`, etc. The driver:

1. Loads SixthDB modules (`require modules/sixthdb/sql-exec.fs`)
2. Redefines `str=` with the safe version (SixthDB's `str=` uses `compare`, which crashes in `do`/`loop`)
3. Defines driver contract words (`db-open`, `db-close`, etc.) that shadow SixthDB's versions for all code compiled afterward

### Initialization

```forth
sixthdb-init ( -- )   \ Clear all state flags and counters
```

### Native SixthDB Access

When using this driver, SixthDB's table-level words are also available for direct B-tree access without SQL parsing:

```forth
s" users" use-table drop
42 tbl-get if
  dup 0 row-get   \ get column 0
  ...
  drop
then
```

---

## Performance Comparison

The three drivers share the same contract but have fundamentally different performance profiles. The difference comes down to what happens inside `db-exec` and `db-row?`.

### What Happens Per Query

**SQLite driver** -- 7 syscalls + file I/O per query:

```
db-exec called
  1. cmd-reset + cmd+ calls         build shell string in cmd-buf (pure memory, fast)
  2. system( cmd$ )                  fork → exec → load sqlite3 binary → parse SQL
                                     → execute query → write results to /tmp file
                                     → write stderr to /tmp file → exit
  3. slurp-file( error-path )        open + read + close the stderr file
db-open called
  4. slurp-file( output-path )       open + read + close the output file
db-row? called (per row)
  5. scan for ASCII 30               pure memory scan, no syscalls
```

Per-query cost: `fork` + `exec` + sqlite3 process startup + SQL parsing + query execution + result serialization to disk + two file reads back into memory. The sqlite3 process loads fresh each time -- no connection pooling, no prepared statements.

**SixthDB CLI driver** -- 8+ syscalls + file I/O per query:

```
db-exec called
  1. cmd-reset + cmd+ calls         build shell string in cmd-buf
  2. system( cmd$ )                  fork → exec → load sixthdb binary → parse SQL
                                     → execute query → pipe through tr process
                                     → write converted results to /tmp file
                                     → write stderr to /tmp file → exit
  3. slurp-file( error-path )        open + read + close the stderr file
db-open called
  4. slurp-file( output-path )       open + read + close the output file
db-row? called (per row)
  5. scan for ASCII 30               pure memory scan, no syscalls
```

Same as SQLite plus an additional `tr` process in the pipeline to convert SixthDB's native `|` and `\n` separators to ASCII 31/30. Two processes forked per query instead of one.

**SixthDB direct-link driver** -- 0 syscalls per query (after first open):

```
db-exec called
  1. sdb-ensure-open                 first call: mmap the database file (one-time cost)
                                     subsequent calls: no-op (already mapped)
  2. expr-init                       reset expression evaluator (memory only)
  3. sql-parse-plan                  parse SQL, generate execution plan (memory only)
  4. exec-bind-table                 locate table in mmap'd B-tree (memory only)
  5. exec-op (loop)                  execute plan operators against mmap'd pages
                                     results accumulate in rs-buf or grp-buf (memory only)
db-open called
  6. reset position counter          one variable write
db-row? called (per row)
  7. format row into str2-buf        read from rs-buf/grp-buf, write to str2-buf
                                     pure memory operations, no syscalls
```

After the one-time mmap, every query is pure computation over memory-mapped pages. No process creation, no file I/O, no serialization/deserialization. The SQL parser, execution engine, and B-tree traversal all run in the same address space as the server.

### Cost Breakdown

| Operation | SQLite | SixthDB CLI | SixthDB Direct |
|-----------|--------|-------------|----------------|
| Process creation | `fork` + `exec` per query | `fork` + `exec` + `tr` per query | None |
| Program loading | sqlite3 loads fresh each query | sixthdb loads fresh each query | One-time mmap at startup |
| SQL parsing | In subprocess (discarded after) | In subprocess (discarded after) | In-process (amortized) |
| Query execution | In subprocess | In subprocess | In-process, over mmap'd pages |
| Result transfer | Write to temp file → slurp back | Write to temp file via pipe → slurp back | Direct memory access (rs-buf/grp-buf) |
| Row iteration | Scan slurped memory for delimiters | Scan slurped memory for delimiters | Read from result buffer, format into str2-buf |
| Temp files | 3 files per query | 2 files per query | None |
| Error detection | Read stderr file | Read stderr file | Check flag variable |

### Where Each Driver Wins

**SQLite subprocess** is the right choice when:
- You have an existing SQLite database and want to serve it
- Query volume is low (dashboard, admin panel)
- You want the full power of SQLite's SQL engine (window functions, CTEs, etc.)
- The subprocess overhead (~1-5ms per query) is acceptable

**SixthDB CLI subprocess** is the right choice when:
- You're using SixthDB but don't want to link it into the server binary
- You want to keep the server binary small
- You're prototyping before committing to direct-link

**SixthDB direct-link** is the right choice when:
- You need maximum throughput
- Every millisecond of latency matters
- You want zero I/O overhead per query
- You're willing to accept a larger binary (SixthDB modules are linked in)
- You want optional direct B-tree access (bypassing SQL entirely)

### Server-Level Performance

Independent of the driver choice, the server framework itself has minimal overhead:

- **No heap allocation during request handling.** All buffers (`str-buf`, `resp-buf`, `cmd-buf`, `http-buf`) are statically allocated at compile time. No `malloc`, no GC pauses, no memory pressure.
- **No interpreter overhead.** Every word compiles to native ARM64 instructions. `json-open-obj` is a handful of machine instructions that write `{` into a buffer, not a method dispatch through an object hierarchy.
- **Route dispatch is a tight loop.** Linear scan over at most 64 entries using `str=`, which is a byte-by-byte comparison with early exit on length mismatch. For typical route counts (10-30), this is faster than a hash table due to cache locality.
- **Chunked streaming avoids buffering entire responses.** `db-json-array` flushes to the socket every 200KB, so memory usage stays flat regardless of result set size.
- **Single-threaded model eliminates synchronization.** No mutexes, no atomic operations, no thread pool management. Each request runs on bare metal from accept to close.

---

## Writing a Custom Driver

To add a new database backend:

1. Create a file `drivers/mydb.fs`
2. Implement all 9 contract words (`db-init`, `db-path!`, `db-path`, `db-exec`, `db-open`, `db-row?`, `db-close`, `db-ok?`, `db-error`)
3. Ensure `db-row?` returns rows with ASCII 31 field separators
4. Require it in the same position as other drivers (after `core.fs`, before `server.fs`)

The framework will work with any driver that conforms to this contract.
