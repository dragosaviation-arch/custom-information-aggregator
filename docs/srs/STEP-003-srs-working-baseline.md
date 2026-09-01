# STEP-003 — System / Software Requirements Specification — Working Baseline

**Status:** In Progress  
**Current control point:** STEP-003.7 — System Constraints & Dependencies  
**Jira Epic:** SPR-11  
**Active sprint:** Sprint 3 — SRS 2  
**Sprint end:** 3 September 2026, 23:30 Europe/Bucharest

This file is the lean GitHub-side working representation of the controlled STEP-003 SRS work. Confluence remains the detailed documentation source during requirements engineering; Jira remains the delivery/work-tracking source.

## STEP-003 status

| Substep | Scope | Jira | Status |
| --- | --- | --- | --- |
| STEP-003.1 | SRS Framework & Requirements Control | SPR-12 | Complete |
| STEP-003.2 | System Functional Requirements | SPR-13 | Complete |
| STEP-003.3 | Data & Information Requirements | SPR-15 | Complete |
| STEP-003.4 | Interface Requirements | SPR-17 | Complete |
| STEP-003.5 | Quality / Non-functional Requirements | SPR-18 | Complete |
| STEP-003.6 | Operational, Error & Recovery Requirements | SPR-19 | Complete |
| STEP-003.7 | System Constraints & Dependencies | SPR-20 | In Progress |
| STEP-003.8 | User Requirement → System Requirement Traceability | SPR-21 | To Do |
| STEP-003.9 | SRS Review & Baseline | SPR-22 | To Do |

## Controlled requirement range

The current controlled System / Software Requirement range is **SR-001 through SR-154**.

- **SR-001–SR-027** — Functional requirements; approved.
- **SR-028–SR-046** — Data & information requirements; approved.
- **SR-047–SR-103** — Interface requirements; approved.
- **SR-104–SR-122** — Quality requirements; approved.
- **SR-123–SR-154** — Operational / Error & Recovery requirements; approved.

Requirement identifiers are permanent and are not renumbered when later requirements are added.

## STEP-003.5 progress

### STEP-003.5.1 — Responsiveness & Feedback — Complete

Approved Quality requirements: **SR-104–SR-109**.

Key controlled expectations include:
- supported UI interactions remain available during long-running processing;
- supported user interactions receive visible acknowledgement within a maximum of 2 seconds under reference test conditions;
- active processing does not go more than 5 seconds without visible activity/status indication unless completion/failure is shown earlier;
- active, completed and failed states are explicitly distinguishable;
- ordinary error/failure comprehension does not depend on developer-oriented diagnostics.

### STEP-003.5.2 — Reliability & Repeatability — Complete

Approved Quality requirements: **SR-110–SR-114**.

Key controlled expectations include:
- expected processing errors do not cause uncontrolled application termination;
- failed processing does not invalidate previously valid profiles, working states or completed exported outputs;
- repeated supported processing with unchanged source data and configuration produces equivalent deterministic result content.

### STEP-003.5.3 — Performance & Workload — Complete

Approved Quality requirements: **SR-115–SR-116**.

Performance baseline:
- timed path begins when the user initiates loading of the controlled supported source archive/package;
- includes supported archive extraction/unpacking, supported XML availability/loading and Discovery processing;
- representative workload yields at least **30,000 supported XML files** and includes archive nesting through **three levels**;
- timed path ends when Discovery results are available for review/selection;
- maximum acceptance time: **30 minutes** on **RTE-001**;
- post-Discovery query/filter, information extraction, review and export are outside the timed path.

Representative large/complex workload baseline:
- at least 30,000 supported XML files;
- nested archive processing through **three levels**;
- no arbitrary package byte-size threshold is imposed without evidence;
- three archive levels is the representative/default depth, not the hard product maximum.

### RTE-001 — Reference Test Environment

RTE-001 is the project reference laptop used for controlled performance verification:

- Dell Vostro 3520
- Intel Core i7-1255U
- 16 GB RAM
- 512 GB Solidigm P41PL NVMe SSD
- Windows 11 Pro x64, 25H2

The exact application build, dataset revision and relevant test conditions are recorded with performance results. Hardware details are a verification baseline, not a deployment requirement.

