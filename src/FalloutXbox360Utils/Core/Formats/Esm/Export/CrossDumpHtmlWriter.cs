using System.IO.Compression;
using System.Text;
using System.Web;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using ImageMagick;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Generates static HTML comparison pages from a cross-dump record index.
///     Each record type gets its own page with collapsible rows, search, and per-line diff highlighting.
/// </summary>
internal static class CrossDumpHtmlWriter
{
    private const string PlacedObjectTagPrefix = "\x01#";

    private const string CssStyles = """
                                         * { box-sizing: border-box; }
                                         body {
                                           font-family: system-ui, -apple-system, sans-serif;
                                           margin: 20px;
                                           background: #fff;
                                           color: #1a1a1a;
                                         }
                                         h1 { margin-bottom: 4px; }
                                         .summary { color: #666; margin-top: 0; }
                                         a { color: #0066cc; }

                                         .controls {
                                           display: flex;
                                           align-items: center;
                                           gap: 12px;
                                           padding: 8px 12px;
                                           flex-wrap: wrap;
                                           position: sticky;
                                           top: 0;
                                           z-index: 10;
                                           background: #fff;
                                           border-bottom: 1px solid #ddd;
                                           margin: 0;
                                         }
                                         .controls input[type="text"] {
                                           padding: 6px 12px;
                                           border: 1px solid #ccc;
                                           border-radius: 4px;
                                           font-size: 13px;
                                           min-width: 300px;
                                         }
                                         .controls button {
                                           padding: 5px 12px;
                                           border: 1px solid #ccc;
                                           border-radius: 4px;
                                           background: #f5f5f5;
                                           cursor: pointer;
                                           font-size: 12px;
                                         }
                                         .controls button:hover { background: #e8e8e8; }
                                         .match-count { font-size: 12px; color: #888; }

                                         table { border-collapse: separate; border-spacing: 0; width: 100%; margin: 8px 0; }
                                         tbody { content-visibility: auto; contain-intrinsic-size: auto 500px; }
                                         table.compact { width: auto; }
                                         table.compact th { position: static; }
                                         th, td { border: 1px solid #ddd; padding: 6px 8px; vertical-align: top; text-align: left; }
                                         th {
                                           background: #f5f5f5;
                                           position: sticky;
                                           top: 38px;
                                           z-index: 3;
                                           font-size: 13px;
                                         }
                                         .col-editor {
                                           position: sticky;
                                           left: 0;
                                           z-index: 1;
                                           background: #fff;
                                           min-width: 120px;
                                           white-space: nowrap;
                                         }
                                         th.col-editor { z-index: 5; }
                                         .col-name { min-width: 80px; white-space: nowrap; }
                                         .col-coords { min-width: 70px; white-space: nowrap; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
                                         .col-formid { min-width: 90px; white-space: nowrap; }
                                         .sortable { cursor: pointer; user-select: none; }
                                         .formid { font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
                                         .name-change { color: #999; font-size: 11px; }
                                         .dump-date { font-size: 11px; color: #888; font-weight: normal; }
                                         .build-header { cursor: pointer; }
                                         .build-header:hover { background: #e8e8f0; }
                                         .build-filter-label { font-size: 10px; font-weight: 600; letter-spacing: 0.5px; text-transform: uppercase; }

                                         .summary-row { cursor: pointer; }
                                         .summary-row:hover { background: #f0f4ff; }
                                         .summary-row:hover .col-editor { background: #f0f4ff; }
                                         .summary-row td { padding: 4px 8px; }
                                         .detail-row td { padding: 2px 4px; }
                                         .detail-row .col-editor { background: #fff; }

                                         .badge {
                                           display: inline-block;
                                           padding: 2px 8px;
                                           border-radius: 3px;
                                           font-size: 10px;
                                           font-weight: 600;
                                           letter-spacing: 0.5px;
                                           text-transform: uppercase;
                                         }
                                         .badge-new { background: #d4edda; color: #155724; }
                                         .badge-changed { background: #fff3cd; color: #856404; }
                                         .badge-same { background: #e9ecef; color: #6c757d; }
                                         .badge-removed { background: #f8d7da; color: #721c24; }
                                         .badge-absent { color: #ccc; }
                                         .badge-sparse { background: #e2e3f1; color: #5a5b8a; }
                                         .badge-base { background: #d6e4f0; color: #2c5282; }

                                         pre.record-text {
                                           margin: 0;
                                           font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace;
                                           font-size: 11px;
                                           line-height: 1.4;
                                           white-space: pre;
                                           tab-size: 4;
                                         }
                                         .line-new { background: #d4edda; }
                                         .line-changed { background: #fff3cd; }
                                         .line-removed { background: #f8d7da; color: #999; text-decoration: line-through; }
                                         .line-sparse { color: #999; font-style: italic; }

                                         .toc {
                                           background: #f8f9fa;
                                           border: 1px solid #ddd;
                                           border-radius: 4px;
                                           padding: 8px 16px;
                                           margin: 8px 0 16px 0;
                                         }
                                         .toc ul { margin: 4px 0; padding-left: 20px; columns: 3; }
                                         .toc li { font-size: 13px; margin: 2px 0; }

                                         .group-section { margin: 8px 0; }
                                         .group-header {
                                           margin: 0;
                                           padding: 8px 0;
                                           border-bottom: 2px solid #0066cc;
                                           color: #0066cc;
                                           font-size: 18px;
                                           cursor: pointer;
                                           user-select: none;
                                         }
                                         .group-header:hover { opacity: 0.8; }

                                         .cell-map-container { margin: 8px 0; border: 1px solid #ccc; display: inline-block; }
                                         .cell-grid-overlay { pointer-events: none; }
                                         .cell-tile { cursor: pointer; pointer-events: auto; box-sizing: border-box; }
                                         .cell-tile:hover { outline: 2px solid #fff; z-index: 1; background: rgba(255,255,255,0.3); }

                                         /* build-hidden: columns hidden/shown via dynamic #build-col-style stylesheet */
                                         .build-nav { display: flex; align-items: center; gap: 6px; margin-left: auto; }
                                         .build-nav button { padding: 3px 8px; font-size: 11px; border: 1px solid #ccc; border-radius: 3px; background: #f5f5f5; cursor: pointer; }
                                         .build-nav button:hover { background: #e0e0e0; }
                                         .build-nav button:disabled { opacity: 0.4; cursor: default; }
                                         .build-nav-label { font-size: 12px; color: #666; min-width: 120px; text-align: center; }
                                         .hidden { display: none !important; }

                                         @media (prefers-color-scheme: dark) {
                                           body { background: #1a1a1a; color: #e0e0e0; }
                                           a { color: #6db3f2; }
                                           th { background: #2a2a2a; border-color: #444; }
                                           td { border-color: #444; }
                                           .controls { background: #1a1a1a; border-bottom-color: #444; }
                                           .col-editor { background: #1a1a1a; }
                                           .detail-row .col-editor { background: #1a1a1a; }
                                           .summary { color: #999; }
                                           .dump-date { color: #777; }
                                           .controls input[type="text"] { background: #2a2a2a; color: #e0e0e0; border-color: #555; }
                                           .controls button { background: #333; color: #e0e0e0; border-color: #555; }
                                           .controls button:hover { background: #444; }
                                           .summary-row:hover { background: #252535; }
                                           .summary-row:hover .col-editor { background: #252535; }
                                           .badge-new { background: #1e3a1e; color: #8fd19e; }
                                           .badge-changed { background: #3a3520; color: #e0c878; }
                                           .badge-same { background: #2a2a2a; color: #888; }
                                           .badge-removed { background: #3a1e1e; color: #e08888; }
                                           .badge-absent { color: #555; }
                                           .badge-sparse { background: #2a2a3a; color: #9999cc; }
                                           .badge-base { background: #1e2a3a; color: #6b9fd4; }
                                           .line-new { background: #1e3a1e; }
                                           .line-changed { background: #3a3520; }
                                           .line-removed { background: #3a1e1e; color: #777; }
                                           .line-sparse { color: #666; }
                                           .group-header { color: #6db3f2; border-bottom-color: #6db3f2; }
                                           .toc { background: #2a2a2a; border-color: #444; }
                                           .cell-map-container { border-color: #555; }
                                           .build-header:hover { background: #333; }
                                           .build-nav button { background: #333; color: #e0e0e0; border-color: #555; }
                                           .build-nav button:hover { background: #444; }
                                           .build-nav-label { color: #999; }
                                         }
                                     """;

