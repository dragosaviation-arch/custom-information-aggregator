---
title: "Source Sets and OutputSets"
layout: default
nav_order: 1
parent: "Feature Reference"
---

# Source Sets and OutputSets

CIA uses two different concepts that can otherwise be easy to confuse.

| Term | What it means | Where you work with it |
|---|---|---|
| **Source Set (parent Set)** | A user-managed group of input sources. It determines which files belong together for Discovery. | Load / Load Settings |
| **OutputSet (derived Set)** | A proposed and then materialized table produced from a parent Source Set and the selected structural interpretation. | Discovery Output, Database, Excel |

## Example from the manual

All 30 selected Goodbooks XML files belong to **Set 1** in the Load screenshot. The Discovery Output screenshot shows that CIA may propose **more than one OutputSet** for this single parent, such as a `series_work` table and an `author` table. Those are not additional input folders or separate copies of the XML files.

![Discovery Output shows proposed derived tables](assets/images/discovery-output-annotated.png)

## How to choose the interpretation

- **Preserve relationships:** Build structurally meaningful derived tables from the selected fields. One parent Source Set may produce multiple OutputSets.
- **One row per source file:** Combine the parent's selected information into one source-associated table, potentially with numbered repeated-value columns. It is optional and off by default.

These modes describe different output projections. They do not merge unrelated parent Source Sets with one another. Toggling a mode changes the **proposed next build**, not the already published Database instantaneously.

## How output reaches Excel

**Parent Source Set → enabled OutputSet(s) → Database output(s) → one worksheet per OutputSet.**

If two different OutputSets each contain a field called `name`, the shared label alone does not create an automatic join between them. The user can correlate them later using whatever keys and provenance the outputs make available.

## Provenance and SourceId

CIA maintains source identity and lineage so that outputs can be traced back to their originating file/source context. The optional **SourceId** companion column is exposed in the Database Columns panel. Identical visible names do not by themselves establish identical structural meaning.
