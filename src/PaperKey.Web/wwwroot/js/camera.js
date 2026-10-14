// Камера и холсты для страницы проверки. Снимок (или фото из файла) обрезается по рамке видоискателя,
// превращается в полутоновый буфер и уходит в .NET; обратно приходят четырёхугольники клеток с цветами,
// которые рисуются поверх снимка.
window.paperkeyCamera = {
    _stream: null,
    _shots: {},   // холсты снимков по ключу — для ленты снимков на странице сканера (у страницы проверки холст в разметке)

    // Холст по ключу: видимый элемент с таким id либо закадровый холст из _shots
    _canvasFor(key) {
        const el = document.getElementById(key);
        if (el) return el;
        if (!this._shots[key]) this._shots[key] = document.createElement("canvas");
        return this._shots[key];
    },

    forget(key) {
        delete this._shots[key];
    },

    // Уменьшенная копия снимка (с разметкой, если уже нарисована) для ленты — data URL JPEG
    thumbnail(key, width) {
        const shot = this._canvasFor(key);
        if (!shot.width) return "";
        const scale = Math.min(1, width / shot.width);
        const c = document.createElement("canvas");
        c.width = Math.round(shot.width * scale);
        c.height = Math.round(shot.height * scale);
        c.getContext("2d").drawImage(shot, 0, 0, c.width, c.height);
        return c.toDataURL("image/jpeg", 0.8);
    },

    // Показать снимок на видимом холсте (крупный просмотр)
    show(key, targetId) {
        const shot = this._canvasFor(key);
        const target = document.getElementById(targetId);
        if (!target || !shot.width) return;
        target.width = shot.width;
        target.height = shot.height;
        target.getContext("2d").drawImage(shot, 0, 0);
    },

    // Запуск камеры. deviceId — конкретная камера; без него берётся задняя, а затем, если телефон
    // подставил сверхширокоугольную или телекамеру, происходит переключение на основную.
    // Ответ: { error, cameras: [{ id, label }], current }
    async start(videoId, deviceId) {
        if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia)
            return { error: "браузер даёт доступ к камере только по защищённому соединению (https) или на localhost; откройте страницу по https либо загрузите фото из файла", cameras: [], current: "" };

        let error = await this._open(videoId, deviceId);
        if (error) return { error, cameras: [], current: "" };

        const cameras = await this._listCameras();
        let current = this._currentId();
        if (!deviceId) {
            const better = this._preferMain(cameras, current);
            if (better && better !== current) {
                error = await this._open(videoId, better);
                if (!error) current = this._currentId();
            }
        }
        return { error: "", cameras, current };
    },

    async _open(videoId, deviceId) {
        this.stop(videoId);
        const video = document.getElementById(videoId);
        const videoConstraints = deviceId
            ? { deviceId: { exact: deviceId }, width: { ideal: 2560 }, height: { ideal: 1440 } }
            : { facingMode: { ideal: "environment" }, width: { ideal: 2560 }, height: { ideal: 1440 } };
        try {
            this._stream = await navigator.mediaDevices.getUserMedia({ video: videoConstraints, audio: false });
        } catch (e) {
            return e.name + ": " + e.message;
        }
        video.srcObject = this._stream;
        await video.play();
        const track = this._stream.getVideoTracks()[0];
        try { await track.applyConstraints({ advanced: [{ focusMode: "continuous" }] }); } catch (e) { }
        return "";
    },

    _currentId() {
        const track = this._stream && this._stream.getVideoTracks()[0];
        return track ? (track.getSettings().deviceId || "") : "";
    },

    async _listCameras() {
        try {
            const devices = await navigator.mediaDevices.enumerateDevices();
            return devices.filter(d => d.kind === "videoinput").map((d, i) => ({ id: d.deviceId, label: d.label || ("Камера " + (i + 1)) }));
        } catch (e) {
            return [];
        }
    },

    // Среди задних камер предпочитаем основную: без слов wide/ultra/tele/macro в названии,
    // с наименьшим номером (на Android основная обычно «camera2 0, facing back»).
    _preferMain(cameras, currentId) {
        const isBack = c => /back|rear|environment|задн/i.test(c.label);
        const isSpecial = c => /wide|ultra|tele|macro|depth|широк|теле|макро/i.test(c.label);
        const backs = cameras.filter(isBack);
        if (backs.length < 2) return null;
        const score = c => (isSpecial(c) ? 1000 : 0) + (parseInt((c.label.match(/\d+/) || ["500"])[0], 10));
        backs.sort((a, b) => score(a) - score(b));
        return backs[0].id;
    },

    stop(videoId) {
        if (this._stream) {
            this._stream.getTracks().forEach(t => t.stop());
            this._stream = null;
        }
        const video = document.getElementById(videoId);
        if (video) video.srcObject = null;
    },

    // Снимок — текущий кадр видео: его угол обзора точно совпадает с видоискателем,
    // поэтому рамка переводится в координаты кадра без догадок. Область — рамка с запасом.
    // Формат ответа: 4 байта ширина и высота (little-endian uint16), затем пиксели.
    capture(videoId, guideId, shotId, maxWidth) {
        const video = document.getElementById(videoId);
        if (!video || !video.videoWidth) return null;
        const crop = this._guideCrop(video, document.getElementById(guideId));
        return this._fromSource(video, video.videoWidth, video.videoHeight, shotId, maxWidth, crop);
    },

    // Фото из файла — для проверки без камеры (без обрезки)
    loadFile(inputId, shotId, maxWidth) {
        const input = document.getElementById(inputId);
        const file = input && input.files && input.files[0];
        if (!file) return Promise.resolve(null);
        return new Promise(resolve => {
            const img = new Image();
            img.onload = () => { URL.revokeObjectURL(img.src); resolve(this._fromSource(img, img.naturalWidth, img.naturalHeight, shotId, maxWidth, null)); };
            img.onerror = () => resolve(null);
            img.src = URL.createObjectURL(file);
        });
    },

    // Положение рамки видоискателя в долях кадра камеры. Видео показано с object-fit: cover,
    // то есть масштабировано по большей стороне и обрезано по центру.
    _guideCrop(video, guide) {
        if (!guide) return null;
        const box = video.getBoundingClientRect();
        const g = guide.getBoundingClientRect();
        const vw = video.videoWidth, vh = video.videoHeight;
        const scale = Math.max(box.width / vw, box.height / vh);
        const dw = vw * scale, dh = vh * scale;
        const ox = box.left + (box.width - dw) / 2;
        const oy = box.top + (box.height - dh) / 2;
        const margin = 0.06;
        let x = (g.left - ox) / dw - margin * g.width / dw;
        let y = (g.top - oy) / dh - margin * g.height / dh;
        let w = g.width / dw * (1 + 2 * margin);
        let h = g.height / dh * (1 + 2 * margin);
        x = Math.max(0, x); y = Math.max(0, y);
        w = Math.min(1 - x, w); h = Math.min(1 - y, h);
        return { x, y, w, h };
    },

    _fromSource(source, srcW, srcH, shotId, maxWidth, crop) {
        const sx = crop ? Math.round(crop.x * srcW) : 0;
        const sy = crop ? Math.round(crop.y * srcH) : 0;
        const sw = crop ? Math.round(crop.w * srcW) : srcW;
        const sh = crop ? Math.round(crop.h * srcH) : srcH;
        const scale = Math.min(1, maxWidth / sw);
        const w = Math.round(sw * scale);
        const h = Math.round(sh * scale);
        const shot = this._canvasFor(shotId);
        shot.width = w;
        shot.height = h;
        const ctx = shot.getContext("2d", { willReadFrequently: true });
        ctx.drawImage(source, sx, sy, sw, sh, 0, 0, w, h);
        const rgba = ctx.getImageData(0, 0, w, h).data;
        const out = new Uint8Array(4 + w * h);
        out[0] = w & 255; out[1] = w >> 8; out[2] = h & 255; out[3] = h >> 8;
        for (let i = 0, p = 0; i < w * h; i++, p += 4)
            out[4 + i] = (rgba[p] * 299 + rgba[p + 1] * 587 + rgba[p + 2] * 114) / 1000;
        return out;
    },

    // Поверх снимка: рамка таблицы и цветные клетки. quads — [{p: [x1,y1,...,x4,y4], c: "rgba(...)"}]
    drawOverlay(shotId, cornersFlat, quads) {
        const shot = this._canvasFor(shotId);
        const ctx = shot.getContext("2d");
        ctx.lineWidth = Math.max(2, shot.width / 400);
        if (cornersFlat && cornersFlat.length === 8) {
            ctx.strokeStyle = "#1f5fbf";
            ctx.beginPath();
            ctx.moveTo(cornersFlat[0], cornersFlat[1]);
            for (let i = 2; i < 8; i += 2) ctx.lineTo(cornersFlat[i], cornersFlat[i + 1]);
            ctx.closePath();
            ctx.stroke();
        }
        for (const q of quads) {
            ctx.fillStyle = q.c;
            ctx.beginPath();
            ctx.moveTo(q.p[0], q.p[1]);
            for (let i = 2; i < 8; i += 2) ctx.lineTo(q.p[i], q.p[i + 1]);
            ctx.closePath();
            ctx.fill();
        }
    }
};
