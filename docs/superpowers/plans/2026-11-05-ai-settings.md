# AI Settings Implementation Plan

**Goal:** Independent AI settings and undoable name/category recognition in extraction.

**Architecture:** Extend LibrarySettings with optional AiSettings. AiClient translates endpoint settings to native provider HTTP requests. Extraction recognition parses strict metadata and applies it through ExtractionReview. Existing Avalonia settings and extraction windows host the controls.

**Tech Stack:** .NET 10, Avalonia, System.Net.Http, System.Text.Json.Nodes, xUnit.

- [x] Add regression tests in `CrystalStudio.Tests/AiTests.cs` for persistence, independent credentials, provider requests, custom templates, invalid classifications, authentication, timeouts, downloads and undo.
- [x] Add `AiSettings.cs`, `AiClient.cs` and `ObjectExtraction.Ai.cs`; extend `SettingsStore.cs`, `StudioJson.cs` and `ObjectExtraction.Models.cs`. Use JsonNode and HttpClient injection for AOT-compatible payloads and offline protocol tests.
- [x] Add `AiSettingsPanel.cs`, integrate it into `SettingsWindow.cs`, and pass settings through `MainWindow.axaml.cs`. Preserve existing settings overloads and control behavior.
- [x] Connect recognition actions in `ObjectExtractionWindow.cs` and live settings access in `MainWindow.ObjectExtraction.cs`. Render PNG previews using StampPreview, accumulate metadata, then apply one review operation after complete success.
- [x] Add localization entries and README usage. Verified `dotnet test CrystalStudio.sln -c Release --no-restore` (105 passed), `dotnet build CrystalStudio.sln -c Release --no-restore` (0 errors), and `git diff --check`.
