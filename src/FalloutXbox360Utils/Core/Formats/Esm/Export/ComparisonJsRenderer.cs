namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>Client-side JavaScript for JSON-driven HTML comparison pages.</summary>
internal static class ComparisonJsRenderer
{
    internal const string Script = """
                                       // --- Diagnostics ---
                                       var DEBUG_ENABLED = /(?:^|[?&#])debug(?:=1|=true)?(?:$|[&#])/i.test(window.location.search || '')
                                         || /(?:^|[?&#])debug(?:=1|=true)?(?:$|[&#])/i.test(window.location.hash || '');
                                       var DEBUG_STATE = { enabled: DEBUG_ENABLED, events: [] };
                                       window.__comparisonDebug = DEBUG_STATE;

                                       function debugLog(eventName, data, force) {
                                         var entry = {
                                           event: eventName,
                                           at: new Date().toISOString(),
                                           data: data || null
                                         };
                                         DEBUG_STATE.events.push(entry);
                                         if (DEBUG_STATE.events.length > 200) {
                                           DEBUG_STATE.events.shift();
                                         }
                                         if (!force && !DEBUG_ENABLED) return;
                                         console.log('[comparison]', eventName, data || '');
                                       }

                                       function findLatestEvent(eventName) {
                                         for (var i = DEBUG_STATE.events.length - 1; i >= 0; i--) {
                                           if (DEBUG_STATE.events[i].event === eventName) {
                                             return DEBUG_STATE.events[i];
                                           }
                                         }
                                         return null;
                                       }

                                       function appendTailBytes(existing, chunk, limit) {
                                         if (chunk.length >= limit) {
                                           return chunk.slice(chunk.length - limit);
                                         }
                                         if (existing.length + chunk.length <= limit) {
                                           var combined = new Uint8Array(existing.length + chunk.length);
                                           combined.set(existing, 0);
                                           combined.set(chunk, existing.length);
                                           return combined;
                                         }
                                         var overflow = existing.length + chunk.length - limit;
                                         var trimmed = existing.slice(Math.min(overflow, existing.length));
                                         var merged = new Uint8Array(trimmed.length + chunk.length);
                                         merged.set(trimmed, 0);
                                         merged.set(chunk, trimmed.length);
                                         return merged;
                                       }

                                       function decodeTailText(bytes) {
                                         try {
                                           return new TextDecoder().decode(bytes);
                                         } catch (err) {
                                           return '[tail decode failed: ' + ((err && err.message) || String(err)) + ']';
                                         }
                                       }

                                       function sanitizeBase64(b64) {
                                         return (b64 || '').replace(/\s+/g, '');
                                       }

                                       function getBase64Metrics(b64) {
                                         var len = b64 ? b64.length : 0;
                                         var pad = len >= 2
                                           ? (b64[len - 1] === '=' ? 1 : 0) + (b64[len - 2] === '=' ? 1 : 0)
                                           : 0;
                                         return {
                                           payloadLength: len,
                                           payloadMod4: len % 4,
                                           padding: pad,
                                           compressedByteLength: len ? ((len * 3 / 4) - pad) : 0
                                         };
                                       }

                                       function setLoadingStatus(text) {
                                         var loading = document.getElementById('loading');
                                         if (loading) {
                                           loading.textContent = text;
                                         }
                                       }

                                       // --- Decompression ---
                                       function decodeBase64Bytes(b64) {
                                         if (!b64) throw new Error('Empty comparison payload');
                                         if ((b64.length % 4) !== 0) {
                                           throw new Error('Invalid base64 length ' + b64.length + ' (mod 4 = ' + (b64.length % 4) + ')');
                                         }
                                         // Decode base64 directly to Uint8Array (no atob, no intermediate string)
                                         var lookup = new Uint8Array(256);
                                         var chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/';
                                         for (var i = 0; i < chars.length; i++) lookup[chars.charCodeAt(i)] = i;
                                         var pad = (b64[b64.length - 1] === '=' ? 1 : 0) + (b64[b64.length - 2] === '=' ? 1 : 0);
                                         var byteLen = (b64.length * 3 / 4) - pad;
                                         var bytes = new Uint8Array(byteLen);
                                         for (var i = 0, j = 0; i < b64.length; i += 4) {
                                           var a = lookup[b64.charCodeAt(i)];
                                           var b = lookup[b64.charCodeAt(i + 1)];
                                           var c = lookup[b64.charCodeAt(i + 2)];
                                           var d = lookup[b64.charCodeAt(i + 3)];
                                           bytes[j++] = (a << 2) | (b >> 4);
                                           if (j < byteLen) bytes[j++] = ((b & 15) << 4) | (c >> 2);
                                           if (j < byteLen) bytes[j++] = ((c & 3) << 6) | d;
                                         }
                                         return bytes;
                                       }

                                       function createInflatedReadable(bytes, format) {
                                         return new Blob([bytes]).stream().pipeThrough(new DecompressionStream(format));
                                       }

                                       async function inflateWithFormat(bytes, format) {
                                         debugLog('inflate-attempt', {
                                           format: format,
                                           compressedByteLength: bytes.length
                                         });
                                         return new Response(createInflatedReadable(bytes, format)).json();
                                       }

                                       async function inspectInflatedBytes(bytes, format) {
                                         var reader = createInflatedReadable(bytes, format).getReader();
                                         var totalBytes = 0;
                                         var chunkCount = 0;
                                         var lastChunkSize = 0;
                                         var tailBytes = new Uint8Array(0);

                                         while (true) {
                                           var result = await reader.read();
                                           if (result.done) break;
                                           var chunk = result.value;
                                           chunkCount++;
                                           lastChunkSize = chunk.length;
                                           totalBytes += chunk.length;
                                           tailBytes = appendTailBytes(tailBytes, chunk, 2048);
                                         }

                                         return {
                                           format: format,
                                           inflatedByteLength: totalBytes,
                                           inflatedChunkCount: chunkCount,
                                           lastChunkSize: lastChunkSize,
                                           tailText: decodeTailText(tailBytes)
                                         };
                                       }

                                       async function inflate(payloadInfo) {
                                         var normalized = sanitizeBase64(payloadInfo.payload);
                                         var metrics = getBase64Metrics(normalized);
                                         setLoadingStatus('Decoding embedded payload...');
                                         var bytes = decodeBase64Bytes(normalized);
                                         debugLog('payload', {
                                           source: payloadInfo.source,
                                           chunkCount: payloadInfo.chunkCount,
                                           firstChunkLength: payloadInfo.firstChunkLength,
                                           lastChunkLength: payloadInfo.lastChunkLength,
                                           payloadLength: metrics.payloadLength,
                                           payloadMod4: metrics.payloadMod4,
                                           compressedByteLength: bytes.length
                                         });

                                         var formats = ['deflate', 'deflate-raw'];
                                         var lastError = null;
                                         for (var fi = 0; fi < formats.length; fi++) {
                                           var format = formats[fi];
                                           try {
                                             setLoadingStatus('Inflating comparison data (' + format + ')...');
                                             var data = await inflateWithFormat(bytes, format);
                                             debugLog('inflate-ok', {
                                               format: format,
                                               recordType: data ? data.recordType : null,
                                               dumpCount: data && data.dumps ? data.dumps.length : 0,
                                               recordCount: data && data.records ? Object.keys(data.records).length : 0
                                             });
                                             return data;
                                           } catch (err) {
                                             lastError = err;
                                             var diagnostic = null;
                                             try {
                                               diagnostic = await inspectInflatedBytes(bytes, format);
                                             } catch (diagErr) {
                                               diagnostic = {
                                                 format: format,
                                                 inspectError: (diagErr && diagErr.message) || String(diagErr)
                                               };
                                             }
                                             debugLog('inflate-failed', {
                                               format: format,
                                               error: (err && err.message) || String(err),
                                               diagnostic: diagnostic
                                             }, true);
                                           }
                                         }

                                         throw lastError || new Error('Failed to inflate record-data payload');
                                       }

                                       // --- Global state ---
                                       var DATA = null;
                                       var _expandCancel = false;
                                       var _pendingBuildSort = null;

                                       function readCompressedPayload() {
                                         var el = document.getElementById('record-data');
                                         if (!el) throw new Error('Missing record-data payload');
                                         var attrPayload = el.getAttribute ? el.getAttribute('data-z') : null;
                                         if (attrPayload) {
                                           return {
                                             payload: attrPayload,
                                             source: 'attribute',
                                             chunkCount: 1,
                                             firstChunkLength: attrPayload.length,
                                             lastChunkLength: attrPayload.length
                                           };
                                         }
                                         var chunks = el.querySelectorAll ? el.querySelectorAll('.record-data-chunk') : [];
                                         if (chunks.length) {
                                           var parts = new Array(chunks.length);
                                           for (var i = 0; i < chunks.length; i++) {
                                             parts[i] = (chunks[i].textContent || '').trim();
                                           }
                                           return {
                                             payload: parts.join(''),
                                             source: 'chunks',
                                             chunkCount: chunks.length,
                                             firstChunkLength: parts[0] ? parts[0].length : 0,
                                             lastChunkLength: parts[parts.length - 1] ? parts[parts.length - 1].length : 0
                                           };
                                         }
                                         var textPayload = (el.textContent || '').trim();
                                         return {
                                           payload: textPayload,
                                           source: 'text',
                                           chunkCount: 1,
                                           firstChunkLength: textPayload.length,
                                           lastChunkLength: textPayload.length
                                         };
                                       }

                                       function showLoadFailure(err) {
                                         DEBUG_STATE.failure = {
                                           error: (err && err.message) || String(err),
                                           stack: err && err.stack ? err.stack : null
                                         };
                                         var summary = buildFailureSummary(err);
                                         DEBUG_STATE.summary = summary;
                                         renderFailureDetails(summary);
                                         console.error('[comparison] load failed', err, summary);
                                       }

                                       function buildFailureSummary(err) {
                                         var payload = findLatestEvent('payload');
                                         var inflateFailure = findLatestEvent('inflate-failed');
                                         var hydrateStart = findLatestEvent('hydrate-start');
                                         return {
                                           href: window.location.href,
                                           error: (err && err.message) || String(err),
                                           stack: err && err.stack ? err.stack : null,
                                           payload: payload ? payload.data : null,
                                           inflateFailure: inflateFailure ? inflateFailure.data : null,
                                           hydrateStart: hydrateStart ? hydrateStart.data : null,
                                           recentEvents: DEBUG_STATE.events.slice(Math.max(0, DEBUG_STATE.events.length - 12))
                                         };
                                       }

                                       function renderFailureDetails(summary) {
                                         var loading = document.getElementById('loading');
                                         if (!loading) {
                                           return;
                                         }

                                         loading.innerHTML = '';

                                         var title = document.createElement('div');
                                         title.textContent = 'Load failed. Diagnostics are shown below.';
                                         loading.appendChild(title);

                                         var hint = document.createElement('div');
                                         hint.textContent = 'You can also inspect window.__comparisonDebug in DevTools.';
                                         loading.appendChild(hint);

                                         var details = document.createElement('details');
                                         details.open = true;

                                         var detailsSummary = document.createElement('summary');
                                         detailsSummary.textContent = 'Comparison load diagnostics';
                                         details.appendChild(detailsSummary);

                                         var pre = document.createElement('pre');
                                         pre.id = 'comparison-debug-output';
                                         pre.style.whiteSpace = 'pre-wrap';
                                         pre.style.wordBreak = 'break-word';
                                         pre.style.maxHeight = '24rem';
                                         pre.style.overflow = 'auto';
                                         pre.textContent = JSON.stringify(summary, null, 2);
                                         details.appendChild(pre);

                                         loading.appendChild(details);
                                       }

                                       // --- Initialization ---
                                       document.addEventListener('DOMContentLoaded', async function() {
                                         debugLog('hydrate-start', { href: window.location.href });
                                         try {
                                           setLoadingStatus('Reading embedded payload...');
                                           var compressed = readCompressedPayload();
                                           DATA = await inflate(compressed);
                                           setLoadingStatus('Rendering comparison table...');
                                           render();
                                           document.getElementById('loading').style.display = 'none';
                                           debugLog('render-complete', {
                                             recordType: DATA.recordType,
                                             dumpCount: DATA.dumps ? DATA.dumps.length : 0,
                                             recordCount: DATA.records ? Object.keys(DATA.records).length : 0
                                           });
                                         } catch (err) {
                                           debugLog('hydrate-failed', {
                                             error: (err && err.message) || String(err)
                                           }, true);
                                           showLoadFailure(err);
                                         }
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
                                         var isDialogue = DATA.recordType === 'Dialogue';
                                         // Resolve groups: groupSets (dual) or groups (single)
                                         var groups;
                                         if (DATA.groupSets) {
                                           var mode = DATA._activeGroupMode || DATA.defaultGroupMode || Object.keys(DATA.groupSets)[0];
                                           groups = DATA.groupSets[mode] || {};
                                         } else {
                                           groups = DATA.groups || {};
                                         }
                                         var gridCoords = DATA.gridCoords || {};
                                         var sparseDumps = new Set(DATA.sparseDumps || []);
                                         var hasCoords = Object.keys(gridCoords).length > 0;

                                         // Wire sparseDumps into the build nav so pagination skips
                                         // builds that have no records of this type, then apply the
                                         // initial window (overrides the static C#-emitted CSS).
                                         var _navEl = document.querySelector('.build-nav');
                                         if (_navEl) {
                                           _navEl._sparseSet = sparseDumps;
                                           applyBuildWindow(0);
                                         }

                                         // Check if we need grouping
                                         var hasGroups = Object.keys(groups).length > 0;

                                         if (DATA.chunked && DATA.groupManifest) {
                                           // Chunked mode: render TOC from manifest, load data on demand
                                           var groupKeys = Object.keys(DATA.groupManifest).sort(function(a, b) {
                                             if (a === 'Interior Cells' && b !== 'Interior Cells') return 1;
                                             if (b === 'Interior Cells' && a !== 'Interior Cells') return -1;
                                             return a < b ? -1 : a > b ? 1 : 0;
                                           });

                                           var tocHtml = '<div class="toc"><strong>Sections:</strong><ul>';
                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/[^a-zA-Z0-9]/g, '_');
                                             var cnt = DATA.groupManifest[gk];
                                             tocHtml += '<li><a href="#' + gid + '" onclick="expandGroup(\'' + gid + '\')">'
                                               + esc(gk) + ' (' + cnt.toLocaleString() + ')</a></li>';
                                           }
                                           tocHtml += '</ul></div>';
                                           container.innerHTML = tocHtml;

                                           // Ensure records dict exists for lazy loading
                                           if (!DATA.records) DATA.records = {};

                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/[^a-zA-Z0-9]/g, '_');
                                             var sectionDiv = document.createElement('div');
                                             sectionDiv.className = 'group-section';
                                             sectionDiv.id = gid;

                                             var cnt = DATA.groupManifest[gk];
                                             var headerEl = document.createElement('h2');
                                             headerEl.className = 'group-header';
                                             headerEl.textContent = '\u25B6 ' + gk + ' (' + cnt.toLocaleString() + ')';
                                             headerEl.setAttribute('data-group', gk);
                                             headerEl.onclick = function() { toggleGroupChunked(this); };
                                             sectionDiv.appendChild(headerEl);

                                             var contentDiv = document.createElement('div');
                                             contentDiv.className = 'group-content';
                                             contentDiv.style.display = 'none';
                                             contentDiv.innerHTML = '<p style="color:#888;padding:8px;">Click to load...</p>';
                                             sectionDiv.appendChild(contentDiv);
                                             container.appendChild(sectionDiv);
                                           }
                                         } else if (hasGroups) {
                                           // Non-chunked grouped mode: all records in memory
                                           var grouped = {};
                                           for (var formId in records) {
                                             var groupKey = groups[formId] || '(Ungrouped)';
                                             if (!grouped[groupKey]) grouped[groupKey] = {};
                                             grouped[groupKey][formId] = records[formId];
                                           }
                                           var groupKeys = Object.keys(grouped).sort(function(a, b) {
                                             if (a === 'Interior Cells' && b !== 'Interior Cells') return 1;
                                             if (b === 'Interior Cells' && a !== 'Interior Cells') return -1;
                                             return a < b ? -1 : a > b ? 1 : 0;
                                           });

                                           var tocHtml = '<div class="toc"><strong>Sections:</strong><ul>';
                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/[^a-zA-Z0-9]/g, '_');
                                             var cnt = Object.keys(grouped[gk]).length;
                                             tocHtml += '<li><a href="#' + gid + '" onclick="expandGroup(\'' + gid + '\')">'
                                               + esc(gk) + ' (' + cnt.toLocaleString() + ')</a></li>';
                                           }
                                           tocHtml += '</ul></div>';
                                           container.innerHTML = tocHtml;

                                           for (var gi = 0; gi < groupKeys.length; gi++) {
                                             var gk = groupKeys[gi];
                                             var gid = 'group-' + gk.replace(/[^a-zA-Z0-9]/g, '_');
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
                                             var tbl = buildTable(grouped[gk], dumps, sparseDumps, hasCoords, gridCoords, isDialogue);
                                             contentDiv.appendChild(tbl);
                                             sectionDiv.appendChild(contentDiv);
                                             container.appendChild(sectionDiv);
                                           }
                                         } else {
                                           var tbl = buildTable(records, dumps, sparseDumps, hasCoords, gridCoords, isDialogue);
                                           container.appendChild(tbl);
                                         }
                                       }

                                       // --- Table builder ---
                                       function buildTable(records, dumps, sparseDumps, hasCoords, gridCoords, isDialogue) {
                                         var table = document.createElement('table');
                                         // Header
                                         var thead = document.createElement('thead');
                                         var headerRow = document.createElement('tr');
                                         if (isDialogue) {
                                           headerRow.innerHTML =
                                             '<th class="col-editor sortable" onclick="sortBy(this,\'editor\')">Quest '
                                             + '<span class="sort-indicator"></span></th>'
                                             + '<th class="col-name sortable" onclick="sortBy(this,\'name\')">Topic '
                                             + '<span class="sort-indicator"></span></th>'
                                             + '<th class="col-formid sortable" onclick="sortBy(this,\'formid\')">Form ID '
                                             + '<span class="sort-indicator"></span></th>';
                                         } else {
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
                                         }
                                         for (var i = 0; i < dumps.length; i++) {
                                           if (sparseDumps.has(i)) continue; // Skip sparse builds entirely
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

                                           // Dialogue-aware column display
                                           var editorIdDisplay, nameDisplay, coordsDisplay, searchData;
                                           if (isDialogue) {
                                             var meta = rec.metadata || {};
                                             var questEid = meta.questEditorId || '';
                                             var questName = meta.questName || '';
                                             var topicEid = meta.topicEditorId || '';
                                             var topicName = meta.topicName || '';
                                             var speakerName = meta.speakerName || '';
                                             editorIdDisplay = esc(questEid);
                                             if (questName) editorIdDisplay += '<br><span class="sub-label">' + esc(questName) + '</span>';
                                             nameDisplay = esc(topicEid);
                                             if (topicName) nameDisplay += '<br><span class="sub-label">' + esc(topicName) + '</span>';
                                             coordsDisplay = '';
                                             editorId = questEid;
                                             displayName = topicEid;
                                             searchData = (formId + ' ' + questEid + ' ' + questName + ' '
                                               + topicEid + ' ' + topicName + ' ' + speakerName + ' ' + (rec.editorId || '')).toLowerCase();
                                           } else {
                                             // Name change display
                                             editorIdDisplay = esc(editorId);
                                             if (rec.editorIdHistory && rec.editorIdHistory.length > 1) {
                                               editorIdDisplay = rec.editorIdHistory.map(esc)
                                                 .join('<br><span class="name-change">\u21B3 </span>');
                                             }
                                             nameDisplay = esc(displayName);
                                             if (rec.nameHistory && rec.nameHistory.length > 1) {
                                               nameDisplay = rec.nameHistory.map(esc)
                                                 .join('<br><span class="name-change">\u21B3 </span>');
                                             }

                                             coordsDisplay = '';
                                             if (hasCoords && gridCoords[formId]) {
                                               coordsDisplay = '(' + gridCoords[formId][0] + ', ' + gridCoords[formId][1] + ')';
                                             }

                                             searchData = (formId + ' ' + editorId + ' ' + displayName + ' ' + coordsDisplay)
                                               .toLowerCase();
                                           }

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

                                           var rowHtml;
                                           if (isDialogue) {
                                             rowHtml =
                                               '<td class="col-editor">' + editorIdDisplay + '</td>'
                                               + '<td class="col-name">' + nameDisplay + '</td>'
                                               + '<td class="col-formid formid">' + formId + '</td>';
                                           } else {
                                             rowHtml =
                                               '<td class="col-editor">' + editorIdDisplay + '</td>'
                                               + '<td class="col-name">' + nameDisplay + '</td>'
                                               + (hasCoords ? '<td class="col-coords">' + esc(coordsDisplay) + '</td>' : '')
                                               + '<td class="col-formid formid">' + formId + '</td>';
                                           }

                                           // Status badges per dump (skip sparse builds entirely)
                                           var previousSnapshotKey = null;
                                           for (var di = 0; di < dumps.length; di++) {
                                             if (sparseDumps.has(di)) continue;
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
                                           var detailHtml;
                                           if (isDialogue) {
                                             detailHtml = '<td class="col-editor"></td><td class="col-name"></td>'
                                               + '<td class="col-formid"></td>';
                                           } else {
                                             detailHtml = '<td class="col-editor"></td><td class="col-name"></td>'
                                               + (hasCoords ? '<td class="col-coords"></td>' : '')
                                               + '<td class="col-formid"></td>';
                                           }
                                           for (var di = 0; di < dumps.length; di++) {
                                             if (sparseDumps.has(di)) continue;
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

                                         // Render each non-sparse column against the unified template
                                         var previousReport = null;
                                         var cellIdx = fixedCols;
                                         for (var di = 0; di < dumps.length; di++) {
                                           if (sparseDumps.has(di)) continue;
                                           var td = cells[cellIdx++];
                                           if (!td) continue;

                                           var report = reports[di];
                                           if (report) {
                                             var isBase = (dumps[di].isBase || di === 0) && previousReport === null;
                                             var html = renderAligned(report, previousReport, isBase,
                                               template, sectionKeyWidths);
                                             td.innerHTML = '<div class="record-detail">' + html + '</div>';
                                             previousReport = report;
                                           } else if (previousReport !== null) {
                                             var msg = dumps[di].isDmp ? '(not present in this dump)' : '(removed)';
                                             td.innerHTML = '<div class="record-detail">'
                                               + '<span class="field-removed">' + msg + '</span></div>';
                                           }
                                         }

                                         // Cross-column slot alignment: equalize each template-slot's height
                                         // so section headers (and individual fields) line up vertically across
                                         // all build columns, even when content heights differ.
                                         alignDetailSlots(detailRow, template.length);
                                       }

                                       // Equalize the rendered height of each template slot across all visible
                                       // build columns. Two passes (clear → measure → write) avoid feedback loops
                                       // from earlier writes affecting later measurements.
                                       function alignDetailSlots(detailRow, slotCount) {
                                         var detailDivs = detailRow.querySelectorAll('td .record-detail');
                                         if (detailDivs.length < 2) return;
                                         var slotLists = [];
                                         for (var i = 0; i < detailDivs.length; i++) {
                                           var list = detailDivs[i].querySelectorAll('[data-slot]');
                                           // Reset any previous alignment so we measure natural heights
                                           for (var j = 0; j < list.length; j++) list[j].style.minHeight = '';
                                           slotLists.push(list);
                                         }
                                         var maxHeights = new Array(slotCount);
                                         for (var k = 0; k < slotCount; k++) maxHeights[k] = 0;
                                         for (var ci = 0; ci < slotLists.length; ci++) {
                                           var slots = slotLists[ci];
                                           for (var si = 0; si < slots.length; si++) {
                                             var idx = parseInt(slots[si].getAttribute('data-slot'));
                                             if (isNaN(idx)) continue;
                                             var h = slots[si].offsetHeight;
                                             if (h > maxHeights[idx]) maxHeights[idx] = h;
                                           }
                                         }
                                         for (var ci2 = 0; ci2 < slotLists.length; ci2++) {
                                           var slots2 = slotLists[ci2];
                                           for (var si2 = 0; si2 < slots2.length; si2++) {
                                             var idx2 = parseInt(slots2[si2].getAttribute('data-slot'));
                                             if (isNaN(idx2)) continue;
                                             if (maxHeights[idx2] > 0) {
                                               slots2[si2].style.minHeight = maxHeights[idx2] + 'px';
                                             }
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

                                         var html = '';
                                         var sectionOpen = false;
                                         for (var ti = 0; ti < template.length; ti++) {
                                           var slot = template[ti];

                                           if (slot.type === 'section') {
                                             if (sectionOpen) html += '</div>'; // close previous section
                                             sectionOpen = true;
                                             var hasSec = curSections.has(slot.section);
                                             var hadSec = prevSections.has(slot.section);
                                             var cls = '';
                                             if (hasSec && previous !== null && !hadSec) cls = ' field-new';
                                             else if (!hasSec && hadSec) cls = ' field-removed';
                                             else if (!hasSec && !hadSec) cls = '" style="visibility:hidden';
                                             html += '<div class="rd-section"><div class="rd-section-header' + cls
                                               + '" data-slot="' + ti + '">'
                                               + esc(slot.section) + '</div>';
                                             continue;
                                           }

                                           // Compute field state. If a section has exactly one field, the
                                           // section header already labels it — drop the per-field key so
                                           // the value isn't pushed right by an unnecessary "Foo:" column.
                                           var sectionFieldCount = template.filter(function(s) {
                                             return s.type === 'field' && s.section === slot.section; }).length;
                                           var isRedundantKey = sectionFieldCount === 1;

                                           var curField = curIndex[slot.section]
                                             ? curIndex[slot.section][slot.key] : null;
                                           var prevField = prevIndex[slot.section]
                                             ? prevIndex[slot.section][slot.key] : null;

                                           var diffCls = '';
                                           var valHtml = '';
                                           if (!curField && !prevField) {
                                             diffCls = ' style="color:#555"'; valHtml = '\u2014';
                                           } else if (!curField && prevField) {
                                             diffCls = ' class="field-removed"';
                                             valHtml = renderFieldValue(prevField.value);
                                           } else if (curField && previous === null) {
                                             if (!isBase) diffCls = ' class="field-new"';
                                             valHtml = renderFieldValue(curField.value);
                                           } else if (curField && !prevField) {
                                             diffCls = ' class="field-new"';
                                             valHtml = renderFieldValue(curField.value);
                                           } else if (valuesEqual(curField.value, prevField.value)) {
                                             valHtml = renderFieldValue(curField.value);
                                           } else if (curField.value && curField.value.type === 'list'
                                                      && prevField.value && prevField.value.type === 'list') {
                                             valHtml = renderListDiffHtml(curField.value, prevField.value);
                                           } else if (curField.value && curField.value.type === 'composite'
                                                      && prevField.value && prevField.value.type === 'composite') {
                                             // Recurse into composite fields so we mark only the sub-fields
                                             // that actually changed (e.g. Position changed but Base stayed).
                                             valHtml = renderCompositeDiffHtml(curField.value, prevField.value);
                                           } else {
                                             diffCls = ' class="field-changed"';
                                             valHtml = renderFieldValue(curField.value);
                                           }

                                           // diffCls is one of: '', ' class="field-..."', or ' style="..."'.
                                           // For the rd-field case the wrapper already has a class attribute,
                                           // so a second one would be ignored — extract any class name from
                                           // diffCls and merge it instead.
                                           var diffClassName = '';
                                           var diffStyleAttr = '';
                                           var mClass = diffCls.match(/class="([^"]*)"/);
                                           if (mClass) diffClassName = ' ' + mClass[1];
                                           var mStyle = diffCls.match(/style="([^"]*)"/);
                                           if (mStyle) diffStyleAttr = ' style="' + mStyle[1] + '"';

                                           if (isRedundantKey) {
                                             html += '<div data-slot="' + ti + '"'
                                               + (diffClassName ? ' class="' + diffClassName.substring(1) + '"' : '')
                                               + diffStyleAttr + '>' + valHtml + '</div>';
                                           } else {
                                             html += '<div class="rd-field' + diffClassName + '" data-slot="' + ti + '"'
                                               + diffStyleAttr + '>'
                                               + '<div class="rd-key">' + esc(slot.key) + '</div>'
                                               + '<div class="rd-val">' + valHtml + '</div></div>';
                                           }
                                         }
                                         // Close last section
                                         html += '</div>';

                                         return html;
                                       }

                                       // Render a field value as HTML (not pre-formatted text)
                                       function renderFieldValue(val) {
                                         if (!val) return '';
                                         if (isMultiLineString(val)) {
                                           // Check if this looks like hex data
                                           var raw = val.raw || '';
                                           if (/^([0-9A-Fa-f]{2}\s)+/.test(raw.trim()))
                                             return '<code class="rd-code">' + esc(raw) + '</code>';
                                           return '<code class="rd-code">' + esc(raw) + '</code>';
                                         }
                                         if (val.type === 'list') return renderListHtml(val);
                                         if (val.type === 'composite') return renderCompositeHtml(val);
                                         return esc(val.display || val.raw || '');
                                       }

                                       function renderListHtml(val) {
                                         if (!val || !val.items || val.items.length === 0) return '(none)';
                                         var h = '';
                                         for (var i = 0; i < val.items.length; i++) {
                                           var item = val.items[i];
                                           if (item.type === 'composite' && item.fields) {
                                             h += '<div class="rd-list-item">' + renderCompositeInline(item) + '</div>';
                                           } else {
                                             h += '<div class="rd-list-item">' + esc(item.display || item.raw || '') + '</div>';
                                           }
                                         }
                                         return h;
                                       }

                                       function renderCompositeHtml(val) {
                                         if (!val || !val.fields) return esc(val ? (val.display || '') : '');
                                         if (looksLikePlacedObjectComposite(val)) return renderPlacedObjectInline(val);
                                         var h = '';
                                         for (var i = 0; i < val.fields.length; i++) {
                                           h += '<div class="rd-field">'
                                             + '<div class="rd-key">' + esc(val.fields[i].key) + '</div>'
                                             + '<div class="rd-val">' + renderFieldValue(val.fields[i].value) + '</div>'
                                             + '</div>';
                                         }
                                         return h;
                                       }

                                       function renderCompositeInline(val) {
                                         if (!val || !val.fields) return esc(val ? (val.display || '') : '');
                                         if (looksLikePlacedObjectComposite(val)) return renderPlacedObjectInline(val);
                                         return esc(val.display || '');
                                       }

                                       function buildCompositeFieldMap(val) {
                                         var map = {};
                                         if (!val || !val.fields) return map;
                                         for (var i = 0; i < val.fields.length; i++) {
                                           map[val.fields[i].key] = val.fields[i].value;
                                         }
                                         return map;
                                       }

                                       function compositeFieldText(fieldMap, key) {
                                         var value = fieldMap[key];
                                         if (!value) return '';
                                         if (value.display !== undefined && value.display !== null
                                             && value.display !== '') {
                                           return String(value.display);
                                         }
                                         if (value.raw !== undefined && value.raw !== null) {
                                           return String(value.raw);
                                         }
                                         return '';
                                       }

                                       function looksLikePlacedObjectComposite(val) {
                                         var fieldMap = buildCompositeFieldMap(val);
                                         return !!fieldMap.FormID && !!fieldMap.Base
                                           && !!fieldMap.Type && !!fieldMap.Position;
                                       }

                                       function renderPlacedObjectInline(val) {
                                         var fieldMap = buildCompositeFieldMap(val);
                                         var header = [];
                                         var baseText = compositeFieldText(fieldMap, 'Base');
                                         var nameText = compositeFieldText(fieldMap, 'Name');
                                         var typeText = compositeFieldText(fieldMap, 'Type');
                                         var formIdText = compositeFieldText(fieldMap, 'FormID');
                                         var positionText = compositeFieldText(fieldMap, 'Position');
                                         var rotationText = compositeFieldText(fieldMap, 'Rotation');
                                         var scaleText = compositeFieldText(fieldMap, 'Scale');
                                         var disabledText = compositeFieldText(fieldMap, 'Disabled');

                                         if (baseText) header.push(esc(baseText));
                                         if (nameText) header.push('"' + esc(nameText) + '"');
                                         if (typeText) header.push('(' + esc(typeText) + ')');
                                         if (formIdText) header.push('[' + esc(formIdText) + ']');
                                         if (disabledText && disabledText !== 'No' && disabledText !== 'False') {
                                           header.push('<span class="rd-list-flag">[DISABLED]</span>');
                                         }

                                         var details = [];
                                         if (positionText) details.push('at ' + esc(positionText));
                                         if (rotationText) details.push('rot=' + esc(rotationText));
                                         if (scaleText && scaleText !== '1' && scaleText !== '1.0'
                                             && scaleText !== '1.00') {
                                           details.push('scale=' + esc(scaleText));
                                         }

                                         var h = header.length > 0 ? header.join(' ') : esc(val.display || '');
                                         if (details.length > 0) {
                                           h += '<span class="rd-list-meta">' + details.join('  ') + '</span>';
                                         }
                                         return h;
                                       }

                                       function renderListDiffHtml(curVal, prevVal) {
                                         var curItems = (curVal && curVal.items) ? curVal.items : [];
                                         var prevItems = (prevVal && prevVal.items) ? prevVal.items : [];
                                         var prevByKey = {};
                                         for (var i = 0; i < prevItems.length; i++)
                                           prevByKey[listItemKey(prevItems[i])] = prevItems[i];
                                         var curKeys = {};
                                         for (var i = 0; i < curItems.length; i++)
                                           curKeys[listItemKey(curItems[i])] = true;
                                         var h = '';
                                         for (var i = 0; i < curItems.length; i++) {
                                           var curItem = curItems[i];
                                           var key = listItemKey(curItem);
                                           var prevItem = prevByKey.hasOwnProperty(key) ? prevByKey[key] : null;
                                           if (prevItem === null) {
                                             // Item is new in this build.
                                             var newText = curItem.type === 'composite'
                                               ? renderCompositeInline(curItem)
                                               : esc(curItem.display || curItem.raw || '');
                                             h += '<div class="rd-list-item field-new">' + newText + '</div>';
                                           } else if (valuesEqual(curItem, prevItem)) {
                                             // Item unchanged.
                                             var sameText = curItem.type === 'composite'
                                               ? renderCompositeInline(curItem)
                                               : esc(curItem.display || curItem.raw || '');
                                             h += '<div class="rd-list-item">' + sameText + '</div>';
                                           } else {
                                             // Same identity, different content — mark the item changed
                                             // and render its body via the diff-aware path so the
                                             // specific sub-fields that differ get yellow.
                                             var diffText;
                                             if (curItem.type === 'composite' && prevItem.type === 'composite') {
                                               diffText = renderCompositeInlineDiff(curItem, prevItem);
                                             } else {
                                               diffText = '<span class="field-changed">'
                                                 + esc(curItem.display || curItem.raw || '') + '</span>';
                                             }
                                             h += '<div class="rd-list-item field-changed">' + diffText + '</div>';
                                           }
                                         }
                                         for (var i = 0; i < prevItems.length; i++) {
                                           var pkey = listItemKey(prevItems[i]);
                                           if (curKeys[pkey]) continue;
                                           var rmText = prevItems[i].type === 'composite'
                                             ? renderCompositeInline(prevItems[i])
                                             : esc(prevItems[i].display || prevItems[i].raw || '');
                                           h += '<div class="rd-list-item field-removed">' + rmText + '</div>';
                                         }
                                         return h;
                                       }

                                       // --- Diff-aware value rendering ---
                                       // Render a value using the previous-build value as baseline so that
                                       // changed sub-pieces can be highlighted in place. Mirrors the
                                       // dispatch table of renderFieldValue.
                                       function renderFieldValueDiff(curVal, prevVal) {
                                         if (!curVal) return '';
                                         if (!prevVal) return renderFieldValue(curVal);
                                         if (valuesEqual(curVal, prevVal)) return renderFieldValue(curVal);
                                         if (curVal.type === 'list' && prevVal.type === 'list')
                                           return renderListDiffHtml(curVal, prevVal);
                                         if (curVal.type === 'composite' && prevVal.type === 'composite')
                                           return renderCompositeDiffHtml(curVal, prevVal);
                                         // Scalar / mismatched-type leaf — wrap the new value in a yellow span.
                                         return '<span class="field-changed">'
                                           + (renderFieldValue(curVal) || '') + '</span>';
                                       }

                                       // Diff two composite values, marking only sub-fields that changed.
                                       function renderCompositeDiffHtml(curVal, prevVal) {
                                         if (!curVal || !curVal.fields) return renderFieldValue(curVal);
                                         if (looksLikePlacedObjectComposite(curVal)) {
                                           return renderPlacedObjectInlineDiff(curVal, prevVal);
                                         }
                                         var prevMap = buildCompositeFieldMap(prevVal);
                                         var prevSeen = {};
                                         var h = '';
                                         for (var i = 0; i < curVal.fields.length; i++) {
                                           var f = curVal.fields[i];
                                           var prevField = prevMap.hasOwnProperty(f.key) ? prevMap[f.key] : null;
                                           if (prevField !== null) prevSeen[f.key] = true;
                                           var cls = '';
                                           var inner;
                                           if (prevField === null) {
                                             cls = ' field-new';
                                             inner = renderFieldValue(f.value);
                                           } else if (valuesEqual(f.value, prevField)) {
                                             inner = renderFieldValue(f.value);
                                           } else {
                                             cls = ' field-changed';
                                             inner = renderFieldValueDiff(f.value, prevField);
                                           }
                                           h += '<div class="rd-field' + cls + '">'
                                             + '<div class="rd-key">' + esc(f.key) + '</div>'
                                             + '<div class="rd-val">' + inner + '</div>'
                                             + '</div>';
                                         }
                                         // Sub-fields that were present in prev but removed in cur.
                                         if (prevVal && prevVal.fields) {
                                           for (var pi = 0; pi < prevVal.fields.length; pi++) {
                                             var pf = prevVal.fields[pi];
                                             if (prevSeen[pf.key]) continue;
                                             h += '<div class="rd-field field-removed">'
                                               + '<div class="rd-key">' + esc(pf.key) + '</div>'
                                               + '<div class="rd-val">' + renderFieldValue(pf.value) + '</div>'
                                               + '</div>';
                                           }
                                         }
                                         return h;
                                       }

                                       // Diff a placed-object composite by comparing its key text pieces and
                                       // wrapping each piece that differs in a field-changed span. Layout
                                       // matches renderPlacedObjectInline so the row stays compact.
                                       function renderPlacedObjectInlineDiff(curVal, prevVal) {
                                         var curMap = buildCompositeFieldMap(curVal);
                                         var prevMap = prevVal && prevVal.fields
                                           ? buildCompositeFieldMap(prevVal) : {};
                                         function piece(key) {
                                           var curText = compositeFieldText(curMap, key);
                                           if (!curText) return '';
                                           var prevText = compositeFieldText(prevMap, key);
                                           if (prevText && curText === prevText) return esc(curText);
                                           return '<span class="field-changed">' + esc(curText) + '</span>';
                                         }
                                         var baseHtml = piece('Base');
                                         var nameHtml = piece('Name');
                                         var typeHtml = piece('Type');
                                         var formIdHtml = piece('FormID');
                                         var positionHtml = piece('Position');
                                         var rotationHtml = piece('Rotation');
                                         var scaleHtml = piece('Scale');
                                         var disabledText = compositeFieldText(curMap, 'Disabled');
                                         var prevDisabled = compositeFieldText(prevMap, 'Disabled');

                                         var header = [];
                                         if (baseHtml) header.push(baseHtml);
                                         if (nameHtml) header.push('"' + nameHtml + '"');
                                         if (typeHtml) header.push('(' + typeHtml + ')');
                                         if (formIdHtml) header.push('[' + formIdHtml + ']');
                                         if (disabledText && disabledText !== 'No' && disabledText !== 'False') {
                                           var dCls = (disabledText !== prevDisabled) ? ' field-changed' : '';
                                           header.push('<span class="rd-list-flag' + dCls + '">[DISABLED]</span>');
                                         }

                                         var details = [];
                                         if (positionHtml) details.push('at ' + positionHtml);
                                         if (rotationHtml) details.push('rot=' + rotationHtml);
                                         var scaleText = compositeFieldText(curMap, 'Scale');
                                         if (scaleText && scaleText !== '1' && scaleText !== '1.0'
                                             && scaleText !== '1.00') {
                                           details.push('scale=' + scaleHtml);
                                         }

                                         var h = header.length > 0 ? header.join(' ') : esc(curVal.display || '');
                                         if (details.length > 0) {
                                           h += '<span class="rd-list-meta">' + details.join('  ') + '</span>';
                                         }
                                         return h;
                                       }

                                       // Inline form of renderCompositeDiffHtml — used inside list items so
                                       // the body stays single-line where possible (placed objects path).
                                       // For non-placed-object composites we currently just diff sub-fields
                                       // the same way; the wrapping list-item class handles the highlight.
                                       function renderCompositeInlineDiff(curVal, prevVal) {
                                         if (!curVal || !curVal.fields) return esc(curVal ? (curVal.display || '') : '');
                                         if (looksLikePlacedObjectComposite(curVal)) {
                                           return renderPlacedObjectInlineDiff(curVal, prevVal);
                                         }
                                         // Generic composite — fall back to the structured diff renderer.
                                         return renderCompositeDiffHtml(curVal, prevVal);
                                       }

                                       // Check if a value is multi-line text (script source, decompiled, etc.)
                                       function isMultiLineString(val) {
                                         return val && val.type === 'string' && val.raw && val.raw.indexOf('\n') !== -1;
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

                                       // --- Group mode switching (dialogue: Quest / NPC) ---
                                       function switchGroupMode(mode) {
                                         DATA._activeGroupMode = mode;
                                         var container = document.getElementById('tables-container');
                                         container.innerHTML = '';
                                         render();
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
                                       // Chunked group toggle: loads chunk data on first expand
                                       async function toggleGroupChunked(header) {
                                         var content = header.nextElementSibling;
                                         if (content.style.display !== 'none') {
                                           content.style.display = 'none';
                                           header.textContent = header.textContent.replace('\u25BC', '\u25B6');
                                           return;
                                         }

                                         // Load chunk data if not yet loaded
                                         if (!content.dataset.loaded) {
                                           content.innerHTML = '<p style="color:#888;padding:8px;">Loading...</p>';
                                           content.style.display = '';
                                           header.textContent = header.textContent.replace('\u25B6', '\u25BC');

                                           var groupName = header.getAttribute('data-group');
                                           // Collect all chunk scripts for this group
                                           var scripts = document.querySelectorAll('script[data-group]');
                                           var chunkScripts = [];
                                           for (var si = 0; si < scripts.length; si++) {
                                             var sg = scripts[si].getAttribute('data-group') || '';
                                             // Match exact group name OR group name with " (part N)" suffix
                                             if (sg === groupName || sg.indexOf(groupName + ' (part ') === 0) {
                                               var rawZ = scripts[si].getAttribute('data-z') || '';
                                               var b64 = sanitizeBase64(rawZ);
                                               if (b64 && b64.length >= 4) {
                                                 chunkScripts.push({ el: scripts[si], b64: b64 });
                                               }
                                             }
                                           }
                                           if (chunkScripts.length === 0) {
                                             content.innerHTML = '<p style="color:#888;padding:8px;">No data chunks found for this group.</p>';
                                             return;
                                           }

                                           // Load all chunks in parallel
                                           content.innerHTML = '<p style="color:#888;padding:8px;">Loading ' + chunkScripts.length + ' chunk(s)...</p>';
                                           var chunkRecords = {};
                                           try {
                                             var promises = chunkScripts.map(function(cs) {
                                               return inflate({
                                                 payload: cs.b64,
                                                 source: 'chunk-' + groupName,
                                                 chunkCount: 1,
                                                 firstChunkLength: cs.b64.length,
                                                 lastChunkLength: cs.b64.length
                                               });
                                             });
                                             var results = await Promise.all(promises);
                                             for (var ri = 0; ri < results.length; ri++) {
                                               var parsed = results[ri];
                                               for (var fid in parsed) {
                                                 chunkRecords[fid] = parsed[fid];
                                                 DATA.records[fid] = parsed[fid];
                                               }
                                               // Free script tag memory
                                               chunkScripts[ri].el.removeAttribute('data-z');
                                             }
                                           } catch (e) {
                                             console.error('Failed to load chunks for', groupName, e);
                                             content.innerHTML = '<p style="color:red;padding:8px;">Failed to load: ' + (e.message || e) + '</p>';
                                             return;
                                           }

                                           // Build the table from loaded records
                                           var dumps = DATA.dumps;
                                           var sparseDumps = new Set(DATA.sparseDumps || []);
                                           var gridCoords = DATA.gridCoords || {};
                                           var hasCoords = Object.keys(gridCoords).length > 0;
                                           var isDialogue = DATA.recordType === 'Dialogue';
                                           content.innerHTML = '';
                                           var tbl = buildTable(chunkRecords, dumps, sparseDumps, hasCoords, gridCoords, isDialogue);
                                           content.appendChild(tbl);
                                           content.dataset.loaded = '1';
                                           if (false) { // cleanup already done above
                                           }
                                         } else {
                                           content.style.display = '';
                                           header.textContent = header.textContent.replace('\u25B6', '\u25BC');
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
                                       // Navigation operates over the *visible* (non-sparse) build indices
                                       // for the current record type. Sparse builds (those with no records of
                                       // this type) are always hidden and never count toward window position.
                                       function _visibleBuildIndices(nav) {
                                         var total = parseInt(nav.dataset.totalRaw || nav.dataset.total);
                                         var sparse = nav._sparseSet || new Set();
                                         var out = [];
                                         for (var i = 0; i < total; i++) if (!sparse.has(i)) out.push(i);
                                         return { total: total, visible: out };
                                       }

                                       function applyBuildWindow(start) {
                                         var nav = document.querySelector('.build-nav');
                                         if (!nav) return;
                                         if (!nav.dataset.totalRaw) nav.dataset.totalRaw = nav.dataset.total;
                                         var info = _visibleBuildIndices(nav);
                                         var total = info.total;
                                         var visible = info.visible;
                                         var visTotal = visible.length;
                                         var size = parseInt(nav.dataset.size);
                                         if (size > visTotal) size = visTotal;
                                         if (start == null) start = parseInt(nav.dataset.start) || 0;
                                         if (start > Math.max(0, visTotal - size)) start = Math.max(0, visTotal - size);
                                         if (start < 0) start = 0;
                                         nav.dataset.start = start;

                                         var visibleNow = new Set();
                                         var end = Math.min(start + size, visTotal);
                                         for (var k = start; k < end; k++) visibleNow.add(visible[k]);

                                         var styleEl = document.getElementById('build-col-style');
                                         if (!styleEl) {
                                           styleEl = document.createElement('style');
                                           styleEl.id = 'build-col-style';
                                           document.head.appendChild(styleEl);
                                         }
                                         var rules = '';
                                         for (var i = 0; i < total; i++) {
                                           if (!visibleNow.has(i)) {
                                             rules += '.build-col-' + i + '{display:none !important}';
                                           }
                                         }
                                         styleEl.textContent = rules;

                                         var label = nav.querySelector('.build-nav-label');
                                         if (label) {
                                           if (visTotal === 0) {
                                             label.textContent = 'No builds with records';
                                           } else {
                                             label.textContent = 'Builds ' + (start + 1) + '\u2013'
                                               + end + ' of ' + visTotal;
                                           }
                                         }

                                         var btns = nav.querySelectorAll('button');
                                         if (btns.length >= 6) {
                                           var atStart = start === 0 || visTotal === 0;
                                           var atEnd = visTotal === 0 || end >= visTotal;
                                           btns[0].disabled = atStart;
                                           btns[1].disabled = atStart;
                                           btns[2].disabled = atStart;
                                           btns[3].disabled = atEnd;
                                           btns[4].disabled = atEnd;
                                           btns[5].disabled = atEnd;
                                         }
                                       }

                                       function navBuilds(dir) {
                                         var nav = document.querySelector('.build-nav');
                                         if (!nav) return;
                                         var info = _visibleBuildIndices(nav);
                                         var visTotal = info.visible.length;
                                         var size = parseInt(nav.dataset.size);
                                         if (size > visTotal) size = visTotal;
                                         var start = parseInt(nav.dataset.start) || 0;
                                         var maxStart = Math.max(0, visTotal - size);
                                         if (dir === 'first') start = 0;
                                         else if (dir === 'prev3') start = Math.max(0, start - size);
                                         else if (dir === 'prev1') start = Math.max(0, start - 1);
                                         else if (dir === 'next1') start = Math.min(maxStart, start + 1);
                                         else if (dir === 'next3') start = Math.min(maxStart, start + size);
                                         else if (dir === 'last') start = maxStart;
                                         applyBuildWindow(start);
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
