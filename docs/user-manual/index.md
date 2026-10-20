---
title: "CIA User Manual"
layout: default
nav_order: 1
---

# Custom Information Aggregator — User Manual

**Release Candidate documentation draft · 20 October 2026**

CIA is a Windows desktop application that helps you inspect structured XML sources, choose the information you need, build a reviewable database, and export selected results to Excel workbooks.

This manual explains the **actual user interface**, using screenshots from a 30-file selection of Goodbooks XML files. You do not need to understand XML internals or software development to follow the main workflow.

> **Draft status:** This documentation is for review against the physically accepted CIA baseline (`6d16aabe74a5a8000dd46c60548e818ced92fde0`). It is not a claim that the final installer, website or public release is available. Screenshot callouts are documentation overlays, not controls inside CIA.

## Find what you need

| Goal | Read |
|---|---|
| Complete your first export | [Quick Start](quick-start.md) |
| Understand every main workspace | [Walkthrough](walkthrough.md) |
| Learn about source and output sets | [Sets and OutputSets](sets-and-outputsets.md) |
| Save selection and blacklist profiles | [Profiles and Blacklists](profiles-and-blacklists.md) |
| Control Database columns and Excel routing | [Columns and Excel Configuration](columns-and-excel.md) |
| Configure CIA and inspect activity | [Settings and Diagnostics](settings-and-diagnostics.md) |
| Solve a problem | [Troubleshooting](troubleshooting.md) |
| Check feature boundaries | [Known Limitations](known-limitations.md) |

## The workflow at a glance

**Load → Discovery (Input → Output) → Create Database → Review → Export to Excel**

1. **Load** XML files, folders or supported archives into a source set.
2. **Discovery / Input** inspects available tags and their structural contexts; you select the values you want.
3. **Discovery / Output** previews the proposed rows and OutputSets before building a Database.
4. **Database** materializes the approved output so you can review rows, columns and inclusion.
5. **Excel** routes included outputs into worksheets and workbooks, then saves the export.

The application groups related records according to their source structure by default. An optional **One row per source file** setting offers a different, wider projection for supported use cases.

## Related documentation

- [Installation Guide](https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/docs/installation/README.md) — installation, upgrades and uninstall instructions.
- [Release Notes / What's New](https://github.com/dragosaviation-arch/custom-information-aggregator/releases)
- [Support](https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/SUPPORT.md)

## Documentation conventions

**Button labels** and **setting names** match the visible CIA controls. Numbered circles in illustrations correspond to the numbered explanations directly below. Source paths have been anonymized in the manual's screenshots.
