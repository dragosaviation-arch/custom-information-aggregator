# STEP-004 — Architecture & Solution Design Baseline

**Status:** Complete  
**Baseline date:** 2 September 2026  
**Jira Epic:** SPR-24

## Purpose

Translate the approved STEP-003 System / Software Requirements Specification into an implementable, traceable solution architecture suitable for delivery planning and implementation.

## Approved architecture baseline

STEP-004 establishes the controlled solution architecture for the Custom Information Aggregator using the current approved **SR-001 through SR-168** requirements baseline.

Final reconciliation confirms:

- **168/168** System Requirements have a primary architecture owner / realization path.
- **0** orphan System Requirements.
- **0** duplicate primary-owner assignments.
- **DRV-001–DRV-009** — 9 approved architecture drivers.
- **CMP-001–CMP-013** — 13 approved logical components.
- **T1–T22** — 22 approved technology source decisions.
- **DP1–DP14** — 14 approved deployment/runtime source decisions.
- **ADR-001–ADR-012** — 12 permanent approved Architecture Decision Records.
- **0** unresolved architecture-significant forks.

Detailed architecture, alternatives, rationale, trade-offs and traceability remain controlled in Confluence.

## Solution structure

The application is a locally installed Windows desktop product built around a clear separation between presentation/orchestration and heavy processing.

Primary logical structure:

- **CMP-001 — Desktop UI / Presentation**
- **CMP-002 — Application Workflow Coordinator**
- **CMP-003 — Processing Host Runtime**
- **CMP-004 — Source Intake & Archive Pipeline**
- **CMP-005 — Source Adapter / Interpreter Layer**
- **CMP-006 — Discovery & Mapping Service**
- **CMP-007 — Structured Information Repository**
- **CMP-008 — Query & Extraction Service**
- **CMP-009 — Export Service**
- **CMP-010 — Working State & Persistence Service**
- **CMP-011 — Settings & Profile Service**
- **CMP-012 — Diagnostics & Processing History Service**
- **CMP-013 — Managed Storage & Cleanup Service**

The interactive UI and Processing Host are separate local OS processes inside one CIA runtime boundary.

## Core architecture decisions

### Runtime and UI

- Primary production platform: **C# / .NET 10 LTS**.
- Desktop UI: **WPF** using **MVVM + CommunityToolkit.Mvvm**.
- Composition/lifetime: **Microsoft Generic Host**, built-in dependency injection and configuration.
- UI and Processing Host communicate locally through **Windows Named Pipes**.
- IPC uses explicit typed, length-prefixed **System.Text.Json** command/response/event contracts.
- Heavy work does not execute in the UI process.

### Processing, concurrency and recovery

- Processing uses bounded producer/consumer execution with **System.Threading.Channels** and backpressure.
- XML processing uses **XmlReader** streaming with bounded XElement/LINQ-to-XML materialization where useful.
- Archive handling uses **SharpCompress**.
- Source/archive intake remains separate from source/schema-specific interpretation.
- User cancellation is initiated by **Esc only**.
- Cancellation is cooperative first, with controlled escalation when required.
- No hidden application-level automatic workload retry/resume loop is introduced for v1.
- Item-level failures do not automatically invalidate independent valid results.
- Terminal outcomes remain truthful and distinguish success, completion with issues, failure and cancellation as applicable.
- The last successfully saved working state is the guaranteed restart recovery boundary.

### Data and persistence

- Structured indexed information uses **SQLite via Microsoft.Data.Sqlite**.
- Data access uses explicit SQL and controlled schema evolution through **PRAGMA user_version** migrations.
- SQLite uses **WAL**, explicit transactions and a coordinated single-writer / snapshot-reader model.
- Protected durable replacement uses candidate → validate → controlled publish/replace semantics where preservation of the prior valid artifact is required.
- Saved working state is represented as a single **.cia** package containing a consistent SQLite snapshot plus a versioned JSON manifest.
- Original loaded source data remains read-only.

### Discovery, mapping and profiles

- Discovery remains a lightweight tag/source/count catalog rather than an eager full occurrence database.
- The Database/structured repository stores occurrence-level indexed/queryable information.
- Database Tag Name Override is user-controlled before Database create/update.
- The resulting Database information type/tag uses the configured Database Tag Name when an override exists.
- Multiple selected source tags may intentionally map to the same resulting Database Tag Name while retaining required value/content and source provenance.
- Reusable information-selection profiles persist **selected/excluded discovered-tag membership only**.
- Database Tag Name Override mappings are **not** stored in or restored from reusable information-selection profiles.

### Diagnostics, export and verification tooling

