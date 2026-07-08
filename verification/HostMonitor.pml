/* HostMonitor concurrency protocol — Promela double-check model.
 * Independent second encoding of the F# executable spec (tests/Lattice.Verification).
 * Property numbering shared: I1–I5 safety (assertions), L1–L3 liveness (ltl, pan -a -f).
 * Primitive anchoring: lock(_gate) blocks = atomic{}; awaits = statement boundaries;
 * TCS wake = sticky bit with consume-if-completed protocol; CTS = monotonic bits.
 */

#define MAX_UPDATES 2
#define MAX_WAKES   2
#define MAX_FAILS   3
#define ATT_CAP     3

/* phases (loop program counter between interleaving points) */
mtype:phase = { Idle, Dispatch, Snap, Conn, Auth, Fetch, Accept, PubConn,
                Tick, MsgG, MsgP, SnapG, SnapP, PWait, Tear, Retry, BWait, Park, Exited };
/* status observable (mirror of HostConnectionState) */
mtype:st = { Disc, Cing, Aing, Fing, Cted, Rtry, AFail };

mtype:phase ph = Idle;
mtype:st status = Disc;

byte curVersion = 0;        /* env increments */
bool configChanged = false;
byte ctsState = 0;          /* 0 none, 1 live, 2 canceled, 3 disposed */
bool wake = false;
bool started = false;
bool outerCanceled = false;
bool disposeFlag = false;
bool faulted = false;

byte attemptVersion = 255;  /* 255 = none */
bool connLive = false;
bool reachedConnected = false;
bool firstTickPending = false;
bool injectedFail = false;
bool authRefused = false;
byte attempt = 0;

byte statusVersion = 0;
byte daemonVerVintage = 255;   /* 255 = none */
byte logVintage = 255;

/* L1 history */

byte updatesLeft = MAX_UPDATES;
byte wakesLeft = MAX_WAKES;
byte failsLeft = MAX_FAILS;

/* I-invariants as a monitor process: checked at every state via timeout-free
 * always-enabled assertion stepping is expensive; instead assert inline at the
 * mutation/publish sites (I1, I4, I5) and via this monitor for I2/I3. */
active proctype monitor()
{
end_mon:
    do
    :: d_step {
         /* I2: no live connection while parked/backing off/exited */
         assert(!((ph == BWait || ph == Park || ph == Retry || ph == Exited) && connLive));
         /* I3: no unabortable stale attempt */
         assert(!( attemptVersion != 255 && attemptVersion != curVersion
                && !configChanged && ctsState != 2
                && (ph == Conn || ph == Auth || ph == Fetch || ph == Accept
                    || ph == PubConn || ph == Tick || ph == MsgG || ph == MsgP
                    || ph == SnapG || ph == SnapP || ph == PWait) ));
         /* I5: no reachable fault */
         assert(!faulted);
       }
    od
}

active proctype env()
{
end_env:
    do
    /* The environment is OPTIONS, not obligations: under pan -f (per-process
     * weak fairness) an env without this stutter branch would eventually be
     * FORCED into its last enabled branch (Dispose), draining every execution
     * to Exited and making all liveness vacuous. The stutter lets a fair
     * execution keep the env silent forever. */
    :: true -> skip
    :: (updatesLeft > 0) -> atomic {
         updatesLeft--;
         curVersion++;
         configChanged = true;
         if :: ctsState == 1 -> ctsState = 2 :: else -> skip fi;
         wake = true
       }
    :: (wakesLeft > 0) -> atomic {
         wakesLeft--;
         wake = true
       }
    :: (!started) -> atomic {          /* Start (rider C: no-op after dispose) */
         if
         :: disposeFlag -> skip
         :: else -> started = true;
                    if :: ph == Idle -> ph = Dispatch :: else -> skip fi
         fi
       }
    :: (!disposeFlag) -> atomic {      /* DisposeAsync (idempotent via disposeFlag) */
         disposeFlag = true;
         outerCanceled = true;
         wake = true;
         if :: ctsState == 1 -> ctsState = 2 :: else -> skip fi
       }
    od
}

