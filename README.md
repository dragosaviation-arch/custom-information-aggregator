# custom-information-aggregator

A configurable application for discovering, indexing, querying, filtering, extracting, aggregating, and exporting structured data from large XML document sets.

## Project status

- STEP-000 — Complete
- STEP-001 — Complete
- STEP-002 — Complete
- STEP-003 — Complete
- **STEP-004 — Complete**
- Next control point: **STEP-005 — Delivery Planning / Backlog Decomposition**

STEP-003 establishes the current approved System / Software Requirements Specification through **SR-168**, with complete 67/67 User Requirement → System Requirement coverage and 168/168 reverse traceability / controlled derivation after the STEP-002.14 controlled amendment.

STEP-004 establishes the approved architecture baseline with **DRV-001–DRV-009**, **CMP-001–CMP-013**, **ADR-001–ADR-012**, complete **168/168 System Requirement → architecture primary ownership**, and no unresolved architecture-significant forks.

Project baselines:

- [STEP-001 — Project Initiation](docs/initiation/STEP-001-project-initiation-baseline.md)
- [STEP-002 — User Requirements](docs/requirements/STEP-002-requirements-baseline.md)
- [STEP-003 — System / Software Requirements Specification](docs/srs/STEP-003-srs-baseline.md)
- [STEP-004 — Architecture & Solution Design](docs/architecture/STEP-004-architecture-baseline.md)

Detailed controlled requirements, architecture decisions and traceability are maintained in Confluence; Jira tracks SDLC execution and sprint work.

## Git branch lifecycle

- `main` is the canonical integrated project baseline.
- At most one long-lived working branch is used for the current major SDLC phase.
- A major-phase branch is created from current `main`, merged through a pull request, and deleted after its baseline is merged.
- Changes discovered later for an already closed phase use a new short-lived amendment/cleanup branch from current `main`; closed phase branches are not revived as development branches.
- Full repository audits must enumerate every non-`main` branch, compare it with `main`, identify its PR/lifecycle state, and flag any stale or orphan branch.
