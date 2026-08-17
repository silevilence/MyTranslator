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
    getItem: function (key) {
        return window.localStorage.getItem(key);
    },
    setItem: function (key, value) {
        window.localStorage.setItem(key, value);
    },
    removeItem: function (key) {
        window.localStorage.removeItem(key);
    },
    copyText: function (text) {
        if (navigator.clipboard && window.isSecureContext) {
            return navigator.clipboard.writeText(text).then(function () { return true; }, function () { return false; });
        }
        try {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.opacity = '0';
            document.body.appendChild(ta);
            ta.select();
            var ok = document.execCommand('copy');
            document.body.removeChild(ta);
            return Promise.resolve(ok);
        } catch (e) {
            return Promise.resolve(false);
        }
    },
};
