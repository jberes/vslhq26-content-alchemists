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
            badge: node.badge ? { text: node.badge, color: accent } : undefined,
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
        // Sized to content: with grouped leaves the cluster is tall and narrow, and a
        // fixed viewport forced it behind zoom/pan. At natural size the page scrolls
        // and every card is readable at 1:1.
        height: 'auto',
        direction: 'top',
        contentKey: 'data',
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

    // A 100%-width svg with a narrow-tall viewBox (the grouped-leaf layout) upscales to
    // fill the container — giant cards. Pin the svg to its natural content size instead,
    // shrinking only when the cluster is wider than the container, never enlarging.
    const sizeToContent = () => requestAnimationFrame(() => {
        const svg = element.querySelector('svg');
        const viewBox = svg?.getAttribute('viewBox')?.split(' ').map(Number);
        if (!svg || !viewBox || viewBox.length !== 4 || !viewBox[2] || !viewBox[3]) {
            return;
        }
        const scale = Math.min(1, (element.clientWidth || viewBox[2]) / viewBox[2]);
        svg.setAttribute('width', Math.round(viewBox[2] * scale));
        svg.setAttribute('height', Math.round(viewBox[3] * scale));
        svg.style.display = 'block';
        svg.style.margin = '0 auto';
    });

    tree.render(toApexNode(data));
    sizeToContent();

    return {
        update(next) {
            tree.render(toApexNode(next));
            sizeToContent();
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
