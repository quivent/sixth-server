\ dashboard-server.fs -- CK dashboard server built on lib/server.fs
\
\ Compile: ./bin/s3 server/dashboard-server.fs bin/dashboard-server
\ Run:     ./bin/dashboard-server [port]
\ Default: http://localhost:3844
\
\ API server for the CK compiler benchmark dashboard.
\ Serves JSON endpoints from .ck-metrics.db and static files
\ from the dashboard/ directory.

require modules/srm/srm.fs
require modules/srm/db.fs
require lib/tcp.fs
require lib/http.fs
require lib/json.fs
require lib/server.fs

\ ============================================================
\ Database
\ ============================================================

: dashboard-db ( -- addr u ) s" .ck-metrics.db" ;

\ ============================================================
\ DSL Endpoints (Shape A: pure SQL -> JSON array of objects)
\ ============================================================

: handle-trajectory ( fd -- )
  field-reset
  F_STR  s" milestone" +field
  F_INT  s" geo"       +field
  F_INT  s" rw"        +field
  F_STR  s" status"    +field
  s" SELECT milestone_name,geomean_target,realwork_target,status FROM trajectory_milestones ORDER BY milestone_order"
  sql-json-array ;

: handle-milestones ( fd -- )
  field-reset
  F_STR s" name"     +field
  F_STR s" criteria" +field
  F_STR s" status"   +field
  F_STR s" blocker"  +field
  s" SELECT milestone,criteria,CASE WHEN is_met=1 THEN 'done' ELSE 'todo' END,key_blocker FROM milestone_criteria ORDER BY milestone ASC"
  sql-json-array ;

: handle-root-causes ( fd -- )
  field-reset
  F_STR s" cause"      +field
  F_STR s" examples"   +field
  F_STR s" fix"        +field
  F_INT s" benchmarks" +field
  F_STR s" fixStatus"  +field
  F_STR s" ratioRange" +field
  s" SELECT root_cause,benchmark_names,fix_roadmap_item,benchmark_count,CASE WHEN fix_roadmap_item IS NOT NULL AND fix_roadmap_item!='' THEN 'linked' ELSE '' END,ratio_range_text FROM gap_analysis ORDER BY benchmark_count DESC"
  sql-json-array ;

: handle-history ( fd -- )
  field-reset
  F_STR s" opt"     +field
  F_INT s" before"  +field
  F_INT s" after"   +field
  F_INT s" saved"   +field
  F_INT s" session" +field
  s" SELECT title,before_geomean,after_geomean,instruction_savings,session_num FROM optimization_history ORDER BY session_num"
  sql-json-array ;

: handle-landmines ( fd -- )
  s" SELECT rule FROM architectural_constraints ORDER BY constraint_name ASC"
  sql-json-strings ;

: handle-failed ( fd -- )
  field-reset
  F_STR s" what" +field
  F_STR s" why"  +field
  s" SELECT attempt_name,reason_why FROM failed_attempts ORDER BY attempt_name"
  sql-json-array ;

: handle-benchmark-results ( fd -- )
  field-reset
  F_STR  s" name"             +field
  F_STR  s" status"           +field
  F_INT  s" compile_sixth_ms" +field
  F_INT  s" compile_gcc_ms"   +field
  F_INT  s" run_sixth_ms"     +field
  F_INT  s" run_gcc_ms"       +field
  F_DEC2 s" ratio"            +field
  s" SELECT br.name,br.status,br.compile_sixth_ms,br.compile_gcc_ms,br.run_sixth_ms,br.run_gcc_ms,CAST(br.ratio*100 AS INTEGER) FROM benchmark_results br WHERE br.run_timestamp=(SELECT run_timestamp FROM benchmark_results ORDER BY run_timestamp DESC LIMIT 1) ORDER BY br.name ASC"
  sql-json-array ;

