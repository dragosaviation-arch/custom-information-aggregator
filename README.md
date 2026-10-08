# Custom Information Aggregator

A Windows desktop application for turning large XML document sets into structured, reviewable and exportable information.

![Custom Information Aggregator — application overview](docs/assets/cia-overview.png)

## What is this?

Custom Information Aggregator started as a practical response to a real Maintenance, Repair and Overhaul (MRO) problem: useful information can be buried inside thousands of structured technical documents, while extracting it manually is slow, repetitive and difficult to scale.

An earlier working prototype proved that a large part of that work could be automated. This repository is a ground-up rebuild of that idea as a configurable desktop application, with the emphasis shifted from a one-off automation script to a maintainable product with a clear workflow, formal requirements, architecture, automated verification and release planning.

The application is designed to let a user load large XML document sets, discover the information structures inside them, choose what matters, build a reviewable database and export the resulting information to Excel without manually searching through the source files.

## Basic workflow

1. **Load** files, folders or archives into the application.
2. **Discover** the XML information available across the loaded sources.
3. **Select** the information to keep, rename it where useful, and exclude unwanted data.
4. **Build the Database** from the selected information.
5. **Review** the resulting data and configure how it should be presented.
6. **Prepare and export** the required fields to Excel.

The major v1 workflow and UI structure are now frozen. Current work focuses on release readiness, compatibility validation, documentation and final verification.

## Installation

See the [CIA v1 Installation Guide](docs/installation/README.md) for supported Windows versions, installation, upgrades, uninstall behavior and release-installation troubleshooting.

## Development status

**Current stage: Release Candidate preparation**

- ✅ Core Load → Discovery → Database → Extraction / Review / Export workflow
- ✅ v1 UI/UX convergence and structure freeze
- ✅ Windows installer, shell integration, upgrade and uninstall lifecycle
- 🟡 Windows compatibility validation
- 🟡 Release documentation and final verification

### Release roadmap

| Milestone | Status |
|---|---|
| **Full Alpha** | Complete |
| **UI/UX convergence / Beta closure** | Complete |
| **Release Candidate** | In preparation |
| **v1.0.0** | After RC acceptance |

## For technical reviewers

The project is being developed as a full software-engineering lifecycle rather than only as a coding exercise. The current implementation uses **C# / .NET 10 / WPF**, with a separate desktop UI and processing host communicating through typed named-pipe IPC. Data-processing workflows use bounded/streaming approaches, SQLite-backed intermediate results and native `.xlsx` generation without requiring Microsoft Excel.

The repository also contains controlled requirements, architecture and delivery-planning baselines, with implementation work developed through short-lived branches and pull requests and supported by an automated test suite.

### Project baselines

- [STEP-001 — Project Initiation](docs/initiation/STEP-001-project-initiation-baseline.md)
- [STEP-002 — User Requirements](docs/requirements/STEP-002-requirements-baseline.md)
- [STEP-003 — System / Software Requirements Specification](docs/srs/STEP-003-srs-baseline.md)
- [STEP-004 — Architecture & Solution Design](docs/architecture/STEP-004-architecture-baseline.md)
- [STEP-005 — Delivery Planning / Backlog Decomposition](docs/delivery/STEP-005-delivery-plan-baseline.md)

Detailed controlled requirements, architecture decisions and traceability are maintained in Confluence; Jira is used to track SDLC execution and sprint work.
