// Selection bubble menu.
//
// @tiptap/extension-bubble-menu has been a declared dependency since the bundle was created
// and was never imported — story 5.1 lists it as shipped, but no code ever mounted one.

import { BubbleMenu } from '@tiptap/extension-bubble-menu';

const MARKS = [
    { label: 'B', title: 'Bold', className: 'cm-editor__bubble-bold', mark: 'bold', run: c => c.toggleBold() },
    { label: 'I', title: 'Italic', className: 'cm-editor__bubble-italic', mark: 'italic', run: c => c.toggleItalic() },
    { label: 'S', title: 'Strikethrough', className: 'cm-editor__bubble-strike', mark: 'strike', run: c => c.toggleStrike() },
    { label: '`', title: 'Inline code', className: 'cm-editor__bubble-code', mark: 'code', run: c => c.toggleCode() },
];

/**
 * Builds the bubble element and its extension.
 *
 * @param {import('@tiptap/core').Editor | (() => import('@tiptap/core').Editor)} getEditor
 */
export function bubbleExtension(getEditor) {
    const bubble = document.createElement('div');
    bubble.className = 'cm-editor__bubble';
    bubble.setAttribute('role', 'toolbar');
    bubble.setAttribute('aria-label', 'Formatting');

    const buttons = MARKS.map(entry => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = `cm-editor__bubble-button ${entry.className}`;
        button.title = entry.title;
        button.setAttribute('aria-label', entry.title);
        button.textContent = entry.label;
        button.addEventListener('mousedown', event => {
            event.preventDefault();
            entry.run(getEditor().chain().focus()).run();
            refresh();
        });
        bubble.appendChild(button);
        return { button, mark: entry.mark };
    });

    const link = document.createElement('button');
    link.type = 'button';
    link.className = 'cm-editor__bubble-button cm-editor__bubble-link';
    link.title = 'Link';
    link.setAttribute('aria-label', 'Link');
    link.textContent = '🔗';
    link.addEventListener('mousedown', event => {
        event.preventDefault();
        const editor = getEditor();
        const current = editor.getAttributes('link').href ?? '';
        // A prompt is a deliberate stopgap over a floating field: this is the one control
        // whose value is free text, and the alternative is a second popover to manage.
        const href = window.prompt('Link URL', current);
        if (href === null) {
            return;
        }
        const chain = editor.chain().focus().extendMarkRange('link');
        // Link is configured autolink:false / openOnClick:false on purpose — we never invent
        // a link the author did not write, so clearing means clearing.
        (href.trim() === '' ? chain.unsetLink() : chain.setLink({ href: href.trim() })).run();
        refresh();
    });
    bubble.appendChild(link);

    /** Reflects the marks under the cursor, so the toolbar shows state and not just actions. */
    function refresh() {
        const editor = getEditor();
        for (const { button, mark } of buttons) {
            button.classList.toggle('cm-editor__bubble-button--on', editor.isActive(mark));
        }
        link.classList.toggle('cm-editor__bubble-button--on', editor.isActive('link'));
    }

    return {
        element: bubble,
        refresh,
        extension: BubbleMenu.configure({
            element: bubble,
            // Code blocks have their own semantics; a bold button over one is a trap.
            shouldShow: ({ editor, from, to }) =>
                from !== to && !editor.isActive('codeBlock') && !editor.isActive('image'),
        }),
    };
}
