---
title: "Known Limitations"
layout: default
nav_order: 6
---

# Known limitations and scope

This section describes the **accepted Release Candidate scope** and clearly separates working functionality from deferred work.

## Optional source-file output: sparse columns and blank rows

**One row per source file** is available and supported as an opt-in output interpretation. Because repeated structures are represented with numbered columns and source identity is preserved, a data set may produce many columns that are empty for most files. Sources with none of the selected values may still produce identifiable blank rows.

A real-world TEM validation exposed blank source rows and excessive sparse column expansion; the owner accepted the default **Preserve relationships** / Root output as the useful v1 alternative. Further investigation is scheduled **after RC as SPR-227**. These observations do not establish missing stored values or license the manual to recommend discarding columns indiscriminately.

**Suggested user choice:** For a concise, family-oriented output, leave **One row per source file** unchecked. Enable the source-file mode when you specifically need source-aligned rows and can accommodate repeated numbered columns.

## Output preview is not the full Database

Output Preview is intentionally bounded. Depending on current schema knowledge and projection mode, it may show a sampled/local view or a paged view (10 data columns per page for source-file mode). **Database** is the authoritative full materialization stage.

## Deferred / unavailable functionality

- **Working State / Recovery** is quarantined from the accepted v1 interface and is not an available workflow.
- **Decomposer** is not available in the accepted user interface.
- Historical repeated-data layout options such as **Align repeated groups by position**, **Structural rows**, **All combinations**, and **Number repeated values into columns** are **not presented as current v1 user choices**.
- CIA does **not** claim to automatically join independently derived OutputSets merely because field names are the same.

## Boundaries of this tutorial

The examples are drawn from an author's local 30-file Goodbooks selection. Their screenshots demonstrate the UI, not a certified result for every possible collection of Goodbooks XML files. The documentation does not distribute the 30 sample files or claim that the final installer has been released.

For installation or compatibility statements, consult the separate [Installation Guide](https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/docs/installation/README.md), rather than treating these workflow screenshots as an installation test.
