
class ChatUI {
    constructor() {

        this.messages = []; // 存储聊天记录
        this.session_id = '';
        this.messageBuffer = '';
        this.controller = null; // 用于中断请求的 AbortController
        this.networkButton = document.getElementById('network-search-button');
        this.networkIcon = document.getElementById('network-icon');
        this.isNetworkEnabled = false; // 默认启用联网搜索

        this.MathJax = window.MathJax;

        this.setupEventListeners();
        // 初始化DOM元素引用
        this.messagesContainer = document.getElementById('messages-container');
        this.messageInput = document.getElementById('message-input');
        this.sendButton = document.getElementById('send-button');
        this.modelSelect = document.getElementById('global-model-selector');

        // 图片上传相关元素
        this.uploadImageButton = document.getElementById('upload-image-button');
        this.imageInput = document.getElementById('image-input');
        this.imagePreview = document.getElementById('image-preview');
        /*this.previewImg = document.getElementById('preview-img');*/
        this.previewContainer = document.getElementById('preview-container'); // 新增用于容纳多个图片的容器
        this.removeImageButton = document.getElementById('remove-image-button');

        // 状态标志
        this.isProcessing = false;
        this.currentMessageElement = null;
        this.copyInProgress = false;
        this.currentUser = '我';

        // 定义用于存储模型配置
        this.chatModels = [];
        this.uploadedImageUrls = []; // 修改为数组以支持多张图片

        // 会话管理相关属性
        this.currentSessionId = this.generateSessionId();
        this.currentSessionTitle = null; // 当前会话标题，用于避免重复生成
        this.uid = this.getUidFromUrl();
        this.sidebarCollapsed = true; // 默认折叠
        this.sessionsLoaded = false;

        // 会话脏标志 - 追踪是否有实际变更
        this.sessionDirty = false;
        this.originalSessionData = null; // 保存原始会话数据用于比较

        // 搜索相关
        this.sessionSearchQuery = '';
        this.allSessions = []; // 保存完整会话列表用于搜索过滤

        // 断线重连相关
        this.currentStreamId = null; // 当前流式传输的 streamId
        this.receivedContentLength = 0; // 已接收的内容长度
        this.isStreaming = false; // 是否正在流式传输
        this.mathRenderDebounceTimer = null; // 公式渲染防抖计时器
        this.setupVisibilityHandler(); // 设置页面可见性监听

        // 设置图片上传事件监听
        this.uploadImageButton.addEventListener('click', () => this.imageInput.click());
        this.imageInput.addEventListener('change', (event) => this.handleImageUpload(event));
        //this.removeImageButton.addEventListener('click', () => this.removeAllImages());

        // 设置模型选择事件监听
        this.modelSelect.addEventListener('change', () => {
            this.toggleImageUploadButton();
            // 检查模型是否与原始模型不同
            if (this.originalSessionData && this.originalSessionData.modelName !== this.modelSelect.value) {
                this.sessionDirty = true;
            }
        });

        this.networkButton.addEventListener('click', () => {
            this.isNetworkEnabled = !this.isNetworkEnabled;
            if (this.isNetworkEnabled) {
                this.networkIcon.classList.remove('bi-wifi-off');
                this.networkIcon.classList.add('bi-globe2');
                this.networkButton.title = "禁用联网搜索";

            } else {
                this.networkIcon.classList.remove('bi-globe2');
                this.networkIcon.classList.add('bi-wifi-off');
                this.networkButton.title = "启用联网搜索";
                // 发送设置更新到后端

            }
        });

        // 初始化图片上传按钮的可见性
        this.fetchChatModels();

        const isDarkMode = window.matchMedia('(prefers-color-scheme: dark)').matches;

        // Mermaid 初始化配置
        mermaid.initialize({
            startOnLoad: false,
            theme: isDarkMode ? 'base' : 'default',

            // 通用配置
            htmlLabels: true,
            useMaxWidth: true,         // 允许图表使用最大宽度
            // 使用默认主题变量
            themeVariables: isDarkMode ? {

                background: '#0d1117',
                mainBkg: '#161b22',
                secondaryBkg: '#21262d',
                mainContrastColor: '#c9d1d9',
                primaryColor: '#58a6ff',
                primaryTextColor: '#ffffff',
                primaryBorderColor: '#58a6ff',
                lineColor: '#30363d',
                textColor: '#c9d1d9',
                border1: '#30363d',
                border2: '#30363d',
                arrowheadColor: '#c9d1d9'
            } : {
                // 浅色模式下保持默认设置
                darkMode: false
            },
            flowchart: {
                useMaxWidth: true,
                htmlLabels: true

            },
            sequence: {
                useMaxWidth: true,
                showSequenceNumbers: true
            }
        });

        // 监听主题变化
        window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', (e) => {
            mermaid.initialize({
                theme: e.matches ? 'dark' : 'default'
            });
        });

        this.setupMarked();

        // 初始化侧边栏
        this.initSidebar();

        this.init();
    }

    async fetchChatModels() {
        try {
            const response = await fetch('/api/chat/GetChatModels');
            if (!response.ok) {
                throw new Error('无法获取聊天模型配置');
            }
            this.chatModels = await response.json();
            this.toggleImageUploadButton(); // 配置加载完毕后更新按钮状态
        } catch (error) {
            console.error('获取聊天模型配置时出错:', error);
        }
    }
    // 添加显示全屏图片的方法
    showFullSizeImage(src) {
        // 创建遮罩层
        const overlay = document.createElement('div');
        overlay.className = 'image-overlay';

        // 创建图片元素
        const img = document.createElement('img');
        img.src = src;
        img.className = 'fullsize-image';

        // 添加关闭提示
        const closeHint = document.createElement('div');
        closeHint.className = 'close-hint';
        closeHint.textContent = '点击任意位置关闭';

        // 组装元素
        overlay.appendChild(img);
        overlay.appendChild(closeHint);
        document.body.appendChild(overlay);

        // 点击关闭
        overlay.addEventListener('click', () => {
            document.body.removeChild(overlay);
        });
    }
    // 方法：根据选择的模型显示或隐藏图片上传按钮
    toggleImageUploadButton() {
        const selectedModel = this.modelSelect.value;
        const model = this.chatModels.find(m => m.name === selectedModel);
        if (model && model.enableImageUpload) {
            this.uploadImageButton.style.display = 'flex'; // 或 'block'，根据您的CSS布局
        } else {
            this.uploadImageButton.style.display = 'none';
        }
        if (model && model.enableSearch) {
            this.networkButton.style.display = 'flex'; // 或 'block'，根据您的CSS布局
        } else {
            this.networkButton.style.display = 'none';
        }
    }

    init() {

        this.sendButton.addEventListener('click', () => this.sendMessage());
        this.messageInput.addEventListener('input', () => this.autoResizeTextarea());
        this.messageInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') {
                if (e.shiftKey) {
                    // Shift + Enter: 允许换行，不阻止默认行为
                    return;
                } else if (e.ctrlKey) {

                    // Ctrl + Enter:  允许换行，不阻止默认行为
                    return;
                } else {
                    // 普通 Enter: 发送消息
                    e.preventDefault();
                    this.sendMessage();
                }
            }
        });

    }

    // 移除图片预览
    removeImage() {
        this.imagePreview.style.display = 'none';
        this.previewImg.src = '';
        this.imageInput.value = '';

    }
    // 移除所有图片预览
    removeAllImages() {
        this.previewContainer.innerHTML = '';
        this.imageInput.value = '';
        this.uploadedImageUrls = [];
        this.previewContainer.style.display = "none";

    }
    // 修改 handleImageUpload 方法，添加单个图片移除功能
    async handleImageUpload(event) {
        const files = event.target.files;
        if (!files.length) return;

        for (const file of files) {
            // 检查文件类型
            if (!file.type.startsWith('image/')) {
                alert('请选择有效的图片文件。');
                continue;
            }

            // 可选：限制文件大小（例如，最大5MB）
            const maxSize = 5 * 1024 * 1024; // 5MB
            if (file.size > maxSize) {
                alert('图片大小不能超过5MB。');
                continue;
            }
            // 压缩图片
            //const compressedFile = await this.compressImage(file,800,0.8); // 目标宽度800px，质量0.7
            // 显示上传中的状态
            this.appendMessage('user', '正在上传图片...', true);
            this.setLoadingState(true);

            try {
                // 创建 FormData 对象
                const formData = new FormData();
                formData.append('image', file);

                // 发送图片到后端API
                const response = await fetch('/api/chat/upload-image', {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    throw new Error('图片上传失败');
                }

                const data = await response.json();
                const imageUrl = data.url; // 假设后端返回图片的URL

                // 创建图片预览容器
                const imgWrapper = document.createElement('div');
                imgWrapper.className = 'image-wrapper';

                // 创建图片元素
                const imgElement = document.createElement('img');
                imgElement.src = imageUrl;
                imgElement.alt = '上传的图片';
                imgElement.className = 'uploaded-image-preview';
                // 添加消息容器的点击事件委托
                imgElement.addEventListener('dblclick', (e) => {

                    this.showFullSizeImage(imgElement.src);

                });
                // 创建移除按钮
                const removeButton = document.createElement('button');
                removeButton.className = 'remove-image-button';
                removeButton.innerHTML = '&times;'; // 使用乘号符号
                removeButton.title = '移除图片';

                // 添加点击事件以移除该图片预览
                removeButton.addEventListener('click', () => {
                    this.previewContainer.removeChild(imgWrapper);
                    const index = this.uploadedImageUrls.indexOf(imageUrl);
                    if (index > -1) {
                        this.uploadedImageUrls.splice(index, 1);
                    }
                    // 如果没有图片，隐藏预览容器
                    if (this.uploadedImageUrls.length === 0) {
                        this.previewContainer.style.display = 'none';
                    }
                    this.updateSendButtonState();
                });

                // 组装图片预览元素
                imgWrapper.appendChild(imgElement);
                imgWrapper.appendChild(removeButton);
                this.previewContainer.appendChild(imgWrapper);
                this.previewContainer.style.display = 'flex';

                // 存储图片URL以便发送
                this.uploadedImageUrls.push(imageUrl);

                // 移除上传中的状态
                this.removeLastUserMessage();

            } catch (error) {
                console.error('图片上传错误:', error);
                this.appendMessage('user', '图片上传失败，请重试。');
            } finally {
                this.setLoadingState(false);
                // 清除文件输入
                this.imageInput.value = '';
            }
        }
    }

    // 修改 compressImage 方法以提高压缩后图片的清晰度
    compressImage(file, maxWidth = 1024, quality = 0.8) {
        return new Promise((resolve, reject) => {
            const img = new Image();
            const canvas = document.createElement('canvas');
            const ctx = canvas.getContext('2d');

            img.onload = () => {
                let { width, height } = img;

                // 仅在图片宽度大于 maxWidth 时进行缩放
                if (width > maxWidth) {
                    height = height * (maxWidth / width);
                    width = maxWidth;
                }

                canvas.width = width;
                canvas.height = height;
                ctx.drawImage(img, 0, 0, width, height);

                // 根据原始文件类型选择适当的输出格式
                const fileExtension = file.name.split('.').pop().toLowerCase();
                let outputFormat = 'image/jpeg'; // 默认格式

                if (fileExtension === 'png') {
                    outputFormat = 'image/png';
                } else if (fileExtension === 'webp') {
                    outputFormat = 'image/webp';
                }

                canvas.toBlob((blob) => {
                    if (blob) {
                        const compressedFileName = file.name.replace(/\.[^/.]+$/, `.${outputFormat.split('/')[1]}`);
                        const compressedFile = new File([blob], compressedFileName, {
                            type: outputFormat,
                            lastModified: Date.now()
                        });
                        resolve(compressedFile);
                    } else {
                        reject(new Error('图片压缩失败'));
                    }
                }, outputFormat, quality);
            };

            img.onerror = () => {
                reject(new Error('图片加载失败'));
            };

            const reader = new FileReader();
            reader.onload = (e) => {
                img.src = e.target.result;
            };
            reader.readAsDataURL(file);
        });
    }



    removeLastUserMessage() {
        if (this.messages.length > 0) {
            const lastMessage = this.messages.pop();
            const lastMessageElement = this.messagesContainer.lastElementChild;
            if (lastMessageElement && lastMessageElement.classList.contains('user-message')) {
                this.messagesContainer.removeChild(lastMessageElement);
            }
        }
    }

    setupEventListeners() {
        const stopButton = document.querySelector('.stop-button');
        const inputBox = document.querySelector('#message-input');

        // 停止按钮点击事件
        stopButton.addEventListener('click', () => {
            this.stopGeneration();
        });

        // 输入框事件处理
        inputBox.addEventListener('input', () => {
            this.adjustInputHeight(inputBox);
        });
    }
    // 显示/隐藏停止按钮
    toggleStopButton(show) {
        const stopButton = document.querySelector('.stop-button');
        stopButton.style.display = show ? 'flex' : 'none';
        this.isGenerating = show;
    }


    stopGeneration() {
        if (this.controller) {
            try {

                this.controller.abort();
            } catch (error) {
                console.error('停止生成时发生错误:', error);
            } finally {
                this.controller = null;
                this.toggleStopButton(false);
                // 清理流式传输状态，防止轮询继续
                this.isStreaming = false;
                this.currentStreamId = null;
                this.setLoadingState(false);
            }
        }
    }

    // 调整输入框高度
    adjustInputHeight(element) {
        element.style.height = 'auto';
        element.style.height = Math.min(element.scrollHeight, 200) + 'px';
    }
    autoResizeTextarea() {
        this.messageInput.style.height = 'auto';
        this.messageInput.style.height = Math.min(this.messageInput.scrollHeight, 200) + 'px';
        this.updateSendButtonState();
    }

    updateSendButtonState() {
        const isEmpty = !this.messageInput.value.trim();
        this.sendButton.disabled = isEmpty && this.uploadedImageUrls.length == 0 || this.isProcessing;
    }

    setLoadingState(loading) {
        this.isProcessing = loading;
        this.sendButton.classList.toggle('loading', loading);
        this.messageInput.disabled = loading;
        this.updateSendButtonState();
    }

    processAllCodeBlocks() {
        document.querySelectorAll('pre').forEach(pre => {
            if (!pre.closest('.code-block-wrapper')) {
                this.enhanceCodeBlock(pre);
            }
        });
    }

    enhanceCodeBlock(pre) {
        // 创建包装器
        const wrapper = document.createElement('div');
        wrapper.className = 'code-block-wrapper';

        // 获取或创建 code 元素
        let code = pre.querySelector('code');
        if (!code) {
            code = document.createElement('code');
            code.textContent = pre.textContent;
            pre.textContent = '';
            pre.appendChild(code);
        }
        // 保存原始代码
        const originalCode = code.textContent;
        // 获取语言
        let language = this.detectLanguage(code);
        //if (language === 'Thoughts') {

        //    setTimeout(() => {

        //        const currentTime = new Date().toLocaleTimeString();
        //        language = `Thoughts: ${currentTime}`;
        //    }, 1000); // 延迟3秒后显示时间
        //}
        // 创建标题栏
        const header = this.createCodeHeader(language, originalCode);

        // 重新组织结构
        pre.parentNode.insertBefore(wrapper, pre);
        wrapper.appendChild(header);
        //包装包装用mermaid-chartmermaid
        if (language.toLowerCase() === 'mermaid') {
            const chartId = `mermaid-${Math.random().toString(36).slice(2, 11)}`;
            const chart = document.createElement('div');
            chart.className = 'mermaid-chart';
            chart.id = chartId
            chart.appendChild(pre);
            wrapper.appendChild(chart);
        }
        else {
            //包装包装用mermaid-chartmermaid
            if (language.toLowerCase() === 'jsmind') {
                const chartId = `jsmind-${Math.random().toString(36).slice(2, 11)}`;
                const chart = document.createElement('div');
                chart.className = 'jsmind-chart';
                chart.id = chartId
                chart.appendChild(pre);
                wrapper.appendChild(chart);
            }
            else {

                if (language.toLowerCase() === 'thoughts') {

                    header.style.display = 'none'; // 隐藏原标题栏

                    wrapper.style.maxWidth = '700px';
                    wrapper.style.width = '100%';
                    wrapper.style.height = 'auto';
                    wrapper.className = 'code-block-wrapper thoughts-wrapper';

                    pre.style.maxWidth = '700px';
                    pre.style.width = '100%';
                    pre.style.height = 'auto';
                    pre.style.overflow = 'hidden';
                    pre.style.whiteSpace = 'pre-wrap';
                    pre.style.overflowWrap = 'break-word';
                    pre.style.wordWrap = 'break-word';

                    code.style.width = '100%';
                    code.style.height = 'auto';
                    code.style.overflow = 'hidden';
                    code.style.whiteSpace = 'pre-wrap';
                    code.style.overflowWrap = 'break-word';

                    // 使用 details/summary 实现可折叠，默认展开
                    const details = document.createElement('details');
                    details.open = true;
                    details.className = 'thoughts-details';

                    const summary = document.createElement('summary');
                    summary.className = 'thoughts-summary';

                    // 创建箭头图标
                    const arrow = document.createElement('span');
                    arrow.className = 'thoughts-arrow';
                    arrow.textContent = '∨'; // 展开时向下箭头

                    // 监听展开/折叠状态变化
                    details.addEventListener('toggle', () => {
                        arrow.textContent = details.open ? '∨' : '❯';
                    });

                    // 创建文本
                    const textSpan = document.createElement('span');
                    const thinkTime = Math.max(3, Math.min(30, Math.floor(code.textContent.length / 100)));
                    textSpan.textContent = `Thought for ${thinkTime}s`;

                    summary.appendChild(arrow);
                    summary.appendChild(textSpan);
                    details.appendChild(summary);
                    details.appendChild(pre);
                    wrapper.appendChild(details);


                }
                else {
                    wrapper.appendChild(pre);

                }
            }
        }

    }

    detectLanguage(codeElement) {
        const classes = Array.from(codeElement.classList);
        const langClass = classes.find(cls => cls.startsWith('language-'));
        return langClass ? langClass.replace('language-', '') : 'plaintext';
    }

    createCodeHeader(language, code) {
        const header = document.createElement('div');
        header.className = 'code-header';

        // 添加语言标识
        const langLabel = document.createElement('span');
        langLabel.className = 'code-language';
        langLabel.textContent = language;
        header.appendChild(langLabel);

        // 添加下载按钮
        const downloadButton = document.createElement('button');
        downloadButton.className = 'download-code-button';
        downloadButton.innerHTML = '<svg viewBox="0 0 16 16" width="16" height="16"><path fill="currentColor" d="M7.47 10.78a.75.75 0 001.06 0l3.75-3.75a.75.75 0 00-1.06-1.06L8.75 8.44V1.75a.75.75 0 00-1.5 0v6.69L4.78 5.97a.75.75 0 00-1.06 1.06l3.75 3.75zM3.75 13a.75.75 0 000 1.5h8.5a.75.75 0 000-1.5h-8.5z"></path></svg>';
        downloadButton.title = '下载代码';
        downloadButton.setAttribute('aria-label', '下载代码');

        // 添加下载功能
        downloadButton.addEventListener('click', () => {
            // 创建文件名，根据语言类型设置适当的扩展名
            let extension = '.txt';
            switch (language.toLowerCase()) {
                case 'javascript': extension = '.js'; break;
                case 'html': extension = '.html'; break;
                case 'css': extension = '.css'; break;
                case 'csharp': case 'cs': extension = '.cs'; break;
                case 'python': extension = '.py'; break;
                case 'java': extension = '.java'; break;
                case 'json': extension = '.json'; break;
                case 'xml': extension = '.xml'; break;
                case 'sql': extension = '.sql'; break;
                case 'typescript': extension = '.ts'; break;
                case 'c': extension = '.c'; break;
                case 'cpp': case 'c++': extension = '.cpp'; break;
                // 可以根据需要添加更多语言类型
            }

            const filename = `code${extension}`;

            // 创建 Blob
            const blob = new Blob([code], { type: 'text/plain;charset=utf-8' });

            // 创建下载链接
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = filename;

            // 触发下载
            document.body.appendChild(a);
            a.click();

            // 清理
            document.body.removeChild(a);
            URL.revokeObjectURL(url);

            // 显示下载成功反馈
            const originalHTML = downloadButton.innerHTML;
            downloadButton.innerHTML = `
            <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                <path fill="currentColor" d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"/>
            </svg>
        `;
            downloadButton.classList.add('download-success');

            setTimeout(() => {
                downloadButton.innerHTML = originalHTML;
                downloadButton.classList.remove('download-success');
            }, 2000);
        });

        header.appendChild(downloadButton);

        // 在 createCodeHeader 方法中修改 HTML 预览功能
        if (language.toLowerCase() === 'html') {
            const runButton = document.createElement('button');
            runButton.className = 'run-html-button';
            runButton.innerHTML = '<svg viewBox="0 0 16 16" width="16" height="16"><path fill-rule="evenodd" d="M5 3.25a.75.75 0 01.75-.75h8.5a.75.75 0 010 1.5h-8.5A.75.75 0 015 3.25zm0 5a.75.75 0 01.75-.75h8.5a.75.75 0 010 1.5h-8.5A.75.75 0 015 8.25zm0 5a.75.75 0 01.75-.75h8.5a.75.75 0 010 1.5h-8.5a.75.75 0 01-.75-.75zM.924 5.31a.75.75 0 011.226-.86l2.25 3.25a.75.75 0 010 .87l-2.25 3.25a.75.75 0 01-1.226-.86l1.95-2.82-1.95-2.82z"></path></svg> 运行';
            runButton.title = '在安全环境中运行此HTML代码';
            header.appendChild(runButton);

            // 运行HTML代码
            runButton.addEventListener('click', () => {
                // 创建遮罩层
                const overlay = document.createElement('div');
                overlay.className = 'run-html';

                // 添加关闭提示
                const closeHint = document.createElement('div');
                closeHint.className = 'close-hint';
                closeHint.textContent = '点击任意位置关闭';

                const contentWrapper = document.createElement('div');
                contentWrapper.className = 'html-content-wrapper';

                // 设置内容包装器样式，使其更宽
                contentWrapper.style.cssText = `
            width: 90%;
            height: 90%;
            margin: 0 auto;
            background: white;
            border-radius: 8px;
            overflow: hidden;
           
        `;

                // 创建安全的iframe元素
                const sandbox = document.createElement('iframe');
                sandbox.className = 'html-sandbox';

                // 改进的iframe样式设置 - 处理滚动和自适应问题
                sandbox.style.cssText = `
    width: 100%;
    min-height: 300px;
    height: 80vh;
    border: none;
    background: white;
    margin: 0;
    padding: 0;
    overflow: auto;
    box-sizing: border-box;
    display: block;
`;

                // 创建HTML内容blob
                const htmlContent = `
    <!DOCTYPE html>
    <html>
    <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <style>
            body {
                margin: 0;
                padding: 16px;
                font-family: system-ui, -apple-system, sans-serif;
                line-height: 1.5;
                overflow-x: hidden; /* 防止水平滚动 */
                word-wrap: break-word; /* 确保长文本换行 */
            }
            img { 
                max-width: 100%; 
                height: auto;
            }
            * { box-sizing: border-box; }
            
            /* 添加响应式布局支持 */
            @media (max-width: 768px) {
                body {
                    padding: 8px;
                }
            }
        </style>
    </head>
    <body>${code}</body>
    </html>
`;

                // 使用 Blob 创建安全的 URL
                const blob = new Blob([htmlContent], { type: 'text/html' });
                const blobUrl = URL.createObjectURL(blob);

                // 设置 iframe src
                sandbox.src = blobUrl;

                // 添加到DOM
                contentWrapper.appendChild(sandbox);
                overlay.appendChild(contentWrapper);
                overlay.appendChild(closeHint);
                document.body.appendChild(overlay);

                // 监听 iframe 加载完成
                sandbox.addEventListener('load', () => {
                    try {
                        // 改进的动态高度调整
                        const resizeIframe = () => {
                            try {
                                // 获取内容高度
                                const doc = sandbox.contentDocument || sandbox.contentWindow.document;
                                const docHeight = doc.body.scrollHeight;
                                const docWidth = doc.body.scrollWidth;
                                const viewportHeight = window.innerHeight * 0.8;

                                // 设置最大高度为视口的80%，但不小于内容实际高度
                                sandbox.style.height = `${Math.min(docHeight + 40, viewportHeight)}px`;

                                // 如果内容宽度超过iframe宽度，添加滚动条
                                if (docWidth > sandbox.clientWidth) {
                                    sandbox.style.overflowX = 'auto';
                                }

                                // 设置内容可滚动
                                doc.body.style.overflow = 'auto';
                            } catch (err) {
                                console.warn('动态调整iframe高度失败:', err);
                            }
                        };

                        // 立即调整高度
                        resizeIframe();

                        // 添加窗口大小变化事件监听
                        const resizeObserver = new ResizeObserver(() => {
                            resizeIframe();
                        });

                        // 观察容器大小变化
                        resizeObserver.observe(contentWrapper);

                        // 当iframe卸载时清理观察器
                        overlay.addEventListener('remove', () => {
                            resizeObserver.disconnect();
                        }, { once: true });

                        // 清理 blob URL
                        URL.revokeObjectURL(blobUrl);
                    } catch (error) {
                        console.warn('无法调整iframe高度', error);
                        sandbox.style.height = '80vh'; // 默认回退高度
                    }
                });

                // 改进的点击关闭处理
                overlay.addEventListener('click', (e) => {
                    if (e.target === overlay || e.target === closeHint) {
                        document.body.removeChild(overlay);
                    }
                });

            });
        }




        const copyButton = document.createElement('button');
        copyButton.className = 'copy-button';
        copyButton.innerHTML = '<i class="bi bi-clipboard"></i>';
        copyButton.setAttribute('aria-label', '复制代码');
        copyButton.dataset.copyContent = code;
        // 添加复制功能
        this.addCopyButtonListener(copyButton);

        header.appendChild(copyButton);

        return header;
    }
    addCopyButtonListener(button) {
        button.addEventListener('click', async () => {
            const code = button.dataset.copyContent;
            try {
                // 使用新的方法复制内容，保持原始格式
                await this.copyWithFormatPreservation(code, button);
            } catch (err) {
                console.error('复制失败:', err);
                this.showCopyFeedback(button, false);
            }
        });
    }



    /**
 * 显示复制操作的反馈
 * @param {HTMLElement} button 按钮元素
 * @param {boolean} success 是否成功
 */
    showCopyFeedback(button, success) {
        if (!button) return;

        const originalHTML = button.innerHTML;

        if (success) {
            button.innerHTML = `
            <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                <path fill="currentColor" d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"/>
            </svg>
        `;
            button.classList.add('copy-success');
        } else {
            button.innerHTML = `
            <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                <path fill="currentColor" d="M3.72 3.72a.75.75 0 0 1 1.06 0L8 6.94l3.22-3.22a.75.75 0 1 1 1.06 1.06L9.06 8l3.22 3.22a.75.75 0 1 1-1.06 1.06L8 9.06l-3.22 3.22a.75.75 0 0 1-1.06-1.06L6.94 8 3.72 4.78a.75.75 0 0 1 0-1.06z"/>
            </svg>
        `;
            button.classList.add('copy-failure');
        }

        // 2秒后恢复按钮原始状态
        setTimeout(() => {
            button.innerHTML = originalHTML;
            button.classList.remove('copy-success', 'copy-failure');
        }, 2000);
    }

    observeNewMessages() {
        const chatContainer = document.querySelector('.chat-messages');
        if (!chatContainer) return;

        const observer = new MutationObserver((mutations) => {
            mutations.forEach(mutation => {
                mutation.addedNodes.forEach(node => {
                    //if (node.nodeType === 1 && node.classList.contains('message')) { // 元素节点且为消息
                    //    const deleteBtn = node.querySelector('.delete-button');
                    //    if (!deleteBtn) {
                    //        const actionsDiv = node.querySelector('.message-actions');
                    //        if (actionsDiv) {
                    //            const deleteButton = document.createElement('button');
                    //            deleteButton.className = 'delete-button';
                    //            deleteButton.innerHTML = '&times;';
                    //            deleteButton.title = '删除消息';
                    //            deleteButton.addEventListener('click', () => {
                    //                this.deleteMessage(node);
                    //            });
                    //            actionsDiv.appendChild(deleteButton);
                    //        }
                    //    }
                    //}

                    if (node.nodeType === 1) { // 元素节点
                        const newCodeBlocks = node.querySelectorAll('pre');
                        newCodeBlocks.forEach(pre => {
                            if (!pre.closest('.code-block-wrapper')) {
                                this.enhanceCodeBlock(pre);
                            }
                        });
                    }
                });
            });
        });

        observer.observe(chatContainer, {
            childList: true,
            subtree: true
        });
    }

    createMessageElement(role, content) {
        const messageDiv = document.createElement('div');
        messageDiv.className = `message ${role}-message`;


        const containerDiv = document.createElement('div');
        containerDiv.className = 'message-container';

        const headerDiv = document.createElement('div');
        headerDiv.className = 'message-header';

        const roleSpan = document.createElement('span');
        roleSpan.className = 'message-role';
        roleSpan.textContent = role === 'assistant' ? 'Ai助手  ' : '您';

        const actionsDiv = document.createElement('div');
        actionsDiv.className = 'message-actions';

        // 添加导出按钮
        const exportGroup = document.createElement('div');
        exportGroup.className = 'export-group';

        // DOCX 导出按钮
        const exportDocxBtn = document.createElement('button');
        exportDocxBtn.className = 'export-button';
        exportDocxBtn.title = '导出为Word文档';
        exportDocxBtn.innerHTML = `
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
        <path d="M19 3H5C3.89543 3 3 3.89543 3 5V19C3 20.1046 3.89543 21 5 21H19C20.1046 21 21 20.1046 21 19V5C21 3.89543 20.1046 3 19 3Z" 
              stroke="currentColor" stroke-width="2" fill="none"/>
        <text x="7" y="17" font-family="Arial" font-size="12" font-weight="bold" fill="currentColor">W</text>
    </svg>`;
        /*exportDocxBtn.onclick = () => this.exportMessageToDocx(content);*/

        // PDF 导出按钮
        const exportPdfBtn = document.createElement('button');
        exportPdfBtn.className = 'export-button';
        exportPdfBtn.title = '导出为PDF文档';
        exportPdfBtn.innerHTML = `
    <svg width="20" height="20" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">  
        <path d="M19 3H5C3.89543 3 3 3.89543 3 5V19C3 20.1046 3.89543 21 5 21H19C20.1046 21 21 20.1046 21 19V5C21 3.89543 20.1046 3 19 3Z" 
              stroke="currentColor" stroke-width="2" fill="none"/>
        <text x="5.5" y="17" font-family="Arial" font-size="10" font-weight="bold" fill="currentColor">PDF</text>
    </svg>`;
        //exportPdfBtn.onclick = () => this.exportMessageToPdf(content);

        exportGroup.appendChild(exportDocxBtn);
        exportGroup.appendChild(exportPdfBtn);

        // 创建删除按钮
        const deleteButton = document.createElement('button');
        deleteButton.className = 'delete-button';
        deleteButton.title = '删除消息';
        deleteButton.setAttribute('aria-label', 'Delete');
        // 更新删除按钮的SVG图标
        deleteButton.innerHTML = `
    <svg class="icon" width="16" height="16" fill="currentColor" viewBox="0 0 16 16">
        <path d="M5.5 5.5A.5.5 0 016 6v6a.5.5 0 01-1 0V6a.5.5 0 01.5-.5zm2.5 0a.5.5 0 01.5.5v6a.5.5 0 01-1 0V6a.5.5 0 01.5-.5zm3 .5a.5.5 0 00-1 0v6a.5.5 0 001 0V6z"/>
        <path fill-rule="evenodd" d="M14.5 3a1 1 0 01-1 1H13v9a2 2 0 01-2 2H5a2 2 0 01-2-2V4h-.5a1 1 0 01-1-1V2a1 1 0 011-1h3.5l1-1h4a1 1 0 011 1v1zM4.118 4L4 4.059V13a1 1 0 001 1h6a1 1 0 001-1V4.059L11.882 4H4.118zM2.5 3V2h11v1h-11z"/>
    </svg>`;

        // 添加删除事件监听
        deleteButton.addEventListener('click', () => {
            this.deleteMessage(messageDiv);
        });



        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content markdown-body';
        contentDiv.dataset.rawContent = content;
        if (this.uploadedImageUrls.length > 0) {
            let imagesHtml = '';
            this.uploadedImageUrls.forEach(url => {


                imagesHtml += `<img src="${url}" alt="上传的图片" class="uploaded-image-preview" />\n`;
            });
            contentDiv.innerHTML = imagesHtml + marked.parse(this.preprocessMarkdown(content));
            // 为所有上传的图片添加双击事件
            contentDiv.querySelectorAll('.uploaded-image-preview').forEach(img => {
                img.addEventListener('dblclick', () => {
                    this.showFullSizeImage(img.src);
                });
            });
            const copyButton = this.createCopyButton(content);
            exportPdfBtn.onclick = () => this.exportMessageToPdf(copyButton.dataset.copyContent);
            exportDocxBtn.onclick = () => this.exportMessageToDocx(copyButton.dataset.copyContent);
            exportGroup.appendChild(deleteButton);
            exportGroup.appendChild(copyButton);
            actionsDiv.appendChild(exportGroup);
            //actionsDiv.appendChild(deleteButton);
            //actionsDiv.appendChild(copyButton);
        }
        else {
            try {
                contentDiv.innerHTML = marked.parse(this.preprocessMarkdown(content));
                // 为所有图片添加双击事件和样式
                contentDiv.querySelectorAll('img').forEach(img => {
                    // 添加样式类
                    img.classList.add('message-image');
                    // 添加双击事件
                    img.addEventListener('dblclick', () => {
                        this.showFullSizeImage(img.src);
                    });
                });

                contentDiv.querySelectorAll('pre code').forEach((block) => {
                    hljs.highlightElement(block);
                });

                // 添加复制按钮到每个代码块
                contentDiv.querySelectorAll('pre').forEach((pre) => {
                    const codeBlock = pre.querySelector('code');
                    if (codeBlock) {
                        const wrapper = document.createElement('div');
                        wrapper.className = 'code-block-wrapper';

                        const copyButton = this.createCopyButton(codeBlock.textContent);
                        copyButton.className = 'code-copy-button';

                        pre.parentNode.insertBefore(wrapper, pre);
                        wrapper.appendChild(pre);
                        wrapper.appendChild(copyButton);
                    }
                });

                // 添加消息复制按钮
                const copyButton = this.createCopyButton(content);
                exportPdfBtn.onclick = () => this.exportMessageToPdf(copyButton.dataset.copyContent);
                exportDocxBtn.onclick = () => this.exportMessageToDocx(copyButton.dataset.copyContent);
                exportGroup.appendChild(deleteButton);
                exportGroup.appendChild(copyButton);
                actionsDiv.appendChild(exportGroup);
                //actionsDiv.appendChild(exportGroup);
                //actionsDiv.appendChild(deleteButton);
                //actionsDiv.appendChild(copyButton);
            } catch (e) {
                console.error('Markdown 渲染错误:', e);
                contentDiv.textContent = content;
            }
        }
        //messageDiv.appendChild(avatarDiv);
        headerDiv.appendChild(roleSpan);

        if (role === 'assistant') {
            const modelSpan = document.createElement('span');
            modelSpan.className = 'message-model';
            modelSpan.textContent = this.modelSelect.value;
            headerDiv.appendChild(modelSpan);
        }
        headerDiv.appendChild(actionsDiv);
        containerDiv.appendChild(headerDiv);
        containerDiv.appendChild(contentDiv);
        messageDiv.appendChild(containerDiv);

        return { messageDiv, contentDiv };
    }

    async exportMessageToDocx(content) {
        try {
            // 导出前移除推理内容
            const filteredContent = this.removeThoughtsForExport(content);

            const response = await fetch('/api/chat/export-message-docx', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    content: filteredContent
                })
            });

            if (!response.ok) {
                throw new Error('导出失败');
            }

            // 获取文件名
            const contentDisposition = response.headers.get('content-disposition');
            let filename = 'chat.docx';
            if (contentDisposition) {
                const filenameMatch = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                if (filenameMatch && filenameMatch[1]) {
                    filename = filenameMatch[1].replace(/['"]/g, '');
                }
            }

            // 获取二进制数据并创建下载
            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = '聊天消息' + filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);
        } catch (error) {
            console.error('导出DOCX失败:', error);
            alert('导出DOCX失败,请重试');
        }
    }

    async exportMessageToPdf(content) {
        try {
            // 导出前移除推理内容
            const filteredContent = this.removeThoughtsForExport(content);

            const response = await fetch('/api/chat/export-message-pdf', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    content: filteredContent
                })
            });

            if (!response.ok) {
                throw new Error('导出失败');
            }

            // 获取文件名
            const contentDisposition = response.headers.get('content-disposition');
            let filename = 'chat.pdf';
            if (contentDisposition) {
                const filenameMatch = contentDisposition.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
                if (filenameMatch && filenameMatch[1]) {
                    filename = filenameMatch[1].replace(/['"]/g, '');
                }
            }

            // 获取二进制数据并创建下载
            const blob = await response.blob();
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = '聊天消息' + filename;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            window.URL.revokeObjectURL(url);
        } catch (error) {
            console.error('导出PDF失败:', error);
            alert('导出PDF失败,请重试');
        }
    }

    deleteMessage(messageElement) {
        if (confirm('确定要删除这条消息吗？')) {
            const index = Array.from(this.messagesContainer.children).indexOf(messageElement);
            if (index > -1) {
                this.messages.splice(index, 1);
            }
            this.messagesContainer.removeChild(messageElement);
            this.sessionDirty = true; // 标记会话有变更
        }
    }

    // 获取复制图标
    getCopyIcon() {
        return `<svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
        <path d="M0 6.75C0 5.784.784 5 1.75 5h1.5a.75.75 0 0 1 0 1.5h-1.5a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 9.25 16h-7.5A1.75 1.75 0 0 1 0 14.25v-7.5z"/>
        <path d="M5 1.75C5 .784 5.784 0 6.75 0h7.5C15.216 0 16 .784 16 1.75v7.5A1.75 1.75 0 0 1 14.25 11h-7.5A1.75 1.75 0 0 1 5 9.25v-7.5zm1.75-.25a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-7.5a.25.25 0 0 0-.25-.25h-7.5z"/>
    </svg>`;
    }
    createCopyButton(textToCopy) {
        const copyButton = document.createElement('button');
        copyButton.className = 'copy-button';
        copyButton.title = '复制消息';
        copyButton.setAttribute('aria-label', 'Copy');
        copyButton.innerHTML = `
        <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
            <path fill="currentColor" d="M0 6.75C0 5.784.784 5 1.75 5h1.5a.75.75 0 0 1 0 1.5h-1.5a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 9.25 16h-7.5A1.75 1.75 0 0 1 0 14.25v-7.5z"/>
            <path fill="currentColor" d="M5 1.75C5 .784 5.784 0 6.75 0h7.5C15.216 0 16 .784 16 1.75v7.5A1.75 1.75 0 0 1 14.25 11h-7.5A1.75 1.75 0 0 1 5 9.25v-7.5zm1.75-.25a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-7.5a.25.25 0 0 0-.25-.25h-7.5z"/>
        </svg>
    `;

        copyButton.addEventListener('click', async () => {
            const contentToCopy = copyButton.dataset.copyContent || textToCopy;
            // 使用新的方法复制内容，保持原始格式
            await this.copyWithFormatPreservation(contentToCopy, copyButton);
        });

        return copyButton;
    }


    setupLinkPreviews() {
        // 创建预览容器
        const previewContainer = document.createElement('div');
        previewContainer.className = 'link-preview-container';
        previewContainer.style.display = 'none';
        document.body.appendChild(previewContainer);

        // 保存预览容器引用
        this.linkPreviewContainer = previewContainer;

        // 添加预览延迟控制变量
        this.previewTimeout = null;
        this.currentPreviewUrl = null;
        this.previewCache = {}; // 用于缓存预览结果

        // 使用委托事件处理以提高性能
        document.addEventListener('mousemove', (e) => {
            const target = e.target.closest('a.external-link');
            if (target && target.dataset.preview) {
                const url = target.dataset.preview;

                // 防止频繁触发预览
                if (this.currentPreviewUrl === url && this.linkPreviewContainer.style.display === 'block') {
                    return;
                }

                // 清除之前的延时
                clearTimeout(this.previewTimeout);

                // 设置新的延时（150ms后显示预览，减少不必要的请求）
                this.previewTimeout = setTimeout(() => {
                    this.currentPreviewUrl = url;
                    this.showLinkPreview(target, url);
                }, 150);
            } else if (!e.target.closest('.link-preview-container') &&
                !e.target.closest('a.external-link')) {
                // 如果鼠标不在链接或预览上，隐藏预览
                clearTimeout(this.previewTimeout);
                this.hideLinkPreview();
            }
        });

        // 监听预览容器的悬停，以避免在预览内容上移动鼠标时隐藏预览
        previewContainer.addEventListener('mouseenter', () => {
            clearTimeout(this.hidePreviewTimeout);
        });

        previewContainer.addEventListener('mouseleave', () => {
            this.hideLinkPreview();
        });
    }

    // 显示链接预览 - 优化版本
    async showLinkPreview(linkElement, url) {

        // 如果是引用式链接，尝试从内容获取实际URL
        if (url.startsWith('#') && linkElement.classList.contains('reference-link')) {
            // 查找引用定义
            const refId = url.substring(1).toLowerCase();
            if (this.renderer && this.renderer.links && this.renderer.links[refId]) {
                url = this.renderer.links[refId].href;
            } else {
                // 尝试从链接文本中提取URL
                const match = linkElement.textContent.match(/\[\d+\]\s+(https?:\/\/\S+)/);
                if (match) {
                    url = match[1];
                }
            }
        }


        // 如果URL无效，不显示预览
        if (!url || url === '#' || url.startsWith('javascript:')) {
            return;
        }

        // 获取链接元素的位置
        const rect = linkElement.getBoundingClientRect();

        // 检查缓存中是否有预览内容
        let previewHtml = this.previewCache[url];

        // 显示加载状态
        if (!previewHtml) {
            this.linkPreviewContainer.innerHTML = `
            <div class="link-preview-loading">
                <div class="spinner"></div>
                <div>加载预览...</div>
            </div>
        `;
        } else {
            // 直接使用缓存内容
            this.linkPreviewContainer.innerHTML = previewHtml;
        }

        // 立即定位和显示容器，无论是否有缓存
        this.positionPreviewContainer(rect);
        this.linkPreviewContainer.style.display = 'block';

        // 如果没有缓存，异步获取预览内容
        if (!previewHtml) {
            try {
                // 创建一个可取消的请求
                if (this.currentPreviewRequest) {
                    this.currentPreviewRequest.abort();
                }

                // 获取预览内容
                previewHtml = await this.fetchLinkPreview(url);

                // 缓存结果
                this.previewCache[url] = previewHtml;

                // 如果当前预览的URL仍然是这个，则更新内容
                if (this.currentPreviewUrl === url) {
                    this.linkPreviewContainer.innerHTML = previewHtml;
                    // 重新定位预览容器以适应新内容
                    this.positionPreviewContainer(rect);
                }
            } catch (error) {
                console.error('获取链接预览失败:', error);

                // 更新为错误状态，但仅当当前URL匹配时
                if (this.currentPreviewUrl === url) {
                    this.linkPreviewContainer.innerHTML = `
                    <div class="link-preview-error">
                        <div>无法加载预览</div>
                        <div class="link-url">${url}</div>
                    </div>
                `;
                }
            }
        }
    }

    // 隐藏链接预览 - 优化版本
    hideLinkPreview() {
        // 使用延时避免鼠标在链接和预览之间移动时闪烁
        clearTimeout(this.hidePreviewTimeout);
        this.hidePreviewTimeout = setTimeout(() => {
            // 检查鼠标是否在预览容器或链接上
            if (!this.linkPreviewContainer.matches(':hover') &&
                !document.querySelector('a.external-link:hover')) {
                this.linkPreviewContainer.style.display = 'none';
                this.currentPreviewUrl = null;
            }
        }, 200);
    }

    // 定位预览容器 - 优化版本
    positionPreviewContainer(linkRect) {
        const container = this.linkPreviewContainer;
        const windowWidth = window.innerWidth;
        const windowHeight = window.innerHeight;

        // 默认显示在链接下方
        let top = linkRect.bottom + 10;
        let left = linkRect.left;

        // 获取容器大小（即使内容还没加载完）
        const containerRect = container.getBoundingClientRect();
        const containerWidth = containerRect.width || 320; // 默认宽度
        const containerHeight = containerRect.height || 200; // 默认高度

        // 如果预览超出右侧边界，向左调整
        if (left + containerWidth > windowWidth - 20) {
            left = Math.max(20, windowWidth - containerWidth - 20);
        }

        // 如果预览超出底部边界，显示在链接上方
        if (top + containerHeight > windowHeight - 20) {
            top = Math.max(20, linkRect.top - containerHeight - 10);
        }

        // 使用transform属性进行平滑过渡
        container.style.left = `${left}px`;
        container.style.top = `${top}px`;
    }

    // 更新 fetchLinkPreview 方法以显示网站图标和站点名称
    async fetchLinkPreview(url) {
        // 检查URL是否为图片
        if (/\.(jpg|jpeg|png|gif|webp|svg)$/i.test(url)) {
            return `
        <div class="link-preview-image">
            <img src="${url}" alt="图片预览" loading="lazy" />
            <div class="link-url">${url}</div>
        </div>
    `;
        }

        // 创建一个可取消的请求
        const abortController = new AbortController();
        this.currentPreviewRequest = abortController;

        try {
            // 添加超时处理
            const timeoutId = setTimeout(() => abortController.abort(), 12000); // 8秒超时

            // 调用后端API获取链接预览
            const response = await fetch('/api/chat/link-preview', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({ url }),
                signal: abortController.signal
            });

            clearTimeout(timeoutId);

            if (!response.ok) {
                throw new Error('获取链接预览失败');
            }

            const data = await response.json();

            // 构建优化的预览HTML，突出显示网站图标和网站名称
            return `
        <div class="link-preview">
           
            <div class="preview-content">
                <div class="preview-site-info">
                    ${data.favicon ? `<img src="${data.favicon}" class="preview-favicon" alt="${data.siteName || '网站'}" />` : ''}
                    <span class="preview-site-name">${data.siteName || new URL(data.url).hostname}</span>
                </div>
                 ${data.image ? `<div class="preview-image"><img src="${data.image}" alt="网站预览" loading="lazy" /></div>` : ''}
                <div class="preview-title">${data.title || '无标题'}</div>
                ${data.description ? `<div class="preview-description">${data.description}</div>` : ''}
                <div class="preview-url">${data.url}</div>
            </div>
        </div>
    `;
        } catch (error) {
            if (error.name === 'AbortError') {
                console.log('链接预览请求已取消');
            } else {
                console.error('获取链接预览失败:', error);
            }

            // 简化的错误预览，包含基本的域名信息
            try {
                const domain = new URL(url).hostname;
                const favicon = `https://www.google.com/s2/favicons?domain=${domain}&sz=64`;

                return `
            <div class="link-preview-simple">
                <div class="preview-site-info">
                    <img src="${favicon}" class="preview-favicon" alt="${domain}" onerror="this.style.display='none'" />
                    <span class="preview-site-name">${domain}</span>
                </div>
                <div class="preview-content">
                    <div class="preview-url">${url}</div>
                </div>
            </div>
        `;
            } catch (e) {
                // 如果URL解析失败，返回最简单的预览
                return `
            <div class="link-preview-simple">
                <div class="preview-icon">
                    <svg width="24" height="24" viewBox="0 0 24 24" fill="none" xmlns="http://www.w3.org/2000/svg">
                        <path d="M10 6H6a2 2 0 00-2 2v10a2 2 0 002 2h10a2 2 0 002-2v-4M14 4h6m0 0v6m0-6L10 14" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
                    </svg>
                </div>
                <div class="preview-content">
                    <div class="preview-url">${url}</div>
                </div>
            </div>
        `;
            }
        }
    }


    // 修改 setupMarked 方法中的链接渲染器部分
    setupMarked() {
        const renderer = new marked.Renderer();
        const originalCode = renderer.code.bind(renderer);


        // 添加预处理函数，用于处理连续的引用链接和 think 标签
        this.preprocessMarkdown = (content) => {
            // 1. 在连续的引用链接之间添加空格，如 [2][3][8] -> [2] [3] [8]
            let result = content.replace(/(\[\d+\])(?=\[\d+\])/g, '$1 ');

            // 2. 处理完整的 think 块 - 使用更宽松的匹配
            // 匹配 <think>...~~~Thoughts...内容...~~~...</think>
            result = result.replace(/<think>[\s\S]*?~~~\s*Thoughts\s*([\s\S]*?)~~~[\s\S]*?<\/think>/gi, '\n```thoughts\n$1\n```\n');

            // 3. 处理不完整的 think 块（流式传输中间状态）
            // 开始标记：<think>...~~~Thoughts
            result = result.replace(/<think>[\s\S]*?~~~\s*Thoughts\s*/gi, '\n```thoughts\n');
            // 结束标记：~~~...</think>
            result = result.replace(/~~~[\s\S]*?<\/think>/gi, '\n```\n');

            // 4. 清理任何残留的 think 标签
            result = result.replace(/<\/?think>/gi, '');

            // 5. 处理其他未处理的 ~~~ 代码围栏
            result = result.replace(/^~~~(\w+)/gm, '```$1');
            result = result.replace(/^~~~\s*$/gm, '```');

            // 调试：输出预处理前后的内容（如果包含 think 或 ~~~）
            if (content.includes('<think>') || content.includes('~~~')) {
                console.log('[DEBUG preprocessMarkdown] 输入包含 think/~~~:', content.substring(0, 200));
                console.log('[DEBUG preprocessMarkdown] 输出:', result.substring(0, 200));
            }

            return result;
        };

        // 导出时移除 Thoughts 推理内容的方法
        this.removeThoughtsForExport = (content) => {
            if (!content) return content;

            let result = content;

            // 1. 移除完整的 <think>...</think> 块（包含所有内容）
            result = result.replace(/<think>[\s\S]*?<\/think>/gi, '');

            // 2. 移除 ```thoughts...``` 代码块
            result = result.replace(/```thoughts[\s\S]*?```/gi, '');

            // 3. 移除 ~~~Thoughts...~~~ 代码块
            result = result.replace(/~~~\s*Thoughts[\s\S]*?~~~/gi, '');

            // 4. 移除不完整的 think 标签（流式传输残留）
            result = result.replace(/<think>[\s\S]*/gi, '');
            result = result.replace(/[\s\S]*<\/think>/gi, '');

            // 5. 清理多余的空行（连续3个以上空行变成2个）
            result = result.replace(/\n{3,}/g, '\n\n');

            // 6. 清理开头的空行
            result = result.replace(/^\s*\n+/, '');

            return result;
        };

        // 修改链接渲染器，增加对引用式链接的支持
        renderer.link = (href, title, text) => {
            // 处理 href 为对象的情况
            let safeHref = '#';
            if (href) {
                if (typeof href === 'object') {
                    // 尝试从对象中获取 URL
                    safeHref = href.url || href.href || href.toString() || '#';
                } else {
                    safeHref = href;
                }
                // 确保 href 是字符串并进行编码
                safeHref = encodeURI(safeHref.toString());
            }

            // 处理文本内容 - 防止引用式链接只显示数字
            let safeText;
            if (text && text.match(/^\[\d+\]$/)) {
                // 如果文本是引用格式 [数字]，则显示更有意义的内容
                safeText = `${text} ${href}`;
            } else {
                safeText = text || (typeof href === 'string' ? href : href.text);
            }

            const titleAttr = title ? ` title="${title.replace(/"/g, '&quot;')}"` : '';

            // 返回安全的链接 HTML，添加 data-preview 属性
            return `<a href="${safeHref}" target="_blank" rel="noopener noreferrer" class="external-link" 
            data-preview="${safeHref}"${titleAttr}>${safeText}<svg class="external-link-icon" width="12" height="12" viewBox="0 0 12 12">
            <path fill="currentColor" d="M3.75 3v-1h6.5v6.5h-1V4.31L3.81 9.69l-.71-.71L8.69 3.5H3.75z"/>
        </svg></a>`;
        };




        renderer.code = (code, language) => {
            // 处理 mermaid 图表
            if (language === 'mermaid') {
                const chartId = `mermaid-${Math.random().toString(36).substr(2, 9)}`;
                return `<div class="mermaid-chart" id="${chartId}">${code}</div>`;
            }

            // 处理 thoughts 代码块 - 确保生成正确的 class
            if (language && language.toLowerCase() === 'thoughts') {
                // 返回带有正确 class 的 pre/code 结构，后续由 enhanceCodeBlock 处理
                return `<pre><code class="language-thoughts">${code}</code></pre>`;
            }

            // 处理其他语言
            if (language && hljs.getLanguage(language)) {
                try {
                    return hljs.highlight(code, {
                        language: language,
                        ignoreIllegals: true
                    }).value;
                } catch (e) {
                    console.error('代码高亮错误:', e);
                }
            }

            // 默认处理
            return originalCode(code, language);
        };
        // 初始化链接预览功能
        this.setupLinkPreviews();

        // 设置 marked 选项
        marked.setOptions({
            renderer: renderer,
            gfm: true,
            breaks: true,
            sanitize: false,
            smartLists: true,
            smartypants: false,
            xhtml: false

        });
        // 6. 在内容更新后触发渲染
        const renderMath = (element) => {
            if (this.MathJax && this.MathJax.typesetPromise) {

                this.MathJax.typesetPromise([element])
                    .catch(err => console.error('MathJax 渲染错误:', err));
            } else if (window.MathJax && window.MathJax.typesetPromise) {
                // 备用方案，使用全局 MathJax
                window.MathJax.typesetPromise([element])
                    .catch(err => console.error('备用 MathJax 渲染错误:', err));
            }

        };

        // 导出renderMath方法供外部使用
        this.renderMath = renderMath;

        // 检测内容是否包含数学公式语法
        this.hasMathContent = (content) => {
            // 检测行内公式 $...$ 或块级公式 $$...$$
            // 排除代码块内的内容
            const withoutCodeBlocks = content.replace(/```[\s\S]*?```/g, '').replace(/`[^`]+`/g, '');
            return /\$\$[\s\S]+?\$\$|\$[^$\n]+?\$/g.test(withoutCodeBlocks);
        };

        // 检测公式是否完整并渲染
        this.renderMathIfReady = (element, content) => {
            // 排除代码块内的内容
            const withoutCodeBlocks = content.replace(/```[\s\S]*?```/g, '').replace(/`[^`]+`/g, '');

            // 计算 $$ 和 $ 的数量，判断公式是否完整
            const blockMathCount = (withoutCodeBlocks.match(/\$\$/g) || []).length;
            const allDollarCount = (withoutCodeBlocks.match(/\$/g) || []).length;
            // 块级公式 $$ 必须成对
            const blockMathComplete = blockMathCount % 2 === 0;
            // 行内公式 $ 数量（排除 $$ 后）必须成对
            const inlineDollarCount = allDollarCount - blockMathCount * 2;
            const inlineMathComplete = inlineDollarCount % 2 === 0;

            // 只有当所有公式都完整时才渲染
            if (blockMathComplete && inlineMathComplete && (blockMathCount > 0 || inlineDollarCount > 0)) {
                renderMath(element);
            }
        };


    }
    async renderMessage(message) {
        // 渲染消息内容
        const rendered = marked(message.content);
        const messageElement = document.createElement('div');
        messageElement.className = `message ${message.role}`;
        messageElement.innerHTML = `
            <div class="message-content">${rendered}</div>
            <div class="message-meta">
                <span class="time">${new Date(message.timestamp).toLocaleTimeString()}</span>
            </div>
        `;

        // 查找并渲染所有 mermaid 图表
        const mermaidCharts = messageElement.querySelectorAll('.mermaid-chart');
        if (mermaidCharts.length > 0) {
            for (const chart of mermaidCharts) {
                try {
                    const code = chart.textContent;
                    const id = chart.id;
                    await this.renderMermaidChart(code, id);
                } catch (error) {
                    console.error('Error rendering chart:', error);
                    chart.innerHTML = `<div class="chart-error">Failed to render chart: ${error.message}</div>`;
                }
            }
        }

        return messageElement.outerHTML;
    }


    async renderMermaidChart(code, containerId) {
        try {
            // 等待 Mermaid 加载完成
            if (!window.mermaid) {
                await new Promise(resolve => setTimeout(resolve, 1000));
            }

            const container = document.getElementById(containerId);
            if (!container) {
                throw new Error(`找不到容器: ${containerId}`);
            }

            // 清理容器内容
            container.innerHTML = code;
            container.classList.add('mermaid');

            // 渲染图表
            //await mermaid.run({
            //    querySelector: `#${containerId}`
            //});

            await mermaid.renderAsync(
                { id: containerId },
                code
            );

        } catch (error) {
            console.error('Mermaid 渲染错误:', error);
            const container = document.getElementById(containerId);
            if (container) {
                container.innerHTML = `
                    <div class="mermaid-error">
                        <p>图表渲染失败</p>
                        <pre>${error.message}</pre>
                    </div>
                `;
            }
        }
    }
    // 接收消息处理
    async handleReceivedMessage(message) {
        try {
            await this.appendMessage1(message);
        } catch (error) {
            console.error('Error handling received message:', error);
            this.showNotification('消息渲染失败', 'error');
        }
    }

    // 根据角色获取对应的图标
    getIconByRole(role) {
        const icons = {
            assistant: `<svg class="icon ai-icon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
            <path d="M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zm0 18c-4.411 0-8-3.589-8-8s3.589-8 8-8 8 3.589 8 8-3.589 8-8 8zm3.707-11.707a1 1 0 0 0-1.414 0L11 11.586l-1.293-1.293a1 1 0 1 0-1.414 1.414l2 2a1 1 0 0 0 1.414 0l4-4a1 1 0 0 0 0-1.414z"/>
        </svg>`,
            user: `<svg class="icon user-icon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
            <path d="M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zM8 21.25v-.625c0-1.725 3.392-3.125 4-3.125s4 1.4 4 3.125v.625c-1.237.526-2.598.75-4 .75s-2.763-.224-4-.75zM12 16c-2.2 0-4-1.8-4-4s1.8-4 4-4 4 1.8 4 4-1.8 4-4 4z"/>
        </svg>`,
            system: `<svg class="icon" viewBox="0 0 24 24" xmlns="http://www.w3.org/2000/svg">
            <path d="M12 2C6.477 2 2 6.477 2 12s4.477 10 10 10 10-4.477 10-10S17.523 2 12 2zm1 15h-2v-2h2v2zm0-4h-2V7h2v6z"/>
        </svg>`
        };
        return icons[role] || icons.system;
    }

    /**
 * 复制内容到剪贴板并保持原始格式
 * @param {string} text 要复制的文本内容
 * @param {HTMLElement} [buttonElement] 可选的按钮元素，用于显示反馈
 * @returns {Promise<boolean>} 复制是否成功
 */
    async copyWithFormatPreservation(text, buttonElement = null) {
        try {
            // 不再尝试强制焦点，直接使用更可靠的备用方案
            if (navigator.clipboard && window.isSecureContext) {
                await navigator.clipboard.writeText(text);
                this.showCopyFeedback(buttonElement, true);
                return true;
            } else {
                // 如果Clipboard API不可用或不在安全上下文中，直接使用备用方法
                throw new Error('使用备用方法');
            }
        } catch (error) {
            console.warn('使用备用复制方法:', error);

            // 使用更可靠的备用方法
            return this.fallbackCopy(text, buttonElement);
        }
    }

    /**
     * 备用的复制方法，使用DOM和execCommand
     * @param {string} text 要复制的文本内容
     * @param {HTMLElement} buttonElement 按钮元素
     * @returns {boolean} 是否成功
     */
    fallbackCopy(text, buttonElement) {
        try {
            // 1. 创建textarea元素
            const textarea = document.createElement('textarea');
            textarea.value = text;

            // 2. 设置样式确保可见但不干扰布局
            textarea.style.position = 'fixed';
            textarea.style.top = '0';
            textarea.style.left = '0';
            textarea.style.width = '2em';
            textarea.style.height = '2em';
            textarea.style.padding = '0';
            textarea.style.border = 'none';
            textarea.style.outline = 'none';
            textarea.style.boxShadow = 'none';
            textarea.style.background = 'transparent';
            textarea.style.zIndex = '-1'; // 置于底层

            // 3. 添加到DOM
            document.body.appendChild(textarea);

            // 4. 选择文本
            textarea.focus();
            textarea.select();

            // 5. 尝试复制
            const successful = document.execCommand('copy');

            // 6. 清理
            document.body.removeChild(textarea);

            // 7. 反馈
            this.showCopyFeedback(buttonElement, successful);
            return successful;

        } catch (err) {
            console.error('备用复制方法失败:', err);
            this.showCopyFeedback(buttonElement, false);
            return false;
        }
    }


    async copyToClipboard(text) {
        try {
            await navigator.clipboard.writeText(text);
            // 可以添加复制成功的提示
            const copyButton = event.currentTarget;
            const originalHTML = copyButton.innerHTML;
            copyButton.innerHTML = `
            <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                <path d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"/>
            </svg>
        `;
            setTimeout(() => {
                copyButton.innerHTML = originalHTML;
            }, 2000);
        } catch (err) {
            console.error('复制失败:', err);
        }
    }
    async copyMessage(button) {
        if (this.copyInProgress) return;
        this.copyInProgress = true;

        try {
            const content = button.dataset.copyContent;
            await navigator.clipboard.writeText(content);

            const originalHTML = button.innerHTML;
            button.innerHTML = '<span class="octicon octicon-check"></span>';

            setTimeout(() => {
                button.innerHTML = originalHTML;
                this.copyInProgress = false;
            }, 2000);
        } catch (err) {
            console.error('复制失败:', err);
            this.copyInProgress = false;
        }
    }

    async appendMessage1(message) {
        const messagesContainer = this.container.querySelector('.chat-messages');
        const rendered = await this.renderMessage(message);

        // 添加消息到容器
        messagesContainer.insertAdjacentHTML('beforeend', rendered);

        // 滚动到底部
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
    }
    // 添加消息到内存和UI
    appendMessage(role, content, isStreaming = false) {
        // 如果是流式响应的第一部分，添加新消息
        if (!this.currentMessageElement || role === "user") {
            // 添加消息到内存
            this.messages.push({
                role: role,
                content: content,
                images: this.uploadedImageUrls.slice() // 复制数组
            });

            // 创建并添加消息元素到UI
            const { messageDiv, contentDiv } = this.createMessageElement(role, content);
            this.messagesContainer.appendChild(messageDiv);

            if (isStreaming || role === "user") {
                this.currentMessageElement = messageDiv;
            }
            else {
                this.currentMessageElement = messageDiv;
                // 更新现有消息的内容
                const contentDiv = this.currentMessageElement.querySelector('.message-content');
                const copyButton = this.currentMessageElement.querySelector('.copy-button');

                if (!contentDiv.dataset.rawContent) {
                    contentDiv.dataset.rawContent = '';
                }
                contentDiv.dataset.rawContent = content;
                contentDiv.dataset.rawContent = this.preprocessMarkdown(contentDiv.dataset.rawContent)
                // 更新内存中最后一条消息的内容
                if (this.messages.length > 0) {
                    this.messages[this.messages.length - 1].content = contentDiv.dataset.rawContent;
                    this.messages[this.messages.length - 1].images = this.uploadedImageUrls.slice(); // 确保 images 更新
                }

                copyButton.dataset.copyContent = contentDiv.dataset.rawContent;

                try {

                    contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);
                    // 为所有图片添加双击事件和样式
                    contentDiv.querySelectorAll('img').forEach(img => {
                        // 添加样式类
                        img.classList.add('message-image');
                        // 添加双击事件
                        img.addEventListener('dblclick', () => {
                            this.showFullSizeImage(img.src);
                        });
                    });
                    // 处理所有代码块
                    contentDiv.querySelectorAll('pre code').forEach((block) => {
                        // 添加语言类标识
                        const language = block.getAttribute('class') || '';
                        if (language) {
                            block.parentElement.classList.add('language-' + language.replace('language-', ''));
                        }
                        // 添加程序框标题和程序框复制按钮

                        const pre = block.parentElement;
                        if (!pre.closest('.code-block-wrapper')) {
                            this.enhanceCodeBlock(pre);
                        }

                        // 应用高亮
                        hljs.highlightElement(block);
                    });

                    //// 在内容更新后触发 MathJax 渲染
                    //if (contentDiv && window.MathJax) {
                    //    try {
                    //        // MathJax 3.x 使用 typesetPromise
                    //        if (window.MathJax.typesetPromise) {
                    //            window.MathJax.typesetPromise([contentDiv])
                    //                .catch(err => console.error('MathJax 渲染错误:', err));
                    //        }
                    //        // 兼容其他版本
                    //        else if (window.MathJax.typeset) {
                    //            window.MathJax.typeset([contentDiv]);
                    //        }
                    //        // MathJax 2.x 兼容处理
                    //        else if (window.MathJax.Hub && window.MathJax.Hub.Queue) {
                    //            window.MathJax.Hub.Queue(["Typeset", window.MathJax.Hub, contentDiv]);
                    //        }
                    //    } catch (mathJaxError) {
                    //        console.error('MathJax 调用失败:', mathJaxError);
                    //    }
                    //}
                } catch (e) {
                    console.error('Markdown 渲染错误:', e);
                    contentDiv.textContent = contentDiv.dataset.rawContent;
                }
            }
        } else {
            // 更新现有消息的内容
            const contentDiv = this.currentMessageElement.querySelector('.message-content');
            const copyButton = this.currentMessageElement.querySelector('.copy-button');

            if (!contentDiv.dataset.rawContent) {
                contentDiv.dataset.rawContent = '';
            }
            contentDiv.dataset.rawContent += content;
            contentDiv.dataset.rawContent = this.preprocessMarkdown(contentDiv.dataset.rawContent)
            // 更新内存中最后一条消息的内容
            if (this.messages.length > 0) {
                this.messages[this.messages.length - 1].content = contentDiv.dataset.rawContent;
                this.messages[this.messages.length - 1].images = this.uploadedImageUrls.slice(); // 确保 images 更新
            }

            copyButton.dataset.copyContent = contentDiv.dataset.rawContent;

            try {

                contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);
                // 为所有图片添加双击事件和样式
                contentDiv.querySelectorAll('img').forEach(img => {
                    // 添加样式类
                    img.classList.add('message-image');
                    // 添加双击事件
                    img.addEventListener('dblclick', () => {
                        this.showFullSizeImage(img.src);
                    });
                });
                // 处理所有代码块
                contentDiv.querySelectorAll('pre code').forEach((block) => {
                    // 添加语言类标识
                    const language = block.getAttribute('class') || '';
                    if (language) {
                        block.parentElement.classList.add('language-' + language.replace('language-', ''));
                    }
                    // 添加程序框标题和程序框复制按钮

                    const pre = block.parentElement;
                    if (!pre.closest('.code-block-wrapper')) {
                        this.enhanceCodeBlock(pre);
                    }

                    // 应用高亮
                    hljs.highlightElement(block);
                });

                /// 在内容更新后触发 MathJax 渲染
                //if (contentDiv) {
                //    renderMath(contentDiv);
                //}
            } catch (e) {
                console.error('Markdown 渲染错误:', e);
                contentDiv.textContent = contentDiv.dataset.rawContent;
            }
        }

        this.scrollToBottom();
    }

    appendStreamContent(content) {
        if (this.currentMessageElement) {
            const contentDiv = this.currentMessageElement.querySelector('.message-content');

            if (contentDiv) {
                this.messageBuffer += content;

                // 同步更新内存中最后一条消息的内容（修复锁屏恢复后内容无法保存的问题）
                if (this.messages.length > 0 && this.messages[this.messages.length - 1].role === 'assistant') {
                    this.messages[this.messages.length - 1].content = this.messageBuffer;
                }
                // full-message类似乎不存在，直接忽略
                // const fullMessageDiv = this.currentMessageElement.querySelector('.full-message');
                // if (fullMessageDiv) fullMessageDiv.textContent = this.messageBuffer;



                try {

                    // 预处理内容（移除 think 标签，转换 ~~~ 为 ```）
                    const processedContent = this.preprocessMarkdown(this.messageBuffer);

                    // 渲染 markdown 内容
                    contentDiv.innerHTML = marked.parse(processedContent);

                    // 更新 rawContent 数据属性，以便于复制等功能
                    contentDiv.dataset.rawContent = this.messageBuffer;

                    // 处理所有代码块
                    contentDiv.querySelectorAll('pre code').forEach((block) => {
                        // 添加语言类标识
                        const language = block.getAttribute('class') || '';
                        if (language) {
                            block.parentElement.classList.add('language-' + language.replace('language-', ''));
                        }

                        // 注意：流式传输期间不调用 enhanceCodeBlock，
                        // 因为代码块可能不完整，会导致 DOM 问题
                        // enhanceCodeBlock 会在流式传输完成后由其他逻辑调用

                        // 应用高亮
                        hljs.highlightElement(block);
                    });

                    // 公式实时渲染 - 使用防抖机制避免频繁调用
                    if (this.hasMathContent(processedContent)) {
                        // 清除之前的防抖计时器
                        if (this.mathRenderDebounceTimer) {
                            clearTimeout(this.mathRenderDebounceTimer);
                        }
                        // 设置防抖延迟（300ms）
                        this.mathRenderDebounceTimer = setTimeout(() => {
                            this.renderMathIfReady(contentDiv, processedContent);
                        }, 300);
                    }
                } catch (e) {
                    console.error('Markdown 渲染错误:', e);
                    contentDiv.textContent = this.messageBuffer;
                }

                this.scrollToBottom();
            } else {

                // 尝试自愈：如果在当前消息元素里找不到 contentDiv，可能是引用错乱，尝试重新获取最后一条消息
                const lastMsg = this.messagesContainer.querySelector('.message.assistant-message:last-child');
                if (lastMsg && lastMsg !== this.currentMessageElement) {
                    this.currentMessageElement = lastMsg;
                    this.appendStreamContent(content); // 递归重试一次
                }
            }
        } else {

            // 自愈：如果没有当前消息元素，尝试获取最后一条
            const lastMsg = this.messagesContainer.querySelector('.message.assistant-message:last-child');
            if (lastMsg) {
                this.currentMessageElement = lastMsg;
                this.appendStreamContent(content); // 递归重试一次
            }
        }
    }

    // 转换聊天记录为API格式
    convertToApiMessages() {
        // 添加系统消息
        const apiMessages = [];

        // 添加历史消息
        this.messages.forEach(msg => {
            // 确保消息格式正确
            if (msg.role && (msg.content || msg.images.length > 0)) {
                apiMessages.push({
                    role: msg.role,
                    content: msg.content,
                    images: msg.images // 包含多张图片
                });
            }
        });

        return apiMessages;
    }

    // 清除所有消息
    clearMessages() {
        this.messages = [];
        this.messagesContainer.innerHTML = '';
        this.currentMessageElement = null;
    }

    scrollToBottom() {
        this.messagesContainer.scrollTop = this.messagesContainer.scrollHeight;
    }

    // 从 DOM 重新同步 messages 数组，确保消息顺序和内容正确
    syncMessagesFromDOM() {
        const allMessages = this.messagesContainer.querySelectorAll('.message');
        const syncedMessages = [];

        allMessages.forEach(msgElement => {
            const isUser = msgElement.classList.contains('user-message');
            const isAssistant = msgElement.classList.contains('assistant-message');

            if (!isUser && !isAssistant) return;

            const contentDiv = msgElement.querySelector('.message-content');
            if (!contentDiv) return;

            // 优先使用 rawContent，否则使用 textContent
            const content = contentDiv.dataset.rawContent || contentDiv.textContent || '';

            syncedMessages.push({
                role: isUser ? 'user' : 'assistant',
                content: content,
                images: [] // 图片信息可能丢失，但至少顺序正确
            });
        });

        // 用同步后的数组替换原数组
        this.messages = syncedMessages;
    }

    // 发送消息
    async sendMessage() {
        this.toggleStopButton(true); // 显示停止按钮
        this.controller = new AbortController();
        const message = this.messageInput.value.trim();
        const imageUrls = this.uploadedImageUrls.slice(); // 复制数组

        if (!message && imageUrls.length == 0 || this.isProcessing) return;

        this.setLoadingState(true);
        this.appendMessage('user', message);
        this.sessionDirty = true; // 标记会话有变更
        this.messageInput.value = '';
        this.removeAllImages(); // 清除图片预览
        this.autoResizeTextarea();

        // 重置断线重连相关状态
        this.currentStreamId = null;
        this.receivedContentLength = 0;
        this.isStreaming = true;

        // 标记流是否正常完成（收到 [DONE] 或用户取消）
        let streamCompleted = false;

        try {
            const message = this.messageInput.value.trim();

            const history = this.convertToApiMessages();
            const response = await fetch('/api/chat/stream', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    message: message,
                    history: history,
                    model: this.modelSelect.value,
                    timestamp: new Date().toISOString(),
                    EnableSearch: this.isNetworkEnabled

                }),
                signal: this.controller.signal

            });

            if (!response.ok) {
                throw new Error('Network response was not ok');
            }

            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            this.currentMessageElement = null;
            try {
                while (true) {
                    const { value, done } = await reader.read();
                    if (done) break;

                    buffer += decoder.decode(value, { stream: true });
                    const lines = buffer.split('\n');

                    buffer = lines.pop() || '';

                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            const data = line.slice(6);
                            if (data === '[DONE]') {
                                streamCompleted = true; // 正常完成
                                this.isStreaming = false;
                                continue;
                            }

                            try {
                                const parsed = JSON.parse(data);

                                // 处理 streamId（首次响应）
                                if (parsed.streamId) {
                                    this.currentStreamId = parsed.streamId;
                                    // 同时保存到 savedStreamId，方便锁屏恢复
                                    this.savedStreamId = parsed.streamId;
                                    this.savedContentLength = 0;
                                    continue;
                                }

                                if (parsed.error) {
                                    throw new Error(parsed.error);
                                }
                                if (parsed.content) {
                                    this.receivedContentLength += parsed.content.length;
                                    // 同步更新 savedContentLength
                                    this.savedContentLength = this.receivedContentLength;
                                    this.appendMessage('assistant', parsed.content, this.stream);
                                }
                            } catch (e) {
                                console.error('SSE数据解析错误:', e);
                            }
                        }
                    }
                }
            }
            catch (error) {
                if (error.name === 'AbortError') {
                    if (this.needsResume) {
                        // 锁屏自动中止，不标记为完成，保留状态
                        console.log('锁屏中止，保留状态以便恢复');
                    } else {
                        // 用户主动取消
                        console.log('用户取消');
                        streamCompleted = true;
                    }
                } else {
                    // 连接异常断开，不标记为完成，保留状态以便恢复
                    console.log('连接异常断开，保留状态以便恢复');
                    throw error;
                }
            } finally {
                reader.cancel();
            }
        } catch (error) {
            console.error('错误:', error);
            if (error.name === 'AbortError') {
                if (this.needsResume) {
                    // 锁屏中止，不显示提示
                } else {
                    // 用户主动取消
                    this.appendStreamContent('\n\n[已停止生成]');
                    streamCompleted = true;
                }
            } else {
                // 网络错误等，不显示错误信息，等待恢复
                console.log('流式传输中断，等待恢复。streamId:', this.currentStreamId);
            }

        } finally {
            this.setLoadingState(false);

            // 只有正常完成时才清理状态
            if (streamCompleted) {
                this.isStreaming = false;

                if (this.currentMessageElement) {
                    try {
                        // 等待数学公式渲染完成
                        await this.renderMath(this.currentMessageElement);
                    } catch (error) {
                        console.error('MathJax 渲染错误:', error);
                    }
                    // 查找并渲染所有 mermaid 图表
                    const mermaidCharts = this.currentMessageElement.querySelectorAll('.mermaid-chart');
                    if (mermaidCharts.length > 0) {
                        for (const chart of mermaidCharts) {
                            try {
                                const code = chart.textContent;
                                const id = chart.id;
                                await this.renderMermaidChart(code, id);
                            } catch (error) {
                                console.error('Error rendering chart:', error);
                                chart.innerHTML = `<div class="chart-error">Failed to render chart: ${error.message}</div>`;
                            }
                        }
                    }

                    const jsmindCharts = this.currentMessageElement.querySelectorAll('.jsmind-chart');
                    if (jsmindCharts.length > 0) {
                        for (const chart of jsmindCharts) {
                            try {

                                var options = {
                                    container: chart.id, // [必选] 容器的ID
                                    editable: false,                // [可选] 是否启用编辑
                                    theme: 'orange'                // [可选] 主题
                                };
                                if (window.jsMind) {
                                    var jm = new jsMind(options);
                                    jm.show();
                                }

                            } catch (error) {
                                console.error('Error rendering chart:', error);
                                chart.innerHTML = `<div class="chart-error">Failed to render chart: ${error.message}</div>`;
                            }
                        }
                    }

                    // 流式传输完成后，处理所有未增强的代码块（包括 Thoughts 折叠处理）
                    this.currentMessageElement.querySelectorAll('pre code').forEach((block) => {
                        const pre = block.parentElement;
                        if (!pre.closest('.code-block-wrapper')) {
                            this.enhanceCodeBlock(pre);
                        }
                    });
                }
                this.currentMessageElement = null;
                this.currentStreamId = null; // 只有正常完成才清除 streamId
                this.toggleStopButton(false);
                this.controller = null;
                this.uploadedImageUrls = [];

                // 自动保存会话
                this.saveCurrentSession();
            } else {
                // 连接中断，保留 isStreaming 和 currentStreamId，等待页面恢复后重连
                this.toggleStopButton(false);
                this.controller = null;
            }
        }
    }

    // chat.js 中添加方法来处理命令
    processMessage(message) {
        // 检查消息是否包含 JavaScript 命令
        const jsCommandRegex = /<js_command>(.*?)<\/js_command>/g;
        const matches = [...message.matchAll(jsCommandRegex)];

        if (matches.length > 0) {
            // 提取并移除所有命令
            let cleanMessage = message;

            matches.forEach(match => {
                try {
                    const commandData = JSON.parse(match[1]);
                    if (commandData.type === "js_command" &&
                        typeof this[commandData.function] === 'function') {
                        // 执行命令
                        this[commandData.function].apply(this, commandData.arguments);
                    }

                    // 从消息中移除命令
                    cleanMessage = cleanMessage.replace(match[0], '');
                } catch (err) {
                    console.error("处理命令时出错:", err);
                }
            });

            return cleanMessage; // 返回清理后的消息
        }

        return message; // 如果没有命令，返回原始消息
    }

    // ==================== 会话管理功能 ====================

    // 从 URL 获取 uid 参数
    getUidFromUrl() {
        const urlParams = new URLSearchParams(window.location.search);
        return urlParams.get('uid') || '';
    }

    // 生成唯一的会话 ID
    generateSessionId() {
        return 'session_' + Date.now() + '_' + Math.random().toString(36).substr(2, 9);
    }

    // 初始化侧边栏
    initSidebar() {
        const sidebar = document.getElementById('sidebar');
        const newChatBtn = document.getElementById('new-chat-btn');
        const collapseSidebarBtn = document.getElementById('collapse-sidebar-btn');
        const expandSidebarBtn = document.getElementById('expand-sidebar-btn');
        const searchInput = document.getElementById('session-search-input');
        const searchClearBtn = document.getElementById('search-clear-btn');

        if (!sidebar) return;

        // 新建会话按钮
        if (newChatBtn) {
            newChatBtn.addEventListener('click', () => this.createNewSession());
        }

        // 搜索框事件监听
        if (searchInput) {
            searchInput.addEventListener('input', (e) => {
                this.sessionSearchQuery = e.target.value.trim().toLowerCase();
                if (searchClearBtn) {
                    searchClearBtn.style.display = this.sessionSearchQuery ? 'flex' : 'none';
                }
                this.filterAndRenderSessions();
            });
        }

        // 清除搜索按钮
        if (searchClearBtn) {
            searchClearBtn.addEventListener('click', () => {
                if (searchInput) {
                    searchInput.value = '';
                    this.sessionSearchQuery = '';
                    searchClearBtn.style.display = 'none';
                    this.filterAndRenderSessions();
                }
            });
        }

        // 折叠/展开侧边栏
        if (collapseSidebarBtn) {
            collapseSidebarBtn.addEventListener('click', () => this.toggleSidebar());
        }
        if (expandSidebarBtn) {
            expandSidebarBtn.addEventListener('click', () => this.toggleSidebar());
        }

        // 应用初始折叠状态
        if (this.sidebarCollapsed) {
            sidebar.classList.add('collapsed');
            if (expandSidebarBtn) expandSidebarBtn.style.display = 'flex';
        }

        // 加载会话列表
        if (this.uid) {
            this.loadSessions();
        }
    }

    // 切换侧边栏折叠状态
    toggleSidebar() {
        const sidebar = document.getElementById('sidebar');
        const expandBtn = document.getElementById('expand-sidebar-btn');
        const chatMain = document.getElementById('chat-main');

        if (!sidebar) return;

        this.sidebarCollapsed = !this.sidebarCollapsed;

        if (this.sidebarCollapsed) {
            sidebar.classList.add('collapsed');
            if (expandBtn) expandBtn.style.display = 'flex';
        } else {
            sidebar.classList.remove('collapsed');
            if (expandBtn) expandBtn.style.display = 'none';
        }
    }

    // 加载用户会话列表
    async loadSessions() {
        if (!this.uid) return;

        const sessionsList = document.getElementById('sessions-list');
        if (!sessionsList) return;

        try {
            const response = await fetch(`/api/sessions/${encodeURIComponent(this.uid)}`);
            if (!response.ok) {
                throw new Error('获取会话列表失败');
            }

            const sessions = await response.json();
            this.allSessions = sessions; // 保存完整列表用于搜索
            this.filterAndRenderSessions();
            this.sessionsLoaded = true;
        } catch (error) {
            console.error('加载会话列表失败:', error);
            sessionsList.innerHTML = '<div class="sessions-empty">加载失败，请刷新重试</div>';
        }
    }

    // 过滤并渲染会话列表
    filterAndRenderSessions() {
        if (!this.sessionSearchQuery) {
            this.renderSessionsList(this.allSessions);
            return;
        }

        const filtered = this.allSessions.filter(session => {
            const title = (session.title || '').toLowerCase();
            const model = (session.modelName || '').toLowerCase();
            return title.includes(this.sessionSearchQuery) || model.includes(this.sessionSearchQuery);
        });

        this.renderSessionsList(filtered, this.sessionSearchQuery);
    }

    // 渲染会话列表
    renderSessionsList(sessions, highlightQuery = '') {
        const sessionsList = document.getElementById('sessions-list');
        if (!sessionsList) return;

        // 搜索无结果
        if (sessions.length === 0 && highlightQuery) {
            sessionsList.innerHTML = `
                <div class="sessions-no-results">
                    <p>没有找到匹配 "${this.escapeHtml(highlightQuery)}" 的会话</p>
                </div>
            `;
            return;
        }

        if (sessions.length === 0) {
            sessionsList.innerHTML = `
                <div class="sessions-empty">
                    <svg viewBox="0 0 24 24" width="48" height="48" fill="none" stroke="currentColor" stroke-width="1.5">
                        <path d="M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z" stroke-linecap="round" stroke-linejoin="round"/>
                    </svg>
                    <p>暂无历史会话</p>
                    <p>开始新的对话吧</p>
                </div>
            `;
            return;
        }

        sessionsList.innerHTML = sessions.map(session => {
            let titleHtml = this.escapeHtml(session.title || '新会话');
            let modelHtml = this.escapeHtml(session.modelName || '');

            // 高亮搜索关键词
            if (highlightQuery) {
                titleHtml = this.highlightText(titleHtml, highlightQuery);
                modelHtml = this.highlightText(modelHtml, highlightQuery);
            }

            return `
            <div class="session-item ${session.id === this.currentSessionId ? 'active' : ''}" data-session-id="${session.id}">
                <div class="session-item-content">
                    <div class="session-title">${titleHtml}</div>
                    <div class="session-meta">
                        <span class="session-time">${this.formatSessionTime(session.updatedAt)}</span>
                        <span class="session-model">${modelHtml}</span>
                    </div>
                </div>
                <div class="session-actions">
                    <button class="session-delete-btn" data-session-id="${session.id}" title="删除会话">
                        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2">
                            <polyline points="3 6 5 6 21 6"></polyline>
                            <path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path>
                        </svg>
                    </button>
                </div>
            </div>
        `}).join('');

        // 绑定点击事件
        sessionsList.querySelectorAll('.session-item').forEach(item => {
            item.addEventListener('click', (e) => {
                // 如果点击的是删除按钮，不切换会话
                if (e.target.closest('.session-delete-btn')) return;
                const sessionId = item.dataset.sessionId;
                this.switchSession(sessionId);
            });
        });

        // 绑定删除事件
        sessionsList.querySelectorAll('.session-delete-btn').forEach(btn => {
            btn.addEventListener('click', (e) => {
                e.stopPropagation();
                const sessionId = btn.dataset.sessionId;
                this.confirmDeleteSession(sessionId);
            });
        });
    }

    // 高亮搜索文本
    highlightText(text, query) {
        if (!query) return text;
        const regex = new RegExp(`(${query.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')})`, 'gi');
        return text.replace(regex, '<mark class="search-highlight">$1</mark>');
    }

    // 格式化会话时间
    formatSessionTime(dateString) {
        const date = new Date(dateString);
        const now = new Date();
        const diffMs = now - date;
        const diffDays = Math.floor(diffMs / (1000 * 60 * 60 * 24));

        if (diffDays === 0) {
            return date.toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' });
        } else if (diffDays === 1) {
            return '昨天';
        } else if (diffDays < 7) {
            return `${diffDays}天前`;
        } else {
            return date.toLocaleDateString('zh-CN', { month: 'short', day: 'numeric' });
        }
    }

    // HTML转义
    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // 切换会话
    async switchSession(sessionId) {
        if (sessionId === this.currentSessionId) return;

        // 先保存当前会话（只有在有变更时才保存）
        if (this.messages.length > 0 && this.sessionDirty) {
            await this.saveCurrentSession();
        }

        try {
            const response = await fetch(`/api/sessions/${encodeURIComponent(this.uid)}/${encodeURIComponent(sessionId)}`);
            if (!response.ok) {
                throw new Error('获取会话详情失败');
            }

            const session = await response.json();

            // 清空当前消息
            this.clearMessages();

            // 加载会话消息
            this.currentSessionId = sessionId;
            this.currentSessionTitle = session.title; // 保留已有的标题
            if (session.modelName) {
                this.modelSelect.value = session.modelName;
                this.toggleImageUploadButton();
            }

            // 恢复消息
            if (session.messages && session.messages.length > 0) {
                session.messages.forEach(msg => {
                    // 显示消息（appendMessageWithoutSave 会通过 appendMessage 添加到 this.messages）
                    this.appendMessageWithoutSave(msg.role, msg.content, msg.imageUrls);
                });
            }

            // 保存原始会话数据快照（用于脏标志比较）
            this.originalSessionData = {
                modelName: session.modelName || '',
                messageCount: (session.messages || []).length
            };
            // 重置脏标志
            this.sessionDirty = false;

            // 更新侧边栏选中状态
            this.updateSessionActiveState(sessionId);

        } catch (error) {
            console.error('加载会话失败:', error);
            alert('加载会话失败，请重试');
        }
    }

    // 不触发保存的消息添加方法（用于加载历史会话）
    appendMessageWithoutSave(role, content, imageUrls = []) {
        // 暂存当前的 uploadedImageUrls
        const originalImageUrls = this.uploadedImageUrls.slice();

        // 如果有图片，设置 uploadedImageUrls 以便 appendMessage 正确处理
        if (imageUrls && imageUrls.length > 0) {
            this.uploadedImageUrls = imageUrls;
        } else {
            this.uploadedImageUrls = [];
        }

        // 使用 appendMessage 但不是流式模式
        this.appendMessage(role, content, false);

        // 恢复原来的 uploadedImageUrls
        this.uploadedImageUrls = originalImageUrls;

        // 重置当前消息元素
        this.currentMessageElement = null;
    }

    // 更新侧边栏选中状态
    updateSessionActiveState(activeSessionId) {
        const sessionsList = document.getElementById('sessions-list');
        if (!sessionsList) return;

        sessionsList.querySelectorAll('.session-item').forEach(item => {
            if (item.dataset.sessionId === activeSessionId) {
                item.classList.add('active');
            } else {
                item.classList.remove('active');
            }
        });
    }

    // 创建新会话
    async createNewSession() {
        // 保存当前会话（只有在有变更时才保存）
        if (this.messages.length > 0 && this.sessionDirty) {
            await this.saveCurrentSession();
        }

        // 清空并创建新会话
        this.clearMessages();
        this.currentSessionId = this.generateSessionId();
        this.currentSessionTitle = null; // 重置标题，等待首次保存时生成

        // 重置脏标志和原始数据
        this.sessionDirty = false;
        this.originalSessionData = null;

        // 更新侧边栏
        this.updateSessionActiveState(this.currentSessionId);

        // 聚焦到输入框
        this.messageInput.focus();
    }

    // 保存当前会话
    async saveCurrentSession() {
        if (!this.uid || this.messages.length === 0) return;

        // 只在没有已保存标题时生成新标题
        if (!this.currentSessionTitle) {
            this.currentSessionTitle = this.generateSessionTitle();
        }

        const modelName = this.modelSelect.value;

        const saveRequest = {
            sessionId: this.currentSessionId,
            uid: this.uid,
            title: this.currentSessionTitle,
            modelName: modelName,
            messages: this.messages.map(msg => ({
                role: msg.role,
                content: msg.content,
                imageUrls: msg.images || []
            }))
        };

        try {
            const response = await fetch('/api/sessions/save', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(saveRequest)
            });

            if (!response.ok) {
                throw new Error('保存会话失败');
            }

            // 刷新会话列表
            await this.loadSessions();
        } catch (error) {
            console.error('保存会话失败:', error);
        }
    }

    // 生成会话标题
    generateSessionTitle() {
        if (this.messages.length === 0) return '新会话';

        // 从第一条用户消息生成标题
        const firstUserMessage = this.messages.find(msg => msg.role === 'user');
        if (firstUserMessage && firstUserMessage.content) {
            let title = firstUserMessage.content.trim();
            // 截取前50个字符
            if (title.length > 50) {
                title = title.substring(0, 50) + '...';
            }
            return title;
        }

        return '新会话';
    }

    // 确认删除会话
    confirmDeleteSession(sessionId) {
        const overlay = document.createElement('div');
        overlay.className = 'delete-confirm-overlay';
        overlay.innerHTML = `
            <div class="delete-confirm-dialog">
                <h3>删除会话</h3>
                <p>确定要删除这个会话吗？此操作不可撤销。</p>
                <div class="delete-confirm-actions">
                    <button class="delete-confirm-cancel">取消</button>
                    <button class="delete-confirm-delete">删除</button>
                </div>
            </div>
        `;

        document.body.appendChild(overlay);

        overlay.querySelector('.delete-confirm-cancel').addEventListener('click', () => {
            document.body.removeChild(overlay);
        });

        overlay.querySelector('.delete-confirm-delete').addEventListener('click', async () => {
            await this.deleteSession(sessionId);
            document.body.removeChild(overlay);
        });

        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) {
                document.body.removeChild(overlay);
            }
        });
    }

    // 删除会话
    async deleteSession(sessionId) {
        try {
            const response = await fetch(`/api/sessions/${encodeURIComponent(sessionId)}`, {
                method: 'DELETE'
            });

            if (!response.ok) {
                throw new Error('删除会话失败');
            }

            // 如果删除的是当前会话，创建新会话
            if (sessionId === this.currentSessionId) {
                this.clearMessages();
                this.currentSessionId = this.generateSessionId();
            }

            // 刷新会话列表
            await this.loadSessions();
        } catch (error) {
            console.error('删除会话失败:', error);
            alert('删除会话失败，请重试');
        }
    }

    // 设置页面可见性变化监听（用于断线重连）
    setupVisibilityHandler() {
        // 调试面板更新
        this.updateDebug = (event) => {
            const el = document.getElementById('debug-info');
            if (el) {
                // 简单追加日志
                const current = el.innerHTML;
                const lines = current.split('<br>');
                if (lines.length > 15) lines.shift(); // 增加到15行
                lines.push(event);
                el.innerHTML = lines.join('<br>');
            }
        };

        document.addEventListener('visibilitychange', async () => {
            if (document.visibilityState === 'hidden') {
                // 检测是否为移动设备（移动设备锁屏可能导致网络中断，需要主动中止以便恢复）
                const isMobile = /Android|webOS|iPhone|iPad|iPod|BlackBerry|IEMobile|Opera Mini/i.test(navigator.userAgent);

                // 只在移动设备上主动中止流式传输
                // 桌面浏览器切换标签页时保持传输不中断
                if (isMobile && this.isStreaming && this.controller && this.savedStreamId) {
                    // savedStreamId 已在接收时持续更新，这里只需标记和中止
                    this.needsResume = true;
                    this.controller.abort();
                }
            } else if (document.visibilityState === 'visible') {
                // 页面重新可见，检查是否需要恢复流式传输
                if (this.needsResume && this.savedStreamId) {
                    const sid = this.savedStreamId;
                    const slen = this.savedContentLength;
                    this.needsResume = false;
                    this.savedStreamId = null;
                    this.savedContentLength = 0;
                    // 等待一小段时间让中止完成
                    await new Promise(r => setTimeout(r, 200));
                    await this.tryResumeStreamWithId(sid, slen);
                }
            }
        });

        // 也监听 focus 事件作为备选
        window.addEventListener('focus', async () => {
            if (this.needsResume && this.savedStreamId) {
                this.needsResume = false;
                await new Promise(r => setTimeout(r, 200));
                await this.tryResumeStreamWithId(this.savedStreamId, this.savedContentLength);
                this.savedStreamId = null;
                this.savedContentLength = 0;
            }
        });
    }

    // 尝试恢复断线的流式传输
    async tryResumeStream() {
        // 检查是否应该继续轮询
        if (!this.currentStreamId || !this.isStreaming) {
            return;
        }

        try {
            // 创建 AbortController 以支持停止按钮
            this.controller = new AbortController();
            const signal = this.controller.signal;

            const url = `/api/chat/stream/${this.currentStreamId}/resume?offset=${this.receivedContentLength}`;
            const response = await fetch(url, { signal });

            if (!response.ok) {
                if (response.status === 404) {
                    // 流已过期
                    this.isStreaming = false;
                    this.currentStreamId = null;
                    return;
                }
                throw new Error('恢复流失败: ' + response.status);
            }

            const result = await response.json();

            // 重要：总是同步后端返回的总长度，防止offset偏差
            if (typeof result.totalLength === 'number') {
                this.receivedContentLength = result.totalLength;
            }

            // 如果有新内容，追加显示
            if (result.content && result.content.length > 0) {
                // 确保有消息元素可以追加（如果断线时丢失了引用）
                if (!this.currentMessageElement) {
                    // 查找页面上最后一个助手消息
                    const assistantMessages = this.messagesContainer.querySelectorAll('.message.assistant-message');
                    if (assistantMessages.length > 0) {
                        this.currentMessageElement = assistantMessages[assistantMessages.length - 1];
                    }
                }

                this.appendStreamContent(result.content);
            }

            // 如果流已完成
            if (result.isCompleted) {
                this.isStreaming = false;
                this.setLoadingState(false);
                this.toggleStopButton(false);

                // 渲染最终内容
                if (this.currentMessageElement) {
                    const contentDiv = this.currentMessageElement.querySelector('.message-content');
                    if (contentDiv) {
                        // 重新渲染整个内容，确保预处理正确应用
                        const processedContent = this.preprocessMarkdown(this.messageBuffer);
                        contentDiv.innerHTML = marked.parse(processedContent);
                        contentDiv.dataset.rawContent = this.messageBuffer;
                    }

                    // 更新 copyButton 的内容，确保导出数据完整
                    const copyButton = this.currentMessageElement.querySelector('.copy-button');
                    if (copyButton) {
                        copyButton.dataset.copyContent = this.messageBuffer;
                    }

                    try {
                        await this.renderMath(this.currentMessageElement);
                    } catch (error) {
                        console.error('MathJax 渲染错误:', error);
                    }

                    // 处理所有未增强的代码块（包括 Thoughts 折叠处理）
                    this.currentMessageElement.querySelectorAll('pre code').forEach((block) => {
                        const pre = block.parentElement;
                        if (!pre.closest('.code-block-wrapper')) {
                            this.enhanceCodeBlock(pre);
                        }
                    });
                }

                this.currentMessageElement = null;
                this.currentStreamId = null;
                this.saveCurrentSession();
            } else {
                // 流还在继续，设置定时轮询（先检查是否仍在流式传输中）
                if (this.isStreaming) {
                    setTimeout(() => this.tryResumeStream(), 1000);
                }
            }
        } catch (error) {
            // 如果是用户主动停止，不显示错误
            if (error.name === 'AbortError') {
                console.log('用户停止了流式传输');
            } else {
                console.error('恢复流式传输失败:', error);
            }
        }
    }

    // 使用指定的 streamId 恢复流式传输（用于锁屏恢复）
    async tryResumeStreamWithId(streamId, offset) {
        // 恢复流式状态：禁用输入，显示停止按钮
        this.isStreaming = true;
        this.setLoadingState(true);
        this.toggleStopButton(true);

        // 恢复 messageBuffer - 从最后一条消息获取已有内容（修复恢复后内容拼接问题）
        // 无论 currentMessageElement 是否存在，都需要确保 messageBuffer 正确同步
        const assistantMessages = this.messagesContainer.querySelectorAll('.message.assistant-message');
        if (assistantMessages.length > 0) {
            this.currentMessageElement = assistantMessages[assistantMessages.length - 1];
            const contentDiv = this.currentMessageElement.querySelector('.message-content');
            if (contentDiv) {
                // 优先从 rawContent 恢复，其次从 messages 数组恢复
                if (contentDiv.dataset.rawContent) {
                    this.messageBuffer = contentDiv.dataset.rawContent;
                } else if (this.messages.length > 0 && this.messages[this.messages.length - 1].role === 'assistant') {
                    this.messageBuffer = this.messages[this.messages.length - 1].content || '';
                } else {
                    this.messageBuffer = '';
                }
            }
        }

        try {
            // 创建一个新的 Controller 用于此次恢复请求，以便支持手动停止
            this.controller = new AbortController();
            const signal = this.controller.signal;

            const url = `/api/chat/stream/${streamId}/resume?offset=${offset}`;
            const response = await fetch(url, { signal });

            if (!response.ok) {
                if (response.status === 404) {
                    this.isStreaming = false;
                    this.setLoadingState(false);
                    this.toggleStopButton(false);
                    return;
                }
                throw new Error('恢复流失败: ' + response.status);
            }

            const result = await response.json();

            // 重要：总是同步后端返回的总长度
            if (typeof result.totalLength === 'number') {
                this.receivedContentLength = result.totalLength;
            }

            // 如果有新内容，追加显示
            if (result.content && result.content.length > 0) {
                // 确保有消息元素可以追加（如果断线时丢失了引用）
                if (!this.currentMessageElement) {
                    const assistantMessages = this.messagesContainer.querySelectorAll('.message.assistant-message');
                    if (assistantMessages.length > 0) {
                        this.currentMessageElement = assistantMessages[assistantMessages.length - 1];
                    }
                }

                this.appendStreamContent(result.content);
            }


            // 如果流已完成
            if (result.isCompleted) {
                if (this.updateDebug) this.updateDebug('completed');
                this.isStreaming = false;
                this.setLoadingState(false);
                this.toggleStopButton(false);

                if (this.currentMessageElement) {
                    const contentDiv = this.currentMessageElement.querySelector('.message-content');
                    if (contentDiv) {
                        // 重新渲染整个内容，确保预处理正确应用
                        const processedContent = this.preprocessMarkdown(this.messageBuffer);
                        contentDiv.innerHTML = marked.parse(processedContent);
                        contentDiv.dataset.rawContent = this.messageBuffer;
                    }

                    // 更新 copyButton 的内容，确保导出数据完整
                    const copyButton = this.currentMessageElement.querySelector('.copy-button');
                    if (copyButton) {
                        copyButton.dataset.copyContent = this.messageBuffer;
                    }

                    try {
                        await this.renderMath(this.currentMessageElement);
                    } catch (error) {
                        console.error('MathJax 渲染错误:', error);
                    }

                    // 处理所有未增强的代码块（包括 Thoughts 折叠处理）
                    this.currentMessageElement.querySelectorAll('pre code').forEach((block) => {
                        const pre = block.parentElement;
                        if (!pre.closest('.code-block-wrapper')) {
                            this.enhanceCodeBlock(pre);
                        }
                    });
                }

                this.currentMessageElement = null;
                this.currentStreamId = null;

                // 从 DOM 重新同步 messages 数组，确保顺序正确
                this.syncMessagesFromDOM();

                this.saveCurrentSession();

            } else {
                // 流还在继续，设置定时轮询（先检查是否仍在流式传输中）
                if (this.isStreaming) {
                    this.currentStreamId = streamId; // 恢复 streamId 以便继续轮询
                    setTimeout(() => this.tryResumeStream(), 1000);
                }
            }
        } catch (error) {
            // 如果是用户主动停止，不显示错误
            if (error.name === 'AbortError') {
                if (this.updateDebug) this.updateDebug('stopped');
                console.log('用户停止了流式传输');
            } else {
                if (this.updateDebug) this.updateDebug(`err:${error.message}`);
                console.error('恢复流式传输失败:', error);
            }
        }
    }

}

// 初始化
document.addEventListener('DOMContentLoaded', () => {
    const chat = new ChatUI();
});
