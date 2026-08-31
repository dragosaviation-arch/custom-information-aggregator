# STEP-002 — User Requirements Baseline

**Status:** Complete  
**Baseline date:** 28 August 2026  
**Amended:** 28 August 2026 — STEP-002.10 Requirements Baseline Amendment  
**Amended:** 31 August 2026 — STEP-002.11 Export Baseline Amendment

## Purpose

Translate the STEP-001 project baseline into controlled user needs, use cases and user requirements before detailed software specification.

## Baseline

STEP-002 established:

- 11 user needs;
- 7 use cases;
- 61 baselined user requirements;
- complete 61/61 requirement traceability;
- 6 resolved open questions;
- 11 recorded requirements decisions.

Primary baseline users are Planning personnel, including Document Control and Quotations.

## Requirement Coverage

The 61 requirements comprise:

- 22 Functional requirements;
- 17 Data / Input / Output requirements;
- 13 Non-functional / Quality requirements;
- 9 Constraints.

The baseline covers source selection, XML processing, nested archives, information discovery and extraction, cross-source structured information indexing/query/filtering, reusable profiles, preview, configurable Excel export field inclusion and ordering, progress and error handling, reliability, local Windows operation and installable delivery.

## Key Decisions

- XML is the baseline input format; CSV is deferred.
- Core processing remains local rather than cloud/SaaS based.
- Normal application use must not require administrator privileges.
- Exact performance targets, archive limits and other measurable technical criteria are deferred to STEP-003.
- The structured cross-source query/filter capability is required without prescribing a specific database technology.
- Excel export configuration allows the user to choose which available result fields are included and their order; source context remains internally retained and available for export without being mandatory in every workbook.

## Traceability

All 61 user requirements are traced back to their project source and associated user need/use case where applicable.

The traceability model will continue into software requirements and verification evidence during later SDLC phases.

## Outcome

STEP-002 established the controlled User Requirements Baseline and was subsequently amended under STEP-002.10 to restore the structured cross-source information indexing/query/filter capability and under STEP-002.11 to restore the flexible Excel-export intent identified during STEP-003.4 review.

The next SDLC activity is **STEP-003 — System / Software Requirements Specification**, where these user-level requirements will be converted into detailed and measurable software requirements.