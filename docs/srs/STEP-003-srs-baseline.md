# STEP-003 — System / Software Requirements Specification Baseline

**Status:** Complete  
**Baseline date:** 1 September 2026  
**Jira Epic:** SPR-11

## Purpose

Translate the approved STEP-002 user requirements into a controlled, implementation-neutral System / Software Requirements Specification suitable for architecture, implementation planning and later verification.

## Approved baseline

STEP-003 establishes **163 approved System / Software Requirements**:

- **SR-001–SR-027** — Functional
- **SR-028–SR-046** — Data & Information
- **SR-047–SR-103** — Interface
- **SR-104–SR-122** — Quality / Non-functional
- **SR-123–SR-154** — Operational / Error & Recovery
- **SR-155–SR-163** — System Constraints & Dependencies

Requirement identifiers are permanent and are not renumbered when later requirements are added or clarified.

## Traceability

The controlled chain is:

**User Need → Use Case → User Requirement → System Requirement → Design → Verification/Test**

Final STEP-003 reconciliation confirmed:

- **62/62** approved User Requirements have downstream System Requirement coverage.
- **163/163** System Requirements have reverse traceability or a controlled derived-requirement justification.
- **SR-080** and **SR-081** are intentionally derived requirements with explicit controlled rationale.
- **0** missing SR IDs.
- **0** duplicate SR IDs.
- **0** unapproved SRs.
- **0** unresolved requirement references.

## Key controlled decisions

- XML remains the baseline supported input format; CSV is deferred.
- Structured cross-source indexing/query/filtering is required without prescribing a database technology.
- Initial query/filter scope includes tag/type, value, supported combined criteria, occurrence count and source context; arbitrary cross-tag joins are not required.
- Excel export allows user-selected result fields and user-defined field ordering.
- Archive processing depth is user-configurable with a default of **three levels**, outermost archive = level 1.
- The performance acceptance baseline is **≤30 minutes** on **RTE-001** from supported archive/package load initiation through Discovery-result availability for a representative workload yielding at least **30,000 supported XML files** with archive nesting through three levels.
- Structured indexed information associated with a successfully saved working state is restorable after restart without reprocessing the original source data.
- User-triggered cancellation is required and is initiated by **Esc only** under RD-012.
- Retry is normal re-initiation of a supported operation from a valid current/restored state; no dedicated Retry control or automatic retry loop is required.
- Core processing is local and does not require cloud/SaaS services.
- Normal application operation must be possible without administrator privileges.
- Installer elevation remains a packaging/deployment decision.
- Original loaded source data must not be modified.
- The core discovery/extraction workflow must support declared-supported non-aircraft XML structures.

## Final review

STEP-003.9 reviewed the SRS for:

- completeness;
- traceability;
- ambiguity;
- atomicity;
- duplication;
- contradiction;
- implementation neutrality;
- verification coverage;
- unresolved questions and decisions.

All six recorded open questions are resolved.

The final review made controlled wording clarifications to **SR-107, SR-123, SR-124, SR-125, SR-130, SR-131, SR-136 and SR-138**. These changes tightened terminology and removed subjective wording without changing IDs, traceability, approved scope or product capability.

Potentially similar requirement pairs were reviewed and retained where they control distinct concerns; for example, extracted-source provenance vs indexed-source provenance, data persistence vs recovery behavior, and execution-environment support vs installable delivery.

## Boundary

STEP-003 defines **what the system shall do**.

Architecture, technology selection, concrete storage/database design, detailed component decomposition and final UI design remain STEP-004 concerns unless already constrained by an approved requirement.

## Outcome

STEP-003 is complete and baselined.

The next SDLC activity is **STEP-004 — Architecture & Solution Design**.
