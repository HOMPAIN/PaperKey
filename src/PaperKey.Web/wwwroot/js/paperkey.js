// Три способа отправить лист на принтер (ТЗ, п. 6.5). Все работают с данными в памяти страницы.
window.paperkey = {
    // A. Скачать PDF файлом
    download(fileName, bytes) {
        const url = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
        const a = document.createElement("a");
        a.href = url;
        a.download = fileName;
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },

    // B. Печать из браузера: в диалог печати уходит только блок с листом (см. @media print в app.css).
    // Размер страницы подставляется под выбранный формат листа — браузер предлагает его по умолчанию.
    print(paper) {
        let style = document.getElementById("paperkey-page");
        if (!style) {
            style = document.createElement("style");
            style.id = "paperkey-page";
            document.head.appendChild(style);
        }
        style.textContent = `@page { size: ${paper} portrait; margin: 0; }`;
        window.print();
    },

    // C. PDF в памяти: открыть во встроенном просмотрщике по временной ссылке
    openPdf(bytes) {
        const url = URL.createObjectURL(new Blob([bytes], { type: "application/pdf" }));
        const opened = window.open(url, "_blank");
        if (!opened) return false;
        setTimeout(() => URL.revokeObjectURL(url), 60000);
        return true;
    }
};
