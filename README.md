# Sixth Server

A general-purpose HTTP/JSON server framework written in Forth, compiled to native ARM64 binaries. No interpreter, no VM, no runtime dependencies. A 30-line Forth file compiles to a ~100KB native executable that serves JSON APIs.

## Why This Exists

Sixth is a Forth compiler that produces native ARM64 macOS binaries. It was built as the compilation backend for the CK compiler benchmarking project, where it needed a dashboard server to visualize benchmark data.

The original dashboard server was a single 817-line Forth file with 22 endpoints. Every endpoint repeated the same pattern: open a SQL query, iterate rows, emit JSON fields, send the response. The patterns were screaming for abstraction.

This framework extracts the repeating shapes into reusable machinery:

- **Route table** — register `( fd -- )` handler words against URL paths, get linear dispatch for free
- **Field descriptor DSL** — declare typed columns (`F_STR`, `F_INT`, `F_DEC2`), then call `db-json-array` and the framework handles query execution, row parsing, JSON generation, and chunked HTTP streaming
- **Driver contract** — swap database backends (SQLite, SixthDB CLI, SixthDB linked) without changing endpoint code
- **Escape hatch** — any word with signature `( fd -- )` works as a handler, so custom endpoints have full access to the JSON, HTTP, and database primitives

## Architecture

The framework is layered so you only link what you need:

```
lib/core.fs              Layer 0: String buffers, numbers, row parsing (zero deps)
lib/tcp.fs               Layer 1: TCP primitives (standalone)
lib/http.fs              Layer 1: HTTP parsing/responses (depends on core)
lib/json.fs              Layer 1: JSON generation (depends on core)
lib/server.fs            Layer 1: Route table + dispatch + lifecycle
lib/db-json.fs           Layer 2: Field DSL + db-json-array/strings (depends on driver)
drivers/sqlite.fs        Driver: SQLite via subprocess
drivers/sixthdb-cli.fs   Driver: SixthDB via subprocess
drivers/sixthdb.fs       Driver: SixthDB linked directly
```

### Three Usage Patterns

**1. No database** — pure HTTP/JSON server:
```forth
require lib/core.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
```

**2. SQLite backend** — query SQLite via subprocess:
```forth
require lib/core.fs
require drivers/sqlite.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

**3. SixthDB backend** — native Forth database:
```forth
require lib/core.fs
require drivers/sixthdb-cli.fs   \ or drivers/sixthdb.fs for direct link
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

## Quick Start

```forth
require lib/core.fs
require drivers/sqlite.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs

: handle-users ( fd -- )
  field-reset
  F_INT s" id"   +field
  F_STR s" name" +field
  s" SELECT id,name FROM users ORDER BY id"
  db-json-array ;

: handle-health ( fd -- )
  >r str-reset json-begin json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj r> http-200 ;

: register-routes ( -- )
  s" /api/users" ['] handle-users add-route
  s" /health"    ['] handle-health add-route ;

: main ( -- )
  sqlite-init  db-json-init  server-init
  s" my.db" db-path!
  register-routes
  8080 server-start ;
main
```

Build and run:

```sh
./bin/s3 my-server.fs bin/my-server
./bin/my-server
# Sixth Server on port 8080
# curl localhost:8080/health → {"status":"ok"}
# curl localhost:8080/api/users → [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
```

## Project Structure

```
bin/s3                       Sixth compiler (ARM64 macOS, ~298KB)
lib/
  core.fs                    String buffers, number conversion, field parsing, str=
  server.fs                  Route table, dispatch, server lifecycle
  http.fs                    HTTP request parsing, response helpers, chunked encoding
  json.fs                    JSON generation with 8-level nesting stack
  tcp.fs                     TCP socket primitives (accept, read, write, close)
  db-json.fs                 Field DSL, db-json-array, db-json-strings
drivers/
  sqlite.fs                  SQLite driver (subprocess, implements driver contract)
  sixthdb-cli.fs             SixthDB CLI driver (subprocess, implements driver contract)
  sixthdb.fs                 SixthDB direct-link driver (skeleton)
modules/srm/
  srm.fs                     Backward compatibility shim (requires core.fs + sqlite.fs)
  db.fs                      Named queries for CK metrics database
examples/
  dashboard-server.fs        22-endpoint benchmark dashboard (real-world example)
```

## Driver Contract

Every database driver implements these words:

```forth
db-init    ( -- )                          \ Initialize driver state
db-path!   ( addr u -- )                   \ Set database file path
db-path    ( -- addr u )                   \ Get database file path
db-exec    ( db-a db-u sql-a sql-u -- )    \ Execute query, prepare results
db-open    ( -- )                          \ Begin result iteration
db-row?    ( -- addr u flag )              \ Next row (ASCII 31 field separator)
db-close   ( -- )                          \ End iteration
db-ok?     ( -- flag )                     \ Last db-exec succeeded?
db-error   ( -- addr u )                   \ Error message if not ok
```

Row format: fields separated by ASCII 31 (unit separator), rows terminated by ASCII 30 (record separator). This is what `parse-pipe` in `core.fs` consumes. SQLite's `-ascii` mode produces this natively. SixthDB drivers convert to this format in their output pipeline.

## Framework API

### Route Table

