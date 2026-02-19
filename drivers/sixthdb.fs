\ drivers/sixthdb.fs -- SixthDB direct-link driver for Sixth Server
\ Links SixthDB Forth modules directly. No subprocess, no temp files.
\ Implements the standard driver contract.
\
\ Requires (caller must require before this file):
\   lib/core.fs                    (str buffers, str2-buf, n>str, parse-pipe)
\   modules/sixthdb/sql-exec.fs    (pulls in entire SixthDB module chain)
\
\ IMPORTANT: Word ordering matters. SixthDB defines db-open, db-close,
\ db-path!, str=, n>str. The driver's internal words (sdb-exec etc.) are
\ defined BEFORE the contract words, so they capture SixthDB's originals
\ at compile time. The contract words (db-open, db-close, etc.) shadow
\ SixthDB's versions for all code compiled afterward (server, db-json).
\
\ The safe str= is redefined AFTER SixthDB modules but BEFORE the
\ server's route-dispatch, which uses str= inside do/loop.

\ ============================================================
\ SixthDB Module Loading
\ ============================================================

require modules/sixthdb/sql-exec.fs

\ ============================================================
\ Redefine safe str= AFTER SixthDB modules
\ SixthDB's table.fs defines: : str= compare 0= ;
\ compare uses the return stack and crashes inside do/loop.
\ ============================================================

variable str=-n

: str= ( a1 u1 a2 u2 -- flag )
  rot over <> if 2drop drop false exit then
  str=-n !
  begin str=-n @ 0> while
    over c@ over c@ <> if 2drop false exit then
    1+ swap 1+ swap
    -1 str=-n +!
  repeat
  2drop true ;

\ ============================================================
\ Driver State
\ ============================================================

variable sdb-ok-flag
variable sdb-db-open?     \ true once database file is open
variable sdb-grouped      \ true if last query used GROUP BY
variable sdb-rs-pos       \ iteration position
variable sdb-rs-rows      \ total rows in result set
variable sdb-rs-cols      \ column count

\ Error message (static buffer, filled on failure)
create sdb-err-buf 256 allot
variable sdb-err-len

\ ============================================================
\ Database Open (lazy, once per path)
\
\ SixthDB uses mmap — opening is expensive, so we open once
\ and keep it resident. On first db-exec call (or path change),
\ we open the database. Subsequent queries reuse the mmap.
\
\ At this point in the file, db-open resolves to SixthDB's
\ ( c-addr u -- ) because the contract hasn't been redefined yet.
\ ============================================================

create sdb-cur-path 256 allot
variable sdb-cur-path-len

: sdb-path-changed? ( addr u -- flag )
  sdb-db-open? @ 0= if 2drop true exit then
  dup sdb-cur-path-len @ <> if 2drop true exit then
  sdb-cur-path over compare 0<> ;

: sdb-ensure-open ( addr u -- )
  2dup sdb-path-changed? 0= if 2drop exit then
  sdb-db-open? @ if db-close then
  2dup sdb-cur-path-len ! sdb-cur-path swap move
  sdb-cur-path sdb-cur-path-len @ db-open
  true sdb-db-open? ! ;

\ ============================================================
\ Query Execution
\
\ Runs SixthDB's full SQL pipeline WITHOUT calling sql-exec
\ (which emits results to stdout). Instead, we loop exec-op
\ directly and leave the result set in rs-buf / grp-buf.
\ ============================================================

