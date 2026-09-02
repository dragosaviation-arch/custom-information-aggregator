# STEP-005 — Delivery Planning / Backlog Decomposition Baseline

**Status:** Complete  
**Baseline date:** 2 September 2026  
**Jira Epic:** SPR-39  
**Final review:** STEP-005.11 / SPR-121

## Purpose

Convert the approved STEP-003 requirements and STEP-004 architecture baselines into an executable, estimated, prioritized and sequenced implementation backlog for STEP-006.

## Delivery-plan baseline

STEP-005 establishes the complete currently known v1 implementation backlog.

Final reconciliation confirms:

- **8** approved implementation Epics: SPR-43 through SPR-50.
- **63** executable implementation items: SPR-52 through SPR-114.
- **32 Stories / 31 Tasks**.
- **0 Subtasks** at baseline; Subtasks remain optional where they later improve execution.
- **63/63** implementation items contain approved acceptance criteria.
- **63/63** implementation items are estimated.
- **328 total Story Points**.
- **78** controlled hard-prerequisite links.
- The dependency graph is **acyclic**.
- **0** dependency / delivery-order violations.
- **63/63** implementation items have exactly one controlled delivery-order label, `delivery-order-001` through `delivery-order-063`.
- Priority distribution: **P0 21 / P1 22 / P2 14 / P3 6**.
- **63/63** implementation items are assigned to exactly one native future Jira sprint from Sprint 6 through Sprint 16.
- All implementation items remain **To Do** at STEP-005 closure.
- **168/168** System Requirements are accounted for downstream with **0 orphan requirements**.
- **DRV-001–DRV-009**, **CMP-001–CMP-013** and **ADR-001–ADR-012** retain complete downstream realization coverage.

Detailed planning records, acceptance criteria, dependency links, traceability and sprint control remain authoritative in Jira and Confluence.

## Backlog hierarchy

The approved hierarchy is:

**Epic → Story / Task → optional Subtask**

- **Story** — user-visible or product behavior naturally expressed as a user goal.
- **Task** — technical, enabling, infrastructure, architecture implementation, test enablement, packaging, documentation or similar work.
- **Subtask** — introduced only when it improves execution clarity; no mandatory pre-decomposition is required.

## Implementation Epics

- **SPR-43 — Application Foundation & Runtime Integration**
- **SPR-44 — Source Intake & Interpretation**
- **SPR-45 — Discovery & Mapping**
- **SPR-46 — Database & Query**
- **SPR-47 — Extraction, Review & Export**
- **SPR-48 — Working State, Profiles & Settings**
- **SPR-49 — Diagnostics, Recovery & Managed Storage**
- **SPR-50 — Deployment, Compatibility & Release Readiness**

Item distribution by Epic is 9 / 8 / 7 / 7 / 7 / 8 / 9 / 8 respectively, totaling 63 implementation items.

## Release / increment strategy

The approved implementation sequence is:

**Walking Skeleton → Alpha → Beta → Release Candidate → v1.0**

STEP-006 favors vertical capability increments while retaining necessary enabling technical work. Milestones are completion thresholds, not prohibitions on pulling enabling or risk-reduction work forward.

Formal verification remains STEP-007. Formal release remains STEP-008.

## Estimation and ordering

CIA uses Fibonacci Story Points:

**1, 2, 3, 5, 8, 13**

Story Points represent relative delivery effort, complexity, uncertainty, integration effort and verification burden. They are not hours or calendar duration.

A 13-SP item is normally a decomposition trigger before sprint commitment; the final baseline contains no unresolved oversize item preventing STEP-006 entry.

The controlled `delivery-order-###` labels are the authoritative implementation pull order because connected Jira Rank updates did not persist reliably during STEP-005.8.

The final ordered backlog remains compatible with all 78 hard dependencies.

## Sprint and milestone plan

Implementation uses one-week sprints initially, with Sprint 6 as the first calibration sprint. Later sprint capacity may be recalibrated from actual completed velocity rather than forcing the baseline estimates into fixed calendar promises.

| Sprint | Items | SP | Planned outcome |
| --- | ---: | ---: | --- |
| Sprint 6 | 6 | 29 | Core runtime / process / IPC foundation |
| Sprint 7 | 5 | 26 | Walking Skeleton + early deployment-risk proof |
| Sprint 8 | 4 | 29 | Operation semantics, diagnostics, cancellation, first source loading |
| Sprint 9 | 6 | 35 | Source interpretation / intake + SQLite foundation |
| Sprint 10 | 6 | 31 | Usable Discovery workflow |
| Sprint 11 | 6 | 34 | Failure/status UX + Database-generation foundations |
| Sprint 12 | 6 | 29 | Database → query → extraction |
| Sprint 13 | 6 | 32 | Alpha + settings / state foundations |
| Sprint 14 | 5 | 31 | Recovery / cleanup + durable profiles / working state |
| Sprint 15 | 7 | 26 | Beta |
| Sprint 16 | 6 | 26 | Release Candidate implementation threshold |

**Total: 63 items / 328 SP.**

Milestone thresholds:

- **Walking Skeleton:** end of Sprint 7.
- **Alpha:** end of Sprint 13, with the integrated core path **Load → Discover → Database → Extract → Review → Excel Export**.
- **Beta:** end of Sprint 15, with planned v1 product capabilities implemented.
- **Release Candidate implementation threshold:** end of Sprint 16.

Sprint forecasts are controlled plans, not immutable calendar promises. Unfinished work rolls forward in approved dependency/delivery order, and sprint capacity may be recalibrated using actual implementation velocity.

## Requirements and architecture traceability

STEP-005.10 reconciled the delivery backlog against the complete approved requirements and architecture baselines.

Final state:

- System Requirements accounted for: **168/168**.
- Orphan System Requirements: **0**.
- Architecture Drivers represented: **9/9**.
- Logical Components represented: **13/13**.
- Permanent ADRs represented: **12/12**.

SR-032 was explicitly added to SPR-65, its existing CMP-005 delivery owner, together with source-value/content preservation acceptance criteria.

SR-115 and SR-116 are Performance Test / Stress Test requirements. Their implementation realization is cross-cutting across the processing path; formal acceptance evidence is intentionally controlled under STEP-007 using the approved ADR-010 / T15 performance-verification approach, including the CIA controlled end-to-end benchmark harness.

## Final review result

STEP-005.11 reviewed:

- all STEP-005 substeps and control states;
- Jira Epic / Story / Task structure;
- acceptance-criteria completeness;
- estimates and total Story Points;
- hard dependencies and graph integrity;
- controlled delivery order and priority tiers;
- native Sprint 6–16 assignment;
- milestone thresholds;
- requirements / architecture traceability;
- Confluence hierarchy and duplicate-page state;
- GitHub branch / baseline state.

The final review found one planning-metadata inconsistency: SPR-40 / STEP-005.1 was complete but had not been assigned to Sprint 5. It was reconciled to Sprint 5 and STEP-005.1 through STEP-005.3 Confluence headers were updated to carry consistent sprint metadata.

No product-scope defect, requirement gap, architecture gap, backlog decomposition defect, estimate defect, dependency defect or sprint-plan defect required reopening an approved baseline.

## Outcome

STEP-005 is complete and baselined.

The project is ready for **STEP-006 — Implementation / Development**, beginning with **Sprint 6 — Runtime Foundation**.
