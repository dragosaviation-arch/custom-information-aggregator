# STEP-003 — System / Software Requirements Specification — Working Baseline

**Status:** In Progress  
**Current control point:** STEP-003.5.6 — Quality Review  
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
| STEP-003.5 | Quality / Non-functional Requirements | SPR-18 | In Progress |
| STEP-003.6 | Operational, Error & Recovery Requirements | SPR-19 | To Do |
| STEP-003.7 | System Constraints & Dependencies | SPR-20 | To Do |
| STEP-003.8 | User Requirement → System Requirement Traceability | SPR-21 | To Do |
| STEP-003.9 | SRS Review & Baseline | SPR-22 | To Do |

## Controlled requirement range

The current controlled System / Software Requirement range is **SR-001 through SR-122**.

- **SR-001–SR-027** — Functional requirements; approved.
- **SR-028–SR-046** — Data & information requirements; approved.
- **SR-047–SR-103** — Interface requirements; approved.
- **SR-104–SR-122** — Quality requirements; draft pending STEP-003.5 review and approval.

Requirement identifiers are permanent and are not renumbered when later requirements are added.

## STEP-003.5 progress

### STEP-003.5.1 — Responsiveness & Feedback — Complete

Draft Quality requirements: **SR-104–SR-109**.

Key controlled expectations include:
- supported UI interactions remain available during long-running processing;
- supported user interactions receive visible acknowledgement within a maximum of 2 seconds under reference test conditions;
- active processing does not go more than 5 seconds without visible activity/status indication unless completion/failure is shown earlier;
- active, completed and failed states are explicitly distinguishable;
- ordinary error/failure comprehension does not depend on developer-oriented diagnostics.

### STEP-003.5.2 — Reliability & Repeatability — Complete

Draft Quality requirements: **SR-110–SR-114**.

Key controlled expectations include:
- expected processing errors do not cause uncontrolled application termination;
- failed processing does not invalidate previously valid profiles, working states or completed exported outputs;
- repeated supported processing with unchanged source data and configuration produces equivalent deterministic result content.

### STEP-003.5.3 — Performance & Workload — Complete

Draft Quality requirements: **SR-115–SR-116**.

Performance baseline:
- reference processing workflow for at least **30,000 supported XML files**;
- maximum acceptance time: **30 minutes**;
- measured on the controlled reference test environment **RTE-001**.

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

Draft Quality requirements: **SR-117–SR-118**.

Key controlled expectations include:
- the intended Planning User can complete UC-001 through UC-005 without developer assistance or knowledge of internal implementation/XML-processing logic;
- extracted information presented for review is structured and distinguishable without requiring raw XML markup inspection.

### STEP-003.5.5 — Diagnostics & User Guidance — Complete

Draft Quality requirements: **SR-119–SR-122**.

Key controlled expectations include:
- retained failed-operation diagnostics identify the operation/stage, affected source/item where determinable, and associated error/failure description;
- the same user-facing concepts, states and operations use consistent terminology across UC-001 through UC-005;
- controls representing the same supported action retain consistent meaning and user-visible effect;
- user guidance is sufficient for an intended Planning User to complete UC-001 through UC-005 without developer-level assistance.

### STEP-003.5.6 — Quality Review — Current

This is the current control point.

The Quality requirement batch **SR-104–SR-122** is ready for completeness, consistency, duplication, ambiguity, implementation-neutrality, verification and traceability review before approval/closure.

## Current traceability state

Forward and reverse traceability has been reconciled through **SR-122** for completed STEP-003.5 derivation substeps.

Performance/workload decisions have been resolved:
- OQ-002 / RD-002 — 30,000 supported XML files within 30 minutes on RTE-001;
- OQ-003 / RD-003 — representative large/complex workload includes at least 30,000 XML files and three-level nested archive processing;
- RD-011 remains unchanged: archive depth is configurable, default three levels.

Final SRS-wide traceability reconciliation is still reserved for STEP-003.8.

## Control boundary

STEP-003 remains implementation-neutral.

- Detailed retry, interruption, continuation, cleanup and recovery mechanics belong to STEP-003.6.
- Operating-environment, deployment and remaining solution constraints belong to STEP-003.7.
- Architecture, technology selection, concrete storage/database design and final UI design belong to STEP-004 unless an approved requirement or constraint mandates otherwise.

## Next activity

Perform **STEP-003.5.6 — Quality Review**.
