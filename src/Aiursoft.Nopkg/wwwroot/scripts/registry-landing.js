(() => {
    const button = document.querySelector('.registry-theme');
    button?.addEventListener('click', async () => {
        const root = document.documentElement;
        const previous = root.dataset.bsTheme;
        const theme = previous === 'dark' ? 'light' : 'dark';
        button.disabled = true;
        root.dataset.bsTheme = theme;
        try {
            const response = await fetch(button.dataset.themeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ theme })
            });
            if (!response.ok) throw new Error('Theme preference could not be saved.');
            button.setAttribute('aria-pressed', String(theme === 'dark'));
        } catch {
            root.dataset.bsTheme = previous;
        } finally {
            button.disabled = false;
        }
    });
})();
