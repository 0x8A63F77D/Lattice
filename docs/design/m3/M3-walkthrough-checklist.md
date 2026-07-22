# M3 walkthrough — owner acceptance checklist

This is the live acceptance session that closes **milestone M3** (control operations). The owner
runs it against a real BOINC daemon on the dedicated test machine; ticking it to completion is the
M3-close signal. No agent asserts M3 done — this checklist is the gate.

Every item below is something you can **see** or **do** in the running app. Framework internals are
out of scope — those are covered by the headless test suites, the F# policy/machine tests, and Codex
review on each M3 PR (A–I).

M3 is Lattice's first **write path** to BOINC: suspend/resume, abort, project update/attach/detach,
run modes, and snooze. Reading the app is no longer the whole story — you are now sending commands to
daemons, so the walkthrough is built around **doing** things and confirming they took effect.

---

## Before you start — prerequisites, data, and one thing that will surprise you

> **Prerequisite for every launch & restart below (macOS):** closing the window leaves Lattice
> **resident in the tray**, and the single-instance guard makes a later `dotnet run` hand off to that
> resident process and exit — so a new launch **silently ignores new `LATTICE_SAMPLE_HOSTS` /
> `LATTICE_SAMPLE_TICK` values**, and any "restart" check is a **false green** (the app never actually
> restarted). Before each launch or restart step, **fully quit via the tray → Exit** (or otherwise
> confirm no Lattice process is running). Start every run from a clean process.

### A real daemon is required to see effects. Read this first.

Control operations only *do something* against a **real** BOINC daemon. Point Lattice at the local
**BOINC 8.2.11** daemon (it lives only while BOINC Manager is running) attached to **Einstein@Home**
(mac-arm64 work is available), and drive the real control checks (§§2–8) there.

