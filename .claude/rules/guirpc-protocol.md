---
paths:
  - "src/Lattice.Boinc.GuiRpc/**"
  - "tests/Lattice.Tests/**"
  - "tools/Lattice.SmokeTest/**"
---

# GuiRpc protocol work

- Fixtures for a protocol we do not own come from the reference implementation's EMITTING code
  (the actual `printf`/serializer in BOINC's `client/*.cpp`), never from protocol prose, a wiki or
  our own docs. An idealized fixture does not merely under-test, it hides bugs: parser and fixture
  share the same wrong assumption forever (the CDATA-less `get_messages.xml` hid our own
  over-escaping of every message body containing `&`, #211).
- Cite the emitting file and function in the fixture or test comment.
- Read the emitter before inventing hostile inputs — it also prevents speculative work.
- Fix a stale fixture in the same PR so the existing suite starts exercising the real shape.
- Fetch BOINC sources raw (`raw.githubusercontent.com/BOINC/boinc/master/...`) and grep locally;
  code search is slow and incomplete for multi-KB C++.
- A missing reply container is contract-breaking, not "empty": never fabricate a default record
  with `?? fallback`. Use the single required-container helper.
- Daemon facts verified live (BOINC 8.2.x): on macOS the client launched by BOINC Manager runs with
  `--launched_by_manager` and exists only while the Manager runs.
