// MyTranslator 共享弹窗焦点管理。
// Blazor 异步错误处理后重渲染会使焦点落回 body；MudDialog 的 Esc 关闭依赖弹窗持有焦点，
// 组件经本命名函数显式归还（.mud-dialog 容器由 MudBlazor 渲染 tabindex="-1"，可直接接收焦点）。
window.mtDialog = {
    focus: function () {
        var el = document.querySelector('.mud-dialog');
        if (el) {
            el.focus();
        }
    },
};
