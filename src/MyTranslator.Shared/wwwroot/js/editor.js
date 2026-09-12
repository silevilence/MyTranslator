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
        if (root?.mtSplit) {
            const { divider, onDown, onMove, onUp } = root.mtSplit;
            divider.removeEventListener('pointerdown', onDown);
            divider.removeEventListener('pointermove', onMove);
            divider.removeEventListener('pointerup', onUp);
            divider.removeEventListener('pointercancel', onUp);
            delete root.mtSplit;
        }
        document.body.classList.remove('mt-split-dragging');
    }
};

// Splitter drag: live CSS-variable updates stay in JS for smoothness; the final
// ratio is reported to Blazor once per gesture for clamping and persistence.
// Returns true when listeners are (or already were) attached; the caller retries
// while the editor still shows the loading skeleton, where no divider exists yet.
window.mtEditor.initSplit = function (id, dotNetRef) {
    const root = document.getElementById(id);
    if (!root) return false;
    if (root.mtSplit) return true;
    const divider = root.querySelector('.mt-split-divider');
    if (!divider) return false;
    let dragging = false;
    const currentRatio = () => {
        const raw = parseFloat(root.style.getPropertyValue('--mt-split')) / 100;
        return Number.isFinite(raw) && raw > 0 ? raw : 0.5;
    };
    const onDown = e => {
        if (!divider.isConnected) return;
        e.preventDefault();
        dragging = true;
        divider.setPointerCapture?.(e.pointerId);
        document.body.classList.add('mt-split-dragging');
    };
    const onMove = e => {
        if (!dragging) return;
        const rect = root.getBoundingClientRect();
        if (rect.width <= 0) return;
        const ratio = Math.min(0.8, Math.max(0.2, (e.clientX - rect.left) / rect.width));
        root.style.setProperty('--mt-split', (ratio * 100).toFixed(2) + '%');
        divider.setAttribute('aria-valuenow', String(Math.round(ratio * 100)));
    };
    const onUp = () => {
        if (!dragging) return;
        dragging = false;
        document.body.classList.remove('mt-split-dragging');
        dotNetRef.invokeMethodAsync('OnSplitDragged', currentRatio());
    };
    divider.addEventListener('pointerdown', onDown);
    divider.addEventListener('pointermove', onMove);
    divider.addEventListener('pointerup', onUp);
    divider.addEventListener('pointercancel', onUp);
    root.mtSplit = { divider, onDown, onMove, onUp };
    return true;
};
