# M2 walkthrough — owner acceptance checklist

This is the live acceptance session that closes **milestone M2** (and issue **#32**). The owner runs
it against the test daemon on the dedicated test machine; ticking it to completion is the M2-close
signal. No agent asserts M2 done — this checklist is the gate.

It doubles as the **README screenshot source (#27)**: capture the canonical shots as you go (last
section).

Every item below is something you can **see** or **do** in the running app. Framework internals are
out of scope — those are covered by the headless test suites and by Codex review on each Wave-3 PR.

---

## Before you start — demo builds & data

> **Prerequisite for every launch & restart below (macOS, post-#92):** closing the window leaves
> Lattice **resident in the tray**, and the single-instance guard makes a later `dotnet run` hand off
> to that resident process and exit — so a new launch **silently ignores new `LATTICE_SAMPLE_HOSTS` /
> `LATTICE_SAMPLE_TICK` values**, and any "restart" check is a **false green** (the app never actually
> restarted). Before each launch or restart step, **fully quit via the tray → Exit** (or otherwise
> confirm no Lattice process is running). Start every run from a clean process.

Two data sources feed this walkthrough. Use both.

### A. Real single-host data (live daemon)

- Start the local **BOINC 8.2.11** daemon (it lives only while BOINC Manager is running). This is the
  live-RPC path (#15).
- **Two daemon configs, run the empty-state check first:**
  1. **No project attached** (fresh daemon, before you attach anything) — this is the state that
     drives the **empty-state** check in §1. Point Lattice at it and confirm the empty states, then
     attach a project.
  2. **Attached to Einstein@Home** (mac-arm64 work is available) — gives a real host with live task/
     message data for the populated checks.
- Even attached, the live daemon will **not** produce the 500-task / all-transfer-states /
  multi-project-aggregate volumes — those come from the sample fleet below.

### B. DEBUG sample fleet (data-rich states)

Run a **DEBUG** build with the sample-host env var set. **Launch the binary directly from a shell —
never `open <binary>`** (an `open` launch hangs the machine, dyld stall):

```
LATTICE_SAMPLE_HOSTS=1 dotnet run -c Debug --project src/Lattice.App
```

This injects a **three-host fleet** (`Sample · Alpha / Beta / Gamma`) in the rail alongside any real
hosts. Under All-hosts scope you get **620 merged tasks** (Alpha alone carries 520 → virtualization
check), transfers in **every** state (active / retrying / queued), and a Projects **Einstein@Home**
row that expands to a **`Varies`-share / mixed-status** aggregate.

To watch the **progress-bar fill motion** live, add the tick aid — it makes the fleet's active
transfers and running tasks advance a step each poll (the canned snapshot is otherwise static, so a
bar would never move):

```
LATTICE_SAMPLE_HOSTS=1 LATTICE_SAMPLE_TICK=1 dotnet run -c Debug --project src/Lattice.App
```

*Sources: sample fleet — PR #102; tick aid — PR #103. Live daemon — #15.*

---

## 1. Multi-host live run — the four views

Run ≥2 hosts (a real Einstein@Home host + the sample fleet).

- [ ] All four views (Tasks / Projects / Transfers / Event log) render live data with no crash or
      blank pane.
- [ ] **Event log** shows real merged messages from multiple hosts, with the **Host** column
      identifying each source.
- [ ] **Event log** Following **pauses** when you scroll up; click the **Resume following** toggle
      (the button relabels to it) to jump back to live. *Scrolling back to the bottom alone does not
      auto-resume — the toggle is the resume path.*
- [ ] **Empty states** — pointing at the **no-project daemon config (setup A.1)**, Tasks / Projects /
      Transfers show the design-correct **empty states** (not errors, not blank). Do this before
      attaching Einstein@Home; once a project is attached that host's Projects gains its row. (The
      sample fleet is always project-attached, so it does not exercise the empty path.)
- [ ] With the **sample fleet**: the **Tasks** grid materializes **500+ rows** and scrolls smoothly
      (virtualization holds — no stutter, no runaway memory).
- [ ] Tasks toolbar: **sort**, **filter text**, **density toggle** (36px medium ↔ 28px compact), and
      **column show/hide** all work.
- [ ] **Projects** parent rows expand to child rows with correct aggregates — the sample
      Einstein@Home row shows **`Varies`** resource share and a **mixed** status across hosts.
- [ ] **Transfers** shows rows in active / retrying / queued; a retrying transfer's **countdown
      ticks** down each second.

*Design: cards `1c` / `1d` (Tasks), `2a` (Projects), `2b` (Transfers), `2c` (Event log). Data: PR #102.*

---

## 2. Connection-state matrix (live)

Drive each state on a real host and confirm the icon **and** text (never colour alone — canonical
set is card `1e`).

- [ ] **Connecting…** on first connect / full fetch (rotating `arrow_sync`, brand blue).
- [ ] **Connected** once state loads (`checkmark_circle`, success green).
- [ ] Quit the daemon → **Retrying in Ns (attempt N)** with a **live per-second countdown** and
      rising attempt number (`arrow_clockwise`, warning orange).
- [ ] Leave it down until the attempt count reaches the **Unreachable** tier (attempt ≥ 4)
      (`dismiss_circle`, danger red; tooltip = last error).
- [ ] Configure a **wrong password** → **Wrong password** state (`key`, danger red); **clicking the
      rail row opens the Edit-host dialog** (card `3b`) with the password field in **error + focused**.

*Design: card `1e` (state matrix), `3b` (host management).*

---

## 3. Scope — All-hosts vs single-host

- [ ] Switching the **Hosts scope** in the rail re-filters **every** view (it is an independent
      global filter, separate from the view selection — both selected states show at once).
- [ ] In **single-host** scope the **Host** column is hidden in the grids.
- [ ] Under **All-hosts** scope with **≥1 host unreachable**, the **partial-results InfoBar**
      (Warning, "Retry now" link) appears above the grid.
- [ ] The InfoBar **dismisses** and **reappears** correctly as the unreachable set changes.

*Design: "Global scope" + "Partial results" (README §Interactions); card `1c`.*

---

## 4. Responsive — resize the window

Min window is **1000×700**. There is **one rail breakpoint (1280)** and a separate **column-shed
breakpoint (1100)** — they are different widths; verify both.

- [ ] **≥1280px:** full **260px** rail (labels visible) and **all columns** present.
- [ ] **1100–1279px:** the rail **auto-collapses to the 48px compact** pane (icons only, always
      visible — never a hidden menu-button-only pane); columns still all present in this band.
- [ ] **Below 1100px** (down to the 1000 minimum): the **Elapsed** column auto-hides into the
      overflow menu; the rail **stays 48px compact**. *(Application's auto-hide threshold coincides
      with the 1000px minimum window, so in the reachable range only Elapsed sheds automatically —
      Application stays visible; you can still hide it manually via column show/hide.)*
- [ ] **Minimum 1000×700** is enforced (the window won't shrink past it).
- [ ] Hide a column via the overflow menu, **fully quit (tray → Exit — see the prerequisite above)**
      and relaunch → the **show/hide choice persists**. *(M2 persists column **visibility**, not column
      **widths** — widths reset to defaults on relaunch; don't check width persistence here. A
      tray-resident reopen is not a restart and would false-green this.)*

*Design: card `1i` (responsive). Rail 1280 breakpoint: PR #100. Column shed at 1100: PR #28.*

---

## 5. Motion & polish — on the owner's eye

These are the Wave-3 motions that shipped. **One version each — judge the feel; there is no "correct"
timing to iterate toward.** Use the `LATTICE_SAMPLE_TICK=1` build for live progress.

- [ ] **View switch** — changing views fades the new page in while it **rises ~8px** (150ms). The
      new page's **data is already there** the instant it appears (motion never delays data).
- [ ] **Progress-bar fill** — a task/transfer progress bar **slides** to its new width (200ms) rather
      than jumping. (Needs the tick build; the bound value updates immediately, the slide is cosmetic.)
- [ ] **Projects expander chevron** — toggling a parent row rotates the chevron smoothly (100ms).
- [ ] **Filled nav icon** — the **selected** view's rail icon is the **filled** glyph; the others are
      outlined. Navigating to **Settings** fills the Settings icon and un-fills the previous one
      (exactly one filled glyph across the rail).
- [ ] **FA control transitions** (optional feel check) — InfoBar appear/dismiss and the Add/Edit
      dialog scrim use FluentAvalonia's built-in transitions (retained at FA defaults, not restyled).
      Note anything that feels off; it's a flag for a follow-up, not a blocker.

> **Not in M2 (do not look for these):** row enter/remove animation and the transfer completed-row
> fade were **deferred to #112**; the OS **reduce-motion** gate was **cut** (owner ruling, 2026-07-19)
> — Avalonia 12.1 surfaces no OS reduce-motion signal. DataGrid **row hover** is intentionally
> **instant** (owner verdict #74) — not a bug.

*Design: card `1h` (motion), rail icon rule (README §Assets). PRs: view-switch + progress #103,
chevron retime #113, filled Settings icon (PR B / `ShellRailTests`).*

---

## 6. Window material (Mica)

- [ ] **On macOS** (this test machine): the app is the **opaque fallback** — clean, no transparency
      artifacts. Command bars take the **nav-region colour** (`#F5F5F5` light / `#202020` dark); the
      nav pane and content read correctly. *(Already owner-eyeballed at PR #99; re-confirm here.)*

> **Granted Mica** (nav + command-bar regions turning translucent) only occurs on **Windows 11**
> hardware and is verified separately in **issue #11** — **out of this checklist**. Nothing on macOS
> should look translucent.

*Design: Mica line (README §Interactions/Motion). PR #99 (code half); #11 (on-hardware).*

---

## 7. Light + dark theme

- [ ] Repeat the visual checks (views, states, scope, responsive, motion, material) in **both**
      themes; each matches the `1f` dark mockups (token substitution only, no layout differences).
- [ ] Switching **Light / Dark / System** in Settings updates the whole app immediately.

*Design: card `1f` (dark), `1g` (tokens).*

---

## 8. Tray residency (sanity coexistence)

Tray residency shipped and was owner-verified via its own 8-item checklist at **PR #106** — not
repeated here.

- [ ] One check only: with the dashboard open, closing to tray and reopening leaves the **dashboard
      and its live data intact** (tray and the M2 dashboard coexist).

*Feature: #92 / PR #106 (full tray checklist lives there).*

---

## 9. README screenshots (#27)

As the session runs, capture the canonical shots — they seed the README:

- [ ] Shell + each of the four views, in **light and dark**.
- [ ] Note which surfaces are **visual-regression baseline candidates for #13** — **flag them for
      #13, do not add baselines here** (bundling baseline expansion into this session would muddy the
      visual gate).

*Screenshots: #27. Baseline candidates: #13.*

---

**Gate:** completing this checklist is M2-close acceptance. Only then does **#32** close (with the
wave). #11 (on-hardware Mica) stays open independently.
