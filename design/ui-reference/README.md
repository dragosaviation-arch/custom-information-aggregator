# CIA Temporary UI Design Reference

## Status

Active temporary implementation guidance.

## Purpose

Preserve the user's current visual and interaction direction in Git so Codex and later implementation work can inspect the same controlled reference instead of relying on screenshots, chat memory, or interpretation chains.

## Authority boundary

- Approved Jira and Confluence requirements and architecture remain authoritative for product behavior, capability, and system boundaries.
- Production source code remains authoritative for currently implemented functionality.
- The controlled top-level CIA navigation is exactly:
  - Load
  - Discovery
  - Database
  - Settings
- These files provide visual and presentation guidance only.
- They do not create new functional requirements.
- If the prototype displays a capability that has not yet been implemented or approved for the current story, its presence does not authorize implementation of that capability.

## Reference interpretation

- `load/CIA_Load_UI_Spec.json` is the primary measurable visual specification for the areas it defines, except for workspace/page count, workspace naming, top-level navigation grouping, or any other area explicitly superseded by controlled project authority.
- `load/CIA_Load_UI_Preview.html` is the complementary visual and interaction reference for composition, hierarchy, and intended appearance.
- `discovery/CIA_Discovery_UI_Spec.json` and `discovery/CIA_Discovery_UI_Preview.html` provide the equivalent temporary guidance for the future Discovery workspace.
- If the HTML and JSON differ on a measurable visual property explicitly defined by the JSON, prefer the JSON.
- Neither reference overrides approved architecture or requirements.

## Controlled top-level navigation

The authoritative top-level navigation is exactly:

- Load
- Discovery
- Database
- Settings

Capability ownership remains controlled as follows:

- extraction, review, and export remain product capabilities within Database;
- activity, processing history, logs, and diagnostics remain product capabilities within Settings;
- configuration and maintenance remain within Settings;
- persistent global operation/status remains an application-level surface and is not a separate tab.

The current references reflect this four-tab structure. Later reference updates must continue to defer to controlled project authority if navigation or capability ownership diverges.

## Prototype-only behavior

The HTML contains demonstration and mock behavior. Mock data, JavaScript simulation, fake loading progress, placeholder workspace behavior, mock add/remove/reload actions, and other prototype-only logic are not production implementation requirements.

Do not port prototype JavaScript or mock state into CIA production code merely because it exists in the HTML.

## Future functionality

The visual reference may show fields, controls, status information, or capabilities whose implementation stories occur later in the backlog. Future production work must connect those presentation elements to real application state only when their owning functionality is implemented.

Do not invent backend, domain, or runtime behavior simply to make the prototype appear functional.

## Runtime isolation

Nothing under `design/ui-reference/` may become a runtime dependency.

Do not:

- load these files at application runtime;
- embed them as WPF resources;
- add them to production project files;
- package them with CIA;
- make runtime behavior depend on them;
- translate HTML or JavaScript directly into production application logic.

These files exist only for humans and Codex to inspect during implementation.

## Revision handling

The canonical current files are:

- `design/ui-reference/load/CIA_Load_UI_Preview.html`
- `design/ui-reference/load/CIA_Load_UI_Spec.json`
- `design/ui-reference/discovery/CIA_Discovery_UI_Preview.html`
- `design/ui-reference/discovery/CIA_Discovery_UI_Spec.json`

When the user supplies a newer approved revision:

- replace or update these canonical files in place;
- do not accumulate numbered duplicate files unless explicitly requested;
- preserve revision history through Git commits;
- use the version contained in the supplied design material where applicable;
- never invent a design revision or silently modify the design;
- apply only explicitly approved semantic reconciliations when an imported reference contains a known interaction contradiction.

## Lifecycle

These reference files are temporary. Once the production CIA UI has reached its final controlled implementation state, the `design/ui-reference/` directory may be removed from the active repository tree. Git history will retain the historical design evolution.
