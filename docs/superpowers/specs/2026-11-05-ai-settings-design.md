# AI settings and extraction recognition

Reference: `D:/projects/MapGenerator/web/js/ai.js`. The user confirmed recognition should primarily correct object names and categories.

Store independent vision, image and chat endpoints in the existing settings file. Each endpoint owns its provider, URL, key, model, authentication, path, timeout, extra JSON and response path. Preserve old settings constructors and files. Presets cover OpenAI compatible, Gemini, Ark, DashScope, Zhipu, SiliconFlow, Stability, SD WebUI and custom requests. Stability and SD WebUI are image-only. Desktop HTTP calls need no browser proxy.

Expose settings in three role tabs with advanced fields and explicit request tests. Image tests clearly indicate that an image is generated. Implement native Gemini, DashScope, Stability and SD requests as well as OpenAI compatible requests and custom JSON placeholders.

Extraction provides selected and all-unreviewed recognition actions. Send isolated artwork and map context to the vision endpoint, requesting a short name, one allowed catalogue category and an explanation. Apply successful results as one undoable review command, keeping results unreviewed. Cancellation or failure leaves the review unchanged. Preserve fragment data, collision, occurrences, source maps and editor history.

Validate old/new settings round trips, role-specific credentials, provider protocols, custom JSON escaping, malformed responses, cancellation and atomic review undo. Run targeted tests, headless UI checks, the solution suite and build.
