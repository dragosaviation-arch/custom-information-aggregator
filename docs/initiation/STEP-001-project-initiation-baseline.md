# STEP-001 — Project Initiation & Legacy System Baseline

**Status:** Complete

## Purpose

Establish the business need, project scope, stakeholders, constraints and legacy-system baseline for the Custom Information Aggregator.

## Business Need

The original workflow required users to search multiple technical sources manually and combine related information. This was repetitive, slow and difficult to scale.

The legacy prototype demonstrated the value of automation: work that had reached roughly 10% completion after significant manual effort could be processed to completion automatically overnight.

## Project Objective

Replace the one-off legacy prototype with a maintainable Windows application that can:

- process supported XML source data and nested archives;
- discover and select information for extraction;
- aggregate, index, query and filter discovered information across supported sources;
- save reusable configurations and working states;
- preview and export structured results;
- provide useful progress, error and logging information.

## Scope

The application runs locally on Windows and processes supported source data without modifying the originals.

The product is intended to be reusable beyond one specific aircraft manual set or extraction task.

Cloud/SaaS operation and aircraft-specific business processes are outside the initial scope.

## Stakeholders

Primary users are Planning personnel, particularly Document Control and Quotations. Project Managers are potential users.

## Legacy Baseline

The legacy application proved the core extraction concept but had limited usability, documentation, logging, progress feedback and maintainability.

Its limitations form the baseline for the redesigned product.

## Outcome

STEP-001 established the project baseline and provided the inputs required for formal requirements engineering in STEP-002.