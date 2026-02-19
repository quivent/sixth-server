---
layout: default
title: Compiler Notes
nav_order: 7
---

# Compiler Notes

The bundled `bin/s3` is the Sixth compiler. It reads Forth source and produces native ARM64 Mach-O executables. These are the key gotchas to know when writing server code.

---

## `cells` is broken at interpret time

The word `cells` does not work correctly when used outside of a compiled word definition (i.e., at interpret time during `allot`).

**Broken:**
```forth
create buf 64 3 * cells allot    \ WRONG: cells is broken at interpret time
```

**Fix:** Use literal byte counts. On ARM64, a cell is 8 bytes.

```forth
create buf 1536 allot             \ 64 * 3 * 8 = 1536
```

The framework source uses this pattern throughout (e.g., `create route-tbl 1536 allot` for 64 entries * 3 cells * 8 bytes).

---

## `compare` crashes in `do`/`loop`

The built-in `compare` word uses the return stack internally. Since `do`/`loop` also pushes parameters onto the return stack, calling `compare` inside a loop corrupts the loop state and crashes.

**Broken:**
```forth
: find-route ( addr u -- n )
  route-count @ 0 do
    2dup i route-path@ compare 0= if   \ CRASH
      2drop i unloop exit
    then
  loop
  2drop -1 ;
```

**Fix:** Use `str=` from `lib/core.fs`, which avoids the return stack entirely:

```forth
: find-route ( addr u -- n )
  route-count @ 0 do
    2dup i route-path@ str= if         \ safe
      2drop i unloop exit
    then
  loop
  2drop -1 ;
```

The framework provides `str=` specifically for this reason, and all internal dispatch code uses it.

---

## `>r`/`r>` cannot cross `do`/`loop` boundaries

The `do`/`loop` construct pushes the loop index and limit onto the return stack. If you push a value with `>r` before entering a loop, `r>` inside the loop will pop the loop parameters instead of your value.

**Broken:**
```forth
: bad-handler ( fd -- )
  >r                          \ push fd onto return stack
  10 0 do
    i . cr
  loop
  r> http-200 ;               \ CRASH: r> pops loop garbage, not fd
```

**Fix:** Use a variable to pass data across loop boundaries:

```forth
variable my-fd

: good-handler ( fd -- )
  my-fd !                     \ save fd in variable
  10 0 do
    i . cr
  loop
  my-fd @ http-200 ;
```

For the common case of saving `fd` across non-looping code, `>r`/`r>` works fine:

```forth
: simple-handler ( fd -- )
  >r str-reset json-begin json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj r> http-200 ;   \ safe: no do/loop between >r and r>
```

---

## Top-level code is silently skipped

The Sixth compiler only executes code that is inside word definitions. Any code written at the top level (outside a `:` ... `;` definition) is silently ignored during compilation.

**Broken:**
```forth
sqlite-init                      \ silently skipped
s" my.db" db-path!              \ silently skipped
server-init                      \ silently skipped
8080 server-start                \ silently skipped
\ binary compiles but does nothing
```

**Fix:** Wrap all initialization in a word and call it:

```forth
: main ( -- )
  sqlite-init
  s" my.db" db-path!
  server-init
  8080 server-start ;
main                              \ this call is compiled and executed
```

The convention is to define a `: main ... ;` word and then call `main` as the last line of the source file. The call to `main` at the top level is itself compiled into the binary's entry point.