: sdb-exec ( db-a db-u sql-a sql-u -- )
  2swap sdb-ensure-open
  \ Initialize expression evaluator
  expr-init
  \ Parse SQL and generate execution plan
  sql-parse-plan
  \ Bind storage for non-DDL statements
  qd-stmt-type@
  dup QD-CREATE-TABLE = over QD-DROP-TABLE = or if
    drop
  else
    drop
    exec-bind-table 0= if
      false sdb-ok-flag !
      s" Table not found" dup sdb-err-len !
      sdb-err-buf swap move
      0 sdb-rs-rows ! 0 sdb-rs-cols !
      exit
    then
  then
  \ Execute plan operators (no stdout emission)
  false sdb-grouped !
  ep-count @ 0 do
    i ep-op-type@ EP-GROUP = if true sdb-grouped ! then
    i exec-op
  loop
  \ Capture result dimensions
  sdb-grouped @ if
    grp-count @ sdb-rs-rows !
    2 sdb-rs-cols !
  else
    rs-count @ sdb-rs-rows !
    qd-stmt-type@ QD-SELECT = if
      tbl-cols sdb-rs-cols !
    else
      0 sdb-rs-cols !
    then
  then
  true sdb-ok-flag !
  0 sdb-rs-pos ! ;

\ ============================================================
\ Result Iteration
\
\ Formats each row into str2-buf with ASCII 31 field separators.
\ Same wire format as SQLite -ascii mode, so parse-pipe works
\ identically across all drivers.
\
\ Handles both regular results (rs-buf) and grouped results
\ (grp-buf) transparently.
\ ============================================================

: sdb-open ( -- )
  0 sdb-rs-pos ! ;

: sdb-close ( -- ) ;

: sdb-row? ( -- addr u flag )
  sdb-rs-pos @ sdb-rs-rows @ >= if 0 0 false exit then
  str2-reset
  sdb-grouped @ if
    \ GROUP BY result: key | count
    sdb-rs-pos @ grp-key@ n>str str2+
    31 str2-char
    sdb-rs-pos @ grp-val@ n>str str2+
  else
    \ Regular result: col0 | col1 | ... | colN
    sdb-rs-pos @ rs-val@
    sdb-rs-cols @ 0 ?do
      i 0> if 31 str2-char then
      dup i row-get n>str str2+
    loop drop
  then
  1 sdb-rs-pos +!
  str2$ true ;

\ ============================================================
\ Initialization
\ ============================================================

: sixthdb-init ( -- )
  true sdb-ok-flag !
  false sdb-db-open? !
  false sdb-grouped !
  0 sdb-rs-pos !
  0 sdb-rs-rows !
  0 sdb-rs-cols !
  0 sdb-cur-path-len !
  0 sdb-err-len ! ;

\ ============================================================
\ Driver Contract
\
\ These definitions shadow SixthDB's db-open, db-close, db-path!
\ for all code compiled AFTER this point (server.fs, db-json.fs).
\ SixthDB's internal code retains its original definitions.
\ ============================================================

create db-path-buf 256 allot
variable db-path-len

: db-path! ( addr u -- ) dup db-path-len ! db-path-buf swap move ;
: db-path ( -- addr u ) db-path-buf db-path-len @ ;

: db-init    ( -- ) sixthdb-init ;
: db-exec    ( db-a db-u sql-a sql-u -- ) sdb-exec ;
: db-open    ( -- ) sdb-open ;
: db-row?    ( -- addr u flag ) sdb-row? ;
: db-close   ( -- ) sdb-close ;
: db-ok?     ( -- flag ) sdb-ok-flag @ ;
: db-error   ( -- addr u )
  db-ok? if s" " else sdb-err-buf sdb-err-len @ then ;

\ ============================================================
\ SixthDB-Native Words
\
\ Available alongside the driver contract for handlers that
\ want direct B-tree access without SQL parsing overhead.
\ These use SixthDB's table.fs words directly.
\
\ Usage in a handler:
\   s" users" use-table drop
\   42 tbl-get if
\     dup 0 row-get   \ get column 0
\     ...
\     drop
\   then
\
\ SixthDB's table-level words (use-table, tbl-get, tbl-insert,
\ tbl-delete, tbl-scan, row-get, row-set, row-clear, etc.) are
\ available directly since we linked the modules above.
\ ============================================================