    private const string JavaScriptCode = """
                                              // Decompress a base64-encoded raw deflate blob via native DecompressionStream
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

                                              // Inflate all compressed cells in a detail row (if not already done)
                                              async function inflateRow(detailRow) {
                                                if (detailRow.dataset.inflated) return;
                                                detailRow.dataset.inflated = '1';
                                                var cells = detailRow.querySelectorAll('td[data-z]');
                                                var promises = Array.from(cells).map(async function(td) {
                                                  var html = await inflate(td.getAttribute('data-z'));
                                                  td.innerHTML = html;
                                                  td.removeAttribute('data-z');
                                                });
                                                await Promise.all(promises);
                                              }

                                              async function toggleDetail(summaryRow) {
                                                var detailRow = summaryRow.nextElementSibling;
                                                if (detailRow && detailRow.classList.contains('detail-row')) {
                                                  if (detailRow.style.display === 'none') {
                                                    await inflateRow(detailRow);
                                                    detailRow.style.display = '';
                                                  } else {
                                                    detailRow.style.display = 'none';
                                                  }
                                                }
                                              }
                                              var _expandCancel = false;
                                              async function expandAll() {
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
                                                async function batch() {
                                                  if (_expandCancel) return;
                                                  var end = Math.min(i + 20, rows.length);
                                                  for (; i < end; i++) {
                                                    if (_expandCancel) return;
                                                    await inflateRow(rows[i]);
                                                    rows[i].style.display = '';
                                                  }
                                                  if (i < rows.length) requestAnimationFrame(batch);
                                                }
                                                await batch();
                                              }
                                              function collapseAll() {
                                                _expandCancel = true;
                                                document.querySelectorAll('.detail-row').forEach(function(r) {
                                                  r.style.display = 'none';
                                                });
                                                // Also collapse all group sections
                                                document.querySelectorAll('.group-content').forEach(function(gc) {
                                                  gc.style.display = 'none';
                                                  var header = gc.previousElementSibling;
                                                  if (header) header.textContent = header.textContent.replace('\u25BC', '\u25B6');
                                                });
                                              }
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
                                                    if (!match) {
                                                      detail.classList.add('hidden');
                                                    } else {
                                                      detail.classList.remove('hidden');
                                                    }
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
                                              // Collapsible group sections
                                              function toggleGroup(header) {
                                                var content = header.nextElementSibling;
                                                if (content.style.display === 'none') {
                                                  content.style.display = '';
                                                  header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                                  // Apply any pending build sort to this newly-visible table
                                                  if (_pendingBuildSort) {
                                                    var tbody = content.querySelector('tbody');
                                                    if (tbody) {
                                                      applyBuildSort(tbody, _pendingBuildSort.idx, _pendingBuildSort.sortType, _pendingBuildSort.fixedCols);
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
                                              // Navigate to a cell row from the grid tile map
                                              function navigateToCell(tile) {
                                                var formId = tile.getAttribute('data-formid');
                                                // Search all tables on the page (cell may be in the same group-section)
                                                var row = document.querySelector('.summary-row[data-formid="' + formId + '"]');
                                                if (!row) return;
                                                // Ensure the group containing this row is expanded
                                                var section = row.closest('.group-content');
                                                if (section && section.style.display === 'none') {
                                                  section.style.display = '';
                                                  var header = section.previousElementSibling;
                                                  if (header) header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                                }
                                                // Scroll after a brief delay to let layout settle
                                                setTimeout(function() {
                                                  row.scrollIntoView({ behavior: 'smooth', block: 'center' });
                                                  row.style.outline = '3px solid #0066cc';
                                                  row.style.outlineOffset = '-1px';
                                                  setTimeout(function() { row.style.outline = ''; row.style.outlineOffset = ''; }, 3000);
                                                }, 50);
                                              }
                                              // Build column pagination — show 3 at a time
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
                                                // Show/hide columns via dynamic stylesheet (O(1) instead of O(n*builds) DOM ops)
                                                var styleId = 'build-col-style';
                                                var styleEl = document.getElementById(styleId);
                                                if (!styleEl) {
                                                  styleEl = document.createElement('style');
                                                  styleEl.id = styleId;
                                                  document.head.appendChild(styleEl);
                                                }
                                                var rules = '';
                                                for (var i = 0; i < total; i++) {
                                                  if (i < start || i >= start + size) {
                                                    rules += '.build-col-' + i + '{display:none !important}';
                                                  }
                                                }
                                                styleEl.textContent = rules;
                                                // Update label
                                                var label = nav.querySelector('.build-nav-label');
                                                if (label) label.textContent = 'Builds ' + (start + 1) + '\u2013' + Math.min(start + size, total) + ' of ' + total;
                                                // Update button states
                                                var btns = nav.querySelectorAll('button');
                                                btns[0].disabled = start === 0;
                                                btns[1].disabled = start === 0;
                                                btns[2].disabled = start === 0;
                                                btns[3].disabled = start + size >= total;
                                                btns[4].disabled = start + size >= total;
                                                btns[5].disabled = start + size >= total;
                                              }
                                              // Build column sort — click header to cycle through badge types, sorting that type to top
                                              var _badgeOrder = {'BASE':0, 'NEW':1, 'CHANGED':2, 'REMOVED':3, 'NOT PRESENT':4, 'SAME':5, 'SPARSE':6, '\u2014':7, '':8};
                                              var _badgeTypes = ['', 'BASE', 'NEW', 'CHANGED', 'REMOVED', 'NOT PRESENT', 'SAME'];
                                              // Pending sort state: applied lazily when a collapsed group is expanded
                                              var _pendingBuildSort = null; // { idx, sortType, fixedCols }
                                              function filterByBuild(th) {
                                                var table = th.closest('table');
                                                var tbody = table.querySelector('tbody');
                                                var idx = parseInt(th.getAttribute('data-dump-idx'));
                                                var fixedCols = tbody.querySelector('.col-coords') ? 4 : 3;
                                                // Cycle sort state
                                                var current = th.dataset.filterState || '';
                                                var curIdx = _badgeTypes.indexOf(current);
                                                var sortType = _badgeTypes[(curIdx + 1) % _badgeTypes.length];
                                                th.dataset.filterState = sortType;
                                                // Update label
                                                var label = th.querySelector('.build-filter-label');
                                                if (label) label.textContent = sortType ? '\u25B2 ' + sortType : '';
                                                // Clear other build sort labels in same table
                                                table.querySelectorAll('.build-header').forEach(function(h) {
                                                  if (h !== th) {
                                                    h.dataset.filterState = '';
                                                    var l = h.querySelector('.build-filter-label');
                                                    if (l) l.textContent = '';
                                                  }
                                                });
                                                // Apply sort to this table
                                                applyBuildSort(tbody, idx, sortType, fixedCols);
                                                // Store pending sort for other tables (applied on group expand)
                                                _pendingBuildSort = sortType ? { idx: idx, sortType: sortType, fixedCols: fixedCols } : null;
                                                // Apply to other VISIBLE tables only
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
                                                  return { summary: sr, detail: sr.nextElementSibling, badge: badgeText, formid: sr.getAttribute('data-formid') || '' };
                                                });
                                                if (!sortType) {
                                                  pairs.sort(function(a, b) { return a.formid < b.formid ? -1 : a.formid > b.formid ? 1 : 0; });
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
                                                // Use DocumentFragment for batch DOM update (avoids per-element reflow)
                                                var frag = document.createDocumentFragment();
                                                pairs.forEach(function(p) {
                                                  frag.appendChild(p.summary);
                                                  if (p.detail) frag.appendChild(p.detail);
                                                });
                                                tbody.appendChild(frag);
                                              }
                                              // Column sorting (per-table)
                                              function sortBy(th, col) {
                                                var table = th.closest('table');
                                                var tbody = table.querySelector('tbody');
                                                // Toggle direction — store sort state on the table element
                                                var prevCol = table.dataset.sortCol;
                                                var asc = prevCol === col ? table.dataset.sortAsc !== 'true' : true;
                                                table.dataset.sortCol = col;
                                                table.dataset.sortAsc = asc;
                                                // Update arrow indicators within this table only
                                                table.querySelectorAll('.sort-indicator').forEach(function(s) { s.textContent = ''; });
                                                th.querySelector('.sort-indicator').textContent = asc ? '\u25B2' : '\u25BC';
                                                // Collect summary+detail row pairs
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
                                                // Re-append in sorted order using DocumentFragment (batch DOM update)
                                                var frag = document.createDocumentFragment();
                                                pairs.forEach(function(p) {
                                                  frag.appendChild(p.summary);
                                                  if (p.detail) frag.appendChild(p.detail);
                                                });
                                                tbody.appendChild(frag);
                                              }
                                          """;

