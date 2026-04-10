namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>CSS styles for HTML comparison pages (light + dark mode).</summary>
internal static class ComparisonCssStyles
{
    internal const string Styles = """
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

                                       #loading {
                                         padding: 20px;
                                         text-align: center;
                                         color: #888;
                                         font-size: 14px;
                                       }

                                       table { border-collapse: separate; border-spacing: 0; width: 100%; margin: 8px 0; table-layout: auto; }
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
                                       /* Fixed identity columns: size to content. width:1px + white-space:nowrap
                                          collapses to the natural max(header, content) width without wrapping
                                          and without padding the column out wider than necessary. */
                                       .col-editor {
                                         position: sticky;
                                         left: 0;
                                         z-index: 1;
                                         background: #fff;
                                         width: 1px;
                                         white-space: nowrap;
                                       }
                                       th.col-editor { z-index: 5; }
                                       .col-name { width: 1px; white-space: nowrap; }
                                       .col-coords { width: 1px; white-space: nowrap; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
                                       .col-formid { width: 1px; white-space: nowrap; font-family: 'Cascadia Code', 'Fira Code', monospace; font-size: 12px; }
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
                                       .detail-row td { padding: 4px 8px; }
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

                                       .record-detail {
                                         font-family: system-ui, -apple-system, sans-serif;
                                         font-size: 12px;
                                         line-height: 1.4;
                                         margin: 0;
                                         overflow-x: auto;
                                       }
                                       .rd-section { margin-bottom: 4px; }
                                       .rd-section-header {
                                         font-weight: 600;
                                         font-size: 12px;
                                         padding: 2px 0;
                                         border-bottom: 1px solid #444;
                                         margin-bottom: 2px;
                                       }
                                       .rd-field {
                                         display: flex;
                                         gap: 6px;
                                         padding: 1px 0 1px 4px;
                                       }
                                       .rd-key {
                                         flex: 0 0 auto;
                                         min-width: 80px;
                                         color: #aaa;
                                         text-align: left;
                                         font-size: 12px;
                                       }
                                       .rd-val {
                                         flex: 1 1 auto;
                                         word-break: break-word;
                                       }
                                       .rd-list-item {
                                         padding: 1px 0 1px 16px;
                                       }
                                       .rd-list-meta {
                                         color: #999;
                                         display: block;
                                         font-size: 11px;
                                         padding-left: 12px;
                                       }
                                       .rd-list-flag {
                                         color: #c26a00;
                                         font-weight: 600;
                                       }
                                       .rd-code {
                                         font-family: 'Cascadia Code', 'Fira Code', 'Consolas', monospace;
                                         font-size: 11px;
                                         background: #1a1a1a;
                                         border: 1px solid #333;
                                         border-radius: 3px;
                                         padding: 4px 8px;
                                         white-space: pre-wrap;
                                         word-break: break-all;
                                         display: block;
                                         margin: 2px 0;
                                         max-height: 200px;
                                         overflow-y: auto;
                                       }
                                       .field-new { background: #d4edda; }
                                       .field-changed { background: #fff3cd; }
                                       .field-removed { background: #f8d7da; color: #999; text-decoration: line-through; }
                                       .field-sparse { color: #999; font-style: italic; }

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

                                       .build-nav { display: flex; align-items: center; gap: 6px; margin-left: auto; }
                                       .build-nav button { padding: 3px 8px; font-size: 11px; border: 1px solid #ccc; border-radius: 3px; background: #f5f5f5; cursor: pointer; }
                                       .build-nav button:hover { background: #e0e0e0; }
                                       .build-nav button:disabled { opacity: 0.4; cursor: default; }
                                       .build-nav-label { font-size: 12px; color: #666; min-width: 120px; text-align: center; }
                                       .hidden { display: none !important; }

                                       .sub-label { font-size: 0.85em; color: #888; }
                                       .response-text { font-style: italic; }
                                       .response-emotion { font-size: 0.85em; color: #c89b3c; margin-left: 0.5em; }
                                       .group-mode-selector { display: inline-flex; gap: 8px; align-items: center; font-size: 13px; }
                                       .group-mode-selector label { cursor: pointer; white-space: nowrap; }

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
                                         .field-new { background: #1e3a1e; }
                                         .field-changed { background: #3a3520; }
                                         .field-removed { background: #3a1e1e; color: #777; }
                                         .field-sparse { color: #666; }
                                         .group-header { color: #6db3f2; border-bottom-color: #6db3f2; }
                                         .toc { background: #2a2a2a; border-color: #444; }
                                         .build-header:hover { background: #333; }
                                         .build-nav button { background: #333; color: #e0e0e0; border-color: #555; }
                                         .build-nav button:hover { background: #444; }
                                         .build-nav-label { color: #999; }
                                         .sub-label { color: #777; }
                                         .response-emotion { color: #d4a84b; }
                                         .rd-section-header { border-bottom-color: #555; }
                                         .rd-key { color: #999; }
                                         .rd-list-meta { color: #aaa; }
                                         .rd-list-flag { color: #ffb347; }
                                         .rd-code { background: #111; border-color: #333; }
                                       }
                                   """;
}
