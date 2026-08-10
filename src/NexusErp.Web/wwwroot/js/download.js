// Sunucuda üretilen dosyayı (PDF/Excel) tarayıcıya indirtir.
window.downloadFile = (fileName, contentType, base64) => {
    const link = document.createElement('a');
    link.download = fileName;
    link.href = `data:${contentType};base64,${base64}`;
    document.body.appendChild(link);
    link.click();
    link.remove();
};
