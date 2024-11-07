
class ChatUI {
    constructor() {
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
        
        // 状态标志
        this.isProcessing = false;
        this.currentMessageElement = null;
        this.copyInProgress = false;
        this.currentUser = '我';
        // 在 Chat 类构造函数中添加主题配置
        mermaid.initialize({
            startOnLoad: false,
            theme: 'default',
            securityLevel: 'loose',
            themeVariables: {
                // 明亮主题
                primaryColor: '#326de6',
                primaryTextColor: '#fff',
                primaryBorderColor: '#2251c9',
                lineColor: '#666',
                secondaryColor: '#f4f4f4',
                tertiaryColor: '#fff',

                // 暗色主题支持
                darkMode: window.matchMedia('(prefers-color-scheme: dark)').matches,
                background: '#0d1117',
                mainBkg: '#161b22',
                secondaryBkg: '#21262d',
                mainContrastColor: '#c9d1d9',
                darkTextColor: '#8b949e',
                lineColor: '#30363d',
                border1: '#30363d',
                border2: '#30363d',
                arrowheadColor: '#8b949e'
            }
        });

        this.setupMarked();

        this.init();
    }

    init() {
        
        this.sendButton.addEventListener('click', () => this.sendMessage());
        this.messageInput.addEventListener('input', () => this.autoResizeTextarea());
        this.messageInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                e.preventDefault();
                this.sendMessage();
            }
        });
        
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
        this.sendButton.disabled = isEmpty || this.isProcessing;
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

        // 获取语言
        const language = this.detectLanguage(code);

        // 创建标题栏
        const header = this.createCodeHeader(language);

        // 重新组织结构
        pre.parentNode.insertBefore(wrapper, pre);
        wrapper.appendChild(header);
        wrapper.appendChild(pre);
    }

    detectLanguage(codeElement) {
        const classes = Array.from(codeElement.classList);
        const langClass = classes.find(cls => cls.startsWith('language-'));
        return langClass ? langClass.replace('language-', '') : 'plaintext';
    }

    createCodeHeader(language) {
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

        // 添加复制功能
        this.addCopyButtonListener(copyButton);

        header.appendChild(copyButton);

        return header;
    }

    addCopyButtonListener(button) {
        button.addEventListener('click', async () => {
            const pre = button.closest('.code-block-wrapper').querySelector('pre');
            const code = pre.textContent;

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

        //// 创建头像和图标容器
        //const avatarDiv = document.createElement('div');
        //avatarDiv.className = 'message-avatar';

        //// 根据角色选择不同的图标
        //const iconSvg = this.getIconByRole(role);
        //avatarDiv.innerHTML = iconSvg;

        const containerDiv = document.createElement('div');
        containerDiv.className = 'message-container';

        const headerDiv = document.createElement('div');
        headerDiv.className = 'message-header';



        const roleSpan = document.createElement('span');
        roleSpan.className = 'message-role';
        roleSpan.textContent = role === 'assistant' ? 'Ai助手' : '您';

        const actionsDiv = document.createElement('div');
        actionsDiv.className = 'message-actions';

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content markdown-body';
        contentDiv.dataset.rawContent = content;

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
            actionsDiv.appendChild(copyButton);
        } catch (e) {
            console.error('Markdown 渲染错误:', e);
            contentDiv.textContent = content;
        }
        //messageDiv.appendChild(avatarDiv);
        headerDiv.appendChild(roleSpan);
        headerDiv.appendChild(actionsDiv);
        containerDiv.appendChild(headerDiv);
        containerDiv.appendChild(contentDiv);
        messageDiv.appendChild(containerDiv);

        return { messageDiv, contentDiv };
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

        // 重写代码块渲染
        renderer.code = (code, language) => {
            if (language === 'mermaid') {
                const chartId = `mermaid-${Math.random().toString(36).substr(2, 9)}`;
                return `<div class="mermaid-chart" id="${chartId}">${code}</div>`;
            }
            return originalCode(code, language);
        };

        // 配置 marked
        marked.setOptions({
            renderer: renderer,
            highlight: function (code, lang) {
                if (lang && hljs.getLanguage(lang)) {
                    try {
                        return hljs.highlight(code, { language: lang }).value;
                    } catch (err) {
                        console.error('代码高亮错误:', err);
                    }
                }
                try {
                    return hljs.highlightAuto(code).value;
                } catch (err) {
                    console.error('代码高亮错误:', err);
                }
                return code; // 如果高亮失败，返回原始代码
            },
            breaks: true,
            gfm: true
        });
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

    async renderMermaidChart(code, id) {
        return new Promise((resolve, reject) => {
            try {
                mermaid.render(id, code, (svgCode) => {
                    const container = document.getElementById(id);
                    if (container) {
                        container.innerHTML = svgCode;
                    }
                    resolve();
                });
            } catch (error) {
                reject(error);
            }
        });
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
        if (!isStreaming || !this.currentMessageElement) {
            // 添加消息到内存
            this.messages.push({
                role: role,
                content: content
            });

            // 创建并添加消息元素到UI
            const { messageDiv, contentDiv } = this.createMessageElement(role, content);
            this.messagesContainer.appendChild(messageDiv);

            if (isStreaming) {
                this.currentMessageElement = messageDiv;
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

            }

            copyButton.dataset.copyContent = contentDiv.dataset.rawContent;

                        try {
                // 配置 marked 选项
                marked.setOptions({
                    gfm: true,
                    tables: true,
                    breaks: true,
                    pedantic: false,
                    sanitize: false,
                    smartLists: true,
                    smartypants: false,
                    highlight: function (code, language) {
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
                        return code;
                    }
                });
                contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);
                // 处理所有代码块
                contentDiv.querySelectorAll('pre code').forEach((block) => {
                    // 添加语言类标识
                    const language = block.getAttribute('class') || '';
                    if (language) {
                        block.parentElement.classList.add('language-' + language.replace('language-', ''));
                    }
                    // 添加程序框标题和程序框复制按钮
                    contentDiv.querySelectorAll('pre').forEach((pre) => {
                        if (!pre.closest('.code-block-wrapper')) {
                            this.enhanceCodeBlock(pre);
                        }
                    });

                    // 应用高亮
                    hljs.highlightElement(block);
                });
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
                    // 配置 marked 选项
                    marked.setOptions({
                        gfm: true,
                        tables: true,
                        breaks: true,
                        pedantic: false,
                        sanitize: false,
                        smartLists: true,
                        smartypants: false,
                        highlight: function (code, language) {
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
                            return code;
                        }
                    });

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
            if (msg.role && msg.content) {
                // 将 'assistant' 转换为 'system' 以匹配API格式
                const role = msg.role === 'assistant' ? 'system' : msg.role;
                apiMessages.push({
                    role: role,
                    content: msg.content
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
        if (!message || this.isProcessing) return;
        
        this.setLoadingState(true);
        this.appendMessage('user', message);
        this.messageInput.value = '';
        this.autoResizeTextarea();

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
                    timestamp: new Date().toISOString()
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
                            const data = line.slice(5);
                            if (data === '[DONE]') continue;

                            try {
                                const parsed = JSON.parse(data);
                                if (parsed.error) {
                                    throw new Error(parsed.error);
                                }
                                if (parsed.content) {
                                    this.appendMessage('assistant', parsed.content, true);
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
            this.currentMessageElement = null;
            this.toggleStopButton(false); // 隐藏停止按钮
            this.controller = null;
        }
    }
}

// 初始化
document.addEventListener('DOMContentLoaded', () => {
    const chat = new ChatUI();
});







