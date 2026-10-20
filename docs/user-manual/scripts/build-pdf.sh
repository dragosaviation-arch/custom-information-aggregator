#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p pdf
pandoc --defaults=pandoc.yaml \
  --lua-filter=scripts/pdf-links.lua \
  --metadata title='Custom Information Aggregator - User Manual' \
  --metadata subtitle='Release Candidate Documentation Draft - 20 October 2026' \
  index.md quick-start.md walkthrough.md load.md discovery-input.md \
  discovery-output.md database.md excel-export.md reference.md \
  sets-and-outputsets.md profiles-and-blacklists.md \
  columns-and-excel.md settings-and-diagnostics.md troubleshooting.md \
  known-limitations.md glossary.md \
  -o pdf/CIA-User-Manual-DRAFT.pdf
