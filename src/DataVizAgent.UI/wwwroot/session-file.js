window.DataVizAgent = window.DataVizAgent || {};

window.DataVizAgent.downloadTextFile = (fileName, contentType, text) => {
    const blob = new Blob([text], { type: contentType || 'text/plain;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
};

window.DataVizAgent.confirmAction = (message) => window.confirm(message);

window.DataVizAgent.printReport = () => {
    document.body.classList.add('printing-report');

    const cleanup = () => {
        document.body.classList.remove('printing-report');
        window.removeEventListener('afterprint', cleanup);
    };

    window.addEventListener('afterprint', cleanup);

    // Fallback cleanup in case afterprint does not fire (e.g. dialog dismissed).
    window.setTimeout(cleanup, 60000);

    window.print();
};