: handle-forth-bugs ( fd -- )
  field-reset
  F_INT s" id"          +field
  F_STR s" title"       +field
  F_STR s" severity"    +field
  F_STR s" category"    +field
  F_STR s" file"        +field
  F_STR s" line_ref"    +field
  F_STR s" effect"      +field
  F_STR s" description" +field
  F_STR s" status"      +field
  F_INT s" fix_order"   +field
  s" SELECT id,title,severity,category,file,line_ref,effect,description,status,fix_order FROM forth_dashboard_bugs ORDER BY fix_order ASC,id ASC"
  sql-json-array ;

: handle-sixthdb-bugs ( fd -- )
  field-reset
  F_INT s" id"          +field
  F_STR s" title"       +field
  F_STR s" severity"    +field
  F_STR s" category"    +field
  F_STR s" file"        +field
  F_STR s" line_ref"    +field
  F_STR s" effect"      +field
  F_STR s" description" +field
  F_STR s" status"      +field
  F_INT s" fix_order"   +field
  s" SELECT id,title,severity,category,file,line_ref,effect,description,status,fix_order FROM sixthdb_bugs ORDER BY fix_order ASC,id ASC"
  sql-json-array ;

: handle-impl-plans ( fd -- )
  field-reset
  F_INT s" id"            +field
  F_STR s" roadmap_item"  +field
  F_INT s" step_number"   +field
  F_STR s" title"         +field
  F_STR s" description"   +field
  F_STR s" file_target"   +field
  F_STR s" status"        +field
  F_STR s" step_type"     +field
  s" SELECT id,roadmap_item,step_number,title,description,file_target,status,step_type FROM implementation_plans ORDER BY roadmap_item,step_number"
  sql-json-array ;

: handle-design-docs ( fd -- )
  field-reset
  F_INT s" id"           +field
  F_STR s" roadmap_id"   +field
  F_STR s" title"        +field
  F_STR s" status"       +field
  F_STR s" summary"      +field
  F_STR s" motivation"   +field
  F_STR s" design"       +field
  F_STR s" dependencies" +field
  F_STR s" risks"        +field
  F_STR s" verification" +field
  F_STR s" created_at"   +field
  F_STR s" updated_at"   +field
  F_STR s" parent_id"    +field
  s" SELECT id,roadmap_id,title,status,summary,motivation,design,dependencies,risks,verification,created_at,updated_at,parent_id FROM design_docs ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'draft' THEN 1 WHEN 'approved' THEN 2 WHEN 'done' THEN 3 ELSE 4 END,updated_at DESC"
  sql-json-array ;

\ ============================================================
\ Custom Endpoints (require special logic)
\ ============================================================

\ --- /api/stats --- (3 queries, 9 temp vars, merged object)
variable stats-geo
variable stats-rw
variable stats-total
variable stats-pass
variable stats-wins
variable stats-rfail
variable stats-cfail
variable stats-grfail
variable stats-wrong

: stats-query1 ( -- )
  dashboard-db
  s" WITH l AS (SELECT name,status,ratio,run_sixth_ms,run_gcc_ms,ROW_NUMBER() OVER (PARTITION BY name ORDER BY run_timestamp DESC) as rn FROM benchmark_results),p AS (SELECT * FROM l WHERE rn=1 AND status='PASS' AND ratio>0) SELECT CAST(ROUND(EXP(AVG(LN(ratio)))*100) AS INTEGER),CAST(COALESCE(ROUND(EXP(AVG(CASE WHEN run_sixth_ms>30 AND run_gcc_ms>30 THEN LN(ratio) END))*100),0) AS INTEGER),SUM(CASE WHEN ratio<1.0 THEN 1 ELSE 0 END) FROM p"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      0 row-int stats-geo !
      1 row-int stats-rw !
      2 row-int stats-wins !
    then 2drop
  else 2drop then
  sql-close ;