    /// <summary>
    ///     Generate all HTML files: one per record type plus an index page.
    /// </summary>
    internal static Dictionary<string, string> GenerateAll(CrossDumpRecordIndex index)
    {
        var files = new Dictionary<string, string>();

        foreach (var (recordType, formIdMap) in index.Records.OrderBy(r => r.Key))
        {
            index.RecordGroups.TryGetValue(recordType, out var groups);
            var html = GenerateRecordTypePage(recordType, formIdMap, index.Dumps, groups,
                index.CellGridCoords, index.CellHeightmaps ?? []);
            if (!string.IsNullOrEmpty(html))
            {
                files[$"compare_{recordType.ToLowerInvariant()}.html"] = html;
            }
        }

        files["index.html"] = GenerateIndexPage(index);
        return files;
    }

    private static string GenerateRecordTypePage(
        string recordType,
        Dictionary<uint, Dictionary<int, (string? EditorId, string? DisplayName, string FormattedText)>> formIdMap,
        List<DumpSnapshot> dumps,
        Dictionary<uint, string>? groups,
        Dictionary<uint, (int X, int Y)>? cellGridCoords,
        Dictionary<string, Dictionary<(int X, int Y), LandHeightmap>> cellHeightmaps)
    {
        var sb = new StringBuilder();
        AppendHtmlHeader(sb, $"{recordType} — Cross-Build Comparison");

        // Initial column visibility — hide columns 3+ until nav buttons change it
        if (dumps.Count > 3)
        {
            sb.Append("  <style id=\"build-col-style\">");
            for (var i = 3; i < dumps.Count; i++)
                sb.Append($".build-col-{i}{{display:none !important}}");
            sb.AppendLine("</style>");
        }

        sb.AppendLine($"  <h1>{Esc(recordType)} — Cross-Build Comparison</h1>");
        sb.AppendLine($"  <p class=\"summary\">{dumps.Count} builds, {formIdMap.Count:N0} records</p>");

        // Navigation + controls
        sb.AppendLine("  <div class=\"controls\">");
        sb.AppendLine("    <a href=\"index.html\">&larr; Back to index</a>");
        sb.AppendLine(
            "    <input type=\"text\" id=\"search\" placeholder=\"Search by FormID, EditorID, or name...\" oninput=\"filterRows()\">");
        sb.AppendLine("    <button onclick=\"expandAll()\">Expand All</button>");
        sb.AppendLine("    <button onclick=\"collapseAll()\">Collapse All</button>");
        sb.AppendLine("    <span id=\"matchCount\" class=\"match-count\"></span>");
        if (dumps.Count > 3)
        {
            sb.AppendLine(
                $"    <div class=\"build-nav\" data-total=\"{dumps.Count}\" data-start=\"0\" data-size=\"3\">");
            sb.AppendLine("      <button onclick=\"navBuilds('first')\">&laquo; First</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev3')\">&lsaquo; 3</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('prev1')\">&lsaquo; 1</button>");
            sb.AppendLine($"      <span class=\"build-nav-label\">Builds 1\u20133 of {dumps.Count}</span>");
            sb.AppendLine("      <button onclick=\"navBuilds('next1')\">1 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('next3')\">3 &rsaquo;</button>");
            sb.AppendLine("      <button onclick=\"navBuilds('last')\">&raquo; Last</button>");
            sb.AppendLine("    </div>");
        }

        sb.AppendLine("  </div>");

        // Detect sparse dumps: dumps with 0 records of this type should not cause false REMOVED/NEW
        var sparseDumps = new HashSet<int>();
        for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
        {
            if (!formIdMap.Values.Any(dm => dm.ContainsKey(dumpIdx)))
                sparseDumps.Add(dumpIdx);
        }

        if (groups != null && groups.Count > 0)
        {
            // Group records into sub-tables (e.g., cells by worldspace / interior)
            var groupedRecords = formIdMap
                .GroupBy(kvp => groups.TryGetValue(kvp.Key, out var g) ? g : "(Ungrouped)")
                .OrderBy(g => g.Key == "Interior Cells" ? 1 : 0) // Exterior first, interior last
                .ThenBy(g => g.Key)
                .ToList();

            // Table of contents for grouped pages
            sb.AppendLine("  <div class=\"toc\">");
            sb.AppendLine("    <strong>Sections:</strong>");
            sb.AppendLine("    <ul>");
            foreach (var group in groupedRecords)
            {
                var groupId = $"group-{group.Key.Replace(' ', '-').Replace('(', '_').Replace(')', '_')}";
                sb.AppendLine(
                    $"      <li><a href=\"#{groupId}\" onclick=\"expandGroup('{groupId}')\">{Esc(group.Key)} ({group.Count():N0})</a></li>");
            }

            sb.AppendLine("    </ul>");
            sb.AppendLine("  </div>");

            foreach (var group in groupedRecords)
            {
                var groupMap = group.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
                var groupId = $"group-{group.Key.Replace(' ', '-').Replace('(', '_').Replace(')', '_')}";

                sb.AppendLine($"  <div class=\"group-section\" id=\"{groupId}\">");
                sb.AppendLine(
                    $"    <h2 class=\"group-header\" onclick=\"toggleGroup(this)\">\u25B6 {Esc(group.Key)} ({groupMap.Count:N0})</h2>");
                sb.AppendLine("    <div class=\"group-content\" style=\"display:none\">");

                // CSS grid tile map for exterior worldspaces with grid coordinates
                if (cellGridCoords != null && group.Key != "Interior Cells")
                {
                    cellHeightmaps.TryGetValue(group.Key, out var wsHeightmaps);
                    AppendCellGridMap(sb, groupMap, cellGridCoords,
                        wsHeightmaps ?? new Dictionary<(int, int), LandHeightmap>(), dumps, sparseDumps);
                }

                AppendRecordTable(sb, groupMap, dumps, sparseDumps, cellGridCoords);
                sb.AppendLine("    </div>");
                sb.AppendLine("  </div>");
            }
        }
        else
        {
            AppendRecordTable(sb, formIdMap, dumps, sparseDumps);
        }

        sb.AppendLine($"  <script>{JavaScriptCode}</script>");
        AppendHtmlFooter(sb);
        return sb.ToString();
    }

