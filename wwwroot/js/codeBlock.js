//class ChatCodeBlockHandler {
//    constructor() {
//        this.init();
//    }

//    init() {
//        // 立即处理现有的代码块
//        this.processAllCodeBlocks();

//        // 设置观察器以处理新的代码块
//        this.observeNewMessages();
//    }

//    processAllCodeBlocks() {
//        document.querySelectorAll('pre').forEach(pre => {
//            if (!pre.closest('.code-block-wrapper')) {
//                this.enhanceCodeBlock(pre);
//            }
//        });
//    }

//    enhanceCodeBlock(pre) {
//        // 创建包装器
//        const wrapper = document.createElement('div');
//        wrapper.className = 'code-block-wrapper';

//        // 获取或创建 code 元素
//        let code = pre.querySelector('code');
//        if (!code) {
//            code = document.createElement('code');
//            code.textContent = pre.textContent;
//            pre.textContent = '';
//            pre.appendChild(code);
//        }

//        // 获取语言
//        const language = this.detectLanguage(code);

//        // 创建标题栏
//        const header = this.createCodeHeader(language);

//        // 重新组织结构
//        pre.parentNode.insertBefore(wrapper, pre);
//        wrapper.appendChild(header);
//        wrapper.appendChild(pre);
//    }

//    detectLanguage(codeElement) {
//        const classes = Array.from(codeElement.classList);
//        const langClass = classes.find(cls => cls.startsWith('language-'));
//        return langClass ? langClass.replace('language-', '') : 'plaintext';
//    }

//    createCodeHeader(language) {
//        const header = document.createElement('div');
//        header.className = 'code-header';

//        // 添加语言标识
//        const langLabel = document.createElement('span');
//        langLabel.className = 'code-language';
//        langLabel.textContent = language;
//        header.appendChild(langLabel);

//        // 添加复制按钮
//        const copyButton = document.createElement('button');
//        copyButton.className = 'copy-button';
//        copyButton.innerHTML = '<i class="bi bi-clipboard"></i>';
//        copyButton.setAttribute('aria-label', '复制代码');

//        // 添加复制功能
//        this.addCopyButtonListener(copyButton);

//        header.appendChild(copyButton);

//        return header;
//    }

//    addCopyButtonListener(button) {
//        button.addEventListener('click', async () => {
//            const pre = button.closest('.code-block-wrapper').querySelector('pre');
//            const code = pre.textContent;

//            try {
//                await navigator.clipboard.writeText(code);
//                this.showCopyFeedback(button, true);
//            } catch (err) {
//                console.error('复制失败:', err);
//                this.showCopyFeedback(button, false);
//            }
//        });
//    }

//    showCopyFeedback(button, success) {
//        const originalHTML = button.innerHTML;
//        button.innerHTML = success ?
//            '<i class="bi bi-clipboard-check"></i>' :
//            '<i class="bi bi-clipboard-x"></i>';

//        setTimeout(() => {
//            button.innerHTML = originalHTML;
//        }, 2000);
//    }

//    observeNewMessages() {
//        const chatContainer = document.querySelector('.chat-messages');
//        if (!chatContainer) return;

//        const observer = new MutationObserver((mutations) => {
//            mutations.forEach(mutation => {
//                mutation.addedNodes.forEach(node => {
//                    if (node.nodeType === 1) { // 元素节点
//                        const newCodeBlocks = node.querySelectorAll('pre');
//                        newCodeBlocks.forEach(pre => {
//                            if (!pre.closest('.code-block-wrapper')) {
//                                this.enhanceCodeBlock(pre);
//                            }
//                        });
//                    }
//                });
//            });
//        });

//        observer.observe(chatContainer, {
//            childList: true,
//            subtree: true
//        });
//    }
//}

//// 初始化
//document.addEventListener('DOMContentLoaded', () => {
//    new ChatCodeBlockHandler();
//});