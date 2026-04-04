namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Client-side JavaScript for JSON-driven HTML comparison pages.</summary>
internal static class ComparisonJsRenderer
{
    internal const string Script = """
                                       // --- Decompression ---
                                       async function inflate(b64) {
                                         var bin = atob(b64);
                                         var bytes = new Uint8Array(bin.length);
                                         for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
                                         var ds = new DecompressionStream('deflate-raw');
                                         var writer = ds.writable.getWriter();
                                         writer.write(bytes);
                                         writer.close();
                                         var reader = ds.readable.getReader();
                                         var chunks = [];
                                         while (true) {
                                           var result = await reader.read();
                                           if (result.done) break;
                                           chunks.push(result.value);
                                         }
                                         var total = chunks.reduce(function(s, c) { return s + c.length; }, 0);
                                         var merged = new Uint8Array(total);
                                         var off = 0;
                                         for (var c of chunks) { merged.set(c, off); off += c.length; }
                                         return new TextDecoder().decode(merged);
                                       }

                                       // --- Global state ---
                                       var DATA = null;
                                       var _expandCancel = false;
                                       var _pendingBuildSort = null;

                                       // --- Initialization ---
                                       document.addEventListener('DOMContentLoaded', async function() {
                                         var el = document.getElementById('record-data');
                                         var compressed = el.getAttribute('data-z');
                                         var json = await inflate(compressed);
                                         DATA = JSON.parse(json);
                                         render();
                                         document.getElementById('loading').style.display = 'none';
                                       });

                                       // --- Escaping ---
                                       function esc(s) {
                                         if (!s) return '';
                                         return s.replace(/&/g,'&amp;').replace(/</g,'&lt;').replace(/>/g,'&gt;').replace(/"/g,'&quot;');
                                       }

                                       // --- Main render ---
                                       function render() {
                                         var container = document.getElementById('tables-container');
                                         var dumps = DATA.dumps;
                                         var records = DATA.records;
                                         var groups = DATA.groups || {};
                                         var gridCoords = DATA.gridCoords || {};
                                         var sparseDumps = new Set(DATA.sparseDumps || []);
                                         var hasCoords = Object.keys(gridCoords).length > 0;

                                         // Check if we need grouping
                                         var hasGroups = Object.keys(groups).length > 0;

                                         if (hasGroups) {
                                           // Group records by group key
                                           var grouped = {};
                                           for (var formId in records) {
                                             var groupKey = groups[formId] || '(Ungrouped)';
                                             if (!grouped[groupKey]) grouped[groupKey] = {};
                                             grouped[groupKey][formId] = records[formId];
                                           }
                                           // Sort groups: exterior first, interior last
                                           var groupKeys = Object.keys(grouped).sort(function(a, b) {
                                             if (a === 'Interior Cells' && b !== 'Interior Cells') return 1;
                                             if (b === 'Interior Cells' && a !== 'Interior Cells') return -1;
                                             return a < b ? -1 : a > b ? 1 : 0;
                                           });

                                           // Table of contents
                                           var tocHtml = '<div class="toc"><strong>Sections:</strong><ul>';
                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/ /g, '-').replace(/\(/g, '_').replace(/\)/g, '_');
                                             var cnt = Object.keys(grouped[gk]).length;
                                             tocHtml += '<li><a href="#' + gid + '" onclick="expandGroup(\'' + gid + '\')">'
                                               + esc(gk) + ' (' + cnt.toLocaleString() + ')</a></li>';
                                           }
                                           tocHtml += '</ul></div>';
                                           container.innerHTML = tocHtml;

                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/ /g, '-').replace(/\(/g, '_').replace(/\)/g, '_');
                                             var sectionDiv = document.createElement('div');
                                             sectionDiv.className = 'group-section';
                                             sectionDiv.id = gid;

                                             var cnt = Object.keys(grouped[gk]).length;
                                             var headerEl = document.createElement('h2');
                                             headerEl.className = 'group-header';
                                             headerEl.textContent = '\u25B6 ' + gk + ' (' + cnt.toLocaleString() + ')';
                                             headerEl.onclick = function() { toggleGroup(this); };
                                             sectionDiv.appendChild(headerEl);

                                             var contentDiv = document.createElement('div');
                                             contentDiv.className = 'group-content';
                                             contentDiv.style.display = 'none';
                                             var tbl = buildTable(grouped[gk], dumps, sparseDumps, hasCoords, gridCoords);
                                             contentDiv.appendChild(tbl);
                                             sectionDiv.appendChild(contentDiv);
                                             container.appendChild(sectionDiv);
                                           }
                                         } else {
                                           var tbl = buildTable(records, dumps, sparseDumps, hasCoords, gridCoords);
                                           container.appendChild(tbl);
                                         }
                                       }

                                       // --- Table builder ---
                                       function buildTable(records, dumps, sparseDumps, hasCoords, gridCoords) {
                                         var table = document.createElement('table');
                                         // Header
                                         var thead = document.createElement('thead');
                                         var headerRow = document.createElement('tr');
                                         headerRow.innerHTML =
                                           '<th class="col-editor sortable" onclick="sortBy(this,\'editor\')">Editor ID '
                                           + '<span class="sort-indicator"></span></th>'
                                           + '<th class="col-name sortable" onclick="sortBy(this,\'name\')">Name '
                                           + '<span class="sort-indicator"></span></th>'
                                           + (hasCoords
                                             ? '<th class="col-coords sortable" onclick="sortBy(this,\'coords\')">Coords '
                                               + '<span class="sort-indicator"></span></th>'
                                             : '')
                                           + '<th class="col-formid sortable" onclick="sortBy(this,\'formid\')">Form ID '
                                           + '<span class="sort-indicator"></span></th>';
                                         for (var i = 0; i < dumps.length; i++) {
                                           headerRow.innerHTML +=
                                             '<th class="build-header build-col-' + i + '" data-dump-idx="' + i
                                             + '" onclick="filterByBuild(this)">'
                                             + esc(dumps[i].shortName) + '<br><span class="dump-date">'
                                             + (dumps[i].isBase ? '(base)' : dumps[i].date.substring(0, 10))
                                             + '</span><br><span class="build-filter-label"></span></th>';
                                         }
                                         thead.appendChild(headerRow);
                                         table.appendChild(thead);

                                         // Body
                                         var tbody = document.createElement('tbody');

                                         // Sort records by editorId
                                         var formIds = Object.keys(records).sort(function(a, b) {
                                           var ea = (records[a].editorId || '').toLowerCase();
                                           var eb = (records[b].editorId || '').toLowerCase();
                                           return ea < eb ? -1 : ea > eb ? 1 : 0;
                                         });

                                         for (var fi = 0; fi < formIds.length; fi++) {
                                           var formId = formIds[fi];
                                           var rec = records[formId];
                                           var editorId = rec.editorId || '';
                                           var displayName = rec.displayName || '';
                                           var present = new Set(rec.present || []);

                                           // Name change display
                                           var editorIdDisplay = esc(editorId);
                                           if (rec.editorIdHistory && rec.editorIdHistory.length > 1) {
                                             editorIdDisplay = rec.editorIdHistory.map(esc)
                                               .join('<br><span class="name-change">\u21B3 </span>');
                                           }
                                           var nameDisplay = esc(displayName);
                                           if (rec.nameHistory && rec.nameHistory.length > 1) {
                                             nameDisplay = rec.nameHistory.map(esc)
                                               .join('<br><span class="name-change">\u21B3 </span>');
                                           }

                                           var coordsDisplay = '';
                                           if (hasCoords && gridCoords[formId]) {
                                             coordsDisplay = '(' + gridCoords[formId][0] + ', ' + gridCoords[formId][1] + ')';
                                           }

                                           var searchData = (formId + ' ' + editorId + ' ' + displayName + ' ' + coordsDisplay)
                                             .toLowerCase();

                                           // Compute badges -- resolve snapshots for each dump
                                           var resolvedSnapshots = resolveSnapshots(rec, dumps.length);

                                           // Summary row
                                           var summaryRow = document.createElement('tr');
                                           summaryRow.className = 'summary-row';
                                           summaryRow.setAttribute('data-search', searchData);
                                           summaryRow.setAttribute('data-editor', editorId);
                                           summaryRow.setAttribute('data-name', displayName);
                                           summaryRow.setAttribute('data-coords', coordsDisplay);
                                           summaryRow.setAttribute('data-formid', formId);
                                           if (hasCoords && gridCoords[formId]) {
                                             summaryRow.setAttribute('data-cx', gridCoords[formId][0]);
                                             summaryRow.setAttribute('data-cy', gridCoords[formId][1]);
                                           }
                                           summaryRow.onclick = function() { toggleDetail(this); };

                                           var rowHtml =
                                             '<td class="col-editor">' + editorIdDisplay + '</td>'
                                             + '<td class="col-name">' + nameDisplay + '</td>'
                                             + (hasCoords ? '<td class="col-coords">' + esc(coordsDisplay) + '</td>' : '')
                                             + '<td class="col-formid formid">' + formId + '</td>';

                                           // Status badges per dump
                                           var previousSnapshotKey = null;
                                           for (var di = 0; di < dumps.length; di++) {
                                             var colClass = 'build-col-' + di;
                                             if (present.has(di)) {
                                               var snapshotKey = resolvedSnapshots[di] !== null
                                                 ? resolvedSnapshots[di].key : null;
                                               if (previousSnapshotKey === null) {
                                                 if (dumps[di].isBase || di === 0)
                                                   rowHtml += '<td class="' + colClass
                                                     + '"><span class="badge badge-base">BASE</span></td>';
                                                 else
                                                   rowHtml += '<td class="' + colClass
                                                     + '"><span class="badge badge-new">NEW</span></td>';
                                               } else if (snapshotKey === previousSnapshotKey) {
                                                 rowHtml += '<td class="' + colClass
                                                   + '"><span class="badge badge-same">SAME</span></td>';
                                               } else {
                                                 rowHtml += '<td class="' + colClass
                                                   + '"><span class="badge badge-changed">CHANGED</span></td>';
                                               }
                                               previousSnapshotKey = snapshotKey;
                                             } else if (sparseDumps.has(di)) {
                                               rowHtml += '<td class="' + colClass
                                                 + '"><span class="badge badge-sparse">SPARSE</span></td>';
                                             } else {
                                               if (previousSnapshotKey !== null) {
                                                 var badgeText = dumps[di].isDmp ? 'NOT PRESENT' : 'REMOVED';
                                                 rowHtml += '<td class="' + colClass
                                                   + '"><span class="badge badge-removed">' + badgeText + '</span></td>';
                                               } else {
                                                 rowHtml += '<td class="' + colClass
                                                   + '"><span class="badge badge-absent">&mdash;</span></td>';
                                               }
                                             }
                                           }
                                           summaryRow.innerHTML = rowHtml;
                                           tbody.appendChild(summaryRow);

                                           // Detail row (hidden, rendered on demand)
                                           var detailRow = document.createElement('tr');
                                           detailRow.className = 'detail-row';
                                           detailRow.style.display = 'none';
                                           detailRow.setAttribute('data-formid', formId);
                                           var detailHtml =
                                             '<td class="col-editor"></td><td class="col-name"></td>'
                                             + (hasCoords ? '<td class="col-coords"></td>' : '')
                                             + '<td class="col-formid"></td>';
                                           for (var di = 0; di < dumps.length; di++) {
                                             detailHtml += '<td class="build-col-' + di + '"></td>';
                                           }
                                           detailRow.innerHTML = detailHtml;
                                           tbody.appendChild(detailRow);
                                         }

                                         table.appendChild(tbody);
                                         return table;
                                       }

                                       // --- Snapshot resolution ---
                                       // Returns array[dumpCount] where each entry is {key, report} or null.
                                       // key = the snapshot dict key that provides this dump's data (for SAME detection).
                                       function resolveSnapshots(rec, dumpCount) {
                                         var result = new Array(dumpCount);
                                         var present = new Set(rec.present || []);
                                         var currentKey = null;
                                         var currentReport = null;
                                         for (var di = 0; di < dumpCount; di++) {
                                           if (rec.snapshots.hasOwnProperty(di.toString())) {
                                             currentKey = di.toString();
                                             currentReport = rec.snapshots[currentKey];
                                           }
                                           if (present.has(di) && currentReport !== null) {
                                             result[di] = { key: currentKey, report: currentReport };
                                           } else {
                                             result[di] = null;
                                           }
                                         }
                                         return result;
                                       }

                                       // --- Detail rendering: unified row template across all columns ---
                                       function renderDetail(detailRow) {
                                         if (detailRow.dataset.rendered) return;
                                         detailRow.dataset.rendered = '1';

                                         var formId = detailRow.getAttribute('data-formid');
                                         var rec = DATA.records[formId];
                                         if (!rec) return;

                                         var dumps = DATA.dumps;
                                         var sparseDumps = new Set(DATA.sparseDumps || []);
                                         var resolvedSnapshots = resolveSnapshots(rec, dumps.length);
                                         var present = new Set(rec.present || []);
                                         var hasCoords = Object.keys(DATA.gridCoords || {}).length > 0;
                                         var fixedCols = hasCoords ? 4 : 3;
                                         var cells = detailRow.querySelectorAll('td');

                                         // Collect all reports across columns
                                         var reports = [];
                                         for (var di = 0; di < dumps.length; di++) {
                                           if (present.has(di) && resolvedSnapshots[di] !== null)
                                             reports.push(resolvedSnapshots[di].report);
                                           else
                                             reports.push(null);
                                         }

                                         // Build unified row template: ordered list of {type, section, key}
                                         // that covers ALL sections and fields across ALL columns
                                         var template = buildUnifiedTemplate(reports);

                                         // Compute max key width per section across all columns
                                         var sectionKeyWidths = {};
                                         for (var ti = 0; ti < template.length; ti++) {
                                           var slot = template[ti];
                                           if (slot.type === 'field' && slot.key) {
                                             var w = sectionKeyWidths[slot.section] || 0;
                                             if (slot.key.length > w) sectionKeyWidths[slot.section] = slot.key.length;
                                           }
                                         }

                                         // Render each column against the unified template
                                         var previousReport = null;
                                         for (var di = 0; di < dumps.length; di++) {
                                           var td = cells[fixedCols + di];
                                           if (!td) continue;

                                           var report = reports[di];
                                           if (report) {
                                             var isBase = (dumps[di].isBase || di === 0) && previousReport === null;
                                             var html = renderAligned(report, previousReport, isBase,
                                               template, sectionKeyWidths);
                                             td.innerHTML = '<div class="record-detail">' + html + '</div>';
                                             previousReport = report;
                                           } else if (sparseDumps.has(di)) {
                                             td.innerHTML = '<div class="record-detail">'
                                               + '<span class="field-sparse">(sparse dump \u2014 type not loaded)</span></div>';
                                           } else if (previousReport !== null) {
                                             var msg = dumps[di].isDmp ? '(not present in this dump)' : '(removed)';
                                             td.innerHTML = '<div class="record-detail">'
                                               + '<span class="field-removed">' + msg + '</span></div>';
                                           }
                                         }
                                       }

                                       // Build a unified template of row slots from all reports
                                       function buildUnifiedTemplate(reports) {
                                         // Collect ordered section names and per-section ordered field keys
                                         var sectionOrder = [];
                                         var sectionSeen = new Set();
                                         var sectionFields = {}; // section -> ordered unique keys

                                         for (var ri = 0; ri < reports.length; ri++) {
                                           var report = reports[ri];
                                           if (!report || !report.sections) continue;
                                           for (var si = 0; si < report.sections.length; si++) {
                                             var sec = report.sections[si];
                                             if (!sectionSeen.has(sec.name)) {
                                               sectionSeen.add(sec.name);
                                               sectionOrder.push(sec.name);
                                               sectionFields[sec.name] = [];
                                             }
                                             var existing = new Set(sectionFields[sec.name]);
                                             for (var fi = 0; fi < sec.fields.length; fi++) {
                                               if (!existing.has(sec.fields[fi].key)) {
                                                 existing.add(sec.fields[fi].key);
                                                 sectionFields[sec.name].push(sec.fields[fi].key);
                                               }
                                             }
                                           }
                                         }

                                         var template = [];
                                         for (var si = 0; si < sectionOrder.length; si++) {
                                           var secName = sectionOrder[si];
                                           template.push({type: 'section', section: secName});
                                           var keys = sectionFields[secName];
                                           for (var fi = 0; fi < keys.length; fi++) {
                                             template.push({type: 'field', section: secName, key: keys[fi]});
                                           }
                                         }
                                         return template;
                                       }

                                       // Render a single column against the unified template
                                       function renderAligned(current, previous, isBase, template, keyWidths) {
                                         if (!current || !current.sections) return '';

                                         // Index current report: section -> {key -> field}
                                         var curIndex = {};
                                         var curSections = new Set();
                                         for (var si = 0; si < current.sections.length; si++) {
                                           var sec = current.sections[si];
                                           curSections.add(sec.name);
                                           curIndex[sec.name] = {};
                                           for (var fi = 0; fi < sec.fields.length; fi++)
                                             curIndex[sec.name][sec.fields[fi].key] = sec.fields[fi];
                                         }

                                         // Index previous report
                                         var prevIndex = {};
                                         var prevSections = new Set();
                                         if (previous && previous.sections) {
                                           for (var si = 0; si < previous.sections.length; si++) {
                                             var ps = previous.sections[si];
                                             prevSections.add(ps.name);
                                             prevIndex[ps.name] = {};
                                             for (var fi = 0; fi < ps.fields.length; fi++)
                                               prevIndex[ps.name][ps.fields[fi].key] = ps.fields[fi];
                                           }
                                         }

                                         var lines = [];
                                         var isFirstSection = true;
                                         for (var ti = 0; ti < template.length; ti++) {
                                           var slot = template[ti];

                                           if (slot.type === 'section') {
                                             // End previous section with blank line (except before first section)
                                             if (!isFirstSection) lines.push('');
                                             var hasSec = curSections.has(slot.section);
                                             var hadSec = prevSections.has(slot.section);
                                             var hr = isFirstSection ? '' : '<hr>';
                                             if (hasSec) {
                                               if (previous !== null && !hadSec)
                                                 lines.push(hr + '<span class="section-header field-new">'
                                                   + esc(slot.section) + '</span>');
                                               else
                                                 lines.push(hr + '<span class="section-header">'
                                                   + esc(slot.section) + '</span>');
                                             } else if (hadSec) {
                                               lines.push(hr + '<span class="section-header field-removed">'
                                                 + esc(slot.section) + '</span>');
                                             } else {
                                               lines.push(hr + '<span class="section-header" style="visibility:hidden">'
                                                 + esc(slot.section) + '</span>');
                                             }
                                             isFirstSection = false;
                                             continue;
                                           }

                                           // Field slot
                                           var maxKeyLen = keyWidths[slot.section] || 0;
                                           var paddedKey = esc(slot.key);
                                           while (paddedKey.length < maxKeyLen) paddedKey += ' ';

                                           var curField = curIndex[slot.section]
                                             ? curIndex[slot.section][slot.key] : null;
                                           var prevField = prevIndex[slot.section]
                                             ? prevIndex[slot.section][slot.key] : null;

                                           if (!curField && !prevField) {
                                             // Field absent in both this column and previous — alignment placeholder
                                             lines.push('<span style="color:#555">  ' + paddedKey
                                               + ': \u2014</span>');
                                           } else if (!curField && prevField) {
                                             // Field was removed in this column
                                             lines.push('<span class="field-removed">  ' + paddedKey + ': '
                                               + formatValue(prevField.value) + '</span>');
                                           } else if (curField && previous === null) {
                                             // First column (baseline)
                                             var displayVal = formatValue(curField.value);
                                             if (isBase)
                                               lines.push('  ' + paddedKey + ': ' + displayVal);
                                             else
                                               lines.push('<span class="field-new">  ' + paddedKey
                                                 + ': ' + displayVal + '</span>');
                                           } else if (curField && !prevField) {
                                             // New field (not in previous)
                                             lines.push('<span class="field-new">  ' + paddedKey + ': '
                                               + formatValue(curField.value) + '</span>');
                                           } else if (valuesEqual(curField.value, prevField.value)) {
                                             lines.push('  ' + paddedKey + ': '
                                               + formatValue(curField.value));
                                           } else if (curField.value && curField.value.type === 'list'
                                                      && prevField.value && prevField.value.type === 'list') {
                                             // Item-level list diff
                                             lines.push('  ' + paddedKey + ':'
                                               + formatListDiff(curField.value, prevField.value, '    '));
                                           } else {
                                             var prevDisplay = formatValue(prevField.value);
                                             var curDisplay = formatValue(curField.value);
                                             lines.push('<span class="field-changed">  ' + paddedKey + ': '
                                               + prevDisplay + '<span class="field-arrow"> \u2192 </span>'
                                               + curDisplay + '</span>');
                                           }
                                         }

                                         return lines.join('\n');
                                       }

                                       // --- Value formatting ---
                                       function formatValue(val) {
                                         if (!val) return '';
                                         switch (val.type) {
                                           case 'int':
                                           case 'float':
                                           case 'bool':
                                             return esc(val.display || String(val.raw));
                                           case 'string':
                                             return esc(val.raw || '');
                                           case 'formId':
                                             return esc(val.display || val.raw || '');
                                           case 'list':
                                             return formatList(val);
                                           case 'composite':
                                             return formatComposite(val);
                                           default:
                                             return esc(val.display || '');
                                         }
                                       }

                                       function formatComposite(val) {
                                         if (!val || !val.fields) return esc(val ? (val.display || '') : '');
                                         if (val.fields.length <= 2) {
                                           // Short composites: inline key=value pairs
                                           var parts = [];
                                           for (var i = 0; i < val.fields.length; i++)
                                             parts.push(esc(val.fields[i].key) + ': ' + formatValue(val.fields[i].value));
                                           return parts.join('  |  ');
                                         }
                                         // Multi-field composites: one field per line, aligned
                                         var maxKey = 0;
                                         for (var i = 0; i < val.fields.length; i++)
                                           maxKey = Math.max(maxKey, val.fields[i].key.length);
                                         var lines = [];
                                         for (var i = 0; i < val.fields.length; i++) {
                                           var k = val.fields[i].key;
                                           while (k.length < maxKey) k += ' ';
                                           lines.push('  ' + esc(k) + ': ' + formatValue(val.fields[i].value));
                                         }
                                         return '\n' + lines.join('\n');
                                       }

                                       function formatListItem(item) {
                                         if (item.type === 'composite' && item.fields) {
                                           return formatComposite(item);
                                         }
                                         return formatValue(item);
                                       }

                                       // Format a list value: short lists inline, long lists one-per-line
                                       function formatList(val) {
                                         if (!val || !val.items || val.items.length === 0)
                                           return esc(val ? (val.display || '(empty)') : '(empty)');
                                         var parts = [];
                                         for (var i = 0; i < val.items.length; i++)
                                           parts.push(formatListItem(val.items[i]));
                                         // Single-item lists stay inline; multi-item always one-per-line for comparison
                                         if (parts.length === 1) return parts[0];
                                         // Multi-line: one item per line, indented
                                         return '\n' + parts.map(function(p) { return '    ' + p; }).join('\n');
                                       }

                                       // Build a stable key for a list item, preferring raw values over display strings
                                       function listItemKey(item) {
                                         if (!item) return '';
                                         if (item.type === 'formId') return item.raw || item.display || '';
                                         if (item.type === 'composite' && item.fields) {
                                           // Use raw values from composite fields for stable matching
                                           var parts = [];
                                           for (var i = 0; i < item.fields.length; i++) {
                                             var v = item.fields[i].value;
                                             parts.push(item.fields[i].key + '=' + (v ? (v.raw !== undefined ? String(v.raw) : (v.display || '')) : ''));
                                           }
                                           return parts.join('|');
                                         }
                                         return item.raw !== undefined ? String(item.raw) : (item.display || '');
                                       }

                                       // Format a list value with item-level diff against a previous list
                                       function formatListDiff(curVal, prevVal, indent) {
                                         var curItems = (curVal && curVal.items) ? curVal.items : [];
                                         var prevItems = (prevVal && prevVal.items) ? prevVal.items : [];
                                         // Build stable keys for matching (using raw values, not display)
                                         var prevKeys = {};
                                         for (var i = 0; i < prevItems.length; i++) {
                                           prevKeys[listItemKey(prevItems[i])] = true;
                                         }
                                         var curKeys = {};
                                         for (var i = 0; i < curItems.length; i++) {
                                           curKeys[listItemKey(curItems[i])] = true;
                                         }
                                         var lines = [];
                                         // Current items: mark new ones
                                         for (var i = 0; i < curItems.length; i++) {
                                           var text = formatListItem(curItems[i]);
                                           var key = listItemKey(curItems[i]);
                                           if (!prevKeys[key])
                                             lines.push('<span class="field-new">' + indent + text + '</span>');
                                           else
                                             lines.push(indent + text);
                                         }
                                         // Removed items
                                         for (var i = 0; i < prevItems.length; i++) {
                                           var text = formatListItem(prevItems[i]);
                                           var key = listItemKey(prevItems[i]);
                                           if (!curKeys[key])
                                             lines.push('<span class="field-removed">' + indent + text + '</span>');
                                         }
                                         return '\n' + lines.join('\n');
                                       }

                                       // --- Deep value equality ---
                                       function valuesEqual(a, b) {
                                         if (a === b) return true;
                                         if (!a || !b) return false;
                                         if (a.type !== b.type) return false;
                                         switch (a.type) {
                                           case 'int': return a.raw === b.raw;
                                           case 'float': return a.raw === b.raw;
                                           case 'bool': return a.raw === b.raw;
                                           case 'string': return a.raw === b.raw;
                                           case 'formId': return a.raw === b.raw;
                                           case 'list':
                                             if (!a.items || !b.items) return a.items === b.items;
                                             if (a.items.length !== b.items.length) return false;
                                             for (var i = 0; i < a.items.length; i++) {
                                               if (!valuesEqual(a.items[i], b.items[i])) return false;
                                             }
                                             return true;
                                           case 'composite':
                                             if (!a.fields || !b.fields) return a.fields === b.fields;
                                             if (a.fields.length !== b.fields.length) return false;
                                             for (var i = 0; i < a.fields.length; i++) {
                                               if (a.fields[i].key !== b.fields[i].key) return false;
                                               if (!valuesEqual(a.fields[i].value, b.fields[i].value)) return false;
                                             }
                                             return true;
                                           default:
                                             return a.display === b.display;
                                         }
                                       }

                                       // --- Row expand/collapse ---
                                       function toggleDetail(summaryRow) {
                                         var detailRow = summaryRow.nextElementSibling;
                                         if (detailRow && detailRow.classList.contains('detail-row')) {
                                           if (detailRow.style.display === 'none') {
                                             renderDetail(detailRow);
                                             detailRow.style.display = '';
                                           } else {
                                             detailRow.style.display = 'none';
                                           }
                                         }
                                       }
                                       function expandAll() {
                                         _expandCancel = false;
                                         // Expand all collapsed group sections first
                                         document.querySelectorAll('.group-content').forEach(function(gc) {
                                           if (gc.style.display === 'none') {
                                             gc.style.display = '';
                                             var header = gc.previousElementSibling;
                                             if (header) header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                           }
                                         });
                                         var rows = Array.from(document.querySelectorAll('.detail-row:not(.hidden)'));
                                         var i = 0;
                                         function batch() {
                                           if (_expandCancel) return;
                                           var end = Math.min(i + 50, rows.length);
                                           for (; i < end; i++) {
                                             if (_expandCancel) return;
                                             renderDetail(rows[i]);
                                             rows[i].style.display = '';
                                           }
                                           if (i < rows.length) requestAnimationFrame(batch);
                                         }
                                         batch();
                                       }
                                       function collapseAll() {
                                         _expandCancel = true;
                                         document.querySelectorAll('.detail-row').forEach(function(r) {
                                           r.style.display = 'none';
                                         });
                                         document.querySelectorAll('.group-content').forEach(function(gc) {
                                           gc.style.display = 'none';
                                           var header = gc.previousElementSibling;
                                           if (header) header.textContent = header.textContent.replace('\u25BC', '\u25B6');
                                         });
                                       }

                                       // --- Search / filter ---
                                       function filterRows() {
                                         var query = document.getElementById('search').value.toLowerCase();
                                         var summaryRows = document.querySelectorAll('.summary-row');
                                         var visible = 0;
                                         summaryRows.forEach(function(row) {
                                           var searchData = row.getAttribute('data-search') || '';
                                           var match = !query || searchData.indexOf(query) !== -1;
                                           row.classList.toggle('hidden', !match);
                                           var detail = row.nextElementSibling;
                                           if (detail && detail.classList.contains('detail-row')) {
                                             if (!match) detail.classList.add('hidden');
                                             else detail.classList.remove('hidden');
                                           }
                                           if (match) visible++;
                                         });
                                         var countEl = document.getElementById('matchCount');
                                         if (query) {
                                           countEl.textContent = visible + ' of ' + summaryRows.length + ' records';
                                         } else {
                                           countEl.textContent = '';
                                         }
                                       }

                                       // --- Group collapse/expand ---
                                       function toggleGroup(header) {
                                         var content = header.nextElementSibling;
                                         if (content.style.display === 'none') {
                                           content.style.display = '';
                                           header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                           if (_pendingBuildSort) {
                                             var tbody = content.querySelector('tbody');
                                             if (tbody) {
                                               applyBuildSort(tbody, _pendingBuildSort.idx,
                                                 _pendingBuildSort.sortType, _pendingBuildSort.fixedCols);
                                             }
                                           }
                                         } else {
                                           content.style.display = 'none';
                                           header.textContent = header.textContent.replace('\u25BC', '\u25B6');
                                         }
                                       }
                                       function expandGroup(groupId) {
                                         var section = document.getElementById(groupId);
                                         if (!section) return;
                                         var header = section.querySelector('.group-header');
                                         var content = section.querySelector('.group-content');
                                         if (content && content.style.display === 'none') {
                                           content.style.display = '';
                                           if (header) header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                         }
                                       }

                                       // --- Build column pagination ---
                                       function navBuilds(dir) {
                                         var nav = document.querySelector('.build-nav');
                                         if (!nav) return;
                                         var total = parseInt(nav.dataset.total);
                                         var size = parseInt(nav.dataset.size);
                                         var start = parseInt(nav.dataset.start);
                                         if (dir === 'first') start = 0;
                                         else if (dir === 'prev3') start = Math.max(0, start - size);
                                         else if (dir === 'prev1') start = Math.max(0, start - 1);
                                         else if (dir === 'next1') start = Math.min(total - size, start + 1);
                                         else if (dir === 'next3') start = Math.min(total - size, start + size);
                                         else if (dir === 'last') start = Math.max(0, total - size);
                                         if (start < 0) start = 0;
                                         nav.dataset.start = start;
                                         var styleEl = document.getElementById('build-col-style');
                                         if (!styleEl) {
                                           styleEl = document.createElement('style');
                                           styleEl.id = 'build-col-style';
                                           document.head.appendChild(styleEl);
                                         }
                                         var rules = '';
                                         for (var i = 0; i < total; i++) {
                                           if (i < start || i >= start + size) {
                                             rules += '.build-col-' + i + '{display:none !important}';
                                           }
                                         }
                                         styleEl.textContent = rules;
                                         var label = nav.querySelector('.build-nav-label');
                                         if (label) label.textContent = 'Builds ' + (start + 1) + '\u2013'
                                           + Math.min(start + size, total) + ' of ' + total;
                                         var btns = nav.querySelectorAll('button');
                                         btns[0].disabled = start === 0;
                                         btns[1].disabled = start === 0;
                                         btns[2].disabled = start === 0;
                                         btns[3].disabled = start + size >= total;
                                         btns[4].disabled = start + size >= total;
                                         btns[5].disabled = start + size >= total;
                                       }

                                       // --- Build column sort ---
                                       var _badgeOrder = {'BASE':0,'NEW':1,'CHANGED':2,'REMOVED':3,
                                         'NOT PRESENT':4,'SAME':5,'SPARSE':6,'\u2014':7,'':8};
                                       var _badgeTypes = ['','BASE','NEW','CHANGED','REMOVED','NOT PRESENT','SAME'];

                                       function filterByBuild(th) {
                                         var table = th.closest('table');
                                         var tbody = table.querySelector('tbody');
                                         var idx = parseInt(th.getAttribute('data-dump-idx'));
                                         var fixedCols = tbody.querySelector('.col-coords') ? 4 : 3;
                                         var current = th.dataset.filterState || '';
                                         var curIdx = _badgeTypes.indexOf(current);
                                         var sortType = _badgeTypes[(curIdx + 1) % _badgeTypes.length];
                                         th.dataset.filterState = sortType;
                                         var label = th.querySelector('.build-filter-label');
                                         if (label) label.textContent = sortType ? '\u25B2 ' + sortType : '';
                                         table.querySelectorAll('.build-header').forEach(function(h) {
                                           if (h !== th) {
                                             h.dataset.filterState = '';
                                             var l = h.querySelector('.build-filter-label');
                                             if (l) l.textContent = '';
                                           }
                                         });
                                         applyBuildSort(tbody, idx, sortType, fixedCols);
                                         _pendingBuildSort = sortType
                                           ? { idx: idx, sortType: sortType, fixedCols: fixedCols } : null;
                                         document.querySelectorAll('.group-content').forEach(function(gc) {
                                           if (gc.style.display !== 'none') {
                                             var otherTbody = gc.querySelector('tbody');
                                             if (otherTbody && otherTbody !== tbody) {
                                               applyBuildSort(otherTbody, idx, sortType, fixedCols);
                                             }
                                           }
                                         });
                                       }
                                       function applyBuildSort(tbody, idx, sortType, fixedCols) {
                                         var summaryRows = Array.from(tbody.querySelectorAll('.summary-row'));
                                         if (summaryRows.length === 0) return;
                                         var pairs = summaryRows.map(function(sr) {
                                           var cells = sr.querySelectorAll('td');
                                           var cell = cells[fixedCols + idx];
                                           var badge = cell ? cell.querySelector('.badge') : null;
                                           var badgeText = badge ? badge.textContent.trim() : '';
                                           return { summary: sr, detail: sr.nextElementSibling,
                                             badge: badgeText, formid: sr.getAttribute('data-formid') || '' };
                                         });
                                         if (!sortType) {
                                           pairs.sort(function(a, b) {
                                             return a.formid < b.formid ? -1 : a.formid > b.formid ? 1 : 0;
                                           });
                                         } else {
                                           pairs.sort(function(a, b) {
                                             var aMatch = a.badge === sortType ? -1 : 0;
                                             var bMatch = b.badge === sortType ? -1 : 0;
                                             if (aMatch !== bMatch) return aMatch - bMatch;
                                             var aOrd = _badgeOrder[a.badge] !== undefined ? _badgeOrder[a.badge] : 9;
                                             var bOrd = _badgeOrder[b.badge] !== undefined ? _badgeOrder[b.badge] : 9;
                                             if (aOrd !== bOrd) return aOrd - bOrd;
                                             return a.formid < b.formid ? -1 : a.formid > b.formid ? 1 : 0;
                                           });
                                         }
                                         var frag = document.createDocumentFragment();
                                         pairs.forEach(function(p) {
                                           frag.appendChild(p.summary);
                                           if (p.detail) frag.appendChild(p.detail);
                                         });
                                         tbody.appendChild(frag);
                                       }

                                       // --- Column sort ---
                                       function sortBy(th, col) {
                                         var table = th.closest('table');
                                         var tbody = table.querySelector('tbody');
                                         var prevCol = table.dataset.sortCol;
                                         var asc = prevCol === col ? table.dataset.sortAsc !== 'true' : true;
                                         table.dataset.sortCol = col;
                                         table.dataset.sortAsc = asc;
                                         table.querySelectorAll('.sort-indicator').forEach(function(s) { s.textContent = ''; });
                                         th.querySelector('.sort-indicator').textContent = asc ? '\u25B2' : '\u25BC';
                                         var summaryRows = Array.from(tbody.querySelectorAll('.summary-row'));
                                         var pairs = summaryRows.map(function(sr) {
                                           return { summary: sr, detail: sr.nextElementSibling };
                                         });
                                         pairs.sort(function(a, b) {
                                           var cmp;
                                           if (col === 'coords') {
                                             var ax = parseInt(a.summary.getAttribute('data-cx')) || 0;
                                             var ay = parseInt(a.summary.getAttribute('data-cy')) || 0;
                                             var bx = parseInt(b.summary.getAttribute('data-cx')) || 0;
                                             var by = parseInt(b.summary.getAttribute('data-cy')) || 0;
                                             cmp = ax !== bx ? ax - bx : ay - by;
                                           } else {
                                             var va = (a.summary.getAttribute('data-' + col) || '').toLowerCase();
                                             var vb = (b.summary.getAttribute('data-' + col) || '').toLowerCase();
                                             cmp = va < vb ? -1 : va > vb ? 1 : 0;
                                           }
                                           return asc ? cmp : -cmp;
                                         });
                                         var frag = document.createDocumentFragment();
                                         pairs.forEach(function(p) {
                                           frag.appendChild(p.summary);
                                           if (p.detail) frag.appendChild(p.detail);
                                         });
                                         tbody.appendChild(frag);
                                       }
                                   """;
}
