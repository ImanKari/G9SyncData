# A real farm's first download on Lynx: rows vs snapshot file (ADR-0072) — 449,758 rows

2026-10-12 07:35 UTC · DOOGEE S96Pro (S96PRO0000000057098) · Android 10 (API 29) · js/apps/lynx-android-host mode farm (Lynx 4.1.0, PrimJS, @g9tm/lynxcontrols-native SQLite, the packed @g9tm/syncdata-lynx) · MessagePack

The farm-site tables of one farm from a real device database (not in the repository): Ridge 4,465, Pot 142,214, Placement 141,626, Tree 141,626, Graft 19,827. Every run a cold app start into a new database whose every table equals the server. Battery = °C before → after the run.

| Run | Connecting | Planning | Downloading | Applying | Page run (RESULT totalMs) | Launch to result | Wire down | Battery |
|---|---|---|---|---|---|---|---|---|
| rows | 0.3 s | 0.1 s | 2.0 s | 348.7 s | 351.3 s | 354 s | 18.2 MB | 33.6 → 34.5 |
| file, server builds | 0.1 s | 0.1 s | 10.4 s | 7.6 s | 18.6 s | 23 s | 29.0 MB | 34.5 → 34.5 |
| file, reused #1 | 0.1 s | 0.1 s | 4.4 s | 7.7 s | 12.5 s | 17 s | 29.0 MB | 34.5 → 34.6 |
| file, reused #2 | 0.1 s | 0.1 s | 4.3 s | 7.1 s | 11.9 s | 14 s | 29.0 MB | 34.6 → 34.7 |
