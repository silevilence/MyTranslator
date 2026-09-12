// Browser-only event cancellation; shortcut actions and draft logic remain in Blazor/C#.
window.mtEditor = {
    attach(id) {
        const root = document.getElementById(id);
        if (!root || root.mtEditorKey) return;
        root.mtEditorKey = e => {
            const input = e.target.closest('input,textarea,[contenteditable="true"]');
            if ((e.ctrlKey && ['s', 'Enter'].includes(e.key === 'S' ? 's' : e.key)) || e.key === 'F2' ||
                (!input && ['ArrowUp', 'ArrowDown', '?'].includes(e.key))) e.preventDefault();
        };
        root.addEventListener('keydown', root.mtEditorKey);
    },
    detach(id) {
        const root = document.getElementById(id);
        if (root?.mtEditorKey) root.removeEventListener('keydown', root.mtEditorKey);
        if (root) delete root.mtEditorKey;
    }
};