: stats-query2 ( -- )
  dashboard-db
  s" WITH l AS (SELECT status,ROW_NUMBER() OVER (PARTITION BY name ORDER BY run_timestamp DESC) as rn FROM benchmark_results) SELECT COUNT(*),SUM(CASE WHEN status='PASS' THEN 1 ELSE 0 END),SUM(CASE WHEN status='RFAIL' THEN 1 ELSE 0 END),SUM(CASE WHEN status='CFAIL' THEN 1 ELSE 0 END),SUM(CASE WHEN status='GRFAIL' THEN 1 ELSE 0 END),SUM(CASE WHEN status NOT IN ('PASS','CFAIL','RFAIL','GRFAIL') THEN 1 ELSE 0 END) FROM l WHERE rn=1"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      0 row-int stats-total !
      1 row-int stats-pass !
      2 row-int stats-rfail !
      3 row-int stats-cfail !
      4 row-int stats-grfail !
      5 row-int stats-wrong !
    then 2drop
  else 2drop then
  sql-close ;

: handle-stats ( fd -- )
  >r
  0 stats-geo ! 0 stats-rw ! 0 stats-total ! 0 stats-pass !
  0 stats-wins ! 0 stats-rfail ! 0 stats-cfail ! 0 stats-grfail ! 0 stats-wrong !
  stats-query1 stats-query2
  str-reset json-begin
  json-open-obj
  stats-geo @ s" geomean" rot json-key-decimal2
  stats-total @ s" benchmark_total" rot json-key-num
  stats-pass @ s" benchmark_pass" rot json-key-num
  stats-wins @ s" benchmark_wins" rot json-key-num
  dashboard-db
  s" SELECT compile_time_sixth_ms,binary_size_bytes FROM core_metrics ORDER BY session_id DESC LIMIT 1"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      0 row-int s" compile_time_ms" rot json-key-num
      1 row-int 1024 / s" binary_size_kb" rot json-key-num
    then 2drop
  else
    2drop 0 s" compile_time_ms" rot json-key-num 0 s" binary_size_kb" rot json-key-num
  then
  sql-close
  stats-rw @ s" geomean_realwork" rot json-key-decimal2
  stats-pass @ s" status_pass" rot json-key-num
  stats-wrong @ s" status_wrong" rot json-key-num
  stats-rfail @ s" status_rfail" rot json-key-num
  stats-cfail @ s" status_cfail" rot json-key-num
  stats-grfail @ s" status_grfail" rot json-key-num
  json-close-obj
  r> http-200 ;

\ --- /api/benchmarks --- (keyed object, not array)
: handle-benchmarks ( fd -- )
  >r
  str-reset json-begin
  dashboard-db
  s" SELECT band_wins_count,band_close_count,band_moderate_count,band_large_count,band_severe_count,band_catastrophic_count,band_wins_examples,band_close_examples,band_moderate_examples,band_large_examples,band_severe_examples,band_catastrophic_examples FROM benchmark_distribution ORDER BY session_id DESC LIMIT 1"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      json-open-obj
      s" wins" json-key json-key-obj 0 row-int s" count" rot json-key-num  6 parse-pipe s" examples" json-key json-str-val json-close-obj
      s" close" json-key json-key-obj 1 row-int s" count" rot json-key-num  7 parse-pipe s" examples" json-key json-str-val json-close-obj
      s" moderate" json-key json-key-obj 2 row-int s" count" rot json-key-num  8 parse-pipe s" examples" json-key json-str-val json-close-obj
      s" large" json-key json-key-obj 3 row-int s" count" rot json-key-num  9 parse-pipe s" examples" json-key json-str-val json-close-obj
      s" severe" json-key json-key-obj 4 row-int s" count" rot json-key-num 10 parse-pipe s" examples" json-key json-str-val json-close-obj
      s" catastrophic" json-key json-key-obj 5 row-int s" count" rot json-key-num 11 parse-pipe s" examples" json-key json-str-val json-close-obj
      2drop
      json-close-obj
    else
      2drop s" {}" str+
    then
  else
    2drop s" {}" str+
  then
  sql-close
  r> http-200 ;

\ --- /api/roadmap --- (tier-grouped nested structure)
create prev-tier 64 allot
variable prev-tier-len