    /// <summary>
    ///     Generate a heightmap image with clickable cell overlay for a worldspace.
    ///     Uses HeightmapColorRenderer to produce a composite PNG embedded as base64.
    ///     Each cell tile is clickable to navigate to its row in the table.
    /// </summary>
    private static void AppendCellGridMap(
        StringBuilder sb,
        Dictionary<uint, Dictionary<int, (string? EditorId, string? DisplayName, string FormattedText)>> groupMap,
        Dictionary<uint, (int X, int Y)> cellGridCoords,
        Dictionary<(int X, int Y), LandHeightmap> cellHeightmaps,
        List<DumpSnapshot> dumps,
        HashSet<int> sparseDumps)
    {
        // Collect cells in this group that have grid coords
        var gridCells = new List<(uint FormId, int X, int Y, string Status, string Label)>();
        foreach (var (formId, dumpMap) in groupMap)
        {
            if (!cellGridCoords.TryGetValue(formId, out var coords)) continue;

            // Determine change status across dumps
            string? prevText = null;
            var hasChanged = false;
            var isNew = true;
            var dumpCount = 0;
            foreach (var dumpIdx in Enumerable.Range(0, dumps.Count))
            {
                if (sparseDumps.Contains(dumpIdx)) continue;
                if (dumpMap.TryGetValue(dumpIdx, out var entry))
                {
                    dumpCount++;
                    if (prevText != null && entry.FormattedText != prevText) hasChanged = true;
                    if (prevText != null) isNew = false;
                    prevText = entry.FormattedText;
                }
                else if (prevText != null)
                {
                    isNew = false;
                }
            }

            string status;
            if (dumpCount <= 1)
                status = isNew ? "new" : "single";
            else
                status = hasChanged ? "changed" : "same";

            string? name = null, edId = null;
            foreach (var (ed, dn, _) in dumpMap.Values)
            {
                edId ??= ed;
                name ??= dn;
            }

            var label = name ?? edId ?? $"0x{formId:X8}";
            gridCells.Add((formId, coords.X, coords.Y, status, label));
        }

        if (gridCells.Count == 0) return;

        // Use heightmap bounds for the grid image (avoids empty space from outlier cells)
        int minX, maxX, minY, maxY;
        if (cellHeightmaps.Count > 0)
        {
            minX = cellHeightmaps.Keys.Min(k => k.X);
            maxX = cellHeightmaps.Keys.Max(k => k.X);
            minY = cellHeightmaps.Keys.Min(k => k.Y);
            maxY = cellHeightmaps.Keys.Max(k => k.Y);
        }
        else
        {
            minX = gridCells.Min(c => c.X);
            maxX = gridCells.Max(c => c.X);
            minY = gridCells.Min(c => c.Y);
            maxY = gridCells.Max(c => c.Y);
        }

        var cols = maxX - minX + 1;
        var rows = maxY - minY + 1;

        // Cell size in pixels for the overlay (displayed at 200% via CSS)
        const int cellPx = 8;
        var imgWidth = cols * cellPx;
        var imgHeight = rows * cellPx;

        // Try to generate a heightmap PNG
        var hasHeightmap = TryGenerateHeightmapBase64(cellHeightmaps, minX, maxX, minY, maxY, out var heightmapBase64);

        // Container with heightmap background (or plain dark bg) and clickable overlay
        // Displayed at 200% scale for easier cell selection
        var displayWidth = imgWidth * 2;
        var displayHeight = imgHeight * 2;
        var displayCellPx = cellPx * 2;

        sb.AppendLine(
            $"      <div class=\"cell-map-container\" style=\"position:relative;display:inline-block;width:{displayWidth}px;height:{displayHeight}px;\">");

        if (hasHeightmap)
        {
            sb.AppendLine(
                $"        <img src=\"data:image/png;base64,{heightmapBase64}\" " +
                $"style=\"width:{displayWidth}px;height:{displayHeight}px;display:block;image-rendering:pixelated;\" alt=\"Heightmap\">");
        }
        else
        {
            sb.AppendLine(
                $"        <div style=\"width:{displayWidth}px;height:{displayHeight}px;background:#1a1a1a;\"></div>");
        }

        // Overlay grid for click/hover interaction
        sb.AppendLine(
            $"        <div class=\"cell-grid-overlay\" style=\"position:absolute;top:0;left:0;display:grid;" +
            $"grid-template-columns:repeat({cols},{displayCellPx}px);grid-template-rows:repeat({rows},{displayCellPx}px);\">");

        var cellLookup = new Dictionary<(int, int), (uint FormId, int X, int Y, string Status, string Label)>();
        foreach (var cell in gridCells)
        {
            // Only include cells within the display bounds
            if (cell.X >= minX && cell.X <= maxX && cell.Y >= minY && cell.Y <= maxY)
                cellLookup.TryAdd((cell.X, cell.Y), cell);
        }

        for (var y = maxY; y >= minY; y--)
        {
            for (var x = minX; x <= maxX; x++)
            {
                if (cellLookup.TryGetValue((x, y), out var cell))
                {
                    sb.Append(
                        $"<div class=\"cell-tile\" title=\"{Esc(cell.Label)} ({x},{y})\" " +
                        $"data-formid=\"0x{cell.FormId:X8}\" onclick=\"navigateToCell(this)\"></div>");
                }
                else
                {
                    sb.Append("<div class=\"cell-tile\"></div>");
                }
            }
        }

        sb.AppendLine("</div>");
        sb.AppendLine("      </div>");
    }

