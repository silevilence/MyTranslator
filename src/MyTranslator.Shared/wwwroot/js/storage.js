// MyTranslator 共享前端本地存储（Token / 界面偏好）。
// 由共享 RCL 提供，Web（WASM）与桌面端（MAUI Blazor Hybrid）均可直接引用。
window.mtStorage = {
    getToken: function () {
        return window.localStorage.getItem('mt.token');
    },
    setToken: function (token) {
        window.localStorage.setItem('mt.token', token);
    },
    clearToken: function () {
        window.localStorage.removeItem('mt.token');
    },
    getDarkMode: function () {
        return window.localStorage.getItem('mt.darkMode') === '1';
    },
    setDarkMode: function (dark) {
        window.localStorage.setItem('mt.darkMode', dark ? '1' : '0');
    },
};
