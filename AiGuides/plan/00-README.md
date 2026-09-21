# Plan and research archive

These are the documents the library was designed from. They are kept because the code, the ADRs in
[`10-Decisions.md`](../10-Decisions.md) and the guides all cite their requirement ids — `P-xx`, `D-xx`
and `Q-xx` — and without the dictionary those ids mean nothing.

They are **history, not instructions.** Where a document and a guide disagree, the guide is right: the
documents were written before the code and are not updated as it changes.

| File | What it is |
|---|---|
| [`G9SyncData.md`](G9SyncData.md) | The v1 research and design plan (2026-09-15). The source of the `P-xx` / `D-xx` / `Q-xx` ids cited throughout the guides and source comments. |
| [`G9SyncData-V2.md`](G9SyncData-V2.md) | The v2 research and design (2026-09-16); says which parts of v1 survived and which changed. |
| [`G9SyncData-Deep-Review.md`](G9SyncData-Deep-Review.md) | The 2026-09-17 review of both libraries against AgriPad. Its findings became ADR-0058 … ADR-0069; nothing else cites its `F-xx` / `NEW-xx` ids. |

They lived in the AgriPad repository root until 2026-09-21. They are library documents, so they moved
here with the 1.1.0 release.
