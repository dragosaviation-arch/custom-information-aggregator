---
title: "Columns and Excel Configuration"
layout: default
nav_order: 3
parent: "Feature Reference"
---

# Columns, metadata and workbook configuration

This page collects the Database and Excel controls that shape what a saved workbook looks like.

## Database Columns panel

![Annotated Columns dialog](assets/images/database-columns-annotated.png)

1. **Open and reset.** The **Columns** control opens the column list; **Reset layout** restores the supported layout defaults.
2. **Name and inclusion.** The name field controls the presented column label where editing is allowed. The leading checkbox determines whether that field is included. Generated columns may have restrictions.
3. **SourceId and order.** Select the companion source-id field where relevant; use up/down arrows to organize field order.

The Database's included columns and presentation order are the expected basis for the Excel **value-field** selection and order. This is separate from workbook naming, metadata fields and post-export settings.

## Excel Output Configuration

![Workbook and worksheet controls](assets/images/database-annotated.png)

From the lower-left **Excel Output Configuration** region, you can:

- include or exclude the current output from the next export;
- assign the output to a CIA-managed workbook definition;
- add or rename a workbook definition using the controls provided;
- name and order its worksheet when the controls are enabled.

CIA prevents incompatible worksheet names by applying Excel naming and uniqueness rules. Worksheet boundaries follow OutputSets, not arbitrary shared field names.

## Repeated data and wide outputs

A relationship-preserving output may put different repeated record families into different worksheets. The optional source-file mode instead preserves one source row while repeated structures expand into numbered column groups. This can result in large, sparse workbooks.

CIA warns when the planned output crosses **500 columns** in source-file mode, counting relevant generated and metadata fields. The warning gives you an opportunity to reconsider selection; it does not authorize silently dropping fields. Check [Known Limitations](known-limitations.md) before using source-file mode as a compact reporting format.

## When to rebuild

If you change source inclusion, detailed-field selection, OutputSet enablement or output interpretation, inspect the current/updated status and build a new Database before exporting those changes. Editing the name or order of included Database columns affects presentation and subsequent export configuration but does not edit the underlying XML.
