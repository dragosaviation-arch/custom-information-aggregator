-- Turn chapter Markdown links into PDF chapter anchors in the assembled document.
local anchors = {
  ['index.md'] = 'custom-information-aggregator-user-manual',
  ['quick-start.md'] = 'quick-start-first-workbook',
  ['walkthrough.md'] = 'walkthrough-workspaces-and-actions',
  ['load.md'] = 'load-add-and-manage-sources',
  ['discovery-input.md'] = 'discovery-input-mode',
  ['discovery-output.md'] = 'discovery-output-mode',
  ['database.md'] = 'database-review-and-prepare-results',
  ['excel-export.md'] = 'excel-export-worksheets-workbooks-and-save-results',
  ['reference.md'] = 'feature-reference',
  ['sets-and-outputsets.md'] = 'source-sets-and-outputsets',
  ['profiles-and-blacklists.md'] = 'selection-profiles-and-blacklists',
  ['columns-and-excel.md'] = 'columns-metadata-and-workbook-configuration',
  ['settings-and-diagnostics.md'] = 'settings-and-log-diagnostics',
  ['troubleshooting.md'] = 'troubleshooting',
  ['known-limitations.md'] = 'known-limitations-and-scope',
  ['glossary.md'] = 'glossary'
}
function Link(el)
  local target = el.target:match('([^/]+%.md)$')
  if target and anchors[target] then
    el.target = '#' .. anchors[target]
  elseif el.target:find('installation/README.md', 1, true) then
    el.target = 'https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/docs/installation/README.md'
  elseif el.target:find('SUPPORT.md', 1, true) then
    el.target = 'https://github.com/dragosaviation-arch/custom-information-aggregator/blob/main/SUPPORT.md'
  end
  return el
end
