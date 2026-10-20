---
title: "Troubleshooting"
layout: default
nav_order: 5
---

# Troubleshooting

Start by checking the relevant workspace status and **Settings → Log & Diagnostics**. If a task was cancelled, failed or used a previous configuration, the screen may still display an older published Database.

| Symptom | What to check / do |
|---|---|
| **Discovery not available** after loading | Confirm intended sources are included and **Ready**, then click **Run Discovery**. |
| Some files are missing from the current output | Inspect **Load** inclusion, source status, active parent Set and any file/parse failures; then re-run Discovery/build as appropriate. |
| Desired tag appears but produces an unexpected result | Expand its **structural contexts** and check direct selection, derived selection and Input Preview. A tag name can occur at several paths. |
| Output Plan has more tables than expected | In Discovery Output, inspect enabled derived OutputSets and the default **Preserve relationships** mode. Independent repeated families may need separate tables. |
| Database is **out of date** or the new selection is not reflected | Inspect the status and select **Create Database** using the current Discovery configuration. Preview changes do not automatically replace a published Database. |
| A row looks empty in source-file mode | The mode can preserve a source row whose selected fields are absent. Check the source identity and selected fields before treating it as lost data. |
| Unexpectedly many columns | Inspect nested/repeated fields and whether **One row per source file** is enabled. Use **Preserve relationships** for a more compact family-oriented projection. |
| Large-column warning | Review your selection and structural repetitions. Above 500 output columns is a warning in source-file mode, not permission to drop data. |
| A Database value appears clipped | Scroll horizontally, adjust relevant column width where supported, or inspect the value without assuming that the underlying export is empty. |
| Workbook already exists | Use the explicit collision dialog: **Cancel**, **Overwrite**, or **Different name**. Check the filename carefully. |
| Export ends without the expected workbook | Inspect destination behavior (**Save to / Browse / Ask**), the result status and **Log & Diagnostics**. Confirm the output was included and the intended workbook name was selected. |

## If an operation is cancelled

Use CIA's supported cancellation controls and inspect the resulting status. Do not assume partial output was published. Where the application preserves a previous valid Database after a failed or cancelled rebuild, it remains the previous published state, not the pending one.

## Reporting a reproducible issue

Record the CIA version, workspace and operation, the user-visible message, source format, output mode and any relevant diagnostic result. Avoid sharing proprietary XML or identifying local paths when preparing a public issue.

For help resources, use [Settings and Diagnostics](settings-and-diagnostics.md) or [Support](https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/SUPPORT.md).