- Logging uses **ILogger<T> + Serilog** with **CLEF / Serilog.Formatting.Compact** structured local logs.
- UI and Processing Host use separate physical log streams correlated by Operation ID/context.
- Technical timestamps use UTC **DateTimeOffset** with ISO-8601 round-trip serialization; correlation IDs use **UUIDv7**.
- Diagnostics do not automatically dump bulk/raw proprietary source content.
- Excel export uses **Open XML SDK + streaming OpenXmlWriter** and does not require Microsoft Excel.
- Automated .NET testing uses **MSTest + Microsoft Testing Platform**.
- Performance investigation uses **BenchmarkDotNet**, while requirement verification uses a CIA-specific end-to-end benchmark harness.

## Deployment baseline

- Publish target: self-contained **win-x64**, multi-file .NET 10 application.
- Primary v1 installer: **NSIS**.
- Installation scope: per-user, under LocalAppData, without administrator elevation.
- Mutable CIA-managed data is stored separately from installed binaries.
- No separate .NET, Excel, SQLite or external archive utility installation is required.
- Windows 11 x64 is the primary supported baseline.
- Windows 10 x64 remains an explicit compatibility-tested target for existing environments where technically compatible.
- Upgrades use a newer user-initiated installer; no automatic updater/background update service is required for v1.
- Only **CIA.exe** is user-facing; the Processing Host is internal and is not installed as a Windows Service/startup application.
- Normal installation uses a graphical installer flow without Command Prompt/PowerShell/console windows.
- Uninstall removes application binaries/integration by default while preserving CIA-managed/user data unless explicit removal is selected.
- Official public v1 releases target **SignPath Foundation** code signing if the project qualifies; no paid signing certificate is required by the v1 baseline.
- MSIX / Microsoft Store remains a possible future packaging channel, not the primary v1 package.

## Architecture drivers

The controlled architecture-driver range is **DRV-001–DRV-009**:

1. large-workload scalability, resource stability and bounded processing time;
2. responsive UI during active processing;
3. controlled lifecycle, cancellation, failure isolation and recovery;
4. structured cross-source indexing, query and filtering;
5. persistence, recoverable working state and deterministic integrity;
6. local data boundary and source immutability;
7. Windows desktop deployment under standard-user operation;
8. source-agnostic core processing;
9. usability, structured review and understandable diagnostics.

## Permanent ADR baseline

- **ADR-001** — Windows/.NET Desktop Foundation
- **ADR-002** — Separate UI / Processing Host and Local IPC Contract
- **ADR-003** — Bounded Processing, Concurrency & Recovery Model
- **ADR-004** — XML / Archive Processing and Source-Adapter Isolation
- **ADR-005** — SQLite Structured Information Repository
- **ADR-006** — Versioned Durable State and .cia Saved-State Package
- **ADR-007** — Structured Diagnostics and Correlation
- **ADR-008** — Streaming Excel Export
- **ADR-009** — Validation Strategy
- **ADR-010** — Automated Test & Performance Verification Tooling
- **ADR-011** — Windows Packaging, Installation & Runtime Distribution
- **ADR-012** — Release Code-Signing Strategy

ADR identifiers are permanent. Later architectural changes supersede prior ADRs rather than deleting or renumbering them.

## Implementation / delivery boundary

STEP-004 intentionally does not freeze low-level implementation parameters such as:

- exact worker counts, channel capacities and batch sizes;
- heartbeat/liveness and cancellation-escalation timing;
- exact SQLite tables, indexes, statements and performance-tuning values;
- code-level synchronization details;
- exact log retention/rotation limits;
- detailed UI controls, styling, wireframes and branding;
- detailed .cia package internals beyond the approved SQLite snapshot + versioned JSON manifest contract;
- release-specific supported XML/archive-format matrix;
- detailed NSIS/build-pipeline implementation;
- detailed test cases and verification evidence.

These are controlled through later delivery, implementation and verification work without silently changing the approved architecture.

## Review result

STEP-004.12 reviewed the complete architecture for:

- consistency with SR-001 through SR-168;
- architecture-driver coverage;
- component ownership;
- requirements-to-architecture traceability;
- logical/runtime/data/UI/cross-cutting coherence;
- technology/deployment consistency;
- ADR completeness and permanence;
- unresolved forks and hidden scope changes;
- implementation-vs-architecture boundary;
- Jira / Confluence / GitHub baseline synchronization.

The review found documentation-state cleanup items only. No architecture defect, orphan requirement or requirements amendment was required.

## Outcome

STEP-004 is complete and baselined.

The architecture is ready for **STEP-005 — Delivery Planning / Backlog Decomposition**.