: handle-roadmap ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-arr
  0 prev-tier-len !
  dashboard-db
  s" SELECT ts.tier_name,ri.item_name,COALESCE(ri.status,'todo'),COALESCE(ri.description,''),ri.estimated_impact_percent,COALESCE(CAST(ri.benchmarks_affected AS TEXT),'') FROM roadmap_items ri JOIN tier_status ts ON ri.tier=ts.tier_number ORDER BY ri.tier,ri.priority,ri.item_name"
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      0 parse-pipe
      2dup prev-tier prev-tier-len @ compare 0= if
        2drop
      else
        prev-tier-len @ 0> if json-close-arr json-close-obj then
        json-open-obj
        2dup s" tier" json-key json-str-val
        s" items" json-key json-key-arr
        dup prev-tier-len ! prev-tier swap move
      then
      json-open-obj
      1 parse-pipe s" name" json-key json-str-val
      2 parse-pipe s" status" json-key json-str-val
      3 parse-pipe s" impact" json-key json-str-val
      4 row-int s" estimated_impact" rot json-key-num
      5 parse-pipe s" benchmarks" json-key json-str-val
      2drop
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  sql-close
  prev-tier-len @ 0> if json-close-arr json-close-obj then
  json-close-arr
  http-end-chunked ;

\ --- /api/benchmark-history --- (wrapped: {"runs": [...]})
: handle-bench-history ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-obj
  s" runs" json-key
  json-key-arr
  dashboard-db
  s" SELECT timestamp,compiler,total,passed,cfail,rfail,grfail,sixth_wins,CAST(runtime_ratio*100 AS INTEGER),CAST(geomean*100 AS INTEGER),CAST(compile_ratio*100 AS INTEGER),CAST(pass_rate*100 AS INTEGER),wall_time_ms FROM benchmark_history ORDER BY timestamp DESC"
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      json-open-obj
      0 parse-pipe s" timestamp" json-key json-str-val
      1 parse-pipe s" compiler" json-key json-str-val
      2 row-int s" total" rot json-key-num
      3 row-int s" passed" rot json-key-num
      4 row-int s" cfail" rot json-key-num
      5 row-int s" rfail" rot json-key-num
      6 row-int s" grfail" rot json-key-num
      7 row-int s" sixth_wins" rot json-key-num
      8 row-int s" runtime_ratio" rot json-key-decimal2
      9 row-int s" geomean" rot json-key-decimal2
      10 row-int s" compile_ratio" rot json-key-decimal2
      11 row-int s" pass_rate" rot json-key-decimal2
      12 row-int s" wall_time_ms" rot json-key-num
      2drop
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  sql-close
  json-close-arr
  json-close-obj
  http-end-chunked ;

\ --- /api/failing-benchmarks --- (timestamp + failures array)
: handle-failing-benchmarks ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-obj
  dashboard-db
  s" SELECT run_timestamp FROM benchmark_results ORDER BY run_timestamp DESC LIMIT 1"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      0 parse-pipe s" run_timestamp" json-key json-str-val
    else
      s" run_timestamp" json-key-null
    then
    2drop
  else
    2drop s" run_timestamp" json-key-null
  then
  sql-close
  s" failures" json-key json-key-arr
  dashboard-db
  s" SELECT br.name,br.status,br.compile_sixth_ms,br.run_sixth_ms,br.run_gcc_ms,ft.first_seen,ft.last_pass,ft.failing_since,ft.speculative_reason,ft.proposed_fix,ft.priority,ft.notes FROM benchmark_results br LEFT JOIN benchmark_failure_tracking ft ON br.name=ft.name WHERE br.run_timestamp=(SELECT run_timestamp FROM benchmark_results ORDER BY run_timestamp DESC LIMIT 1) AND br.status!='PASS' ORDER BY CASE br.status WHEN 'CFAIL' THEN 1 WHEN 'GRFAIL' THEN 2 WHEN 'RFAIL' THEN 3 END,br.name ASC"
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      json-open-obj
      0 parse-pipe s" name" json-key json-str-val
      1 parse-pipe s" fail_type" json-key json-str-val
      2 row-int s" compile_sixth_ms" rot json-key-num
      3 row-int s" run_sixth_ms" rot json-key-num
      4 row-int s" run_gcc_ms" rot json-key-num
      5 parse-pipe s" first_seen" json-key json-str-val
      6 parse-pipe s" last_pass" json-key json-str-val
      7 parse-pipe s" failing_since" json-key json-str-val
      8 parse-pipe s" speculative_reason" json-key json-str-val
      9 parse-pipe s" proposed_fix" json-key json-str-val
      10 parse-pipe s" priority" json-key json-str-val
      11 parse-pipe s" notes" json-key json-str-val
      2drop
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  sql-close
  json-close-arr
  json-close-obj
  http-end-chunked ;

