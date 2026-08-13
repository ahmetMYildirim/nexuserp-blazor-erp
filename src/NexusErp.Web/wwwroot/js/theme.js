// Tema tercihi. CSS değişkenleri :root[data-theme] üzerinden döndüğü için
// MudThemeProvider'a ek olarak bu özniteliğin de set edilmesi gerekiyor.
window.nexusTheme = {
    key: 'nexuserp-theme',

    get() {
        try { return localStorage.getItem(this.key) === 'dark' ? 'dark' : 'light'; }
        catch { return 'light'; }
    },

    set(mode) {
        const value = mode === 'dark' ? 'dark' : 'light';
        document.documentElement.setAttribute('data-theme', value);
        try { localStorage.setItem(this.key, value); } catch { /* özel mod */ }
        return value;
    },

    apply() {
        const value = this.get();
        document.documentElement.setAttribute('data-theme', value);
        return value;
    }
};

// Blazor devresi kurulmadan uygula — açılışta beyaz parlama olmasın.
window.nexusTheme.apply();
