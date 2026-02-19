\ drivers/sqlite.fs -- SQLite driver for Sixth Server
\ Executes queries via the sqlite3 CLI subprocess.
\ Implements the standard driver contract (db-init, db-path!, db-exec, etc.)
\
\ Requires (caller must require before this file):
\   lib/core.fs   (str buffers, cmd buffer, parse-pipe, s>number?, n>str)

\ ============================================================
\ Database Path (permanent buffer)
\ ============================================================

create db-path-buf 256 allot
variable db-path-len

: db-path! ( addr u -- ) dup db-path-len ! db-path-buf swap move ;
: db-path ( -- addr u ) db-path-buf db-path-len @ ;

\ ============================================================
\ SQL Temp Files (permanent buffers)
\ ============================================================

create sql-output-buf 32 allot
variable sql-output-len
: sql-output ( -- addr u ) sql-output-buf sql-output-len @ ;

create sql-count-buf 32 allot
variable sql-count-len
: sql-count-output ( -- addr u ) sql-count-buf sql-count-len @ ;

\ ============================================================
\ Error Detection (stderr capture)
\ ============================================================

create sql-error-buf 32 allot
variable sql-error-len
: sql-error-path ( -- addr u ) sql-error-buf sql-error-len @ ;

variable sql-err-addr
variable sql-err-total
variable sql-ok-flag

: sql-ok? ( -- flag ) sql-ok-flag @ ;

: sql-error ( -- addr u )
  sql-ok? if s" " else sql-err-addr @ sql-err-total @ then ;

: sql-check-error ( -- )
  sql-error-path slurp-file
  dup 0> if
    sql-err-total ! sql-err-addr !
    false sql-ok-flag !
  else
    2drop true sql-ok-flag !
  then ;

\ ============================================================
\ Command Building (double-quote wrapping)
\ ============================================================

: sql-stderr ( -- )
  s"  2>" cmd+ sql-error-path cmd+ ;

: sql-cmd-query ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 -ascii " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  > " cmd+
  sql-output cmd+
  sql-stderr ;

: sql-cmd-count ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  s"  > " cmd+
  sql-count-output cmd+
  sql-stderr ;

\ ============================================================
\ Command Building (exec -- no output redirect)
\ ============================================================

: sql-cmd-exec ( db$ sql$ -- )
  cmd-reset
  s" sqlite3 " cmd+
  2swap cmd+
  s"  " cmd+ [char] " cmd-char
  cmd+
  [char] " cmd-char
  sql-stderr ;

\ ============================================================
\ Query Execution
\ ============================================================

: sql-exec ( db$ sql$ -- )
  sql-cmd-query cmd$ system sql-check-error ;

: sql-run ( db$ sql$ -- )
  sql-cmd-exec cmd$ system sql-check-error ;

\ ============================================================
\ Slurp-based result reading
\ Sixth has slurp-file but not read-line.
\ ============================================================

variable sql-slurp-addr
variable sql-slurp-total
variable sql-slurp-pos

: sql-open ( -- )
  sql-output slurp-file sql-slurp-total ! sql-slurp-addr !
  0 sql-slurp-pos ! ;

: sql-close ( -- ) ;

: sql-row? ( -- addr u flag )
  sql-slurp-pos @ sql-slurp-total @ >= if 0 0 false exit then
  sql-slurp-addr @ sql-slurp-pos @ +
  0
  begin
    sql-slurp-pos @ sql-slurp-total @ < while
    sql-slurp-addr @ sql-slurp-pos @ + c@ 30 = if
      1 sql-slurp-pos +!
      true exit
    then
    1 sql-slurp-pos +!
    1+
  repeat
  dup 0> ;

: sql-count ( db$ sql$ -- n )
  sql-cmd-count cmd$ system sql-check-error
  sql-ok? 0= if 0 exit then
  sql-count-output slurp-file
  dup 0> if
    2dup + 1- c@ 10 = if 1- then
  then
  s>number? if drop else 2drop 0 then ;

\ ============================================================
\ High-Level Iteration
\ ============================================================

: sql-each ( db$ sql$ xt -- )
  >r sql-exec sql-open
  begin sql-row? while
    dup 0> if
      r@ execute
    else
      2drop
    then
  repeat 2drop
  sql-close r> drop ;

: sql-dump ( db$ sql$ -- )
  sql-exec sql-open
  begin sql-row? while
    dup 0> if type cr else 2drop then
  repeat 2drop
  sql-close ;

\ ============================================================
\ SRM Convenience Layer (backward compatibility)
\ ============================================================

: srm-db ( -- addr u ) db-path ;

: srm-exec ( sql$ -- )
  srm-db 2swap sql-run ;

: srm-query ( sql$ -- )
  srm-db 2swap sql-exec ;

