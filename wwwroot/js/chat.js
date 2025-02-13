
class ChatUI {
    constructor() {

        //this.networkButton = document.getElementById('network-search-button');
        //this.networkIcon = document.getElementById('network-icon');
        this.isNetworkEnabled = false; // 默认启用联网搜索

        this.MathJax = window.MathJax;
        this.messages = []; // 存储聊天记录
        this.session_id = '';
        this.messageBuffer = '';
        this.controller = null; // 用于中断请求的 AbortController
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

        // 设置图片上传事件监听
        this.uploadImageButton.addEventListener('click', () => this.imageInput.click());
        this.imageInput.addEventListener('change', (event) => this.handleImageUpload(event));
        //this.removeImageButton.addEventListener('click', () => this.removeAllImages());

        // 设置模型选择事件监听
        this.modelSelect.addEventListener('change', () => this.toggleImageUploadButton());
        
        //this.networkButton.addEventListener('click', () => {
        //    this.isNetworkEnabled = !this.isNetworkEnabled;
        //    if (this.isNetworkEnabled) {
        //        this.networkIcon.classList.remove('bi-wifi-off');
        //        this.networkIcon.classList.add('bi-globe2');
        //        this.networkButton.title = "禁用联网搜索";
               
        //    } else {
        //        this.networkIcon.classList.remove('bi-globe2');
        //        this.networkIcon.classList.add('bi-wifi-off');
        //        this.networkButton.title = "启用联网搜索";
        //        // 发送设置更新到后端
              
        //    }
        //});

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
        //if (model && model.enableSearch) {
        //    this.networkButton.style.display = 'flex'; // 或 'block'，根据您的CSS布局
        //} else {
        //    this.networkButton.style.display = 'none';
        //}
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
        this.previewContainer.style.display = "display: none;"
        
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


    // 添加压缩图片的辅助方法
    // 修改 compressImage 方法以提高压缩后图片的清晰度
    //compressImage(file, maxWidth = 1024, quality = 0.8) {
    //    return new Promise((resolve, reject) => {
    //        const img = new Image();
    //        const canvas = document.createElement('canvas');
    //        const ctx = canvas.getContext('2d');

    //        img.onload = () => {
    //            let { width, height } = img;

    //            // 仅在图片宽度大于 maxWidth 时进行缩放
    //            if (width > maxWidth) {
    //                height = height * (maxWidth / width);
    //                width = maxWidth;
    //            }

    //            canvas.width = width;
    //            canvas.height = height;
    //            ctx.drawImage(img, 0, 0, width, height);

    //            // 根据原始文件类型选择适当的输出格式
    //            const fileExtension = file.name.split('.').pop().toLowerCase();
    //            let outputFormat = 'image/jpeg'; // 默认格式

    //            if (fileExtension === 'png') {
    //                outputFormat = 'image/png';
    //            } else if (fileExtension === 'webp') {
    //                outputFormat = 'image/webp';
    //            }

    //            canvas.toBlob((blob) => {
    //                if (blob) {
    //                    const compressedFileName = file.name.replace(/\.[^/.]+$/, `.${outputFormat.split('/')[1]}`);
    //                    const compressedFile = new File([blob], compressedFileName, {
    //                        type: outputFormat,
    //                        lastModified: Date.now()
    //                    });
    //                    resolve(compressedFile);
    //                } else {
    //                    reject(new Error('图片压缩失败'));
    //                }
    //            }, outputFormat, quality);
    //        };

    //        img.onerror = () => {
    //            reject(new Error('图片加载失败'));
    //        };

    //        const reader = new FileReader();
    //        reader.onload = (e) => {
    //            img.src = e.target.result;
    //        };
    //        reader.readAsDataURL(file);
    //    });
    //}


    // 移除最后一条用户消息（用于移除“正在上传图片...”的提示）
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
        this.sendButton.disabled = isEmpty && this.uploadedImageUrls.length==0 || this.isProcessing;
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
        if (language === 'mermaid') {
            const chartId = `mermaid-${Math.random().toString(36).slice(2, 11)}`;
            const chart = document.createElement('div');
            chart.className = 'mermaid-chart';
            chart.id = chartId
            chart.appendChild(pre);
            wrapper.appendChild(chart);
        }
        else
        {
            //包装包装用mermaid-chartmermaid
            if (language === 'jsmind') {
                const chartId = `jsmind-${Math.random().toString(36).slice(2, 11)}`;
                const chart = document.createElement('div');
                chart.className = 'jsmind-chart';
                chart.id = chartId
                chart.appendChild(pre);
                wrapper.appendChild(chart);
            }
            else {
                if (language === 'Thoughts') {
                    
                    header.style.maxWidth = '700px'
                    wrapper.style.maxWidth = '700px'
                   
                    pre.style.maxWidth = '700px'

                    header.style.width = '100%'; // 添加这行代码，设置宽度为100%

                    wrapper.style.width = '100%'; // 添加这行代码，设置宽度为100%
                    wrapper.style.height ='auto'

                    pre.style.width = '100%';          // 代码块宽度100%
                    pre.style.height = 'auto';         // 代码块高度自动

                    pre.style.overflow = 'hidden';     // 隐藏滚动条
                    pre.style.whiteSpace = 'pre-wrap'; // 允许内容自动换行
                    pre.style.overflowwrap = 'break-word';      // 防止长内容不换行
                    pre.style.wordWrap = 'break-word';      // 防止长内容不换行
                    code.style.width = '100%';          // 代码块宽度100%
                    code.style.height = 'auto'; 
                    code.style.overflow = 'hidden';     // 隐藏滚动条
                    code.style.whiteSpace = 'pre-wrap'; // 允许内容自动换行
                    code.style.overflowwrap = 'break-word';      // 防止长内容不换行

                    const details = document.createElement('details');
                    details.open = true;
                    const summary = document.createElement('summary');
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

        // 添加复制按钮
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
            const pre = button.closest('.code-block-wrapper').querySelector('pre');
            /*const code = pre.textContent;*/
            const code = button.dataset.copyContent
            try {
                await navigator.clipboard.writeText(code);
                this.showCopyFeedback(button, true);
            } catch (err) {
                console.error('复制失败:', err);
                this.showCopyFeedback(button, false);
            }
        });
    }

    showCopyFeedback(button, success) {
        const originalHTML = button.innerHTML;
        button.innerHTML = success ?
            '<i class="bi bi-clipboard-check"></i>' :
            '<i class="bi bi-clipboard-x"></i>';

        setTimeout(() => {
            button.innerHTML = originalHTML;
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

        // 创建删除按钮
        const deleteButton = document.createElement('button');
        deleteButton.className = 'delete-button';
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
            contentDiv.innerHTML = imagesHtml + marked.parse(content);
            // 为所有上传的图片添加双击事件
            contentDiv.querySelectorAll('.uploaded-image-preview').forEach(img => {
                img.addEventListener('dblclick', () => {
                    this.showFullSizeImage(img.src);
                });
            });
            const copyButton = this.createCopyButton(content);
            
            actionsDiv.appendChild(deleteButton);
            actionsDiv.appendChild(copyButton);
        }
        else {
            try {
                contentDiv.innerHTML = marked.parse(content);
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
                
                actionsDiv.appendChild(deleteButton);
                actionsDiv.appendChild(copyButton);
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

    // 删除消息的方法
    deleteMessage(messageElement) {
        if (confirm('确定要删除这条消息吗？')) {
            const index = Array.from(this.messagesContainer.children).indexOf(messageElement);
            if (index > -1) {
                this.messages.splice(index, 1);
            }
            this.messagesContainer.removeChild(messageElement);
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
        copyButton.setAttribute('aria-label', 'Copy');
        copyButton.innerHTML = `
            <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                <path fill="currentColor" d="M0 6.75C0 5.784.784 5 1.75 5h1.5a.75.75 0 0 1 0 1.5h-1.5a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-1.5a.75.75 0 0 1 1.5 0v1.5A1.75 1.75 0 0 1 9.25 16h-7.5A1.75 1.75 0 0 1 0 14.25v-7.5z"/>
                <path fill="currentColor" d="M5 1.75C5 .784 5.784 0 6.75 0h7.5C15.216 0 16 .784 16 1.75v7.5A1.75 1.75 0 0 1 14.25 11h-7.5A1.75 1.75 0 0 1 5 9.25v-7.5zm1.75-.25a.25.25 0 0 0-.25.25v7.5c0 .138.112.25.25.25h7.5a.25.25 0 0 0 .25-.25v-7.5a.25.25 0 0 0-.25-.25h-7.5z"/>
            </svg>
        `;

        copyButton.addEventListener('click', async () => {
            try {
                const contentToCopy = copyButton.dataset.copyContent || textToCopy;
                // 确保复制完整内容
                await navigator.clipboard.writeText(contentToCopy);
                //// 确保复制完整内容
                //await navigator.clipboard.writeText(copyButton.dataset.copyContent);

                // 更新按钮状态
                const originalHTML = copyButton.innerHTML;
                copyButton.innerHTML = `
                    <svg class="icon" viewBox="0 0 16 16" xmlns="http://www.w3.org/2000/svg">
                        <path fill="currentColor" d="M13.78 4.22a.75.75 0 0 1 0 1.06l-7.25 7.25a.75.75 0 0 1-1.06 0L2.22 9.28a.751.751 0 0 1 .018-1.042.751.751 0 0 1 1.042-.018L6 10.94l6.72-6.72a.75.75 0 0 1 1.06 0Z"/>
                    </svg>
                `;
                copyButton.classList.add('copy-success');

                // 2秒后恢复原始状态
                setTimeout(() => {
                    copyButton.innerHTML = originalHTML;
                    copyButton.classList.remove('copy-success');
                }, 2000);
            } catch (err) {
                console.error('复制失败:', err);
            }
        });

        return copyButton;
    }

    setupMarked() {

        const renderer = new marked.Renderer();
        const originalCode = renderer.code.bind(renderer);
        // 修改链接渲染器
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

            // 处理文本内容
            const safeText = text || (typeof href === 'string' ? href : href.text);
            const titleAttr = title ? ` title="${title.replace(/"/g, '&quot;')}"` : '';

            // 返回安全的链接 HTML
            return `<a href="${safeHref}" target="_blank" rel="noopener noreferrer" class="external-link"${titleAttr}>${safeText}<svg class="external-link-icon" width="12" height="12" viewBox="0 0 12 12">
            <path fill="currentColor" d="M3.75 3v-1h6.5v6.5h-1V4.31L3.81 9.69l-.71-.71L8.69 3.5H3.75z"/>
        </svg></a>`;
        };

        // 修改图片渲染器
        renderer.image = (href, title, text) => {
            // 确保所有参数都有效
            const safeHref = href || '';
            const safeAlt = text ? ` alt="${text.replace(/"/g, '&quot;')}"` : '';
            const titleAttr = title ? ` title="${title.replace(/"/g, '&quot;')}"` : '';

            // 如果没有有效的图片地址,返回替代文本
            if (!safeHref) {
                return text || '';
            }

            // 返回安全的图片HTML
            return `<img src="${safeHref}"${safeAlt}${titleAttr} class="message-image" ondblclick="window.chatUI.showFullSizeImage('${safeHref}')">`;
        };

        renderer.code = (code, language) => {
            if (language === 'mermaid') {
                const chartId = `mermaid-${Math.random().toString(36).substr(2, 9)}`;
                return `<div class="mermaid-chart" id="${chartId}">${code}</div>`;
            }
            else {
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

                /*return code;*/
            }
            return originalCode(code, language);
        };

        //// 配置 marked
        //marked.setOptions({
        //    renderer: renderer,
        //    gfm: true,
        //    tables: true,
        //    breaks: true,
        //    pedantic: false,
        //    smartLists: true,
        //    smartypants: false,
        //    sanitize: false
        //});
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
            if (MathJax && MathJax.typesetPromise) {
                MathJax.typesetPromise()
                    .catch(err => console.error('MathJax 渲染错误:', err));

            }

        };

        // 导出renderMath方法供外部使用
        this.renderMath = renderMath;
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
            await mermaid.run({
                querySelector: `#${containerId}`
            });

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
            await this.appendMessage(message);
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

    async appendMessage(message) {
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

            if (isStreaming || role==="user") {
                this.currentMessageElement = messageDiv;
            }
            else
            {
                this.currentMessageElement = messageDiv;
                // 更新现有消息的内容
                const contentDiv = this.currentMessageElement.querySelector('.message-content');
                const copyButton = this.currentMessageElement.querySelector('.copy-button');

                if (!contentDiv.dataset.rawContent) {
                    contentDiv.dataset.rawContent = '';
                }
                contentDiv.dataset.rawContent = content;

                // 更新内存中最后一条消息的内容
                if (this.messages.length > 0) {
                    this.messages[this.messages.length - 1].content = contentDiv.dataset.rawContent;
                    this.messages[this.messages.length - 1].images = this.uploadedImageUrls.slice(); // 确保 images 更新
                }

                copyButton.dataset.copyContent = contentDiv.dataset.rawContent;

                try {

                    contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);

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
        } else {
            // 更新现有消息的内容
            const contentDiv = this.currentMessageElement.querySelector('.message-content');
            const copyButton = this.currentMessageElement.querySelector('.copy-button');

            if (!contentDiv.dataset.rawContent) {
                contentDiv.dataset.rawContent = '';
            }
            contentDiv.dataset.rawContent += content;

            // 更新内存中最后一条消息的内容
            if (this.messages.length > 0) {
                this.messages[this.messages.length - 1].content = contentDiv.dataset.rawContent;
                this.messages[this.messages.length - 1].images = this.uploadedImageUrls.slice(); // 确保 images 更新
            }

            copyButton.dataset.copyContent = contentDiv.dataset.rawContent;

            try {

                contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);

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
            const fullMessageDiv = this.currentMessageElement.querySelector('.full-message');
            const contentDiv = this.currentMessageElement.querySelector('.message-content');

            if (fullMessageDiv && contentDiv) {
                this.messageBuffer += content;
                fullMessageDiv.textContent = this.messageBuffer;

                try {

                    // 渲染 markdown 内容
                    contentDiv.innerHTML = marked.parse(this.messageBuffer);

                    // 处理所有代码块
                    contentDiv.querySelectorAll('pre code').forEach((block) => {
                        // 添加语言类标识
                        const language = block.getAttribute('class') || '';
                        if (language) {
                            block.parentElement.classList.add('language-' + language.replace('language-', ''));
                        }

                        // 应用高亮
                        hljs.highlightElement(block);
                    });
                } catch (e) {
                    console.error('Markdown 渲染错误:', e);
                    contentDiv.textContent = this.messageBuffer;
                }

                this.scrollToBottom();
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
            if (msg.role && (msg.content || msg.images.length>0)) {
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

    // 发送消息
    async sendMessage() {
        this.toggleStopButton(true); // 显示停止按钮
        this.controller = new AbortController();
        const message = this.messageInput.value.trim();
        const imageUrls = this.uploadedImageUrls.slice(); // 复制数组

        if (!message && imageUrls.length==0 || this.isProcessing) return;

        this.setLoadingState(true);
        this.appendMessage('user', message);
        this.messageInput.value = '';
        this.removeAllImages(); // 清除图片预览
        this.autoResizeTextarea();
        //this.uploadedImageUrl = null; // 清除已上传的图片URL
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
                    EnableSearch:this.isNetworkEnabled
                    
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
                            if (data === '[DONE]') continue;

                            try {
                                const parsed = JSON.parse(data);
                                if (parsed.error) {
                                    throw new Error(parsed.error);
                                }
                                if (parsed.content) {
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
                    console.log('请求被用户取消');
                } else {
                    throw error;
                }
            } finally {
                reader.cancel();
            }
        } catch (error) {
            console.error('错误:', error);
            if (error.name === 'AbortError') {
                this.appendStreamContent('\n\n[已停止生成]');
            } else {
                this.appendStreamContent('\n\n[发生错误]');
            }

        } finally {
            this.setLoadingState(false);


            if (this.currentMessageElement) {
                //渲染数学公式
                this.renderMath(this.currentMessageElement);
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
                            var jm = new jsMind(options);
                            jm.show();
                        } catch (error) {
                            console.error('Error rendering chart:', error);
                            chart.innerHTML = `<div class="chart-error">Failed to render chart: ${error.message}</div>`;
                        }
                    }
                }
            }
            this.currentMessageElement = null;
            this.toggleStopButton(false); // 隐藏停止按钮
            this.controller = null;
            this.uploadedImageUrls = []; // 清除已上传的图片URLs
        }
    }
}

// 初始化
document.addEventListener('DOMContentLoaded', () => {
    const chat = new ChatUI();
});
