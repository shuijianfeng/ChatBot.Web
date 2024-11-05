class ChartRenderer {
    constructor() {
        // 初始化 mermaid
        mermaid.initialize({
            startOnLoad: false,
            theme: 'default',
            securityLevel: 'loose',
            flowchart: {
                useMaxWidth: true,
                htmlLabels: true,
                curve: 'basis'
            }
        });

        // 扩展 marked 以支持图表
        this.setupMarked();
    }

    setupMarked() {
        const renderer = new marked.Renderer();
        const originalCode = renderer.code.bind(renderer);

        // 重写 code 渲染方法
        renderer.code = (code, language) => {
            if (this.isChartLanguage(language)) {
                return this.renderChart(code, language);
            }
            return originalCode(code, language);
        };

        // 配置 marked
        marked.setOptions({
            renderer: renderer,
            highlight: (code, lang) => {
                if (this.isChartLanguage(lang)) {
                    return code;
                }
                // 可以添加其他代码高亮处理
                return code;
            }
        });
    }

    isChartLanguage(language) {
        return ['mermaid', 'chart', 'pie', 'bar', 'line'].includes(language);
    }

    renderChart(code, type) {
        const chartId = `chart-${Math.random().toString(36).substr(2, 9)}`;

        // 根据不同图表类型处理代码
        let chartCode = code;
        if (type !== 'mermaid') {
            chartCode = this.convertToMermaid(code, type);
        }

        try {
            // 异步渲染图表
            setTimeout(() => {
                mermaid.render(chartId, chartCode, (svgCode) => {
                    const container = document.getElementById(chartId);
                    if (container) {
                        container.innerHTML = svgCode;
                    }
                });
            }, 0);

            return `<div class="chart-container" id="${chartId}">
                      <div class="chart-loading">Loading chart...</div>
                    </div>`;
        } catch (error) {
            console.error('Chart rendering error:', error);
            return `<div class="chart-error">Failed to render chart: ${error.message}</div>`;
        }
    }

    convertToMermaid(code, type) {
        // 将其他格式转换为 Mermaid 语法
        switch (type) {
            case 'pie':
                return this.convertToPieChart(code);
            case 'bar':
                return this.convertToBarChart(code);
            case 'line':
                return this.convertToLineChart(code);
            default:
                return code;
        }
    }

    convertToPieChart(code) {
        const lines = code.trim().split('\n');
        let mermaidCode = 'pie\n';

        lines.forEach(line => {
            const [label, value] = line.split(':').map(s => s.trim());
            if (label && value) {
                mermaidCode += `    "${label}" : ${value}\n`;
            }
        });

        return mermaidCode;
    }

    convertToBarChart(code) {
        const lines = code.trim().split('\n');
        let mermaidCode = 'graph TD\n';

        lines.forEach((line, index) => {
            const [label, value] = line.split(':').map(s => s.trim());
            if (label && value) {
                mermaidCode += `    ${index}["${label}"] --- |${value}| B${index}((${value}))\n`;
            }
        });

        return mermaidCode;
    }

    convertToLineChart(code) {
        const lines = code.trim().split('\n');
        let mermaidCode = 'xychart-beta\n    title "Line Chart"\n    x-axis [';

        const points = [];
        lines.forEach(line => {
            const [x, y] = line.split(',').map(s => s.trim());
            if (x && y) {
                points.push([x, y]);
            }
        });

        mermaidCode += points.map(p => `"${p[0]}"`).join(' ') + ']\n';
        mermaidCode += '    y-axis "Value"\n';
        mermaidCode += '    line [' + points.map(p => p[1]).join(' ') + ']\n';

        return mermaidCode;
    }
}