> **Project roles (so the attach checks don't collide).** Throughout this walkthrough your real
> **Einstein@Home** attachment is the *standing attached* project used by the control checks (§§2–3, 7)
> and by the already-attached error check (§6c). The *fresh-attach* checks (§6a happy path, §6b bad
> key) must target a URL the host is **not currently attached to** — attaching a URL that is already
> attached only ever produces the "Already attached to project" error (that is what §6c verifies), so
> it cannot exercise a real attach. Each attach step below states which precondition it needs.

**The sample fleet accepts control ops but silently ignores them.** If you run the DEBUG sample fleet
(below), every control command on a `Sample · Alpha/Beta/Gamma` host *reports success* but the canned
data never changes:

- Suspend / Resume / Abort / project ops → the op "succeeds" (no red failure bar), but the task or
  project **does not change state**, because the sample host serves fixed canned replies.
- Snooze → reports success, but **no snooze pill appears** (the sample host's `cc_status` is fixed, so
  there is never a snooze delay to display).
- Attach → the dialog reports **"attached" and closes**, but **no new project row appears**.

So: use the sample fleet **only** for the things that are decided *client-side, before the op runs* —
the confirmation **dialogs** and the multi-host **blast-radius receipt** (§4) — and use the **real
daemon** for everything where you need to see an actual **effect**. Each item below says which data
source it needs.

### The DEBUG sample fleet (for the dialog/receipt checks only)

Launch the binary directly from a shell — **never `open <binary>`** (an `open` launch hangs the
machine, dyld stall):

```bash
LATTICE_SAMPLE_HOSTS=1 dotnet run -c Debug --project src/Lattice.App
```

This injects a **three-host fleet** (`Sample · Alpha / Beta / Gamma`) **merged into** your real host
list (it does not replace it). All three connect and are attached to **Einstein@Home**, so in All-hosts
scope the Projects view shows one **Einstein@Home parent row** — which §4 uses to exercise the
multi-host confirmation receipt without owning three real daemons. Note the sample fleet shares the
**same Einstein@Home URL** as the real daemon, so if your real host is also attached the parent row
covers it too; §4 explains how that changes the count and why you must **Cancel** (never confirm) its
dialogs.

*Sources: control lane + failure taxonomy — PR D (#131); attach flow — PR E (#132); Tasks ops — PR F;
Projects ops — PR G; run modes + snooze — PR H (#137); attach dialog — PR I (#138). Sample fleet —
PR #102.*

---

## 1. Sanity — the four views still read live data

Point at the real Einstein@Home daemon (and optionally add the sample fleet).

- [ ] All four views (Tasks / Projects / Transfers / Event log) render live data with no crash or
      blank pane. (M3 added write commands; it must not have regressed the M2 read path.)
- [ ] Project rows are **live** now: they refresh on the poll tick like tasks already did. (M3 added
      `get_project_status` to the steady poll — DI-5 — so a project you suspend in §3 becomes visible
      within about a second instead of staying stale.)

*Design: M2 dashboard (unchanged); live project status — DI-5 / PR C.*

---

## 2. Task control ops — suspend / resume / abort (real daemon)

Scope a single real host and open the **Tasks** view. Select a running task.

- [ ] The command bar shows **Suspend**, **Resume**, then — after a visible gap (a `Separator`) —
      **Abort**. The gap is deliberate: it puts misclick distance between the reversible pause buttons
      and the one button that throws work away (DI-1(c)).
- [ ] **Suspend** the selected task → it executes **immediately, no dialog**, and within about a
      second the task's state icon/row shows **Suspended** (dimmed row). Suspend and resume are
      instantly reversible, so they never ask "are you sure?" (DI-1).
- [ ] **Resume** the suspended task → executes immediately, task returns to Running within ~1 s.
- [ ] **Abort** the selected task → a **confirmation dialog** appears:
      - Title: **"Abort task?"**
      - Body: **`Abort "<task name>" on <host>? The task's computed work will be permanently lost.`**
      - The primary button is **"Abort"** and is styled with the **danger (red) accent**; the **safe
        "Cancel" button is the default** (pressing **Enter** does *not* abort — it cancels). This is
        the only task op that confirms, because it destroys computed work.
- [ ] Press **Cancel** (or Enter) → nothing happens, the task keeps running.
- [ ] Abort again and press **Abort** → the task disappears from the list within ~1 s.
- [ ] **Right-click a task row** → a context menu offers the same **Suspend / Resume / (divider) /
      Abort** (Abort in red), acting on the row you right-clicked. The right-click selects that row
      first, so the menu never targets a stale selection.

*Design: DI-1 (confirm only work-destroying ops), DI-1(c) (misclick distance); PR F.*

---

## 3. Project control ops on ONE host — update / suspend / resume / detach (real daemon)

Scope the single real host and open the **Projects** view. Select the **Einstein@Home** row (with one
host it is a single, leaf-style row).

- [ ] The command bar shows **Add project…**, **Update**, **Suspend**, **Resume**, then — after a
      `Separator` — **Detach**. Detach sits behind the gap for the same misclick reason as Abort.
- [ ] **Update** → executes immediately (no dialog); the daemon contacts the project. (Effect is a
      scheduler contact; watch the **Event log** for a "Sending scheduler request"/"update requested"
      style message from Einstein@Home.)
- [ ] **Suspend** the project → executes immediately; within ~1 s the project row shows the
      **suspended** indicator (this is the DI-5 live-project-status path working — the effect is
      visible without a full refetch).
- [ ] **Resume** the project → executes immediately; the suspended indicator clears within ~1 s.
- [ ] **Detach** → a **danger-styled confirmation** appears:
      - Title: **"Detach project?"**
      - Body: **`Detach "Einstein@Home" from <host>? Its in-progress tasks on that host will be lost
        and must be re-downloaded to re-attach.`**
      - Primary **"Detach"** in red; **Cancel** is the default.
- [ ] Press **Cancel** — nothing happens. (You will actually detach + re-attach in §7, after the
      attach dialog is covered.)
- [ ] **Right-click a project row** → the same **Update / Suspend / Resume / (divider) / Detach**
      (Detach in red) context menu.

*Design: DI-1; live project status — DI-5; PR G.*

---

## 4. Project control ops across MANY hosts — the blast-radius receipt (sample fleet)

This section verifies **DI-2**: a project action on an aggregated parent row acts on **every** host,
and any multi-host action confirms first and **spells out which hosts** it will touch. You do not need
three real daemons — the **sample fleet** gives you a real multi-host parent row. You are checking the
*dialog* (the blast-radius receipt), not the effect.

> **Important — the real Einstein@Home host joins this row.** The sample fleet is attached to the same
> Einstein@Home URL as the real daemon from the prerequisites above, and sample mode **merges the sample fleet into your
> real host list** (it does not replace it). So if your real Einstein@Home host is configured and
> connected, the All-hosts Einstein@Home parent row spans **the real host *and* the three samples**
> (count **> 3**), and — critically — **confirming an op on that row WILL execute it on the real host**
> (only the sample hosts ignore ops). Two consequences:
> 1. To see an **exactly-3-host** receipt, run the sample fleet in a session where **no real host is
>    attached to Einstein@Home** (e.g. before you attach the real daemon per the prerequisites, or
>    with it detached).
> 2. In this section, **read each dialog and then Cancel it** — do **not** confirm. The receipt (the
>    host enumeration) is the whole DI-2 requirement; confirming risks a real detach/suspend on your
>    live project.

Launch with `LATTICE_SAMPLE_HOSTS=1`, switch to **All-hosts** scope, open **Projects**, and select the
**Einstein@Home parent row** (in a sample-only session it spans `Sample · Alpha`, `Sample · Beta`,
`Sample · Gamma`).

- [ ] **Suspend** (or **Resume** / **Update**) the parent row → because this is a **reversible op on
      more than one host**, a **Caution confirmation** appears even though the op is reversible:
      - Title: **`Suspend on N hosts?`** (the op label + the covered-host count — **3** in a
        sample-only session, more if a real Einstein@Home host is also in the row)
      - Body: **`Suspend "Einstein@Home" on N hosts: <the host names>.`** — it **enumerates every
        covered host** (`Sample · Alpha`, `Sample · Beta`, `Sample · Gamma`, plus your real host if
        present).
      - The named list is the "receipt" so a fat-finger on an aggregate row can't silently hit N
        daemons. (Single-host reversible ops in §3 had **no** dialog; the dialog is the multi-host
        difference.) **Cancel** it.
- [ ] **Detach** the parent row → a **danger** confirmation enumerating every covered host:
      - Title: **"Detach project?"**
      - Body: **`Detach "Einstein@Home" from N hosts: <the host names>? In-progress tasks on those
        hosts will be lost and must be re-downloaded to re-attach.`**
      - **Cancel** it (confirming here would really detach every listed host, including your real one).
- [ ] Confirm the receipt matched reality: the count equals the number of hosts actually attached to
      Einstein@Home in scope, and each is named. You **Cancelled** both dialogs, so nothing executed
      on any host.

*Design: DI-2 (act on all hosts, confirm when N > 1, enumerate the hosts); PR G.*

---

## 5. Run modes & snooze — attached to the host, not the grid (real daemon)

This verifies **DI-4**: run-mode and snooze controls live on a *host*, reached two ways — the rail
right-click menu, and a "Computing" dropdown in the Tasks command bar when a single host is scoped.

### 5a. Rail context menu

Right-click the real host's row in the left **rail**.

- [ ] The context menu **opens to the RIGHT of the row**, into the content area (not over the pane).
- [ ] The menu order is: **Edit host** and **Test connection** at the top, a divider, then **Run
      modes**, **Snooze**, and — only while a snooze is active — **Resume computing**, a divider, and
      **Remove host** at the bottom. (The run-mode block sits between the Edit/Test pair and Remove,
      not after all three.)
- [ ] **Run modes** expands to **CPU**, **GPU**, **Network**, each with **Always / Auto / Never** as
      **radio items**. The **currently-selected mode shows only as the radio checkmark** — there is no
      right-aligned mode text in the menu. The checkmark reflects the host's **permanent** mode.
- [ ] Set **CPU → Never** → within ~1 s BOINC stops running CPU tasks (watch the Tasks view / Event
      log). Set **CPU → Auto** again to restore. Run-mode changes are **instant — no confirmation**
      (DI-1: instantly reversible).
- [ ] **Snooze** offers **15 minutes**, **1 hour**, **4 hours**. Pick **15 minutes**.

### 5b. The snooze pill

- [ ] After snoozing, the host's rail row shows a **"Snoozed until HH:mm" pill** (a pause glyph + the
      local wake time). This is a temporary CPU-Never override — the **CPU radio still shows your
      standing permanent mode** (Auto), it did *not* flip to Never.
- [ ] Right-click the rail row again → **Resume computing** is now visible. Click it → the snooze pill
      **disappears** and computing resumes (this sends "restore").
- [ ] Snooze again, then **narrow the window below ~1280px** so the rail collapses to the **48px
      compact** pane → the snooze pill is **hidden**, but **hovering the row's tooltip still shows
      "Snoozed until HH:mm"** (the compact rail carries the snooze in the tooltip). Widen again → the
      pill returns.
      - *(On a very long host name the expanded-rail pill degrades from the "HH:mm" text pill to a
        small 20×20 pause **icon chip**, with the time moving to the tooltip. Optional to trigger;
        both forms are correct.)*
- [ ] Let a **15-minute** snooze… actually, don't wait — instead confirm the pill shows a sensible
      future time and that **Resume computing** clears it immediately. (The pill also auto-clears on
      its own at the deadline.)

### 5c. The "Computing" dropdown (Tasks view only, single host only)

- [ ] With a **single host scoped**, open the **Tasks** view → the command bar shows a **"Computing"**
      button (play-settings icon + chevron). Its dropdown is the **same** Run modes / Snooze / Resume
      menu as the rail.
- [ ] Switch to **All-hosts** scope → the **"Computing" button disappears** (no fleet-wide run modes
      in M3, DI-4). It exists **only** in the Tasks view and **only** when one host is scoped — it is
      not in Projects, Transfers, or Event log.

*Design: DI-4 (host-scoped run modes; 15 min / 1 h / 4 h; no fleet-wide in M3); snooze semantics —
design 1.4; PR H.*

---

## 6. Attach a project — and what "attached" does and does not mean (real daemon)

This verifies the attach flow (PR I) and pins one behavior that is **easy to misread as a bug**.

Open **Projects**, click **Add project…** (enabled because a Connected host is in scope).

- [ ] The dialog title is **"Add project"** with a primary **"Attach"** button. It shows: a **host
      picker** (locked to the scoped host when one host is scoped; a dropdown of Connected hosts in
      All-hosts scope), a **project URL** field, and a **credential toggle** — **email & password** by
      default, **account key** when switched on. An informational bar explains the flow.
- [ ] **Attach button stays disabled** until the host, URL, and the active credential fields are all
      filled.

### 6a. Happy path (email + password)

- [ ] Attach to a project you have an account on that the host is **not currently attached to**. If
      your only account is Einstein@Home (which is attached), do §7's **detach** first and re-attach
      it here; otherwise use any other project you have an account on. Fill URL + email + password,
      click **Attach**.
- [ ] The progress area shows **"Contacting project…"** then **"Attaching…"** (these are the flow's
      real lookup→attach stages, not a fake spinner).
- [ ] On success the **dialog closes** and the new project **appears in the Projects view** within a
      poll tick. Any greeting message from the project is not a failure.

### 6b. Deliberately bad account key — "attached" ≠ "verified" (READ THIS)

- [ ] Switch the credential toggle to **account key** and enter a **deliberately wrong key** for a
      real project URL the host is **not currently attached to** (e.g. LHC@home,
      `https://lhcathome.cern.ch/lhcathome/` — any real project you don't already have attached; do
      **not** use the still-attached Einstein@Home URL, which would only produce the already-attached
      error of §6c instead of exercising the bad-key path). Click **Attach**.
- [ ] **Expected — and this is correct, not a Lattice bug:** the dialog reports the attach **succeeded
      and closes**, and the project **appears** in the Projects view. The daemon does **not** validate
      the account key at attach time — it only accepts the request and creates the entry. The bad key
      surfaces **later**, as an **error in the Event log** the first time BOINC contacts the project's
      scheduler (e.g. an authentication failure message). The UI deliberately says **"attached"**,
      never "verified", for exactly this reason.
- [ ] Confirm the failure shows up in the **Event log** shortly after, and **detach** the bad
      attachment (§3 detach) to clean up.

### 6c. Already-attached error (verbatim daemon text)

- [ ] Try to **Add project…** for a URL the host is **already attached to** (e.g. Einstein@Home while
      still attached). Click **Attach**.
- [ ] The dialog stays open and its **error area shows the daemon's own message verbatim** (e.g.
      **"Already attached to project"**). Lattice displays this text exactly as received and does not
      reword or branch on it.

*Design: attach semantics — design 2.3 ("attached", not "verified"); DI-3 (Connected-host picker);
PR I. The direct-key path's authenticator is unvalidated by design.*

---

## 7. Detach + re-attach a real project (real daemon, end-to-end)

- [ ] **Detach** Einstein@Home from the real host (§3 detach dialog → confirm). The project row
      disappears within ~1 s and its in-progress tasks are dropped.
- [ ] **Re-attach** Einstein@Home via **Add project…** (§6a, email + password). It reappears and
      begins requesting work. (This is the full destructive-then-recover loop the detach dialog warns
      about.)

*Design: DI-1 (detach is Destructive) + PR I attach.*

---

## 8. Acting on an offline host is refused, not queued (DI-3, real daemon)

- [ ] With the host **Connected**, note the Tasks/Projects command buttons are **enabled**.
- [ ] **Quit BOINC Manager** (kill the daemon). The rail host walks to **Retrying…** then
      **Unreachable**.
- [ ] While the host is not Connected: the **Suspend / Resume / Abort** (Tasks) and **Update /
      Suspend / Resume / Detach** (Projects) buttons are **disabled**, and **hovering a disabled
      button shows the tooltip "Host is not connected"**. The rail right-click **Run modes / Snooze**
      items are disabled too. Nothing is queued for later — Lattice only acts on hosts that are live
      *now* (DI-3).
- [ ] Set a **wrong password** for the host (Edit host) while the daemon is running → the rail shows
      the **Wrong-password** state, and the same control buttons are **disabled with the "Host is not
      connected" tooltip** (a host that can't authenticate is not Connected, so its controls are
      gated off exactly like an unreachable one). Restore the correct password to recover.
