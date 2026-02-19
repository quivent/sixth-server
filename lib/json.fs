\ lib/json.fs -- JSON generation for Sixth Server
\ Uses the str-reset/str+/str$/str-char pattern from lib/core.fs.
\ The core string buffer is used for building JSON responses.
\
\ Requires (caller must require before this file):
\   lib/core.fs   (str-char, str+, n>str)

\ ============================================================
\ JSON nesting state (handles up to 8 levels deep)
\ ============================================================

8 constant JSON-MAX-DEPTH
create json-stack 64 allot  \ 8 cells for json-first flags
variable json-depth

: json-init ( -- )
  0 json-depth !
  JSON-MAX-DEPTH 0 do
    0 i cells json-stack + !
  loop ;

: json-first? ( -- flag )
  json-depth @ 0> if
    json-depth @ 1- cells json-stack + @
  else
    -1
  then ;

: json-clear-first ( -- )
  json-depth @ 0> if
    0 json-depth @ 1- cells json-stack + !
  then ;

: json-push-level ( -- )
  json-depth @ JSON-MAX-DEPTH < if
    -1 json-depth @ cells json-stack + !
    1 json-depth +!
  then ;

: json-pop-level ( -- )
  json-depth @ 0> if
    -1 json-depth +!
  then ;

\ ============================================================
\ Separator: emit comma before non-first elements
\ ============================================================

: json-sep ( -- )
  json-first? if
    json-clear-first
  else
    [char] , str-char
  then ;

\ ============================================================
\ Structural
\ ============================================================

: json-begin ( -- ) json-init ;

: json-open-obj ( -- )
  json-sep [char] { str-char json-push-level ;

: json-close-obj ( -- )
  json-pop-level [char] } str-char ;

: json-open-arr ( -- )
  json-sep [char] [ str-char json-push-level ;

: json-close-arr ( -- )
  json-pop-level [char] ] str-char ;

\ Value-position openers: use after json-key (no separator)
: json-key-obj ( -- ) [char] { str-char json-push-level ;
: json-key-arr ( -- ) [char] [ str-char json-push-level ;

\ ============================================================
\ JSON string escaping
\ ============================================================

: json-hex-nib ( n -- ) dup 10 < if [char] 0 + else 10 - [char] a + then str-char ;

: json-escape+ ( addr u -- )
  0 do
    dup i + c@
    dup [char] " = if drop [char] \ str-char [char] " str-char
    else dup [char] \ = if drop [char] \ str-char [char] \ str-char
    else dup 10 = if drop [char] \ str-char [char] n str-char
    else dup 13 = if drop [char] \ str-char [char] r str-char
    else dup  9 = if drop [char] \ str-char [char] t str-char
    else dup 32 < if
      [char] \ str-char [char] u str-char [char] 0 str-char [char] 0 str-char
      dup 4 rshift json-hex-nib 15 and json-hex-nib
    else str-char
    then then then then then then
  loop drop ;

\ ============================================================
\ Key/Value Primitives
\ ============================================================

: json-key ( addr u -- )
  json-sep
  [char] " str-char json-escape+ [char] " str-char
  [char] : str-char ;

: json-str ( addr u -- )
  json-sep
  [char] " str-char json-escape+ [char] " str-char ;

: json-str-val ( addr u -- )
  [char] " str-char json-escape+ [char] " str-char ;

: json-key-str ( k-addr k-u v-addr v-u -- )
  2swap json-key json-str-val ;

: json-num ( n -- )
  json-sep n>str str+ ;

: json-key-num ( addr u n -- )
  >r json-key r> n>str str+ ;

: json-comma ( -- ) [char] , str-char ;

: json-null ( -- ) json-sep s" null" str+ ;

: json-true ( -- ) json-sep s" true" str+ ;

: json-false ( -- ) json-sep s" false" str+ ;

: json-key-null ( addr u -- ) json-key s" null" str+ ;

\ Emit a JSON number with 2 decimal places from integer*100
\ e.g., 632 -> 6.32
: json-decimal2 ( n -- )
  json-sep
  dup 0< if [char] - str-char negate then
  dup 100 / n>str str+
  [char] . str-char
  100 mod dup 10 < if [char] 0 str-char then
  n>str str+ ;

: json-key-decimal2 ( addr u n -- )
  >r json-key r>
  dup 0< if [char] - str-char negate then
  dup 100 / n>str str+
  [char] . str-char
  100 mod dup 10 < if [char] 0 str-char then
  n>str str+ ;
