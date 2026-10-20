#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p preview
pandoc --standalone --from=markdown+yaml_metadata_block --to=html5 \
  --metadata title='CIA User Manual - Offline Review Preview' \
  --lua-filter=scripts/pdf-links.lua --toc --toc-depth=2 \
  --embed-resources --css=assets/docs-preview.css \
  index.md quick-start.md walkthrough.md load.md discovery-input.md \
  discovery-output.md database.md excel-export.md reference.md \
  sets-and-outputsets.md profiles-and-blacklists.md \
  columns-and-excel.md settings-and-diagnostics.md troubleshooting.md \
  known-limitations.md glossary.md \
  -o preview/CIA-Manual-Offline-Preview.html