```forth
server-init ( -- )                     \ Zero all state (call once at startup)
add-route ( path-addr path-u xt -- )   \ Register a URL path → handler mapping
set-index ( addr u -- )                \ Set index page for "/" requests
server-start ( port -- )               \ Bind port, enter accept loop (does not return)
```

Handler signature is `( fd -- )`. The fd is a TCP socket file descriptor.

### Field Descriptor DSL

```forth
field-reset ( -- )                     \ Clear the field table
+field ( type name-addr name-u -- )    \ Add a column: type + JSON key name
db-json-init ( -- )                    \ Initialize field state (call once at startup)
```

Field types:
- `F_STR` — string value (emitted as `"key":"value"`)
- `F_INT` — integer value (emitted as `"key":123`)
- `F_DEC2` — decimal stored as integer * 100 (emitted as `"key":1.23`)

### Database-to-JSON Handlers

```forth
db-json-array ( fd sql-a sql-u -- )    \ Emit [{col:val, ...}, ...] from query
db-json-strings ( fd sql-a sql-u -- )  \ Emit ["val1", "val2", ...] from single-column query
```

Both use chunked transfer encoding with a 200KB auto-flush threshold.

### Custom Handlers

For endpoints that need custom logic (multi-query, nested JSON, non-SQL data), write a `( fd -- )` handler using the primitives directly:

- **JSON**: `json-begin`, `json-open-obj`, `json-close-obj`, `json-open-arr`, `json-close-arr`, `json-key`, `json-str-val`, `json-key-num`, `json-key-str`, `json-key-decimal2`
- **HTTP**: `http-200`, `http-200-chunked`, `http-end-chunked`, `http-404`, `http-500`, `http-send-file`
- **Database**: `db-path`, `db-exec`, `db-open`, `db-row?`, `db-close`, `parse-pipe`, `row-int`

### String Comparison

The framework provides `str=` (in `lib/core.fs`) which is safe to use inside `do`/`loop` constructs (the built-in `compare` is not):

```forth
str= ( a1 u1 a2 u2 -- flag )          \ True if strings are equal
```

## Migration from Pre-Modular Code

If you have code that uses the old monolithic API:

| Old | New |
|-----|-----|
| `require modules/srm/srm.fs` | Still works (compatibility shim). Or: `require lib/core.fs` + `require drivers/sqlite.fs` |
| `srm-init` | `sqlite-init` (alias still works) |
| `sql-exec` | `db-exec` |
| `sql-open` | `db-open` |
| `sql-row?` | `db-row?` |
| `sql-close` | `db-close` |
| `sql-json-array` | `db-json-array` (alias still works) |
| `sql-json-strings` | `db-json-strings` (alias still works) |
| `server-init` (zeroed field-count) | `server-init` + `db-json-init` |
| `sql-output-len ! sql-output-buf swap move` | `sqlite-output-path!` |
| `sql-count-len ! sql-count-buf swap move` | `sqlite-count-path!` |
| `sql-error-len ! sql-error-buf swap move` | `sqlite-error-path!` |

The SQLite driver also exports all original `sql-*` words (`sql-exec`, `sql-open`, `sql-row?`, etc.) for backward compatibility. The `srm-*` convenience layer (`srm-exec`, `srm-query`, `srm-scalar`, `srm-print`, `srm-each`, `srm-table`) is available when using the SQLite driver.

## Example: The Dashboard Server

The included `examples/dashboard-server.fs` is a real 22-endpoint API server for a compiler benchmarking dashboard. It demonstrates all endpoint patterns:

**DSL endpoints** (11 endpoints, ~7 lines each):
```forth
: handle-trajectory ( fd -- )
  field-reset
  F_STR  s" milestone" +field
  F_INT  s" geo"       +field
  F_INT  s" rw"        +field
  F_STR  s" status"    +field
  s" SELECT milestone_name,geomean_target,realwork_target,status FROM trajectory_milestones ORDER BY milestone_order"
  db-json-array ;
```

**String array endpoints** (1 endpoint, 3 lines):
```forth
: handle-landmines ( fd -- )
  s" SELECT rule FROM architectural_constraints ORDER BY constraint_name ASC"
  db-json-strings ;
```

**Custom endpoints** (10 endpoints): multi-query stats, nested tier-grouped roadmap, keyed distribution objects, SSE events, static ratio bands.

## Compiler Notes

The included `bin/s3` is the Sixth compiler. It reads Forth source and produces native ARM64 Mach-O executables. Key things to know when writing server code:

- **`cells` is broken at interpret time.** Use literal byte counts in `allot`: write `create buf 1536 allot`, not `create buf 64 3 * cells allot`
- **`compare` uses the return stack.** It crashes inside `do`/`loop`. Use `str=` (provided by `lib/core.fs`) instead.
- **`>r`/`r>` cannot cross `do`/`loop` boundaries.** The loop pushes parameters onto the return stack. Use variables to pass data across loop boundaries.
- **Top-level executable code is silently skipped.** All initialization must happen inside a word that is explicitly called (e.g., define `: main ... ; main`).

## Requirements

- macOS on Apple Silicon (ARM64)
- `sqlite3` on PATH (for SQLite driver — ships with macOS)
- `sixthdb` on PATH (for SixthDB CLI driver — optional)

## License

MIT
