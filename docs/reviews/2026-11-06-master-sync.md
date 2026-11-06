# 2026-11-06 master synchronization

## Inputs and safety

- Original local commit: `90a244df4437c35d2f294370ecdba7eb27a31941`.
- Initial upstream baseline: `161aaad`; final tested upstream baseline: `e51ba82`.
- The working tree was clean; local and upstream had one and fourteen unique commits respectively.
- Local backup: `codex/master-before-sync-20261106`.
- Replay the unpublished local commit on upstream; retain upstream history and use a normal push, not a forced push.
- Upstream gained `3ad2ef5` and `e51ba82` during verification. The first ordinary push safely rejected that concurrent update; replay the local work again and retain both minimap-export commits.

## Conflict decisions

- Keep upstream's `MapCreationSession` and creation loop: name/output edits retain the preview, while size/type/parameter edits rebuild it. Do not restore the older duplicate navigation loop.
- Keep upstream templates, preset management, generation-only preset schema, resource preflight, candidates, favorites, and point-based detail inspection.
- Keep local numeric editors, scene/theme drafts, read-only standalone navigation, earlier seed restoration, naming/path validation, and bounded feedback areas.
- Connect the wrapped preview image to upstream's inspection viewport so resource feedback and candidate completion handlers are still registered. Hide the standalone navigator only while using Quick Create, avoiding duplicate navigation controls.
- Keep view position across equal-size image updates and support middle-button panning in the Quick Create viewport.
- Do not reapply all saved parameters after asynchronous artwork loading. Before loading finishes, returning to settings captures editable non-resource parameters while retaining pending resource selections.
- Use the session-based return button and adapt legacy tests to its return-to-preview cancellation behavior. Per-type draft restoration and the generation-only preset constructor follow upstream's newer contract.
- Preserve user-defined preset labels without translating them; the built-in last-settings option remains translated.

## Validation

- Release interaction/integration selection: 75 passed, zero failures.
- Final Release rebuild: zero errors, 108 warnings.
- Final Release full suite, including upstream minimap-export tests: 299 executed, 298 passed, one failed, zero skipped.
- The failing test is `AssetSourceFormatTests.SanxiuCategoryPreviewsResolveAllThreeLayerLibraryIds`, reporting `The category preview for layer 1 was transparent.`
- Isolated snapshots of unmodified upstream `161aaad` and `e51ba82` both reproduce that same test failure and message. This is not a new integration regression; no unrelated category-preview code was changed. The upstream full suite was not rerun.
- Seven additional integration cases cover wrapped inspection controls, unchanged generation parameters, equal-size zoom retention, resource-loading edits, and early navigation with pending resource selections.
- Localization JSON has no duplicate keys and preserves every entry/value from both input versions: 1,181 final upstream entries, 1,036 local entries, 1,202 combined entries.
- Reviewed the generated Chinese minimum-size Quick Create screenshot. Tests use fixture artwork; this is not validation with real game artwork or a full native application acceptance test.

Evidence is in ignored build outputs:

- `Build/TestResults/sync-integration-targeted.trx`
- `Build/sync-final-build.log`
- `Build/TestResults/sync-final-full.trx`
- `Build/TestResults/sync-final-upstream-baseline.trx`
- `Build/Release/Tests/QuickMapScreenshots/refinement-zh-minimum.png`