\ --- /api/priorities --- (timestamp + priorities array)
: handle-priorities ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-obj
  dashboard-db
  s" SELECT run_timestamp FROM benchmark_results ORDER BY run_timestamp DESC LIMIT 1"
  sql-exec sql-open
  sql-row? if
    dup 0> if
      0 parse-pipe s" run_timestamp" json-key json-str-val
    else
      s" run_timestamp" json-key-null
    then
    2drop
  else
    2drop s" run_timestamp" json-key-null
  then
  sql-close
  s" priorities" json-key json-key-arr
  dashboard-db
  s" SELECT name,status,CAST(ratio*100 AS INTEGER),run_sixth_ms,run_gcc_ms FROM benchmark_results WHERE run_timestamp=(SELECT run_timestamp FROM benchmark_results ORDER BY run_timestamp DESC LIMIT 1) AND status='PASS' AND ratio IS NOT NULL ORDER BY ratio DESC LIMIT 5"
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      json-open-obj
      0 parse-pipe s" name" json-key json-str-val
      1 parse-pipe s" status" json-key json-str-val
      2 row-int s" ratio" rot json-key-decimal2
      3 row-int s" run_sixth_ms" rot json-key-num
      4 row-int s" run_gcc_ms" rot json-key-num
      2drop
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  sql-close
  json-close-arr
  json-close-obj
  http-end-chunked ;

\ --- /api/bench-fixes --- (object keyed by benchmark name)
: handle-bench-fixes ( fd -- )
  >r
  str-reset json-begin
  json-open-obj
  dashboard-db
  s" SELECT benchmark_name,fix_category,note FROM bench_fixes ORDER BY benchmark_name ASC"
  sql-exec sql-open
  begin sql-row? while
    dup 0> if
      0 parse-pipe 2dup json-key json-key-obj
      1 parse-pipe s" fix" json-key json-str-val
      2 parse-pipe s" note" json-key json-str-val
      2drop json-close-obj
    else
      2drop
    then
  repeat 2drop
  sql-close
  json-close-obj
  r> http-200 ;