- [ ] Restart BOINC Manager → the host reconnects and the buttons re-enable on their own.

> **The red failure bar (optional, timing-dependent).** Lattice surfaces a genuine op failure as a
> dismissible **red InfoBar** at the bottom of the view ("`<op> failed`" + the daemon/transport
> reason), and clears it on the next success. Because DI-3 **disables** the controls the moment a host
> stops being Connected, the only way to see this bar is to catch the narrow window where a host was
> Connected at the last poll but dies before your click lands (quit the daemon, then click Suspend
> within about a poll interval). It is inherently racy — treat it as a bonus sighting, not a required
> step. The reliable place to see verbatim failure text is the **attach dialog's** error area (§6c).

*Design: DI-3 (refuse offline, disable with reason, never queue); failure surface — design 2.1 /
PR F.*

---

## 9. Which operations ask "are you sure?" — reference

Confirm the taxonomy you observed matches this table (this is the whole confirmation model; DI-1/DI-2):

| Operation | Confirmation |
|---|---|
| Task suspend / resume | **Instant** (no dialog) |
| **Task abort** | **Confirm — danger** ("permanently lost") |
| Project suspend / resume / update, **1 host** | **Instant** |
| Project suspend / resume / update, **N > 1 hosts** | **Confirm — caution** (lists the hosts) |
| **Project detach** (any host count) | **Confirm — danger** (lists the hosts, warns of task loss) |
| Run mode / snooze / resume computing | **Instant** |

- [ ] Everything you drove above matches this table: only **abort** and **detach** ever show a
      **danger** dialog; multi-host reversible project ops show a **caution** dialog that enumerates
      the hosts; everything else is instant.

*Design: DI-1, DI-2; policy is the F# `ConfirmationPolicy` (exhaustively tested).*

---

## 10. Light + dark (the new M3 surfaces)

- [ ] Repeat a spot-check of the **confirmation dialog** (danger button + body), the **attach
      dialog**, the **snooze pill**, and the **run-mode menu** in **both** themes — the danger accent,
      the warning-tinted snooze pill, and the menu radios all read correctly in light and dark.

*(Full four-view theme parity was already accepted at M2 §7; this only re-checks the M3 additions.)*

---

**Gate:** completing this checklist is M3-close acceptance. The confirmation dialog, attach dialog,
snooze pill, and run-mode menu are the visual surfaces the owner eyeballed at their PR gates (F–I);
this session is the end-to-end acceptance against a live daemon that closes the milestone.
