# STEP-003 — System / Software Requirements Specification Baseline

**Status:** Complete  
**Baseline date:** 1 September 2026  
**Jira Epic:** SPR-11

## Purpose

Translate the approved STEP-002 user requirements into a controlled, implementation-neutral System / Software Requirements Specification suitable for architecture, implementation planning and later verification.

## Approved baseline

STEP-003 established 163 requirements at initial closure and currently controls **166 approved System / Software Requirements** after the STEP-002.13 post-baseline amendment:

- **SR-001–SR-027** — Functional
- **SR-028–SR-046** — Data & Information
- **SR-047–SR-103** — Interface
- **SR-104–SR-122** — Quality / Non-functional
- **SR-123–SR-154** — Operational / Error & Recovery
- **SR-155–SR-163** — System Constraints & Dependencies
- **SR-164** — Functional post-baseline amendment: Database Tag Name Override
- **SR-165** — Data post-baseline amendment: normalized resulting Database tag representation
- **SR-166** — Interface post-baseline amendment: modified-mapping visibility/locatability

Requirement identifiers are permanent and are not renumbered when later requirements are added or clarified.

## Traceability

The controlled chain is:

**User Need → Use Case → User Requirement → System Requirement → Design → Verification/Test**

Final STEP-003 reconciliation confirmed:

- **65/65** current approved User Requirements have downstream System Requirement coverage.
- **166/166** current System Requirements have reverse traceability or a controlled derived-requirement justification.
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
- The user may override the resulting Database Tag Name of selected discovered information before Database creation/update.
- Multiple selected source tags assigned the same resulting Database Tag Name are intentionally represented under that one resulting Database information type while retaining required value/content and source provenance; this does not introduce arbitrary cross-tag joins.
- Modified Database Tag Name mappings must be distinguishable and locatable in Discovery; exact visual treatment remains a STEP-004.7 design decision.
- For indexed Database occurrences, the retained information type/tag is the resulting Database Tag Name. Where an override applies, the configured Database Tag Name is the retained tag identity; the original source tag name/namespace need not remain in the resulting Database, while value/content and source provenance remain required.

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

### Post-baseline controlled amendment — STEP-002.13

During STEP-004.2 architecture review, Database Tag Name Override was identified as a genuine user-facing capability rather than an architecture-only implementation choice. The change was routed through requirements change control before architecture closure.

The current amended baseline adds UR-F-024, UR-F-025, UR-D-018, SR-164 through SR-166, and RD-013. The original STEP-003.9 closure remains historically valid for the 163-SR baseline approved earlier on 1 September 2026.

A subsequent STEP-004.2 literal full audit clarified UR-D-014 / SR-041 against RD-013 without adding scope: when a Database Tag Name Override applies, the indexed occurrence's resulting information type/tag is the configured Database Tag Name.

The final review made controlled wording clarifications to **SR-107, SR-123, SR-124, SR-125, SR-130, SR-131, SR-136 and SR-138**. These changes tightened terminology and removed subjective wording without changing IDs, traceability, approved scope or product capability.

Potentially similar requirement pairs were reviewed and retained where they control distinct concerns; for example, extracted-source provenance vs indexed-source provenance, data persistence vs recovery behavior, and execution-environment support vs installable delivery.

## Boundary

STEP-003 defines **what the system shall do**.

Architecture, technology selection, concrete storage/database design, detailed component decomposition and final UI design remain STEP-004 concerns unless already constrained by an approved requirement.

## Outcome

STEP-003 is complete and baselined.

The next SDLC activity is **STEP-004 — Architecture & Solution Design**.