\ --- /api/commands --- (shell-out to list .claude/commands/*.md)
variable cmd-slurp-addr
variable cmd-slurp-total
variable cmd-slurp-pos

: cmd-row? ( -- addr u flag )
  cmd-slurp-pos @ cmd-slurp-total @ >= if 0 0 false exit then
  cmd-slurp-addr @ cmd-slurp-pos @ +
  0
  begin
    cmd-slurp-pos @ cmd-slurp-total @ < while
    cmd-slurp-addr @ cmd-slurp-pos @ + c@ 30 = if
      1 cmd-slurp-pos +!
      true exit
    then
    1 cmd-slurp-pos +!
    1+
  repeat
  dup 0> ;

: handle-commands ( fd -- )
  http-200-chunked
  str-reset json-begin
  json-open-arr
  cmd-reset
  s" for f in .claude/commands/*.md; do [ -f " cmd+
  [char] " cmd-char s" $f" cmd+  [char] " cmd-char
  s"  ] || continue; n=$(basename " cmd+
  [char] " cmd-char s" $f" cmd+ [char] " cmd-char
  s"  .md); t=$(head -5 " cmd+
  [char] " cmd-char s" $f" cmd+ [char] " cmd-char
  s"  | grep -m1 '^# ' | sed 's/^# //'); printf '%s" cmd+
  31 cmd-char s" %s" cmd+ 30 cmd-char
  s" ' " cmd+ [char] " cmd-char s" $n" cmd+ [char] " cmd-char
  s"  " cmd+ [char] " cmd-char s" ${t:-$n}" cmd+ [char] " cmd-char
  s" ; done > /tmp/dash-cmdlist.txt" cmd+
  cmd$ system
  s" /tmp/dash-cmdlist.txt" slurp-file
  cmd-slurp-total ! cmd-slurp-addr !
  0 cmd-slurp-pos !
  begin cmd-row? while
    dup 0> if
      json-open-obj
      0 parse-pipe s" name" json-key json-str-val
      1 parse-pipe s" title" json-key json-str-val
      2drop
      json-close-obj
      chunk-check
    else
      2drop
    then
  repeat 2drop
  json-close-arr
  http-end-chunked ;

\ --- /api/ratio-bands --- (static data, no SQL)
: emit-band ( band-addr band-u min max -- )
  >r >r
  json-open-obj
  s" band" json-key json-str-val
  s" min" r> json-key-num
  s" max" r> json-key-num
  json-close-obj ;

: handle-ratio-bands ( fd -- )
  >r
  str-reset json-begin
  json-open-arr
  s" <1x"     0 1      emit-band
  s" 1-2x"    1 2      emit-band
  s" 2-5x"    2 5      emit-band
  s" 5-20x"   5 20     emit-band
  s" 20-100x" 20 100   emit-band
  s" 100x+"   100 999999 emit-band
  json-close-arr
  r> http-200 ;

\ --- /api/events --- (SSE)
: handle-events ( fd -- ) http-sse ;

\ --- /health ---
: handle-health ( fd -- )
  >r
  str-reset json-begin
  json-open-obj
  s" status" s" ok" json-key-str
  json-close-obj
  r> http-200 ;

\ ============================================================
\ Route Registration
\ ============================================================

: register-routes ( -- )
  \ DSL endpoints
  s" /api/trajectory"        ['] handle-trajectory        add-route
  s" /api/milestones"        ['] handle-milestones        add-route
  s" /api/root-causes"       ['] handle-root-causes       add-route
  s" /api/history"           ['] handle-history           add-route
  s" /api/landmines"         ['] handle-landmines         add-route
  s" /api/failed"            ['] handle-failed            add-route
  s" /api/benchmark-results" ['] handle-benchmark-results add-route
  s" /api/forth-dashboard-bugs" ['] handle-forth-bugs      add-route
  s" /api/sixthdb-bugs"      ['] handle-sixthdb-bugs      add-route
  s" /api/implementation-plans" ['] handle-impl-plans      add-route
  s" /api/design-docs"       ['] handle-design-docs       add-route
  \ Custom endpoints
  s" /api/stats"             ['] handle-stats             add-route
  s" /api/benchmarks"        ['] handle-benchmarks        add-route
  s" /api/roadmap"           ['] handle-roadmap           add-route
  s" /api/benchmark-history" ['] handle-bench-history     add-route
  s" /api/failing-benchmarks" ['] handle-failing-benchmarks add-route
  s" /api/priorities"        ['] handle-priorities        add-route
  s" /api/bench-fixes"       ['] handle-bench-fixes       add-route
  s" /api/commands"          ['] handle-commands          add-route
  s" /api/ratio-bands"       ['] handle-ratio-bands       add-route
  s" /api/events"            ['] handle-events            add-route
  s" /health"                ['] handle-health            add-route ;

\ ============================================================
\ Main
\ ============================================================

3844 constant DEFAULT-PORT

: main ( -- )
  srm-init
  server-init
  s" /tmp/dash-query.txt" dup sql-output-len ! sql-output-buf swap move
  s" /tmp/dash-count.txt" dup sql-count-len ! sql-count-buf swap move
  s" /tmp/dash-error.txt" dup sql-error-len ! sql-error-buf swap move
  dashboard-db db-path!
  s" /dashboard.html" set-index
  register-routes
  ." Database: " dashboard-db type cr
  argc 2 >= if
    1 argv s>number? if
      drop server-start
    else
      2drop DEFAULT-PORT server-start
    then
  else
    DEFAULT-PORT server-start
  then ;

main