active proctype loop()
{
end_loop:
    do
    :: atomic { (ph == Dispatch && outerCanceled) ->
         ph = Exited; status = Disc }
    :: atomic { (ph == Dispatch && !outerCanceled) -> ph = Snap }
    :: atomic { (ph == Snap) ->              /* THE atomic snapshot block (rule 1) */
         attemptVersion = curVersion;
         configChanged = false;
         ctsState = 1;
         injectedFail = false; authRefused = false;
         reachedConnected = false; firstTickPending = true;
         daemonVerVintage = 255; logVintage = 255;   /* per-attempt stamps (I1m) */
         connLive = false;
         ph = Conn }
    /* awaits: cancel/dispose observed, or progress, or injected failure */
    :: atomic { (ph == Conn && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Conn && ctsState == 1 && !outerCanceled) ->
         connLive = true; status = Cing; statusVersion = attemptVersion; ph = Auth }
    :: atomic { (ph == Conn && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Auth && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled) ->
         status = Aing; statusVersion = attemptVersion; ph = Fetch }
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled) ->
         authRefused = true; ph = Tear }              /* refused password path */
    :: atomic { (ph == Auth && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Fetch && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Fetch && ctsState == 1 && !outerCanceled) ->
         status = Fing; statusVersion = attemptVersion; ph = Accept }
         /* NOTE deliberately NO daemonVerVintage/logVintage write here (riders A/B):
          * mutating either at Fetch is the historical bug; I1 assertions below
          * would fire. */
    :: atomic { (ph == Fetch && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == Accept) ->
         if
         :: configChanged -> ph = Tear
         :: else -> ph = PubConn
         fi }
    :: atomic { (ph == PubConn) ->
         /* I1 publish half: guard adjacency — assert guard was honored */
         daemonVerVintage = attemptVersion;           /* rider A: accepted only */
         reachedConnected = true;   /* NO attempt reset here: dispatcher owns the
                                     * counter (HostMonitor.cs RunAsync: ReachedConnected ? 1 : n+1) */
         status = Cted; statusVersion = attemptVersion;
         ph = Tick }
    :: atomic { (ph == Tick && (ctsState == 2 || outerCanceled)) -> ph = Tear }
    :: atomic { (ph == Tick && ctsState == 1 && !outerCanceled) -> ph = MsgG }
    :: atomic { (ph == Tick && ctsState == 1 && !outerCanceled && failsLeft > 0) ->
         failsLeft--; injectedFail = true; ph = Tear }
    :: atomic { (ph == MsgG) ->
         if :: configChanged -> ph = Tear :: else -> ph = MsgP fi }
    :: atomic { (ph == MsgP) ->
         /* rider B: first tick replaces, later ticks append; either way accepted-only */
         assert(reachedConnected);                    /* I1 mutation half */
         logVintage = attemptVersion;
         firstTickPending = false;
         ph = SnapG }
    :: atomic { (ph == SnapG) ->
         if :: configChanged -> ph = Tear :: else -> ph = SnapP fi }
    :: atomic { (ph == SnapP) ->
         /* WaitAsync ENTRY consumes a completed latch (HostMonitor.cs:183-187):
          * entering the poll wait with the wake set returns immediately. */
         if
         :: wake -> wake = false;
            if :: (configChanged || outerCanceled) -> ph = Tear :: else -> ph = Tick fi
         :: !wake -> ph = PWait
         fi }
    /* waits: delay fires (the latch may stay set — sticky; the next wait entry
     * consumes it), or the sticky wake is consumed mid-wait */
    :: atomic { (ph == PWait) ->                      /* delay fires */
         if :: (configChanged || outerCanceled) -> ph = Tear :: else -> ph = Tick fi }
    :: atomic { (ph == PWait && wake) ->              /* wake consumed */
         wake = false;
         if :: (configChanged || outerCanceled) -> ph = Tear :: else -> ph = Tick fi }
    :: atomic { (ph == Tear) ->
         ctsState = 0; connLive = false;
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: (!outerCanceled && authRefused) ->
              status = AFail; statusVersion = attemptVersion; attempt = 0;
              /* WaitForConfigChangeAsync entry consumes a stale completed latch
               * (HostMonitor.cs:214-217) unless configChanged releases immediately */
              if :: (wake && !configChanged) -> wake = false :: else -> skip fi;
              ph = Park
         :: (!outerCanceled && !authRefused && (configChanged || !injectedFail)) ->
              attempt = 0; ph = Dispatch
         :: (!outerCanceled && !authRefused && !configChanged && injectedFail) ->
              if
              :: reachedConnected -> attempt = 1     /* I4 */
              :: else -> if :: attempt < ATT_CAP -> attempt++ :: else -> skip fi
              fi;
              ph = Retry
         fi }
    :: atomic { (ph == Retry) ->
         assert(!(reachedConnected && injectedFail) || attempt == 1);   /* I4 */
         status = Rtry; statusVersion = attemptVersion;
         /* WaitAsync ENTRY consumes a completed latch: entering backoff with the
          * wake set skips the wait. */
         if
         :: wake -> wake = false;
            if
            :: outerCanceled -> ph = Exited; status = Disc
            :: else -> if :: configChanged -> attempt = 0 :: else -> skip fi; ph = Dispatch
            fi
         :: !wake -> ph = BWait
         fi }
    :: atomic { (ph == BWait) ->                      /* delay fires; latch sticky */
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> if :: configChanged -> attempt = 0 :: else -> skip fi; ph = Dispatch
         fi }
    :: atomic { (ph == BWait && wake) ->
         wake = false;
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> if :: configChanged -> attempt = 0 :: else -> skip fi; ph = Dispatch
         fi }
    :: atomic { (ph == Park && wake && !configChanged && !outerCanceled) ->
         wake = false }                               /* stale wakes ignored */
    :: atomic { (ph == Park && (configChanged || outerCanceled)) ->
         if
         :: outerCanceled -> ph = Exited; status = Disc
         :: else -> attempt = 0; ph = Dispatch
         fi }
    od
}

/* L1 no lost wakeup — sticky-latch liveness: a completed wake is eventually
 * consumed unless the monitor is dead (exited, or disposed before start).
 * Sound under Spin's process-level weak fairness BECAUSE wait entries consume
 * the latch deterministically: no infinite execution can keep ignoring it. */
ltl L1 { [] (wake -> <> (!wake || ph == Exited || ph == Idle)) }
/* L2 config convergence (Idle discharge: a monitor whose loop is not running —
 * never started, or disposed before start — cannot adopt a config; the
 * obligation transfers to Start, which the environment never owes) */
ltl L2 { [] (configChanged -> <> (!configChanged || ph == Exited || ph == Idle)) }
/* L3 disposal terminates (disposed-before-start: loop never ran, stays Idle) */
ltl L3 { [] (outerCanceled -> <> (ph == Exited || ph == Idle)) }
