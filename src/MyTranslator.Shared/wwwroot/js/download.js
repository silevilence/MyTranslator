// MyTranslator 共享前端文件下载（Blazor WASM 导出下载用；桌面端由 Hybrid 桥接处理）。
// 调用方先把文件字节转为 base64 再传入，避免跨 JsInterop 传大二进制数组。
window.mtDownload = {
    download: function (fileName, mediaType, base64) {
        try {
            var bytes = Uint8Array.from(atob(base64), function (c) { return c.charCodeAt(0); });
            var blob = new Blob([bytes], { type: mediaType || 'application/octet-stream' });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = fileName;
            a.rel = 'noopener';
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
            return true;
        } catch (e) {
            return false;
        }
    },
};
