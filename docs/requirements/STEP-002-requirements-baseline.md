# STEP-002 — User Requirements Baseline

**Status:** Complete  
**Baseline date:** 28 August 2026  
**Amended:** 28 August 2026 — STEP-002.10 Requirements Baseline Amendment  
**Amended:** 31 August 2026 — STEP-002.11 Export Baseline Amendment  
**Amended:** 1 September 2026 — STEP-002.12 Cancellation Baseline Amendment  
**Amended:** 1 September 2026 — STEP-002.13 Tag Name Override Baseline Amendment

## Purpose

Translate the STEP-001 project baseline into controlled user needs, use cases and user requirements before detailed software specification.

## Baseline

STEP-002 established:

- 11 user needs;
- 7 use cases;
- 65 baselined user requirements;
- complete 65/65 requirement traceability;
- 6 resolved open questions;
- 13 recorded requirements decisions.

Primary baseline users are Planning personnel, including Document Control and Quotations.

## Requirement Coverage

The 65 requirements comprise:

- 25 Functional requirements;
- 18 Data / Input / Output requirements;
- 13 Non-functional / Quality requirements;
- 9 Constraints.

The baseline covers source selection, XML processing, nested archives, information discovery and extraction, cross-source structured information indexing/query/filtering, reusable profiles, preview, configurable Excel export field inclusion and ordering, Database Tag Name Override and modified-mapping identification, user-controlled processing cancellation, progress and error handling, reliability, local Windows operation and installable delivery.

## Key Decisions

- XML is the baseline input format; CSV is deferred.
- Core processing remains local rather than cloud/SaaS based.
- Normal application use must not require administrator privileges.
- Exact performance targets, archive limits and other measurable technical criteria are deferred to STEP-003.
- The structured cross-source query/filter capability is required without prescribing a specific database technology.
- Excel export configuration allows the user to choose which available result fields are included and their order; source context remains internally retained and available for export without being mandatory in every workbook.
- Active supported processing can be cancelled by the user; the approved system/interface control is the Esc key only, with no separate on-screen cancellation control or alternative cancellation shortcut.
- Selected discovered information may receive a user-defined Database Tag Name override before Database creation/update; multiple selected source tags assigned the same resulting Database Tag Name are intentionally represented under that one resulting Database information type while preserving required value/content and source provenance.
- Modified Database Tag Name mappings must be identifiable and locatable in Discovery, while exact visual treatment remains a STEP-004 UI-design decision.

## Traceability

All 65 user requirements are traced back to their project source and associated user need/use case where applicable.

The traceability model will continue into software requirements and verification evidence during later SDLC phases.

## Outcome

STEP-002 established the controlled User Requirements Baseline and was subsequently amended under STEP-002.10 to restore the structured cross-source information indexing/query/filter capability and under STEP-002.11 to restore the flexible Excel-export intent identified during STEP-003.4 review, and under STEP-002.12 to add explicit user-controlled cancellation identified during STEP-003.6 review, and under STEP-002.13 to formalize the Database Tag Name Override capability identified during STEP-004.2 architecture review.

The next SDLC activity is **STEP-003 — System / Software Requirements Specification**, where these user-level requirements will be converted into detailed and measurable software requirements.