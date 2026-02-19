\ lib/server.fs -- General-purpose HTTP server framework for Sixth
\
\ Provides:
\   1. Route table with linear dispatch
\   2. Index page support
\   3. Static file serving
\   4. Server lifecycle (init, listen, serve)
\
\ Requires (caller must require before this file):
\   lib/core.fs   (str=, str-reset, str+, str-char)
\   lib/tcp.fs    (tcp-server-init, accept-tcp, close-tcp)
\   lib/http.fs   (http-read-request, http-200, http-200-chunked, etc.)
\
\ Optional (for database endpoints):
\   lib/json.fs       (json-begin, json-open-obj, json-key, etc.)
\   lib/db-json.fs    (field-reset, +field, db-json-array, etc.)
\   A database driver  (drivers/sqlite.fs or drivers/sixthdb-cli.fs, etc.)
\
\ Usage example:
\
\   require lib/core.fs
\   require drivers/sqlite.fs
\   require lib/tcp.fs
\   require lib/http.fs
\   require lib/json.fs
\   require lib/server.fs
\   require lib/db-json.fs
\
\   : handle-items ( fd -- )
\     field-reset
\     F_INT s" id"     +field
\     F_STR s" name"   +field
\     F_DEC2 s" ratio" +field
\     s" SELECT id,name,CAST(ratio*100 AS INTEGER) FROM items ORDER BY id"
\     db-json-array ;
\
\   : handle-health ( fd -- )
\     >r str-reset json-begin json-open-obj
\     s" status" s" ok" json-key-str
\     json-close-obj r> http-200 ;
\
\   : register-routes ( -- )
\     s" /api/items" ['] handle-items add-route
\     s" /health"    ['] handle-health add-route ;
\
\   : main ( -- )
\     sqlite-init  db-json-init  server-init
\     s" mydb.db" db-path!
\     s" /index.html" set-index
\     register-routes
\     3000 server-start ;
\   main

\ ============================================================
\ Route Table
\
\ Linear scan dispatch. Each entry: 3 cells = [path-addr] [path-u] [xt]
\ Max 64 routes. Handler signature: ( fd -- )
\ ============================================================

64 constant MAX-ROUTES
variable route-count
create route-tbl 1536 allot   \ MAX-ROUTES(64) * 3 entries * 8 bytes

\ String pool: route paths are copied here so addresses are stable
2048 constant RPOOL-SIZE
create route-pool RPOOL-SIZE allot
variable rpool-used

: route-path@ ( n -- addr u )
  3 cells * route-tbl + dup @ swap cell+ @ ;

: route-xt@ ( n -- xt )
  3 cells * route-tbl + 2 cells + @ ;

\ Temporaries for add-route (avoids stack gymnastics)
variable ar-xt
variable ar-len
variable ar-pool

: add-route ( path-addr path-u xt -- )
  route-count @ MAX-ROUTES >= if drop 2drop exit then
  ar-xt !
  dup ar-len !
  route-pool rpool-used @ + ar-pool !
  drop
  ar-pool @ ar-len @ move
  route-count @ 3 cells * route-tbl +
  ar-pool @ over !
  ar-len @ over cell+ !
  ar-xt @ swap 2 cells + !
  ar-len @ rpool-used +!
  1 route-count +! ;

\ ============================================================
\ Index Page (for "/" redirect)
\ ============================================================

create index-path 256 allot
variable index-path-len

: set-index ( addr u -- )
  dup index-path-len !
  index-path swap move ;

\ ============================================================
\ Route Dispatch
\
\ 1. Check route table for exact path match
\ 2. If "/" and index configured, serve index page
\ 3. Try static file from filesystem
\ 4. 404
\ ============================================================

variable dispatch-fd

: route-dispatch ( fd -- )
  dispatch-fd !
  get-path
  route-count @ 0 ?do
    2dup i route-path@ str= if
      2drop dispatch-fd @ i route-xt@ execute
      unloop exit
    then
  loop
  index-path-len @ 0> if
    2dup s" /" str= if
      2drop index-path index-path-len @
      dispatch-fd @ http-send-file if exit then
      dispatch-fd @ http-404 exit
    then
  then
  2dup dispatch-fd @ http-send-file if 2drop exit then
  2drop dispatch-fd @ http-404 ;

\ ============================================================
\ Server Lifecycle
\ ============================================================

: server-handle-client ( client-fd -- )
  dup http-read-request
  get-method s" OPTIONS" str= if
    dup http-options
  else
    dup route-dispatch
  then
  close-tcp drop ;

: server-start ( port -- )
  dup ." Sixth Server on port " . cr
  tcp-server-init
  ." Listening..." cr
  begin
    dup accept-tcp
    dup 0> if
      server-handle-client
    else
      drop
    then
  again ;

: server-init ( -- )
  0 route-count !
  0 index-path-len !
  0 rpool-used ! ;
