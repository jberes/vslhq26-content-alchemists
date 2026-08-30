// ApexTree interop for the campaign content hierarchy. The Razor component owns the data
// and actions; this module owns only rendering, pan/zoom, and translating a node click back
// into the typed .NET callbacks.

import ApexTree from 'apextree';

const palette = {
    accent: 'var(--cm-accent)',
    accentStrong: 'var(--cm-accent-strong)',
    onAccent: 'var(--cm-on-accent)',
    surface: 'var(--cm-surface-raised)',
    surfaceHover: 'var(--cm-surface)',
    ink: 'var(--cm-on-surface)',
    muted: 'var(--cm-on-surface-muted)',
    rule: 'var(--cm-rule-strong)',
    success: 'var(--cm-success)',
};

/**
 * Convert the small semantic payload from Blazor into ApexTree's rich card model.
 * No user-authored value is interpolated into HTML: the built-in node card renders it.
 */
function toApexNode(node) {
    const accent = node.tone === 'success'
        ? palette.success
        : node.tone === 'gap'
            ? palette.rule
            : palette.accent;
    const badgeColor = node.tone === 'success'
        ? palette.success
        : palette.accentStrong;

    return {
        id: node.id,
        name: node.name,
        data: {
            name: node.name,
            title: node.title,
            subtitle: node.subtitle,
            action: node.action,
            value: node.value,
            accentColor: accent,
            badge: node.badge ? { text: node.badge, color: badgeColor } : undefined,
        },
        options: {
            nodeBGColor: node.tone === 'gap' ? 'transparent' : palette.surface,
            nodeBGColorHover: palette.surfaceHover,
            borderColor: accent,
            borderColorHover: palette.accentStrong,
            borderStyle: node.tone === 'gap' ? 'dashed' : 'solid',
            fontColor: palette.ink,
        },
        children: (node.children ?? []).map(toApexNode),
    };
}

function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;',
    })[character]);
}

function nodeCard(content) {
    const badge = content.badge?.text
        ? `<span style="align-self:flex-start;flex-shrink:0;font-size:0.72em;padding:3px 7px;border-radius:999px;background:${escapeHtml(content.badge.color)};color:${palette.onAccent};font-weight:700;">${escapeHtml(content.badge.text)}</span>`
        : '';
    const title = content.title
        ? `<div style="font-size:0.85em;color:${palette.muted};line-height:1.25;margin-top:2px;">${escapeHtml(content.title)}</div>`
        : '';
    const subtitle = content.subtitle
        ? `<div style="font-size:0.78em;color:${palette.muted};line-height:1.25;margin-top:1px;">${escapeHtml(content.subtitle)}</div>`
        : '';

    return `<div style="display:flex;align-items:stretch;height:100%;box-sizing:border-box;text-align:left;overflow:hidden;">
        <span aria-hidden="true" style="flex-shrink:0;align-self:stretch;width:4px;background:${escapeHtml(content.accentColor)};"></span>
        <div style="display:flex;align-items:center;gap:10px;flex:1;min-width:0;padding:10px 12px;">
            <div style="min-width:0;flex:1;overflow:hidden;">
                <div style="font-weight:600;line-height:1.25;">${escapeHtml(content.name)}</div>
                ${title}
                ${subtitle}
            </div>
            ${badge}
        </div>
    </div>`;
}

/**
 * @param {HTMLElement} element
 * @param {object} data root hierarchy node
 * @param {{invokeMethodAsync: (name: string, ...args: unknown[]) => Promise<unknown>}} dotnet
 */
export function initContentClusterTree(element, data, dotnet) {
    if (!(element instanceof HTMLElement)) {
        throw new TypeError('castmill-apextree: element must be an HTMLElement');
    }

    const configuredKey = globalThis.CASTMILL_APEX_LICENSE_KEY;
    if (typeof configuredKey === 'string' && configuredKey.length > 0) {
        ApexTree.setLicense(configuredKey);
    }

    const tree = new ApexTree(element, {
        width: '100%',
        height: '100%',
        direction: 'top',
        contentKey: 'data',
        nodeTemplate: nodeCard,
        nodeWidth: 218,
        nodeHeight: 108,
        siblingSpacing: 28,
        childrenSpacing: 72,
        // A pillar fans out to ~a dozen supporting pieces; spread horizontally they render
        // as one unreadably wide row. Grouping stacks leaf nodes vertically under the
        // pillar instead (the apextree "group leaf nodes" layout).
        groupLeafNodes: true,
        groupLeafNodesSpacing: 14,
        paddingX: 44,
        paddingY: 54,
        edgeStyle: 'curved',
        edgeWidth: 2,
        edgeColor: palette.rule,
        edgeColorHover: palette.accent,
        borderRadius: '8px',
        borderWidth: 2,
        fontColor: palette.ink,
        fontFamily: 'inherit',
        fontSize: '14px',
        highlightOnHover: true,
        enableAnimation: !globalThis.matchMedia?.('(prefers-reduced-motion: reduce)').matches,
        enableExpandCollapse: true,
        enableExpandCollapseZoom: true,
        enableToolbar: true,
        enableZoomPan: true,
        enableSearch: countNodes(data) > 10,
        enableSelection: 'single',
        a11y: {
            enabled: true,
            label: 'Campaign content hierarchy',
        },
        onNodeClick: node => {
            const action = node?.data?.action;
            const value = node?.data?.value;
            if (action && action !== 'none' && value) {
                void dotnet.invokeMethodAsync('HandleNodeAsync', action, value);
            }
        },
    });

    let graph = tree.render(toApexNode(data));

    return {
        update(next) {
            graph = tree.render(toApexNode(next));
        },
        destroy() {
            tree.destroy();
            element.replaceChildren();
        },
    };
}

function countNodes(node) {
    return 1 + (node.children ?? []).reduce((total, child) => total + countNodes(child), 0);
}
