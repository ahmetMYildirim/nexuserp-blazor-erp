// Ayarlar ekranındaki anahtarlar. theme.js ile aynı desen: tercih
// localStorage'da, uygulama <html> özniteliğine yansıtılır, CSS oradan okur.
// Böylece tercih Blazor devresi kurulmadan, sayfa açılışında anında uygulanır.
window.nexusUi = {
    key: 'nexuserp-ui-prefs',

    defaults: { rail: true, zebra: false },

    get() {
        try {
            const raw = localStorage.getItem(this.key);
            return raw ? { ...this.defaults, ...JSON.parse(raw) } : { ...this.defaults };
        } catch { return { ...this.defaults }; }
    },

    set(prefs) {
        try { localStorage.setItem(this.key, JSON.stringify(prefs)); } catch { /* özel mod */ }
        this.apply(prefs);
    },

    apply(prefs) {
        const html = document.documentElement;
        html.setAttribute('data-rail', prefs.rail ? 'show' : 'hide');
        html.setAttribute('data-zebra', prefs.zebra ? 'on' : 'off');
    },

    reset() {
        this.set({ ...this.defaults });
        return { ...this.defaults };
    }
};

window.nexusUi.apply(window.nexusUi.get());
