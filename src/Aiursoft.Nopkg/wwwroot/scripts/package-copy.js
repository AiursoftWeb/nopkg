(() => {
    const text = (key, fallback) => document.querySelector(`#package-copy-loc [data-key="${key}"]`)?.textContent || fallback;
    document.querySelectorAll('.copy-command').forEach(button => button.addEventListener('click', async () => {
        if (button.disabled) return;
        const root = button.closest('.package-command');
        const code = root.querySelector('code');
        const status = root.querySelector('.copy-status');
        button.disabled = true;
        status.textContent = text('pending', 'Copying...');
        try {
            await navigator.clipboard.writeText(code.textContent);
            status.textContent = text('success', 'Copied');
        } catch {
            status.textContent = text('failed', 'Copy failed. Copy the selected text manually.');
            code.focus();
            const range = document.createRange();
            range.selectNodeContents(code);
            const selection = window.getSelection();
            selection.removeAllRanges();
            selection.addRange(range);
        } finally { button.disabled = false; }
    }));
})();
