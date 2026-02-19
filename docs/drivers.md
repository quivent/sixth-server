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

## Writing a Custom Driver

To add a new database backend:

1. Create a file `drivers/mydb.fs`
2. Implement all 9 contract words (`db-init`, `db-path!`, `db-path`, `db-exec`, `db-open`, `db-row?`, `db-close`, `db-ok?`, `db-error`)
3. Ensure `db-row?` returns rows with ASCII 31 field separators
4. Require it in the same position as other drivers (after `core.fs`, before `server.fs`)

The framework will work with any driver that conforms to this contract.
