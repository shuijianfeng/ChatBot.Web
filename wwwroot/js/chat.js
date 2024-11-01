document.addEventListener('DOMContentLoaded', function () {
    // DOM 元素
    const messagesContainer = document.getElementById('messages-container');
    const messageInput = document.getElementById('message-input');
    const sendButton = document.getElementById('send-button');
    const modelSelect = document.getElementById('global-model-selector');

    // 获取当前选择的模型
    function getSelectedModel() {
        return modelSelect.value;
    }

    // 初始状态设置
    let isProcessing = false;
    updateSendButtonState();

    function autoResizeTextarea() {
        messageInput.style.height = 'auto';
        const newHeight = Math.min(messageInput.scrollHeight, 200);
        messageInput.style.height = `${newHeight}px`;

        // 更新发送按钮状态
        const isEmpty = !messageInput.value.trim();
        sendButton.disabled = isEmpty;
    }

    // 更新发送按钮状态
    function updateSendButtonState() {
        const isEmpty = !messageInput.value.trim();
        sendButton.disabled = isEmpty || isProcessing;
    }

    // 设置加载状态
    function setLoadingState(loading) {
        isProcessing = loading;
        sendButton.classList.toggle('loading', loading);
        messageInput.disabled = loading;
        updateSendButtonState();
    }

    // 发送消息
    async function sendMessage() {
        const message = messageInput.value.trim();
        if (!message || isProcessing) return;

        setLoadingState(true);

        try {
            // 添加用户消息
            appendMessage('user', message);
            const selectedModel = getSelectedModel();
            // 清空输入框
            messageInput.value = '';
            autoResizeTextarea();

            // 发送到服务器并获取响应
            const response = await fetch('/api/chat', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                    'X-CSRF-TOKEN': document.querySelector('meta[name="csrf-token"]').content
                },
                body: JSON.stringify({
                    message,
                    model: selectedModel,
                    timestamp: new Date().toISOString(),
                    user: 'shuijianfeng'  // 使用当前用户
                })
            });

            if (!response.ok) {
                throw new Error('Network response was not ok');
            }

            // 处理服务器响应
            const data = await response.json();
            appendMessage('assistant', data.response);

        } catch (error) {
            console.error('Error:', error);
            appendMessage('system', '发送消息失败，请重试');
        } finally {
            setLoadingState(false);
        }
    }

    // 添加消息到界面
    function appendMessage(role, content) {
        const messageDiv = document.createElement('div');
        messageDiv.className = `message ${role}-message`;

        const contentDiv = document.createElement('div');
        contentDiv.className = 'message-content';
        contentDiv.textContent = content;

        messageDiv.appendChild(contentDiv);
        messagesContainer.appendChild(messageDiv);

        // 滚动到底部
        messagesContainer.scrollTop = messagesContainer.scrollHeight;
    }

    // 监听输入事件
    messageInput.addEventListener('input', autoResizeTextarea);

    // 监听键盘事件
    messageInput.addEventListener('keydown', function (e) {
        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
            e.preventDefault();
            if (!sendButton.disabled) {
                sendMessage();
            }
        }
    });

    sendButton.addEventListener('click', sendMessage);

    // 初始化调整
    autoResizeTextarea();
});