: srm-scalar ( sql$ -- n )
  srm-db 2swap sql-count ;

: srm-print ( sql$ -- )
  srm-db 2swap sql-dump ;

: srm-each ( sql$ xt -- )
  >r srm-db 2swap r> sql-each ;

: srm-int ( addr u n -- n )
  parse-pipe s>number? if drop >r 2drop r> else 2drop 2drop 0 then ;

\ ============================================================
\ Formatted Table Output (two-pass column alignment)
\ ============================================================

16 constant max-cols
create col-widths max-cols cells allot
variable col-count

: col-width@ ( n -- w ) cells col-widths + @ ;
: col-width! ( w n -- ) cells col-widths + ! ;

: col-widths-reset ( -- )
  0 col-count !
  max-cols 0 do 0 i col-width! loop ;

: col-widths-update ( addr u -- )
  0 >r
  begin
    2dup 31 field-length
    dup r@ col-width@ > if r@ col-width! else drop then
    r@ 1+ col-count @ > if r@ 1+ col-count ! then
    31 skip-to-delim
    r> 1+ >r
    dup 0=
  until
  2drop r> drop ;

: emit-pad ( n -- ) 0 ?do space loop ;

: emit-field-padded ( addr u width -- )
  over - >r type r> 0 max emit-pad ;

: emit-row ( addr u -- )
  col-count @ 0 ?do
    2dup i parse-pipe
    i col-width@ emit-field-padded
    i col-count @ 1- < if ."  | " then
  loop
  2drop cr ;

: emit-separator ( -- )
  col-count @ 0 ?do
    i col-width@ 0 ?do [char] - emit loop
    i col-count @ 1- < if ." -+-" then
  loop
  cr ;

variable tbl-db-a   variable tbl-db-u
variable tbl-sql-a  variable tbl-sql-u

: tbl-save ( db$ sql$ -- )
  tbl-sql-u ! tbl-sql-a ! tbl-db-u ! tbl-db-a ! ;

: tbl-db ( -- addr u ) tbl-db-a @ tbl-db-u @ ;
: tbl-sql ( -- addr u ) tbl-sql-a @ tbl-sql-u @ ;

: sql-table ( db$ sql$ -- )
  tbl-save col-widths-reset
  tbl-db tbl-sql sql-exec sql-open
  begin sql-row? while
    dup 0> if col-widths-update else 2drop then
  repeat 2drop sql-close
  tbl-db tbl-sql sql-exec sql-open
  begin sql-row? while
    dup 0> if emit-row else 2drop then
  repeat 2drop sql-close ;

: srm-table ( sql$ -- )
  srm-db 2swap sql-table ;

\ ============================================================
\ Dynamic SQL (build in str2-buf)
\ ============================================================

: sql-exec2 ( db$ -- )
  str2$ sql-exec ;

: sql-dump2 ( db$ -- )
  str2$ sql-dump ;

: sql-count2 ( db$ -- n )
  str2$ sql-count ;

\ ============================================================
\ Table Utilities
\ ============================================================

: sql-tables ( db$ -- )
  s" SELECT name FROM sqlite_master WHERE type='table' ORDER BY name" sql-dump ;

: sql-table-count ( db$ table$ -- n )
  str2-reset
  s" SELECT COUNT(*) FROM " str2+
  str2+
  str2$ sql-count ;

\ ============================================================
\ Config Words (set temp file paths from application code)
\ ============================================================

: sqlite-output-path! ( addr u -- )
  dup sql-output-len ! sql-output-buf swap move ;

: sqlite-count-path! ( addr u -- )
  dup sql-count-len ! sql-count-buf swap move ;

: sqlite-error-path! ( addr u -- )
  dup sql-error-len ! sql-error-buf swap move ;

\ ============================================================
\ Initialization
\ ============================================================

: sqlite-init ( -- )
  s" /tmp/srm-query.txt" sqlite-output-path!
  s" /tmp/srm-count.txt" sqlite-count-path!
  s" /tmp/srm-error.txt" sqlite-error-path!
  true sql-ok-flag ! ;

\ Backward compatibility alias (preserves original default db path)
: srm-init ( -- ) sqlite-init s" .ck-metrics.db" db-path! ;

\ ============================================================
\ Driver Contract Wrappers
\ Uniform interface for db-json.fs and application code.
\ ============================================================

: db-init    ( -- ) sqlite-init ;
: db-exec    ( db-a db-u sql-a sql-u -- ) sql-exec ;
: db-open    ( -- ) sql-open ;
: db-row?    ( -- addr u flag ) sql-row? ;
: db-close   ( -- ) sql-close ;
: db-ok?     ( -- flag ) sql-ok? ;
: db-error   ( -- addr u ) sql-error ;
