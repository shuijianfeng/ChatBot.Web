class ChatUI {
    constructor() {
        this.messages = []; // 存储聊天记录
        this.session_id = '';
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

        // 配置 marked.js
        marked.setOptions({
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

    createMessageElement(role, content) {
        const messageDiv = document.createElement('div');
        messageDiv.className = `message ${role}-message`;

        // 添加头像
        const avatarDiv = document.createElement('div');
        avatarDiv.className = 'message-avatar';
        const avatarIcon = document.createElement('span');
        avatarIcon.className = role === 'user' ? 'octicon octicon-person' : 'octicon octicon-copilot';
        avatarDiv.appendChild(avatarIcon);

        // 消息容器
        const containerDiv = document.createElement('div');
        containerDiv.className = 'message-container';

        // 消息头部
        const headerDiv = document.createElement('div');
        headerDiv.className = 'message-header';

        // 角色名称
        const roleDiv = document.createElement('div');
        roleDiv.className = 'message-role';
        roleDiv.textContent = role === 'user' ? this.currentUser : 'Ai助手';

        // 操作按钮
        const actionsDiv = document.createElement('div');
        actionsDiv.className = 'message-actions';

        // 复制按钮
        const copyButton = document.createElement('button');
        copyButton.className = 'copy-button';
        copyButton.setAttribute('aria-label', 'Copy message');
        copyButton.innerHTML = '<span class="octicon octicon-copy"></span>';
        copyButton.dataset.copyContent = content;

        copyButton.addEventListener('click', (e) => {
            e.stopPropagation();
            this.copyMessage(e.currentTarget);
        });

        actionsDiv.appendChild(copyButton);
        headerDiv.appendChild(roleDiv);
        headerDiv.appendChild(actionsDiv);

        // 消息内容
        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content markdown-body';

        try {
            contentDiv.innerHTML = marked.parse(content);
            // 确保对所有代码块应用高亮
            contentDiv.querySelectorAll('pre code').forEach((block) => {
                hljs.highlightElement(block);
            });
        } catch (e) {
            console.error('Markdown 渲染错误:', e);
            contentDiv.textContent = content;
        }


        containerDiv.appendChild(headerDiv);
        containerDiv.appendChild(contentDiv);
        messageDiv.appendChild(avatarDiv);
        messageDiv.appendChild(containerDiv);

        return { messageDiv, contentDiv };
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
                contentDiv.innerHTML = marked.parse(contentDiv.dataset.rawContent);
                contentDiv.querySelectorAll('pre code').forEach((block) => {
                    hljs.highlightElement(block);
                });
            } catch (e) {
                console.error('Markdown 渲染错误:', e);
                contentDiv.textContent = contentDiv.dataset.rawContent;
            }
        }

        this.scrollToBottom();
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
                })
            });

            if (!response.ok) {
                throw new Error('Network response was not ok');
            }

            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            let buffer = '';

            this.currentMessageElement = null;

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

        } catch (error) {
            console.error('错误:', error);
            this.appendMessage('system', `发送消息失败: ${error.message}`);
        } finally {
            this.setLoadingState(false);
            this.currentMessageElement = null;
        }
    }
}

// 初始化
document.addEventListener('DOMContentLoaded', () => {
    const chat = new ChatUI();
});