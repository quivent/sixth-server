\ lib/core.fs -- Core utilities for Sixth Server
\ Layer 0: String buffers, number conversion, field parsing.
\ Zero external dependencies. Used by all other layers.

\ ============================================================
\ Standard Constants (not built into Sixth primitives)
\ ============================================================

-1 constant true
0 constant false

\ ============================================================
\ String Buffer (primary output buffer)
\ ============================================================

262144 constant str-max
create str-buf str-max allot
variable str-len
variable str-overflow

: str-reset ( -- ) 0 str-len ! false str-overflow ! ;

: str+ ( addr u -- )
  dup str-len @ + str-max <= if
    str-buf str-len @ + swap dup str-len +! move
  else
    2drop true str-overflow !
  then ;

: str$ ( -- addr u ) str-buf str-len @ ;

: str-char ( c -- )
  str-len @ str-max < if
    str-buf str-len @ + c!
    1 str-len +!
  else
    drop true str-overflow !
  then ;

\ ============================================================
\ Second String Buffer (for nested operations / dynamic SQL)
\ ============================================================

4096 constant str2-max
create str2-buf str2-max allot
variable str2-len
variable str2-overflow

: str2-reset ( -- ) 0 str2-len ! false str2-overflow ! ;

: str2+ ( addr u -- )
  dup str2-len @ + str2-max <= if
    str2-buf str2-len @ + swap dup str2-len +! move
  else
    2drop true str2-overflow !
  then ;

: str2$ ( -- addr u ) str2-buf str2-len @ ;

\ ============================================================
\ Number Conversion (n -> string)
\ ============================================================

: n>str ( n -- addr u ) dup abs 0 <# #s rot sign #> ;

\ ============================================================
\ Number Parsing (string -> n)
\ Sixth lacks s>number?. Implement decimal parser.
\ ( addr u -- addr u false | n 0 true )
\ ============================================================

: s>number? ( addr u -- addr u false | n 0 true )
  dup 0= if false exit then
  2dup
  over c@ [char] - = dup >r
  if 1 /string then
  dup 0= if r> drop 2drop false exit then
  0 >r
  begin dup 0> while
    over c@ [char] 0 -
    dup 0 < over 9 > or if
      drop 2drop r> drop r> drop false exit
    then
    r> 10 * + >r
    1 /string
  repeat
  2drop 2drop
  r> r> if negate then
  0 true ;

\ ============================================================
\ Field Parsing (pipe-delimited rows, ASCII 31 separator)
\ ============================================================

: skip-to-delim ( addr u delim -- addr' u' )
  >r
  begin
    dup 0> while
    over c@ r@ = if 1 /string r> drop exit then
    1 /string
  repeat
  r> drop ;

: field-length ( addr u delim -- n )
  0 >r -rot
  begin dup 0> while
    over c@ 3 pick = if 2drop drop r> exit then
    1 /string r> 1+ >r
  repeat
  2drop drop r> ;

: parse-pipe ( addr u n -- addr u field-addr field-u )
  >r 2dup r>
  0 ?do 31 skip-to-delim loop
  2dup 31 field-length >r drop r> ;

\ ============================================================
\ Command Buffer (separate from str-buf to avoid clobbering)
\ ============================================================

4096 constant cmd-max
create cmd-buf cmd-max allot
variable cmd-len

: cmd-reset ( -- ) 0 cmd-len ! ;

: cmd+ ( addr u -- )
  dup cmd-len @ + cmd-max <= if
    cmd-buf cmd-len @ + swap dup cmd-len +! move
  else
    2drop
  then ;

: cmd-char ( c -- )
  cmd-len @ cmd-max < if
    cmd-buf cmd-len @ + c!
    1 cmd-len +!
  else
    drop
  then ;

: cmd$ ( -- addr u ) cmd-buf cmd-len @ ;

\ ============================================================
\ Row Helpers
\ ============================================================

: row-field ( addr u n -- addr u field-addr field-u )
  parse-pipe ;

: row-int ( addr u n -- addr u value )
  parse-pipe s>number? if drop else 2drop 0 then ;

: row-count ( addr u -- n )
  1 >r
  begin dup 0> while
    over c@ 31 = if r> 1+ >r then
    1 /string
  repeat
  2drop r> ;

\ ============================================================
\ String Equality (safe inside do/loop -- no return stack use)
\ compare crashes in do/loop in Sixth. This word avoids it.
\ ============================================================

variable str=-n

: str= ( a1 u1 a2 u2 -- flag )
  rot over <> if 2drop drop false exit then
  \ lengths match
  str=-n !
  begin str=-n @ 0> while
    over c@ over c@ <> if 2drop false exit then
    1+ swap 1+ swap
    -1 str=-n +!
  repeat
  2drop true ;