    /// <summary>
    ///     Generate a composite heightmap PNG from available cell heightmaps and return as base64.
    ///     Uses amber/sepia tinting (FNV Pip-Boy HUD color) with cell gridlines.
    /// </summary>
    private static bool TryGenerateHeightmapBase64(
        Dictionary<(int X, int Y), LandHeightmap> cellHeightmaps,
        int minX, int maxX, int minY, int maxY,
        out string base64)
    {
        base64 = "";

        // Collect heightmaps within the grid bounds
        var cells = new List<(int X, int Y, float[,] Heights)>();
        for (var cx = minX; cx <= maxX; cx++)
        {
            for (var cy = minY; cy <= maxY; cy++)
            {
                if (cellHeightmaps.TryGetValue((cx, cy), out var hm))
                {
                    cells.Add((cx, cy, hm.CalculateHeights()));
                }
            }
        }

        if (cells.Count == 0) return false;

        // Compute global height range for consistent normalization
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        foreach (var (_, _, heights) in cells)
        {
            var (cMin, cMax) = HeightmapColorRenderer.GetHeightRange(heights);
            if (cMin < globalMin) globalMin = cMin;
            if (cMax > globalMax) globalMax = cMax;
        }

        var globalRange = globalMax - globalMin;
        if (globalRange < 0.001f) globalRange = 1f;

        // Amber tint (FNV Pip-Boy HUD: #FFB642)
        const float tintR = 1f;
        const float tintG = 182f / 255f;
        const float tintB = 66f / 255f;

        // Render composite image (8 pixels per cell — matching overlay cellPx)
        const int cellPx = 8;
        var cols = maxX - minX + 1;
        var rows = maxY - minY + 1;
        var imgW = cols * cellPx;
        var imgH = rows * cellPx;
        var pixels = new byte[imgW * imgH * 3];

        // Fill with black background for cells without LAND data
        Array.Fill(pixels, (byte)0);

        // Build a hashset of occupied cells for gridline rendering
        var occupiedCells = new HashSet<(int, int)>();
        foreach (var (cx, cy, _) in cells)
        {
            occupiedCells.Add((cx, cy));
        }

        foreach (var (cx, cy, heights) in cells)
        {
            var imgCellX = cx - minX;
            var imgCellY = maxY - cy; // Y inverted

            for (var py = 0; py < cellPx; py++)
            {
                for (var px = 0; px < cellPx; px++)
                {
                    // Sample height from 33x33 grid, mapped to cellPx x cellPx
                    var hx = (int)(px * 32f / (cellPx - 1));
                    var hy = (int)(py * 32f / (cellPx - 1));
                    hx = Math.Clamp(hx, 0, 32);
                    hy = Math.Clamp(hy, 0, 32);

                    var normalized = (heights[32 - hy, hx] - globalMin) / globalRange;
                    var gray = Math.Clamp(normalized, 0f, 1f);

                    // Apply amber tint
                    var r = (byte)(gray * 255 * tintR);
                    var g = (byte)(gray * 255 * tintG);
                    var b = (byte)(gray * 255 * tintB);

                    // Draw gridlines on cell edges (darken by 40%)
                    if (px == 0 || py == 0)
                    {
                        r = (byte)(r * 0.6f);
                        g = (byte)(g * 0.6f);
                        b = (byte)(b * 0.6f);
                    }

                    var imgX = imgCellX * cellPx + px;
                    var imgY = imgCellY * cellPx + py;
                    var idx = (imgY * imgW + imgX) * 3;
                    pixels[idx] = r;
                    pixels[idx + 1] = g;
                    pixels[idx + 2] = b;
                }
            }
        }

        // Encode to PNG via ImageMagick, then base64
        var settings = new MagickReadSettings
        {
            Width = (uint)imgW,
            Height = (uint)imgH,
            Format = MagickFormat.Rgb,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        using var ms = new MemoryStream();
        image.Write(ms, MagickFormat.Png);
        base64 = Convert.ToBase64String(ms.ToArray());
        return true;
    }

    private static void AppendRecordTable(
        StringBuilder sb,
        Dictionary<uint, Dictionary<int, (string? EditorId, string? DisplayName, string FormattedText)>> formIdMap,
        List<DumpSnapshot> dumps,
        HashSet<int> sparseDumps,
        Dictionary<uint, (int X, int Y)>? gridCoords = null)
    {
        var hasCoords = gridCoords != null && gridCoords.Count > 0;

        sb.AppendLine("  <table>");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr>");
        sb.AppendLine(
            "        <th class=\"col-editor sortable\" onclick=\"sortBy(this,'editor')\">Editor ID <span class=\"sort-indicator\"></span></th>");
        sb.AppendLine(
            "        <th class=\"col-name sortable\" onclick=\"sortBy(this,'name')\">Name <span class=\"sort-indicator\"></span></th>");
        if (hasCoords)
            sb.AppendLine(
                "        <th class=\"col-coords sortable\" onclick=\"sortBy(this,'coords')\">Coords <span class=\"sort-indicator\"></span></th>");
        sb.AppendLine(
            "        <th class=\"col-formid sortable\" onclick=\"sortBy(this,'formid')\">Form ID <span class=\"sort-indicator\"></span></th>");
        for (var i = 0; i < dumps.Count; i++)
        {
            var hiddenClass = "";
            sb.AppendLine(
                $"        <th class=\"build-header build-col-{i}{hiddenClass}\" data-dump-idx=\"{i}\" onclick=\"filterByBuild(this)\">" +
                $"{Esc(dumps[i].ShortName)}<br><span class=\"dump-date\">{(dumps[i].IsBase ? "(base)" : $"{dumps[i].FileDate:yyyy-MM-dd}")}</span>" +
                $"<br><span class=\"build-filter-label\"></span></th>");
        }

        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        // Sort records by EditorID
        var sorted = formIdMap
            .OrderBy(kvp =>
            {
                foreach (var dm in kvp.Value.Values)
                    if (dm.EditorId != null)
                        return dm.EditorId;
                return "";
            })
            .ToList();

        foreach (var (formId, dumpMap) in sorted)
        {
            // Collect all distinct EditorID/Name values in chronological order
            var allEditorIds = dumpMap.OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value.EditorId)
                .Where(e => e != null)
                .Cast<string>()
                .Distinct()
                .ToList();
            var allNames = dumpMap.OrderBy(kvp => kvp.Key)
                .Select(kvp => kvp.Value.DisplayName)
                .Where(n => n != null)
                .Cast<string>()
                .Distinct()
                .ToList();

            var editorIdDisplay = allEditorIds.Count > 1
                ? string.Join("<br><span class=\"name-change\">\u21B3 </span>", allEditorIds.Select(Esc))
                : Esc(allEditorIds.FirstOrDefault() ?? "");
            var nameDisplay = allNames.Count > 1
                ? string.Join("<br><span class=\"name-change\">\u21B3 </span>", allNames.Select(Esc))
                : Esc(allNames.FirstOrDefault() ?? "");
            var editorIdSearch = string.Join(" ", allEditorIds);
            var nameSearch = string.Join(" ", allNames);

            // Coords string for cell pages
            var coordsDisplay = "";
            if (hasCoords && gridCoords!.TryGetValue(formId, out var gc))
                coordsDisplay = $"({gc.X}, {gc.Y})";

            var searchData = $"0x{formId:X8} {editorIdSearch} {nameSearch} {coordsDisplay}".ToLowerInvariant();

            // --- Summary row (always visible, clickable) ---
            var coordAttrs = "";
            if (hasCoords && gridCoords!.TryGetValue(formId, out var gcAttr))
                coordAttrs = $" data-cx=\"{gcAttr.X}\" data-cy=\"{gcAttr.Y}\"";
            sb.Append(
                $"      <tr class=\"summary-row\" data-search=\"{Esc(searchData)}\" data-editor=\"{Esc(editorIdSearch)}\" data-name=\"{Esc(nameSearch)}\" data-coords=\"{Esc(coordsDisplay)}\"{coordAttrs} data-formid=\"0x{formId:X8}\" onclick=\"toggleDetail(this)\">");
            sb.Append($"<td class=\"col-editor\">{editorIdDisplay}</td>");
            sb.Append($"<td class=\"col-name\">{nameDisplay}</td>");
            if (hasCoords)
                sb.Append($"<td class=\"col-coords\">{Esc(coordsDisplay)}</td>");
            sb.Append($"<td class=\"col-formid formid\">0x{formId:X8}</td>");

            // Status badge per dump (skip sparse dumps for previousText tracking)
            string? previousText = null;
            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                var colClass = $"build-col-{dumpIdx}";
                // Visibility controlled by #build-col-style dynamic stylesheet

                if (dumpMap.TryGetValue(dumpIdx, out var entry))
                {
                    if (previousText == null)
                    {
                        if (dumps[dumpIdx].IsBase)
                            sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-base\">BASE</span></td>");
                        else
                            sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-new\">NEW</span></td>");
                    }
                    else if (entry.FormattedText == previousText)
                    {
                        sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-same\">SAME</span></td>");
                    }
                    else
                    {
                        sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-changed\">CHANGED</span></td>");
                    }

                    previousText = entry.FormattedText;
                }
                else if (sparseDumps.Contains(dumpIdx))
                {
                    sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-sparse\">SPARSE</span></td>");
                }
                else
                {
                    if (previousText != null)
                    {
                        var badgeText = dumps[dumpIdx].IsDmp ? "NOT PRESENT" : "REMOVED";
                        sb.Append(
                            $"<td class=\"{colClass}\"><span class=\"badge badge-removed\">{badgeText}</span></td>");
                    }
                    else
                    {
                        sb.Append($"<td class=\"{colClass}\"><span class=\"badge badge-absent\">&mdash;</span></td>");
                    }
                }
            }

            sb.AppendLine("</tr>");

            // --- Detail row (hidden by default, content compressed) ---
            // Pre-compute all column diffs, then pad to uniform line count for vertical alignment
            var columnDiffs = new string[dumps.Count];
            previousText = null;
            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                if (dumpMap.TryGetValue(dumpIdx, out var entry))
                {
                    columnDiffs[dumpIdx] = RenderDiffedText(entry.FormattedText, previousText,
                        dumps[dumpIdx].IsBase);
                    previousText = entry.FormattedText;
                }
                else if (sparseDumps.Contains(dumpIdx))
                {
                    // Sparse dump — don't update previousText for diff continuity
                    columnDiffs[dumpIdx] =
                        "<span class=\"line-sparse\">(sparse dump \u2014 type not loaded)</span>";
                }
                else
                {
                    var detailMsg = dumps[dumpIdx].IsDmp ? "(not present in this dump)" : "(removed)";
                    columnDiffs[dumpIdx] = previousText != null
                        ? $"<span class=\"line-removed\">{detailMsg}</span>"
                        : "";
                }
            }

            // Pad all columns to the same line count so text aligns vertically
            var maxLineCount = columnDiffs.Max(d => d.Split('\n').Length);
            for (var i = 0; i < columnDiffs.Length; i++)
            {
                var lineCount = columnDiffs[i].Split('\n').Length;
                if (lineCount < maxLineCount)
                    columnDiffs[i] += new string('\n', maxLineCount - lineCount);
            }

            sb.Append("      <tr class=\"detail-row\" style=\"display:none\">");
            sb.Append("<td class=\"col-editor\"></td><td class=\"col-name\"></td>");
            if (hasCoords)
                sb.Append("<td class=\"col-coords\"></td>");
            sb.Append("<td class=\"col-formid\"></td>");
            for (var dumpIdx = 0; dumpIdx < dumps.Count; dumpIdx++)
            {
                var colClass = $"build-col-{dumpIdx}";
                // Visibility controlled by #build-col-style dynamic stylesheet

                if (string.IsNullOrEmpty(columnDiffs[dumpIdx]))
                {
                    sb.Append($"<td class=\"{colClass}\"></td>");
                }
                else
                {
                    var compressed = CompressToBase64(
                        $"<pre class=\"record-text\">{columnDiffs[dumpIdx]}</pre>");
                    sb.Append($"<td class=\"{colClass}\" data-z=\"{compressed}\"></td>");
                }
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");
    }

    /// <summary>
    ///     Render a record's text with per-line diff highlighting compared to the previous dump's text.
    ///     Uses LCS (longest common subsequence) to correctly handle inserted/removed sections
    ///     without false CHANGED markers on shifted-but-identical lines.
    /// </summary>
    private static string RenderDiffedText(string currentText, string? previousText, bool isBase = false)
    {
        var currentLines = currentText.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var sb = new StringBuilder();

        if (previousText == null)
        {
            // First appearance — base build lines are unhighlighted, others are green (new)
            for (var i = 0; i < currentLines.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                if (isBase)
                    sb.Append(Esc(currentLines[i]));
                else
                    sb.Append($"<span class=\"line-new\">{Esc(currentLines[i])}</span>");
            }

            return sb.ToString();
        }

        var previousLines = previousText.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Pre-process: tag placed-object sub-lines with their parent's FormID
        // so the LCS matches them to the correct object block instead of false-matching
        // duplicate lines (e.g., "model: Marker_Map.NIF") across different objects.
        var taggedCurrent = TagPlacedObjectLines(currentLines);
        var taggedPrevious = TagPlacedObjectLines(previousLines);

        // Compute LCS-based diff on tagged lines, then strip tags for display
        var diff = ComputeDiff(taggedPrevious, taggedCurrent);
        var first = true;
        foreach (var (tag, tagged) in diff)
        {
            if (!first) sb.Append('\n');
            first = false;

            // Strip the FormID tag prefix for display
            var line = StripPlacedObjectTag(tagged);

            switch (tag)
            {
                case DiffTag.Equal:
                    sb.Append(Esc(line));
                    break;
                case DiffTag.Added:
                    sb.Append($"<span class=\"line-new\">{Esc(line)}</span>");
                    break;
                case DiffTag.Removed:
                    sb.Append($"<span class=\"line-removed\">{Esc(line)}</span>");
                    break;
                case DiffTag.Changed:
                    sb.Append($"<span class=\"line-changed\">{Esc(line)}</span>");
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Tag placed-object sub-lines with their parent's FormID so the LCS
    ///     matches them to the correct block. Lines like "model: X.nif" that appear
    ///     under multiple objects become unique per-object.
    /// </summary>
    private static string[] TagPlacedObjectLines(string[] lines)
    {
        var result = new string[lines.Length];
        string? currentFormId = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            // Detect placed object header: "- ObjectName (TYPE) [0xFormID]"
            if (trimmed.StartsWith("- ", StringComparison.Ordinal) && trimmed.Contains("[0x"))
            {
                var bracketStart = trimmed.IndexOf("[0x", StringComparison.Ordinal);
                var bracketEnd = trimmed.IndexOf(']', bracketStart);
                if (bracketEnd > bracketStart)
                {
                    currentFormId = trimmed.Substring(bracketStart, bracketEnd - bracketStart + 1);
                }

                result[i] = line;
            }
            else if (currentFormId != null && line.Length > 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                // Sub-line of a placed object — tag it
                result[i] = $"{PlacedObjectTagPrefix}{currentFormId}{line}";
            }
            else
            {
                // Section header or other non-sub-line — reset context
                if (!string.IsNullOrWhiteSpace(line) && !line.StartsWith("  ", StringComparison.Ordinal))
                    currentFormId = null;
                result[i] = line;
            }
        }

        return result;
    }

    private static string StripPlacedObjectTag(string line)
    {
        if (!line.StartsWith(PlacedObjectTagPrefix, StringComparison.Ordinal))
            return line;

        // Find the end of the tag: "\x01#[0xFormID]"
        var closeBracket = line.IndexOf(']', PlacedObjectTagPrefix.Length);
        return closeBracket >= 0 ? line[(closeBracket + 1)..] : line;
    }

    /// <summary>
    ///     Compute a line-level diff between two string arrays using LCS.
    ///     Produces Equal/Added/Removed entries. Consecutive Remove+Add blocks
    ///     are merged into Changed entries.
    /// </summary>
    private static List<(DiffTag Tag, string Line)> ComputeDiff(string[] oldLines, string[] newLines)
    {
        var oldLen = oldLines.Length;
        var newLen = newLines.Length;

        // Build LCS length table
        var lcs = new int[oldLen + 1, newLen + 1];
        for (var i = oldLen - 1; i >= 0; i--)
        {
            for (var j = newLen - 1; j >= 0; j--)
            {
                if (oldLines[i] == newLines[j])
                    lcs[i, j] = lcs[i + 1, j + 1] + 1;
                else
                    lcs[i, j] = Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        // Walk the table to produce raw diff (Remove/Add/Equal)
        var raw = new List<(DiffTag Tag, string Line)>();
        int oi = 0, ni = 0;
        while (oi < oldLen && ni < newLen)
        {
            if (oldLines[oi] == newLines[ni])
            {
                raw.Add((DiffTag.Equal, newLines[ni]));
                oi++;
                ni++;
            }
            else if (lcs[oi + 1, ni] >= lcs[oi, ni + 1])
            {
                raw.Add((DiffTag.Removed, oldLines[oi]));
                oi++;
            }
            else
            {
                raw.Add((DiffTag.Added, newLines[ni]));
                ni++;
            }
        }

        while (oi < oldLen)
        {
            raw.Add((DiffTag.Removed, oldLines[oi]));
            oi++;
        }

        while (ni < newLen)
        {
            raw.Add((DiffTag.Added, newLines[ni]));
            ni++;
        }

        // Post-process: merge blocks of consecutive Removed+Added into Changed pairs
        var result = new List<(DiffTag Tag, string Line)>();
        var ri = 0;
        while (ri < raw.Count)
        {
            if (raw[ri].Tag == DiffTag.Removed)
            {
                // Collect consecutive Removed entries
                var removed = new List<string>();
                while (ri < raw.Count && raw[ri].Tag == DiffTag.Removed)
                {
                    removed.Add(raw[ri].Line);
                    ri++;
                }

                // Collect consecutive Added entries that follow
                var added = new List<string>();
                while (ri < raw.Count && raw[ri].Tag == DiffTag.Added)
                {
                    added.Add(raw[ri].Line);
                    ri++;
                }

                // Pair them 1:1 as Changed; remainder stays as Removed or Added
                var paired = Math.Min(removed.Count, added.Count);
                for (var p = 0; p < paired; p++)
                    result.Add((DiffTag.Changed, added[p]));
                for (var p = paired; p < removed.Count; p++)
                    result.Add((DiffTag.Removed, removed[p]));
                for (var p = paired; p < added.Count; p++)
                    result.Add((DiffTag.Added, added[p]));
            }
            else
            {
                result.Add(raw[ri]);
                ri++;
            }
        }

        return result;
    }

    private static string GenerateIndexPage(CrossDumpRecordIndex index)
    {
        var sb = new StringBuilder();
        AppendHtmlHeader(sb, "Cross-Build Comparison Index");

        sb.AppendLine("  <h1>Cross-Build Comparison Index</h1>");
        sb.AppendLine(
            $"  <p class=\"summary\">{index.Dumps.Count} builds, {index.Records.Values.Sum(m => m.Count):N0} total records</p>");

        // Build info table
        sb.AppendLine("  <h2>Builds</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead><tr><th>#</th><th>File</th><th>Build Date</th></tr></thead>");
        sb.AppendLine("    <tbody>");
        for (var i = 0; i < index.Dumps.Count; i++)
        {
            var d = index.Dumps[i];
            var displayFileName = d.IsBase ? d.ShortName : d.FileName;
            var displayDate = d.IsBase ? "(base)" : $"{d.FileDate:yyyy-MM-dd HH:mm}";
            sb.AppendLine(
                $"      <tr><td>{i + 1}</td><td>{Esc(displayFileName)}</td><td>{displayDate}</td></tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        // Record type summary with links
        sb.AppendLine("  <h2>Record Types</h2>");
        sb.AppendLine("  <table class=\"compact\">");
        sb.AppendLine("    <thead>");
        sb.AppendLine("      <tr><th>Type</th><th>Records</th>");
        foreach (var d in index.Dumps)
            sb.AppendLine($"        <th>{Esc(d.ShortName)}</th>");
        sb.AppendLine("      </tr>");
        sb.AppendLine("    </thead>");
        sb.AppendLine("    <tbody>");

        foreach (var (recordType, formIdMap) in index.Records.OrderBy(r => r.Key))
        {
            var filename = $"compare_{recordType.ToLowerInvariant()}.html";
            sb.Append($"      <tr><td><a href=\"{filename}\">{Esc(recordType)}</a></td>");
            sb.Append($"<td>{formIdMap.Count:N0}</td>");
            for (var dumpIdx = 0; dumpIdx < index.Dumps.Count; dumpIdx++)
            {
                var count = formIdMap.Values.Count(dm => dm.ContainsKey(dumpIdx));
                sb.Append($"<td>{count:N0}</td>");
            }

            sb.AppendLine("</tr>");
        }

        sb.AppendLine("    </tbody>");
        sb.AppendLine("  </table>");

        AppendHtmlFooter(sb);
        return sb.ToString();
    }

    private static void AppendHtmlHeader(StringBuilder sb, string title)
    {
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head>");
        sb.AppendLine("  <meta charset=\"utf-8\">");
        sb.AppendLine($"  <title>{Esc(title)}</title>");
        sb.AppendLine("  <style>");
        sb.AppendLine(CssStyles);
        sb.AppendLine("  </style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");
    }

    private static void AppendHtmlFooter(StringBuilder sb)
    {
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");
    }

    /// <summary>
    ///     Compress an HTML string with Deflate and return as base64.
    ///     Decompressed in-browser via native DecompressionStream API on first expand.
    /// </summary>
    private static string CompressToBase64(string html)
    {
        var raw = Encoding.UTF8.GetBytes(html);
        using var ms = new MemoryStream();
        using (var deflate = new DeflateStream(ms, CompressionLevel.Optimal, true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    private static string Esc(string text)
    {
        return HttpUtility.HtmlEncode(text);
    }

    private enum DiffTag
    {
        Equal,
        Added,
        Removed,
        Changed
    }
}
