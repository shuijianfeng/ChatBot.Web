(function (root) {
    'use strict';
    const maxFile = 20 * 1024 * 1024;
    const maxTotal = 50 * 1024 * 1024;
    function validate(files) {
        if (files.length > 5) throw new Error('每条消息最多 5 个附件。');
        if (files.some(f => !/\.(docx?|xlsx?|wps|txt|md|csv|xml|json|html?|pdf|png|jpe?g|webp|gif|bmp|tiff?)$/i.test(f.name)))
            throw new Error('请选择 DOC/DOCX、XLS/XLSX、WPS、TXT、MD、CSV、XML、JSON、HTML、PDF 或支持的图片文件。');
        if (files.some(f => (f.size ?? f.Size) <= 0 || (f.size ?? f.Size) > maxFile))
            throw new Error('单个附件应大于 0 且不超过 20MB。');
        if (files.reduce((n, f) => n + f.size, 0) > maxTotal) throw new Error('附件总大小不能超过 50MB。');
    }
    function metadata(file) {
        return { id: file.id, name: file.name, contentType: file.contentType, size: file.size };
    }
    async function json(response) {
        const body = await response.json().catch(() => ({}));
        if (!response.ok) throw new Error(body.error || `附件请求失败（${response.status}）`);
        return body;
    }
    const methods = {
        async handleImageUpload(event) {
            const files = Array.from(event.target.files || []);
            event.target.value = '';
            if (!files.length || this.isProcessing) return;
            try { validate([...this.pendingAttachments, ...files]); }
            catch (error) { alert(error.message); return; }
            const items = files.map(file => ({ name: file.name, size: file.size, contentType: file.type, file, status: 'pending' }));
            this.pendingAttachments.push(...items);
            this.renderAttachmentPreviews();
            for (const item of items) await this.uploadAttachment(item);
        },
        async uploadAttachment(item) {
            if (this.isProcessing || item.status === 'uploading') return;
            item.status = 'uploading';
            this.renderAttachmentPreviews();
            try {
                const body = new FormData(); body.append('file', item.file);
                const result = await json(await fetch(this.apiUrl('chat/upload-file'), { method: 'POST', body }));
                Object.assign(item, result, { status: 'ready', file: null, error: null });
            } catch (error) { item.status = 'failed'; item.error = error.message; }
            this.renderAttachmentPreviews();
        },
        renderAttachmentPreviews() {
            this.previewContainer.replaceChildren();
            for (const file of this.pendingAttachments) {
                const card = document.createElement('div'); card.className = 'attachment-card';
                if (file.id && file.contentType?.startsWith('image/')) {
                    const image = document.createElement('img');
                    image.src = this.apiUrl(`chat/attachments/${encodeURIComponent(file.id)}/download`);
                    image.alt = '图片附件'; image.className = 'uploaded-image-preview';
                    const preview = document.createElement('button'); preview.type = 'button'; preview.className = 'attachment-image-button';
                    preview.setAttribute('aria-label', '预览图片'); preview.onclick = () => this.previewAttachmentImage(file);
                    preview.append(image); card.append(preview);
                }
                const label = document.createElement('span');
                label.textContent = `${file.name} · ${
                    { pending: '等待上传', uploading: '正在上传', ready: '已上传', failed: file.error }[file.status] || '已上传'}`;
                if (file.contentType?.startsWith('image/')) label.textContent = file.status === 'ready' || !file.status ? '' :
                    ({ pending: '等待上传', uploading: '正在上传', failed: file.error }[file.status] || '');
                label.title = label.textContent;
                if (label.textContent) card.append(label);
                if (file.id && !file.contentType?.startsWith('image/')) {
                    const open = document.createElement('a'); open.href = this.apiUrl(`chat/attachments/${encodeURIComponent(file.id)}/open`);
                    open.target = '_blank'; open.rel = 'noopener'; open.className = 'attachment-file-link';
                    label.replaceWith(open); open.append(label);
                }
                if (file.status === 'failed') {
                    const retry = document.createElement('button'); retry.type = 'button'; retry.textContent = '重试';
                    retry.onclick = () => this.uploadAttachment(file); card.append(retry);
                }
                const remove = document.createElement('button'); remove.type = 'button';
                remove.className = 'attachment-remove'; remove.title = '删除附件';
                remove.setAttribute('aria-label', '删除附件：' + file.name);
                remove.innerHTML = '<svg xmlns="http://www.w3.org/2000/svg" width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M3 6h18M9 6V4h6v2M5 6l1 14h12l1-14M10 10v6M14 10v6"/></svg>';
                remove.disabled = file.status === 'uploading' || this.isProcessing;
                remove.onclick = () => { this.pendingAttachments = this.pendingAttachments.filter(f => f !== file); this.renderAttachmentPreviews(); };
                card.append(remove); this.previewContainer.append(card);
            }
            this.previewContainer.style.display = this.pendingAttachments.length ? 'contents' : 'none';
            this.updateSendButtonState();
        },
        removeAllImages() {
            this.pendingAttachments = []; this.uploadedImageUrls = [];
            this.previewContainer.replaceChildren(); this.previewContainer.style.display = 'none'; this.imageInput.value = '';
        },
        updateSendButtonState() {
            const attachments = this.pendingAttachments || [];
            this.sendButton.disabled = this.isProcessing || attachments.some(f => f.status && f.status !== 'ready') ||
                (!this.messageInput.value.trim() && !this.uploadedImageUrls.length && !attachments.length);
        },
        toggleImageUploadButton() {
            this.uploadImageButton.style.display = 'flex';
            this.networkButton.style.display = 'none';
        },
        previewAttachmentImage(file) {
            const dialog = document.createElement('dialog'); dialog.className = 'attachment-image-dialog';
            dialog.setAttribute('aria-label', '图片预览');
            const close = document.createElement('button'); close.type = 'button'; close.textContent = '×';
            close.className = 'attachment-preview-close'; close.setAttribute('aria-label', '关闭预览'); close.onclick = () => dialog.close();
            const image = document.createElement('img');
            image.src = this.apiUrl(`chat/attachments/${encodeURIComponent(file.id)}/download`); image.alt = '图片附件';
            dialog.append(close, image); dialog.addEventListener('close', () => dialog.remove(), { once: true });
            dialog.addEventListener('click', event => { if (event.target === dialog) dialog.close(); });
            document.body.append(dialog); dialog.showModal();
        },
        renderMessageAttachments(container, attachments) {
            if (!attachments?.length) return;
            const list = document.createElement('div'); list.className = 'message-attachments';
            for (const file of attachments) {
                const link = document.createElement('a');
                link.href = this.apiUrl(`chat/attachments/${encodeURIComponent(file.id)}/open`);
                link.textContent = file.name;
                link.target = '_blank'; link.rel = 'noopener'; link.className = 'attachment-card';
                if (file.contentType?.startsWith('image/')) {
                    link.textContent = ''; link.classList.add('attachment-image'); link.setAttribute('aria-label', '预览图片');
                    link.onclick = event => { event.preventDefault(); event.stopPropagation(); this.previewAttachmentImage(file); };
                    const image = document.createElement('img'); image.src = this.apiUrl(`chat/attachments/${encodeURIComponent(file.id)}/download`); image.alt = '图片附件';
                    image.className = 'uploaded-image-preview'; link.prepend(image);
                }
                list.append(link);
            }
            container.append(list);
        },
        async prepareAttachments(question, signal) {
            const history = this.convertToApiMessages();
            if (!history.some(m => m.attachments?.length)) { this.attachmentJobId = null; return; }
            const panel = document.createElement('div'); panel.className = 'attachment-progress';
            panel.setAttribute('role', 'status'); panel.textContent = '正在准备附件…';
            this.messagesContainer.append(panel);
            await this.saveCurrentSession();
            try {
                const fingerprint = JSON.stringify([this.currentSessionId, this.modelSelect.value, question,
                    history.flatMap(m => (m.attachments || []).map(f => f.id)).sort()]);
                let cached;
                try { cached = JSON.parse(sessionStorage.getItem('chat-attachment-job') || 'null'); } catch { }
                let id;
                if (cached?.fingerprint === fingerprint) {
                    const state = await json(await fetch(this.apiUrl(`chat/attachment-jobs/${cached.id}`), { signal }));
                    id = cached.id;
                    if (state.status === 'failed' || state.status === 'cancelled')
                        await json(await fetch(this.apiUrl(`chat/attachment-jobs/${id}/retry`), { method: 'POST', signal }));
                } else {
                    const job = await json(await fetch(this.apiUrl('chat/attachment-jobs'), {
                        method: 'POST', headers: { 'Content-Type': 'application/json' }, signal,
                        body: JSON.stringify({ history, model: this.modelSelect.value, question })
                    }));
                    id = job.id;
                    sessionStorage.setItem('chat-attachment-job', JSON.stringify({ id, fingerprint }));
                }
                this.attachmentJobId = id;
                for (;;) {
                    signal.throwIfAborted();
                    const state = await json(await fetch(this.apiUrl(`chat/attachment-jobs/${id}`), { signal }));
                    panel.textContent = state.stage;
                    if (state.status === 'completed') break;
                    if (state.status === 'failed') throw new Error(state.error || '附件处理失败。');
                    if (state.status === 'cancelled') throw new DOMException('已取消', 'AbortError');
                    await new Promise((resolve, reject) => {
                        const abort = () => { clearTimeout(timer); reject(new DOMException('已取消', 'AbortError')); };
                        const timer = setTimeout(() => { signal.removeEventListener('abort', abort); resolve(); }, 1500);
                        signal.addEventListener('abort', abort, { once: true });
                    });
                }
            } catch (error) {
                panel.textContent = error.name === 'AbortError' ? '附件处理已取消，可重试。' : error.message;
                const retry = document.createElement('button'); retry.type = 'button'; retry.textContent = '重试读取并回答';
                retry.onclick = () => {
                    if (this.isProcessing) return;
                    this.messageInput.value = question;
                    this.retryAttachmentMessage = this.messages.filter(m => m.role === 'user').at(-1)?.content === question;
                    panel.remove(); this.sendMessage();
                };
                panel.append(retry);
                throw error;
            }
        }
    };
    if (typeof module !== 'undefined') module.exports = { validate, metadata, methods };
    root.ChatAttachments = methods;
})(typeof window !== 'undefined' ? window : globalThis);
