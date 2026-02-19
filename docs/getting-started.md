---
layout: default
title: Getting Started
nav_order: 2
---

# Getting Started

## Requirements

- **macOS on Apple Silicon** (ARM64) -- the Sixth compiler produces ARM64 Mach-O binaries
- **sqlite3** on PATH (for SQLite driver -- ships with macOS)
- **sixthdb** on PATH (for SixthDB CLI driver -- optional)

## Building

The bundled `bin/s3` is the Sixth compiler. It reads Forth source and produces a native executable:

```sh
./bin/s3 my-server.fs bin/my-server
```

This compiles `my-server.fs` and writes a native ARM64 binary to `bin/my-server`. Run it directly:

```sh
./bin/my-server
# Sixth Server on port 8080
```

## Minimal Server

The simplest possible server -- a health endpoint with no database:

```forth
require lib/core.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs

: handle-health ( fd -- )
  >r str-reset json-begin json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj r> http-200 ;

: main ( -- )
  server-init
  s" /health" ['] handle-health add-route
  8080 server-start ;
main
```

## Three Usage Patterns

### 1. No database -- pure HTTP/JSON

Link the core framework only. Good for health checks, static JSON responses, file serving.

```forth
require lib/core.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
```

### 2. SQLite backend

Add the SQLite driver and `db-json.fs` to query SQLite via subprocess. The `sqlite3` CLI (shipped with macOS) handles execution; the framework handles result parsing and JSON generation.

```forth
require lib/core.fs
require drivers/sqlite.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

### 3. SixthDB backend

Swap in a SixthDB driver. The CLI driver uses a subprocess (like SQLite). The direct-link driver links SixthDB's Forth modules directly -- no subprocess, no temp files, mmap-based access.

```forth
require lib/core.fs
require drivers/sixthdb-cli.fs   \ or drivers/sixthdb.fs for direct link
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

## Startup Sequence

Every server follows the same initialization pattern:

```forth
: main ( -- )
  sqlite-init      \ initialize the driver
  db-json-init     \ initialize the field DSL
  server-init      \ zero the route table
  s" my.db" db-path!   \ set the database path
  register-routes  \ add your endpoints
  8080 server-start ;  \ bind port, enter accept loop (does not return)
main
```

Key points:

- Call the driver's init word first (`sqlite-init`, `sixthdb-cli-init`, or `sixthdb-init`)
- Call `db-json-init` if using the field DSL
- Call `server-init` to zero the route table
- All initialization must happen inside a word -- top-level code is silently skipped by the compiler (see [Compiler Notes](compiler-notes))
- `server-start` enters an infinite accept loop and does not return

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
  sixthdb.fs                 SixthDB direct-link driver (mmap, no subprocess)
modules/srm/
  srm.fs                     Backward compatibility shim (requires core.fs + sqlite.fs)
  db.fs                      Named queries for CK metrics database
examples/
  dashboard-server.fs        22-endpoint benchmark dashboard (real-world example)
```
