---
title: "Discovery Input"
layout: default
nav_order: 2
parent: "Walkthrough"
---

# Discovery — Input mode

**Input** is the place to understand the actual XML information CIA found and choose what should be considered for output. It is a selection and inspection surface, **not** the final Database.

![Discovery Input annotated screenshot](assets/images/discovery-input-annotated.png)

## Screenshot callouts

1. **Selection toolbar.** Search tags or mapped names; use **Select visible** and **Deselect visible** on the displayed subset. The toolbar reports how many selections are active. Where a saved selection profile exists, choose it and select **Load profile**.
2. **Tag and structural-context table.** Rows show source set, tag, mapped Database tag, occurrence count, source count, sample value and blacklist action. Expand a logical tag to see separately selectable detailed contexts. A selected parent may show a mixed-state checkbox because only some descendants are selected.
3. **Input Preview.** Displays the selected occurrence and whether its detailed field is currently selected, plus the source structure and example value. Use the **Previous**, **Next** and occurrence-number controls to inspect examples.
4. **General / Profiles / Blacklist.** The lower-right tabs provide workflow/list settings and stored selection or blacklist profile management. They do not replace the main Input/Output mode switch.
5. **Create Database.** Materializes the currently approved interpretation once selection and Output Plan are ready. For a first run, inspect Output mode before using it.

## Direct selection versus derived selection

A single named tag can occur at more than one structural location. For example, `title` may describe the book itself or a related series/work. **Selecting a tag name is not necessarily the same as selecting every structural meaning of that name.**

For precise output, expand the tag, inspect the detailed source contexts and select the field(s) that match the information you need. CIA can also derive related fields from structural selections, so the selected logical-tag count can differ from the detailed-field count. Use the summary and Output Plan to confirm the resulting projection.

## Reading the counts

The screenshot displays different units:

- **Logical tags:** distinct discovered names or logical entries.
- **Detailed fields:** structurally distinguished selectable contexts.
- **Occurrences:** instances of a selected field/context found across sources.
- **Sources:** distinct source files containing that field/context.
- **Output tables / OutputSets:** proposed materialized result groups, not source-file or occurrence counts.

Do not expect those numbers to match. Likewise, previewing one occurrence does not mean the entire data set contains only one row.

## Typical title/author selection

1. Search for `title` and inspect its available contexts.
2. Select the context for the book title rather than unrelated structural titles.
3. Find `name` and `role` under the appropriate author context; review their paths and sample values.
4. Inspect the Input Preview for the candidate fields.
5. Switch to [Discovery Output](discovery-output.md) to understand which rows and OutputSets the selection will produce.

The pictured fields and occurrence counts are **specific to the author's 30-file screenshot state**. They are illustrative, not required output values for another data set.