### STEP-003.5.4 — Usability & Readability — Complete

Approved Quality requirements: **SR-117–SR-118**.

Key controlled expectations include:
- the intended Planning User can complete UC-001 through UC-005 without developer assistance or knowledge of internal implementation/XML-processing logic;
- extracted information presented for review is structured and distinguishable without requiring raw XML markup inspection.

### STEP-003.5.5 — Diagnostics & User Guidance — Complete

Approved Quality requirements: **SR-119–SR-122**.

Key controlled expectations include:
- retained failed-operation diagnostics identify the operation/stage, affected source/item where determinable, and associated error/failure description;
- the same user-facing concepts, states and operations use consistent terminology across UC-001 through UC-005;
- controls representing the same supported action retain consistent meaning and user-visible effect;
- user guidance is sufficient for an intended Planning User to complete UC-001 through UC-005 without developer-level assistance.

### STEP-003.5.6 — Quality Review — Complete

The full **SR-104–SR-122** batch passed review after five approved corrections:
- removed the undefined long-running-operation threshold from SR-104–SR-106 and anchored SR-105 to RTE-001;
- explicitly defined the SR-115 stopwatch boundary from source archive/package load through Discovery-result availability;
- made the SR-116 representative workload self-contained at ≥30,000 supported XML files and three archive levels;
- strengthened SR-122 to require guidance usable without person-to-person explanation of the standard workflow;
- removed the weak SR-082 → UR-NF-012 supporting trace.

Final review: 19/19 Quality SRs present, 13/13 non-functional URs directly covered, 19/19 Quality SRs reverse-traced, no duplicate Quality obligations identified.

### STEP-003.5.7 — Approval / Closure — Complete

**SR-104–SR-122 are Approved.**

STEP-003.5 closed on 1 September 2026 after full Quality review, traceability reconciliation and final project-wide audit.

### STEP-003.6 — Operational, Error & Recovery Requirements — Complete

**SR-123–SR-154 are Approved.**

STEP-003.6 closed on 1 September 2026 after derivation, review and traceability reconciliation.

Key controlled outcomes include:
- whole-operation failure and item-level partial-failure semantics;
- bounded failure propagation so unrelated valid results remain usable;
- explicit partial-completion, cancelled and detectable interrupted/incomplete states;
- user-triggered cancellation through **Esc only** under STEP-002.12 / RD-012;
- no dedicated Retry control or automatic retry loop: retry is normal re-initiation from a valid current/restored state;
- saved indexed information restores from a successfully saved working state without reprocessing original source data under RD-009;
- safe cleanup boundaries for temporary/session/interrupted artifacts;
- persistent archive extraction is protected from ordinary temporary cleanup;
- Reset to defaults restores configuration without becoming a destructive user-data cleanup action.

Final STEP-003.6 review:
- 32/32 requirement IDs present and unique;
- 32/32 requirements approved;
- 32/32 reverse-traced;
- zero affected forward-traceability gaps;
- SR-138 corrected to mandatory `shall` wording before closure.

### STEP-003.7 — System Constraints & Dependencies — Current

This is the current control point.

Scope includes operating environment, deployment, administrator-right restrictions, supported-environment boundaries and remaining solution constraints/dependencies not already controlled by earlier STEP-003 work.

## Current traceability state

Forward and reverse traceability has been reconciled through **SR-154** for completed STEP-003.6 derivation.

Performance/workload decisions have been resolved:
- OQ-002 / RD-002 — 30,000 supported XML files within 30 minutes on RTE-001;
- OQ-003 / RD-003 — representative large/complex workload includes at least 30,000 XML files and three-level nested archive processing;
- RD-011 remains unchanged: archive depth is configurable, default three levels.

Final SRS-wide traceability reconciliation is still reserved for STEP-003.8.

## Control boundary

STEP-003 remains implementation-neutral.

- Approved retry, interruption, continuation, cleanup and recovery semantics are controlled by completed STEP-003.6.
- Operating-environment, deployment and remaining solution constraints belong to STEP-003.7.
- Architecture, technology selection, concrete storage/database design and final UI design belong to STEP-004 unless an approved requirement or constraint mandates otherwise.

## Next activity

Continue **STEP-003.7 — System Constraints & Dependencies**.
