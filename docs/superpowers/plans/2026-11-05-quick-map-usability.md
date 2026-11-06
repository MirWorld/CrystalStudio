# Quick Map Usability Implementation Plan

**Goal:** Make quick map creation reversible, easier to configure, and reusable without sharing machine-specific output paths.

**Approved design:** Implement the first three recommendations from the conversation: reversible creation and preview, quick/advanced settings with compatible sizes, and purpose templates with portable presets. Keep generator algorithms, map formats, overwrite protection, and recovery unchanged.

**Architecture:** Keep generation staged until confirmation. Returning from preview opens setup with the current draft. Name and output changes retain the live preview; generation-affecting changes reopen the generator with retained parameters. Built-in templates contain supported parameter combinations rather than new layout constraints. Named presets contain generation settings only; last-used settings retain local output preferences. Migrate existing version-1 profiles when loading.

**Tech Stack:** .NET 10, C#, Avalonia 11.3.4, xUnit, Avalonia.Headless.XUnit.

## Constraints

- Work in the current workspace without creating branches or commits.
- Do not add packages or change generation algorithms.
- Preserve cancellation, no-overwrite saves, source format, recipes, and recovery.
- Translate new labels and retain stable generator identifiers.
- Add focused failing tests before implementation, then run regression tests and a Release build.

## Tasks

- [x] Add session, size, and template tests; verify expected failures.
- [x] Implement session invalidation and portable preset operations with compatible loading.
- [x] Add supported size choices, aspect-ratio controls, and conditional save settings.
- [x] Integrate preview back-navigation, retained plans, and quick/advanced controls.
- [x] Add built-in purpose templates and rename, duplicate, delete, import/export preset controls.
- [x] Run targeted tests, full regression suite, Release build, and diff checks; document actual behavior and remaining scope.

## Verification (2026-11-05)

- Added 15 tests in MapCreationUsabilityTests. The initial directory-validation and local-output-preservation tests failed for the expected reasons before implementation. A profile-growth regression also failed before adding a size guard.
- Focused map-creation tests: 43 passed. Final complete Debug regression: 126 passed, 0 failed, 0 skipped.
- An earlier full run had one transient category-panel selection failure in an unchanged test. Its isolated rerun and subsequent full-suite reruns passed; unrelated category code was not changed.
- Release solution build: succeeded, 0 errors; 110 nullable, dependency-advisory and Avalonia-loader warnings remain. No warnings were reported in the new creation/session/preset components.
- Checked Chinese setup screenshots at 780 and 480 pixels high; the action footer remains visible. Checked generator settings structure and rendered all three generator windows using test libraries. These are UI/fixture checks, not acceptance of actual generated maps with production artwork.
- git diff --check passed. No branches, commits, new project dependencies, production services, or global memory changes were made.
- Usage and limits are documented in README.md. Multi-candidate comparison, gameplay connectivity checks, fixed-area reservation, and protected local regeneration remain later phases. NativeAOT publishing and real-game-artwork generation acceptance were not run.
