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

                                       function readChunkScriptPayload(el) {
                                         var inlinePayload = el.getAttribute('data-z') || '';
                                         if (inlinePayload) return inlinePayload;
                                         var externalKey = el.getAttribute('data-external-key') || el.id || '';
                                         var externalChunks = window.__comparisonExternalChunks || {};
                                         if (externalKey && externalChunks[externalKey]) {
                                           return externalChunks[externalKey];
                                         }
                                         return '';
                                       }

                                       function releaseChunkScriptPayload(el) {
                                         el.removeAttribute('data-z');
                                         var externalKey = el.getAttribute('data-external-key') || el.id || '';
                                         if (externalKey && window.__comparisonExternalChunks) {
                                           delete window.__comparisonExternalChunks[externalKey];
                                         }
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
                                           document.addEventListener('toggle', function(ev) {
                                             if (!ev.target || !ev.target.classList
                                                 || !ev.target.classList.contains('rd-field-disclosure')) {
                                               return;
                                             }

                                             var detailRow = ev.target.closest('tr.detail-row');
                                             if (!detailRow) return;
                                             var slotCount = parseInt(detailRow.dataset.slotCount || '0');
                                             if (slotCount > 0) {
                                               requestAnimationFrame(function() {
                                                 alignDetailSlots(detailRow, slotCount);
                                               });
                                             }
                                           }, true);
                                           document.getElementById('loading').style.display = 'none';
                                           await navigateToHashRecord();
                                           window.addEventListener('hashchange', function() {
                                             navigateToHashRecord();
                                           });
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

                                       function normalizeFormIdKey(value) {
                                         if (value === null || value === undefined) return '';
                                         var s = String(value).trim();
                                         var m = s.match(/0x[0-9a-fA-F]{8}/);
                                         if (m) return '0x' + m[0].substring(2).toUpperCase();
                                         if (/^[0-9a-fA-F]{8}$/.test(s)) return '0x' + s.toUpperCase();
                                         return '';
                                       }

                                       function recordDomId(formId) {
                                         var key = normalizeFormIdKey(formId) || String(formId || '');
                                         return 'record-' + key.replace(/^0x/i, '').toUpperCase();
                                       }

                                       function recordLinkTargetExists(formId) {
                                         var key = normalizeFormIdKey(formId);
                                         if (!key) return false;
                                         return (DATA.records && DATA.records.hasOwnProperty(key))
                                           || (DATA.groups && DATA.groups.hasOwnProperty(key));
                                       }

                                       function renderFormIdValue(val) {
                                         if (!val) return '';
                                         var text = val.display || val.raw || '';
                                         var key = normalizeFormIdKey(val.raw || text);
                                         if (DATA && DATA.recordType === 'Cell' && recordLinkTargetExists(key)) {
                                           return '<a href="#' + recordDomId(key) + '" onclick="navigateToRecord(\''
                                             + key + '\'); return false;">' + esc(text) + '</a>';
                                         }
                                         return esc(text);
                                       }

                                       function renderCellReferenceValue(val) {
                                         if (!val) return '';
                                         if (DATA && DATA.recordType === 'Cell') return renderFormIdValue(val);
                                         return renderCellPageLink(val);
                                       }

                                       function renderCellPageLink(val) {
                                         if (!val) return '';
                                         var text = val.display || val.raw || '';
                                         var key = normalizeFormIdKey(val.raw || text);
                                         if (!key) return esc(text);
                                         return '<a href="compare_cell.html#' + recordDomId(key) + '">'
                                           + esc(text) + '</a>';
                                       }

                                       function renderFieldValueForContext(sectionName, fieldKey, val) {
                                         if (shouldRenderCellPageLink(sectionName, fieldKey, val)) {
                                           return renderCellReferenceValue(val);
                                         }
                                         if (shouldRenderTextContent(sectionName, fieldKey, val)) {
                                           return renderTextContentValue(val);
                                         }
                                         return renderFieldValue(val);
                                       }

                                       function shouldRenderTextContent(sectionName, fieldKey, val) {
                                         if (!val || val.type !== 'string') return false;
                                         if (sectionName === 'Content' && fieldKey === 'Text') return true;
                                         if (sectionName === 'Description' && fieldKey === 'Text') return true;
                                         return false;
                                       }

                                       function renderTextContentValue(val) {
                                         if (!val) return '';
                                         return '<div class="rd-text">' + esc(val.raw || val.display || '') + '</div>';
                                       }

                                       function shouldRenderCellPageLink(sectionName, fieldKey, val) {
                                         if (!val || val.type !== 'formId') return false;
                                         if (!fieldKey) return false;
                                         return fieldKey === 'Cell' || fieldKey === 'Containing Cell';
                                       }

                                       function hashRecordKey() {
                                         var hash = window.location.hash || '';
                                         if (!hash) return '';
                                         var m = hash.match(/^#record-([0-9a-fA-F]{8})$/);
                                         if (m) return '0x' + m[1].toUpperCase();
                                         return normalizeFormIdKey(hash.substring(1));
                                       }

                                       async function navigateToHashRecord() {
                                         var key = hashRecordKey();
                                         if (key) await navigateToRecord(key);
                                       }

                                       async function navigateToRecord(formId) {
                                         var key = normalizeFormIdKey(formId);
                                         if (!key) return;

                                         var row = document.getElementById(recordDomId(key));
                                         if (!row && DATA.chunked && DATA.groups) {
                                           var groupName = DATA.groups[key];
                                           var header = findGroupHeaderForRecordGroup(groupName);
                                           if (header) {
                                             await ensureChunkedGroupVisible(header);
                                             row = document.getElementById(recordDomId(key));
                                           }
                                         }

                                         if (!row) return;
                                         row.scrollIntoView({ block: 'center', behavior: 'smooth' });
                                         if (!row.classList.contains('expanded')) {
                                           toggleDetail(row);
                                         }
                                         row.classList.add('nav-flash');
                                         setTimeout(function() { row.classList.remove('nav-flash'); }, 1800);
                                       }

                                       function findGroupHeaderForRecordGroup(groupName) {
                                         if (!groupName) return null;
                                         var headers = document.querySelectorAll('.group-header[data-group]');
                                         for (var i = 0; i < headers.length; i++) {
                                           if (headers[i].getAttribute('data-group') === groupName) return headers[i];
                                         }
                                         return null;
                                       }

                                       async function ensureChunkedGroupVisible(header) {
                                         if (!header) return;
                                         var content = header.nextElementSibling;
                                         if (!content) return;
                                         if (!content.dataset.loaded) {
                                           await toggleGroupChunked(header);
                                           return;
                                         }
                                         if (content.style.display === 'none') {
                                           content.style.display = '';
                                           header.textContent = header.textContent.replace('\u25B6', '\u25BC');
                                           requestAnimationFrame(alignRenderedDetailRows);
                                         }
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

                                         // Default record order: named records first by Editor ID, then
                                         // cells by numeric grid position, then Form ID.
                                         var formIds = Object.keys(records).sort(function(a, b) {
                                           return compareRecordsForDefaultOrder(
                                             a, records[a], b, records[b], hasCoords, gridCoords);
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
                                             var meta = rec.metadata || {};
                                             // Name change display
                                             editorIdDisplay = esc(editorId);
                                             var editorHistory = visibleNameHistory(rec.editorIdHistory);
                                             if (editorHistory.length > 1) {
                                               editorIdDisplay = editorHistory.map(esc)
                                                 .join('<br><span class="name-change">\u21B3 </span>');
                                             }
                                             nameDisplay = esc(displayName);
                                             var nameHistory = visibleNameHistory(rec.nameHistory);
                                             if (nameHistory.length > 1) {
                                               nameDisplay = nameHistory.map(esc)
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
                                           summaryRow.id = recordDomId(formId);
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
                                             var statusCellPrefix = '<td class="' + colClass
                                               + ' status-cell" data-dump-idx="' + di + '" data-status="';
                                             if (present.has(di)) {
                                               var snapshotKey = resolvedSnapshots[di] !== null
                                                 ? resolvedSnapshots[di].key : null;
                                               if (previousSnapshotKey === null) {
                                                 if (dumps[di].isBase || di === 0)
                                                   rowHtml += statusCellPrefix + 'BASE'
                                                     + '"><span class="badge badge-base">BASE</span></td>';
                                                 else
                                                   rowHtml += statusCellPrefix + 'NEW'
                                                     + '"><span class="badge badge-new">NEW</span></td>';
                                               } else if (snapshotKey === previousSnapshotKey) {
                                                 rowHtml += statusCellPrefix + 'SAME'
                                                   + '"><span class="badge badge-same">SAME</span></td>';
                                               } else {
                                                 rowHtml += statusCellPrefix + 'CHANGED'
                                                   + '"><span class="badge badge-changed">CHANGED</span></td>';
                                               }
                                               previousSnapshotKey = snapshotKey;
                                             } else {
                                               if (previousSnapshotKey !== null) {
                                                 var badgeText = dumps[di].isDmp ? 'NOT PRESENT' : 'REMOVED';
                                                 rowHtml += statusCellPrefix + badgeText
                                                   + '"><span class="badge badge-removed">' + badgeText + '</span></td>';
                                               } else {
                                                 rowHtml += statusCellPrefix + '\u2014'
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
                                             var virtualAudit = getMetadataDumpValue(
                                               rec.metadata, 'upgradedVirtualFormIdsByDump', di);
                                             if (virtualAudit) {
                                               html = '<div class="rd-audit">Virtual cell aligned from '
                                                 + esc(virtualAudit) + '</div>' + html;
                                             }
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
                                         detailRow.dataset.slotCount = String(template.length);
                                         alignDetailSlots(detailRow, template.length);
                                         requestAnimationFrame(function() {
                                           alignDetailSlots(detailRow, template.length);
                                         });
                                       }

                                       // Equalize the rendered height of each template slot across all visible
                                       // build columns. Two passes (clear → measure → write) avoid feedback loops
                                       // from earlier writes affecting later measurements.
                                       function alignDetailSlots(detailRow, slotCount) {
                                         if (detailRow.style.display === 'none') return;
                                         var allDetailDivs = detailRow.querySelectorAll('td .record-detail');
                                         var detailDivs = [];
                                         for (var di = 0; di < allDetailDivs.length; di++) {
                                           if (allDetailDivs[di].offsetParent !== null) {
                                             detailDivs.push(allDetailDivs[di]);
                                           }
                                         }
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
                                           var reportSectionNames = [];
                                           for (var rsi = 0; rsi < report.sections.length; rsi++) {
                                             reportSectionNames.push(canonicalSectionName(report.sections[rsi].name));
                                           }
                                           for (var si = 0; si < report.sections.length; si++) {
                                             var sec = report.sections[si];
                                             var secName = reportSectionNames[si];
                                             if (!sectionSeen.has(secName)) {
                                               sectionSeen.add(secName);
                                               insertSectionInReportOrder(sectionOrder, reportSectionNames, si);
                                               sectionFields[secName] = [];
                                             }
                                             var existing = new Set(sectionFields[secName]);
                                             for (var fi = 0; fi < sec.fields.length; fi++) {
                                               if (!existing.has(sec.fields[fi].key)) {
                                                 existing.add(sec.fields[fi].key);
                                                 sectionFields[secName].push(sec.fields[fi].key);
                                               }
                                             }
                                           }
                                         }

                                         sectionOrder = orderDetailSections(sectionOrder);

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

                                       function canonicalSectionName(name) {
                                         var text = String(name || '').trim();
                                         // Report builders historically included counts in section titles
                                         // ("Inventory (4)", "Variables (12)", "Contents (3 items)").
                                         // Treat those as the same logical section so item-level diffs
                                         // and cross-column vertical alignment stay stable.
                                         return text.replace(/\s+\((?:\d+(?:\s+\w+)?|\d+\/\d+\s+\w+)\)$/i, '');
                                       }

                                       function insertSectionInReportOrder(sectionOrder, reportSectionNames, sectionIndex) {
                                         var sectionName = reportSectionNames[sectionIndex];
                                         var insertAt = -1;
                                         var prevAt = -1;

                                         for (var ni = sectionIndex + 1; ni < reportSectionNames.length; ni++) {
                                           var nextAt = sectionOrder.indexOf(reportSectionNames[ni]);
                                           if (nextAt >= 0) {
                                             insertAt = nextAt;
                                             break;
                                           }
                                         }

                                         for (var pi = sectionIndex - 1; pi >= 0; pi--) {
                                           prevAt = sectionOrder.indexOf(reportSectionNames[pi]);
                                           if (prevAt >= 0) break;
                                         }

                                         if (insertAt >= 0 && prevAt >= 0 && insertAt <= prevAt) {
                                           insertAt = prevAt + 1;
                                         } else if (insertAt < 0 && prevAt >= 0) {
                                           insertAt = prevAt + 1;
                                         } else if (insertAt < 0) {
                                           insertAt = sectionOrder.length;
                                         }

                                         sectionOrder.splice(insertAt, 0, sectionName);
                                       }

                                       function orderDetailSections(sectionOrder) {
                                         var originalIndex = {};
                                         for (var i = 0; i < sectionOrder.length; i++) {
                                           originalIndex[sectionOrder[i]] = i;
                                         }

                                         return sectionOrder.slice().sort(function(a, b) {
                                           var ar = detailSectionRank(a);
                                           var br = detailSectionRank(b);
                                           if (ar !== br) return ar - br;
                                           return originalIndex[a] - originalIndex[b];
                                         });
                                       }

                                       function detailSectionRank(name) {
                                         if (name === 'Identity') return 0;
                                         if (name === 'Environment') return 10;
                                         if (name === 'Heightmap') return 20;
                                         if (name === 'Door Links') return 30;
                                         if (name === 'Placed Objects') return 40;
                                         return 1000;
                                       }

                                       function getMetadataDumpValue(metadata, key, dumpIdx) {
                                         if (!metadata || !metadata[key]) return '';
                                         var prefix = dumpIdx + ':';
                                         var entries = metadata[key].split(';');
                                         for (var i = 0; i < entries.length; i++) {
                                           var entry = entries[i].trim();
                                           if (entry.indexOf(prefix) === 0) {
                                             return entry.substring(prefix.length);
                                           }
                                         }
                                         return '';
                                       }

                                       function visibleNameHistory(history) {
                                         if (!history || !history.length) return [];
                                         var seen = {};
                                         var result = [];
                                         for (var i = 0; i < history.length; i++) {
                                           var value = history[i] === null || history[i] === undefined
                                             ? '' : String(history[i]).trim();
                                           if (!value || isSyntheticVirtualLabel(value) || seen[value]) continue;
                                           seen[value] = true;
                                           result.push(value);
                                         }
                                         return result;
                                       }

                                       function isSyntheticVirtualLabel(value) {
                                         return /^\[?Virtual\s/i.test(value || '');
                                       }

                                       function compareRecordsForDefaultOrder(formIdA, recA,
                                                                              formIdB, recB,
                                                                              hasCoords, gridCoords) {
                                         var cmp = compareEditorValues(
                                           recA ? recA.editorId : '',
                                           recB ? recB.editorId : '',
                                           true);
                                         if (cmp !== 0) return cmp;
                                         if (hasCoords) {
                                           cmp = compareCoordinateValues(
                                             gridCoords ? gridCoords[formIdA] : null,
                                             gridCoords ? gridCoords[formIdB] : null);
                                           if (cmp !== 0) return cmp;
                                         }
                                         return compareFormIdText(formIdA, formIdB);
                                       }

                                       function compareEditorValues(a, b, asc) {
                                         var va = normalizeSortText(a);
                                         var vb = normalizeSortText(b);
                                         var aBlank = va.length === 0;
                                         var bBlank = vb.length === 0;
                                         if (aBlank !== bBlank) return aBlank ? 1 : -1;
                                         var cmp = va < vb ? -1 : va > vb ? 1 : 0;
                                         return asc ? cmp : -cmp;
                                       }

                                       function normalizeSortText(value) {
                                         return (value === null || value === undefined)
                                           ? '' : String(value).trim().toLowerCase();
                                       }

                                       function compareCoordinateValues(aCoords, bCoords) {
                                         var ax = aCoords && aCoords.length >= 2 ? parseInt(aCoords[0]) : NaN;
                                         var ay = aCoords && aCoords.length >= 2 ? parseInt(aCoords[1]) : NaN;
                                         var bx = bCoords && bCoords.length >= 2 ? parseInt(bCoords[0]) : NaN;
                                         var by = bCoords && bCoords.length >= 2 ? parseInt(bCoords[1]) : NaN;
                                         var aHasCoords = !isNaN(ax) && !isNaN(ay);
                                         var bHasCoords = !isNaN(bx) && !isNaN(by);
                                         if (aHasCoords !== bHasCoords) return aHasCoords ? -1 : 1;
                                         if (!aHasCoords && !bHasCoords) return 0;
                                         return compareCoordinateNumbers(ax, ay, bx, by);
                                       }

                                       function compareCoordinateNumbers(ax, ay, bx, by) {
                                         // Row-major map order: north/top first, then west/left to east/right.
                                         if (ay !== by) return by - ay;
                                         return ax - bx;
                                       }

                                       function compareFormIdText(a, b) {
                                         return a < b ? -1 : a > b ? 1 : 0;
                                       }

                                       // Render a single column against the unified template
                                       function renderAligned(current, previous, isBase, template, keyWidths) {
                                         if (!current || !current.sections) return '';

                                         // Index current report: section -> {key -> field}
                                         var curIndex = {};
                                         var curSections = new Set();
                                         for (var si = 0; si < current.sections.length; si++) {
                                           var sec = current.sections[si];
                                           var secName = canonicalSectionName(sec.name);
                                           curSections.add(secName);
                                           curIndex[secName] = {};
                                           for (var fi = 0; fi < sec.fields.length; fi++)
                                             curIndex[secName][sec.fields[fi].key] = sec.fields[fi];
                                         }

                                         // Index previous report
                                         var prevIndex = {};
                                         var prevSections = new Set();
                                         if (previous && previous.sections) {
                                           for (var si = 0; si < previous.sections.length; si++) {
                                             var ps = previous.sections[si];
                                             var psName = canonicalSectionName(ps.name);
                                             prevSections.add(psName);
                                             prevIndex[psName] = {};
                                             for (var fi = 0; fi < ps.fields.length; fi++)
                                               prevIndex[psName][ps.fields[fi].key] = ps.fields[fi];
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
                                            diffCls = ' style="visibility:hidden"'; valHtml = '\u2014';
                                           } else if (!curField && prevField) {
                                             diffCls = ' class="field-removed"';
                                             valHtml = renderFieldValueForContext(slot.section, slot.key, prevField.value);
                                           } else if (curField && previous === null) {
                                             if (!isBase) diffCls = ' class="field-new"';
                                             valHtml = renderFieldValueForContext(slot.section, slot.key, curField.value);
                                           } else if (curField && !prevField) {
                                             diffCls = ' class="field-new"';
                                             valHtml = renderFieldValueForContext(slot.section, slot.key, curField.value);
                                           } else if (valuesEqual(curField.value, prevField.value)) {
                                             valHtml = renderFieldValueForContext(slot.section, slot.key, curField.value);
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
                                             valHtml = renderFieldValueForContext(slot.section, slot.key, curField.value);
                                           }

                                           var valueForDisclosure = curField ? curField.value
                                             : (prevField ? prevField.value : null);
                                           valHtml = maybeWrapCollapsedField(slot.section, slot.key,
                                             valueForDisclosure, valHtml);

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
                                               + '<div class="rd-key">' + esc(displayFieldKey(slot.key)) + '</div>'
                                               + '<div class="rd-val">' + valHtml + '</div></div>';
                                           }
                                         }
                                         // Close last section
                                         html += '</div>';

                                         return html;
                                       }

                                       function displayFieldKey(key) {
                                         return key === 'FormID' ? 'Form ID' : key;
                                       }

                                       function maybeWrapCollapsedField(sectionName, fieldKey, value, html) {
                                         if (!shouldCollapseFieldByDefault(sectionName, fieldKey, value)) {
                                           return html;
                                         }

                                         var label = value && value.display ? value.display : 'show details';
                                         return '<details class="rd-field-disclosure"><summary>'
                                           + esc(label) + '</summary><div class="rd-field-disclosure-body">'
                                           + html + '</div></details>';
                                       }

                                       function shouldCollapseFieldByDefault(sectionName, fieldKey, value) {
                                         if (sectionName !== 'FaceGen Morph Data') return false;
                                         if (!/Controls$/i.test(fieldKey || '')) return false;
                                         return value && value.type === 'list'
                                           && value.items && value.items.length > 0;
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
                                         if (val.type === 'formId') return renderFormIdValue(val);
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
                                            + '<div class="rd-key">' + esc(displayFieldKey(val.fields[i].key)) + '</div>'
                                            + '<div class="rd-val">' + renderFieldValue(val.fields[i].value) + '</div>'
                                            + '</div>';
                                         }
                                         return h;
                                       }

                                       function renderCompositeInline(val) {
                                         if (!val || !val.fields) return esc(val ? (val.display || '') : '');
                                         if (looksLikePlacedObjectComposite(val)) return renderPlacedObjectInline(val);
                                         var fieldMap = buildCompositeFieldMap(val);
                                         if (fieldMap.Control && fieldMap.Value) return renderCompositeHtml(val);
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
                                         var linksToVal = fieldMap['Links to'];
                                         var destinationDoorVal = fieldMap['Destination Door'];
                                         var containingCellVal = fieldMap['Containing Cell'];
                                         var worldspaceVal = fieldMap.Worldspace;
                                         var cellVal = fieldMap.Cell;
                                         var gridText = compositeFieldText(fieldMap, 'Grid');

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
                                         if (containingCellVal) {
                                           details.push('in ' + renderCellReferenceValue(containingCellVal));
                                         }
                                         if (worldspaceVal) {
                                           details.push('worldspace: ' + renderFormIdValue(worldspaceVal));
                                         }
                                         if (cellVal) {
                                           details.push('cell: ' + renderCellReferenceValue(cellVal));
                                         }
                                         if (gridText) {
                                           details.push('grid: ' + esc(gridText));
                                         }
                                         if (linksToVal) {
                                           details.push('links to: ' + renderFormIdValue(linksToVal));
                                         }
                                         if (destinationDoorVal) {
                                           details.push('destination door: ' + renderFormIdValue(destinationDoorVal));
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
                                          var prevIndexByKey = {};
                                          for (var i = 0; i < prevItems.length; i++)
                                            prevIndexByKey[listItemKey(prevItems[i])] = i;

                                          var consumedPrev = {};
                                          var prevCursor = 0;
                                          var h = '';
                                          for (var i = 0; i < curItems.length; i++) {
                                            var curItem = curItems[i];
                                            var key = listItemKey(curItem);
                                            var matchIndex = prevIndexByKey.hasOwnProperty(key)
                                              ? prevIndexByKey[key] : -1;
                                            if (matchIndex >= prevCursor) {
                                              h += renderRemovedListItemsUntil(prevItems, curKeys,
                                                consumedPrev, prevCursor, matchIndex);
                                              prevCursor = matchIndex + 1;
                                            }
                                            h += renderCurrentListItem(curItem,
                                              prevByKey.hasOwnProperty(key) ? prevByKey[key] : null);
                                            if (matchIndex >= 0) consumedPrev[key] = true;
                                          }
                                          h += renderRemovedListItemsUntil(prevItems, curKeys,
                                            consumedPrev, prevCursor, prevItems.length);
                                          return h;
                                       }

                                       function renderCurrentListItem(curItem, prevItem) {
                                         if (prevItem === null) {
                                           var newText = curItem.type === 'composite'
                                             ? renderCompositeInline(curItem)
                                             : esc(curItem.display || curItem.raw || '');
                                           return '<div class="rd-list-item field-new">' + newText + '</div>';
                                         }
                                         if (valuesEqual(curItem, prevItem)) {
                                           var sameText = curItem.type === 'composite'
                                             ? renderCompositeInline(curItem)
                                             : esc(curItem.display || curItem.raw || '');
                                           return '<div class="rd-list-item">' + sameText + '</div>';
                                         }
                                         var diffText;
                                         if (curItem.type === 'composite' && prevItem.type === 'composite') {
                                           diffText = renderCompositeInlineDiff(curItem, prevItem);
                                         } else {
                                           diffText = '<span class="field-changed">'
                                             + esc(curItem.display || curItem.raw || '') + '</span>';
                                         }
                                         return '<div class="rd-list-item field-changed">' + diffText + '</div>';
                                       }

                                       function renderRemovedListItemsUntil(prevItems, curKeys,
                                                                            consumedPrev, start, end) {
                                         var h = '';
                                         for (var i = start; i < end; i++) {
                                           var pkey = listItemKey(prevItems[i]);
                                           if (curKeys[pkey] || consumedPrev[pkey]) continue;
                                           var rmText = prevItems[i].type === 'composite'
                                             ? renderCompositeInline(prevItems[i])
                                             : esc(prevItems[i].display || prevItems[i].raw || '');
                                           h += '<div class="rd-list-item field-removed">' + rmText + '</div>';
                                           consumedPrev[pkey] = true;
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
                                            + '<div class="rd-key">' + esc(displayFieldKey(f.key)) + '</div>'
                                            + '<div class="rd-val">' + inner + '</div>'
                                            + '</div>';
                                         }
                                         // Sub-fields that were present in prev but removed in cur.
                                         if (prevVal && prevVal.fields) {
                                           for (var pi = 0; pi < prevVal.fields.length; pi++) {
                                             var pf = prevVal.fields[pi];
                                             if (prevSeen[pf.key]) continue;
                                             h += '<div class="rd-field field-removed">'
                                              + '<div class="rd-key">' + esc(displayFieldKey(pf.key)) + '</div>'
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
                                         function formPiece(key) {
                                           var curValue = curMap[key];
                                           if (!curValue) return '';
                                           var html = (key === 'Cell' || key === 'Containing Cell')
                                             ? renderCellReferenceValue(curValue)
                                             : renderFormIdValue(curValue);
                                           var prevValue = prevMap[key];
                                           if (prevValue && scalarKeyText(curValue) === scalarKeyText(prevValue)) {
                                             return html;
                                           }
                                           return '<span class="field-changed">' + html + '</span>';
                                         }

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
                                         var containingCellHtml = formPiece('Containing Cell');
                                         if (containingCellHtml) {
                                           details.push('in ' + containingCellHtml);
                                         }
                                         var worldspaceHtml = formPiece('Worldspace');
                                         if (worldspaceHtml) {
                                           details.push('worldspace: ' + worldspaceHtml);
                                         }
                                         var cellHtml = formPiece('Cell');
                                         if (cellHtml) {
                                           details.push('cell: ' + cellHtml);
                                         }
                                         var gridHtml = piece('Grid');
                                         if (gridHtml) {
                                           details.push('grid: ' + gridHtml);
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
                                         var fieldMap = buildCompositeFieldMap(curVal);
                                         if (fieldMap.Control && fieldMap.Value) {
                                           return renderCompositeDiffHtml(curVal, prevVal);
                                         }
                                         return renderGenericCompositeInlineDiff(curVal, prevVal);
                                       }

                                       function renderGenericCompositeInlineDiff(curVal, prevVal) {
                                         // Inventory, faction, script-reference, and similar list entries
                                         // should remain one row even when one subfield changes. The list
                                         // item wrapper carries the changed highlight.
                                         return esc(curVal.display || '');
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
                                           var fieldMap = buildCompositeFieldMap(item);
                                           if (fieldMap.FormID) {
                                             return 'FormID=' + scalarKeyText(fieldMap.FormID);
                                           }
                                           if (fieldMap.Item) {
                                             return 'Item=' + scalarKeyText(fieldMap.Item);
                                           }
                                           if (fieldMap.Faction) {
                                             return 'Faction=' + scalarKeyText(fieldMap.Faction);
                                           }
                                           if (fieldMap.Control) {
                                             return 'Control=' + scalarKeyText(fieldMap.Control);
                                           }
                                           if (fieldMap.Index && (fieldMap.Text || fieldMap.Log)) {
                                             return 'Index=' + scalarKeyText(fieldMap.Index);
                                           }
                                           if (fieldMap.Base && fieldMap.Type && fieldMap.Position) {
                                             return 'Placed=' + scalarKeyText(fieldMap.Base) + '|'
                                               + scalarKeyText(fieldMap.Type) + '|'
                                               + scalarKeyText(fieldMap.Position);
                                           }
                                           // Use raw values from composite fields for stable matching
                                           var parts = [];
                                           for (var i = 0; i < item.fields.length; i++) {
                                             var v = item.fields[i].value;
                                             parts.push(item.fields[i].key + '=' + scalarKeyText(v));
                                           }
                                           return parts.join('|');
                                         }
                                         return item.raw !== undefined ? String(item.raw) : (item.display || '');
                                       }

                                       function scalarKeyText(value) {
                                         if (!value) return '';
                                         if (value.rawInt !== undefined && value.rawInt !== null) return String(value.rawInt);
                                         if (value.raw !== undefined && value.raw !== null) return String(value.raw);
                                         if (value.display !== undefined && value.display !== null) return String(value.display);
                                         return '';
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
                                             detailRow.style.display = '';
                                             summaryRow.classList.add('expanded');
                                             renderDetail(detailRow);
                                           } else {
                                             detailRow.style.display = 'none';
                                             summaryRow.classList.remove('expanded');
                                           }
                                         }
                                       }

                                       function alignRenderedDetailRows() {
                                         var rows = document.querySelectorAll('.detail-row[data-rendered="1"]');
                                         for (var i = 0; i < rows.length; i++) {
                                           var slotCount = parseInt(rows[i].dataset.slotCount || '0');
                                           if (slotCount > 0) alignDetailSlots(rows[i], slotCount);
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
                                             if (rows[i].previousElementSibling)
                                               rows[i].previousElementSibling.classList.add('expanded');
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
                                         document.querySelectorAll('.summary-row.expanded').forEach(function(r) {
                                           r.classList.remove('expanded');
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
                                           requestAnimationFrame(alignRenderedDetailRows);
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
                                               var rawZ = readChunkScriptPayload(scripts[si]);
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
                                               releaseChunkScriptPayload(chunkScripts[ri].el);
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
                                           requestAnimationFrame(alignRenderedDetailRows);
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
                                           requestAnimationFrame(alignRenderedDetailRows);
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
                                         requestAnimationFrame(alignRenderedDetailRows);
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
                                           var cell = sr.querySelector('td.status-cell[data-dump-idx="' + idx + '"]');
                                           var badge = cell ? cell.querySelector('.badge') : null;
                                           var badgeText = cell
                                             ? (cell.getAttribute('data-status') || (badge ? badge.textContent.trim() : ''))
                                             : '';
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
                                             cmp = compareCoordinateValues(
                                               [a.summary.getAttribute('data-cx'),
                                                a.summary.getAttribute('data-cy')],
                                               [b.summary.getAttribute('data-cx'),
                                                b.summary.getAttribute('data-cy')]);
                                           } else {
                                             var va = a.summary.getAttribute('data-' + col) || '';
                                             var vb = b.summary.getAttribute('data-' + col) || '';
                                             cmp = col === 'editor'
                                               ? compareEditorValues(va, vb, asc)
                                               : compareSortTextValues(va, vb);
                                           }
                                           if (cmp !== 0) return col === 'editor' ? cmp : (asc ? cmp : -cmp);
                                           var coordCmp = compareCoordinateValues(
                                             [a.summary.getAttribute('data-cx'),
                                              a.summary.getAttribute('data-cy')],
                                             [b.summary.getAttribute('data-cx'),
                                              b.summary.getAttribute('data-cy')]);
                                           if (coordCmp !== 0) return coordCmp;
                                           return compareFormIdText(
                                             a.summary.getAttribute('data-formid') || '',
                                             b.summary.getAttribute('data-formid') || '');
                                         });
                                         var frag = document.createDocumentFragment();
                                         pairs.forEach(function(p) {
                                           frag.appendChild(p.summary);
                                           if (p.detail) frag.appendChild(p.detail);
                                         });
                                         tbody.appendChild(frag);
                                       }

                                       function compareSortTextValues(a, b) {
                                         var va = normalizeSortText(a);
                                         var vb = normalizeSortText(b);
                                         return va < vb ? -1 : va > vb ? 1 : 0;
                                       }
                                   """;
}
