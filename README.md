<div align="center">

```text
  ___ _____  _ _____ _  _    ___ ___ _____   _____ ___ 
 / __|_ _\ \/ |_   _| || |__/ __| __| _ \ \ / / __| _ \
 \__ \| | >  <  | | | __ |__\__ \ _||   /\ V /| _||   /
 |___/___/_/\_\ |_| |_||_|  |___/___|_|_\ \_/ |___|_|_\
```

**General-purpose HTTP/JSON server framework**

*Compiles to native ARM64 binaries for the Sixth compiler.*

[![Language](https://img.shields.io/badge/Language-Forth-blue.svg?style=for-the-badge)](#)
[![Platform](https://img.shields.io/badge/Platform-macOS%20ARM64-lightgrey.svg?style=for-the-badge)](#)
[![License](https://img.shields.io/badge/License-MIT-green.svg?style=for-the-badge)](#)

</div>

---

## ⚡ Overview

Sixth Server is a general-purpose HTTP/JSON server framework written in Forth, compiled to native ARM64 binaries. No interpreter, no VM, no runtime dependencies. A 30-line Forth file compiles to a ~100KB native executable that serves JSON APIs.

Originally built as a dashboard server for the CK compiler benchmarking project, it extracts repetitive boilerplate patterns into reusable machinery: a route table, a field descriptor DSL, a flexible database driver contract, and powerful escape hatches for custom endpoint logic.

---

## ✨ Features

- **Layered Architecture**: Link only what you need (Core, TCP, HTTP, JSON, DB drivers).
- **Zero-Dependency Core**: Pure HTTP/JSON server mode available.
- **Database Driver Contract**: Seamlessly swap between SQLite (subprocess), SixthDB (subprocess), or SixthDB (linked).
- **Field Descriptor DSL**: Declare typed columns (`F_STR`, `F_INT`, `F_DEC2`) and let the framework handle query execution, parsing, JSON generation, and chunked HTTP streaming.
- **Native ARM64 Compilation**: Compiles via `bin/s3` into native Mach-O executables.

---

## 📦 Usage Patterns

### Pure HTTP/JSON Server (No Database)
```forth
require lib/core.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
```

### SQLite Backend
```forth
require lib/core.fs
require drivers/sqlite.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs
require lib/db-json.fs
```

---

## 🚀 Quick Start

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
```

---

## 📖 Framework API

### Route Table
Register a URL path mapping to a handler `( fd -- )`:
```forth
server-init ( -- )                     \ Zero all state (call once at startup)
add-route ( path-addr path-u xt -- )   \ Register a URL path → handler mapping
set-index ( addr u -- )                \ Set index page for "/" requests
server-start ( port -- )               \ Bind port, enter accept loop
```

### Database-to-JSON Handlers
```forth
db-json-array ( fd sql-a sql-u -- )    \ Emit [{col:val, ...}, ...] from query
db-json-strings ( fd sql-a sql-u -- )  \ Emit ["val1", "val2", ...] from single-column query
```
> [!NOTE]
> Both `db-json-*` words use chunked transfer encoding with a 200KB auto-flush threshold.

> [!WARNING]
> **Compiler Notes for `bin/s3`:**
> - `cells` is broken at interpret time. Use literal byte counts in `allot`.
> - `compare` uses the return stack and crashes inside `do`/`loop`. Use `str=` instead.
> - `>r`/`r>` cannot cross `do`/`loop` boundaries. Use variables instead.

---

## 🤝 Example Application

The included `examples/dashboard-server.fs` is a real 22-endpoint API server for a compiler benchmarking dashboard. It demonstrates DSL endpoints, string array endpoints, and custom endpoint logic (multi-query stats, SSE events, static ratio bands).

---

## 📄 License

MIT
