---
layout: default
title: Home
nav_order: 1
---

# Sixth Server

A general-purpose HTTP/JSON server framework written in Forth, compiled to native ARM64 binaries. No interpreter, no VM, no runtime dependencies.

The entire framework is ~1,000 lines of Forth. The compiler that builds it is 291KB. A 22-endpoint API server compiles to a 97KB native binary. From source to running server is one command and takes milliseconds.

---

## By the Numbers

| | |
|---|---|
| **97KB** | Binary size of a 22-endpoint production API server |
| **291KB** | The Sixth compiler itself -- reads Forth, emits ARM64 Mach-O |
| **~1,000 lines** | The entire framework: HTTP, JSON, routing, database bridge, chunked streaming |
| **~7 lines** | A typical database-backed JSON endpoint using the field DSL |
| **1 command** | `./bin/s3 my-server.fs bin/my-server` -- source to native binary |
| **0 dependencies** | No runtime, no shared libraries, no package manager |

## What Makes It Different

Most server frameworks optimize for developer ergonomics at the cost of massive dependency trees, startup times, and binary sizes. Sixth Server goes the other direction: the entire stack -- compiler, framework, and your application -- fits in under 400KB and compiles instantly.

- **The compiler ships with the project.** `bin/s3` is a 291KB native binary. There is no toolchain to install, no version to manage. It reads Forth source and writes an ARM64 Mach-O executable.
- **Compilation is instant.** The Sixth compiler does a single pass over the source. A 600-line server compiles in milliseconds, not seconds.
- **Binaries are tiny and self-contained.** A 22-endpoint API server with SQLite support, chunked transfer encoding, CORS, and static file serving compiles to 97KB. No dynamic linking, no runtime loader.
- **The framework is small enough to read.** Six library files totaling ~1,000 lines. You can read the entire HTTP layer (350 lines), the entire JSON generator (150 lines), or the entire route dispatcher (165 lines) in a sitting. There is no hidden complexity.
- **Endpoints collapse to declarations.** The field DSL turns a database-backed JSON endpoint into ~7 lines: declare typed columns, provide a SQL query, done. The framework handles query execution, row parsing, JSON generation, and chunked HTTP streaming.
- **Swappable database backends.** A 9-word driver contract means you can switch between SQLite (subprocess), SixthDB CLI (subprocess), or SixthDB direct-link (mmap, no subprocess) without changing a single endpoint.

## Quick Example

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
# curl localhost:8080/health        → {"status":"ok"}
# curl localhost:8080/api/users     → [{"id":1,"name":"alice"},{"id":2,"name":"bob"}]
```

That's the complete source for a working API server with a health check and a database-backed users endpoint. It compiles to a native binary in milliseconds.

## Documentation

| Page | Description |
|------|-------------|
| [Getting Started](getting-started) | Requirements, build commands, usage patterns |
| [Architecture](architecture) | Layers, data flow, buffer system |
| [API Reference](api-reference) | Every framework word with stack effects |
| [Drivers](drivers) | Driver contract, SQLite, SixthDB |
| [Examples](examples) | Walkthrough of the 22-endpoint dashboard server |
| [Compiler Notes](compiler-notes) | Sixth compiler gotchas and workarounds |
