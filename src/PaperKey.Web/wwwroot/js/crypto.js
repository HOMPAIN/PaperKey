// Argon2id в браузере. Управляемая реализация в .NET WebAssembly неприменима: она ждёт потоки,
// которых у страницы нет, и зависает; здесь используется hash-wasm (MIT, js/vendor) — Argon2
// скомпилирован в WebAssembly напрямую, 64 МиБ считаются за секунду.
window.paperkeyCrypto = {
    // password и salt — hex; возвращает hex тега длиной hashLength байт
    async argon2id(passwordHex, saltHex, memoryKiB, iterations, parallelism, hashLength) {
        const toBytes = hex => Uint8Array.from(hex.match(/../g), h => parseInt(h, 16));
        return await hashwasm.argon2id({
            password: toBytes(passwordHex),
            salt: toBytes(saltHex),
            memorySize: memoryKiB,
            iterations: iterations,
            parallelism: parallelism,
            hashLength: hashLength,
            outputType: "hex"
        });
    }
};
