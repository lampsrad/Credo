

window.ShowContextMenu = (id, x, y) => {
    let menu = document.getElementById(id);
    if (menu) {
        menu.style.left = x + "px";
        menu.style.top = y + "px";
        menu.style.display = "block";
    }
};
window.HideContextMenu = function (id) {
    var menu = document.getElementById(id);
    if (menu) {
        menu.style.display = "none";
    }
};
function FocusElement(id) {
    let element = document.getElementById(id);
    if (element) {
        element.focus();
        element.select();
    }
}

let _marketChart = null;
let _chartParams = null;
let _chartPct = false;
let _displayOffset = 0;
let _rebaseHandler = null;
let _rebaseCanvas = null;
let _defaultTooltipHandler = null;
let _defaultTooltipCanvas = null;

function buildChart(canvasId, labels, rawData, rawSpy, dataLabel, pct, rawTrades, rawSellTrades, enableRebaseClick = false) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    if (_marketChart) { _marketChart.destroy(); _marketChart = null; }
    Chart.Tooltip.positioners.topLeft = function(_el, _pos) {
        const ca = this.chart.chartArea;
        return { x: ca.left + 10, y: ca.top + 35};
    };
    const isSecurity = !!dataLabel;

    let plotData, plotSpy, plotTrades, plotSellTrades, leftLabel, rightLabel;
    const pctFromStart = arr => {
        const baseVal = arr.find(v => v != null);
        if (baseVal == null || baseVal === 0) return arr.map(() => null);
        return arr.map(v => v != null ? +((v / baseVal - 1) * 100).toFixed(2) : null);
    };
    if (pct && isSecurity) {
        plotData = pctFromStart(rawData);
        plotSpy = rawSpy ? pctFromStart(rawSpy) : rawSpy;
        leftLabel = dataLabel + ' (% change)';
        rightLabel = 'S&P 500 (% change)';
        if (rawTrades && rawTrades.length) {
            plotTrades = rawTrades.map((v, i) => v != null ? plotData[i] : null);
        }
        if (rawSellTrades && rawSellTrades.length) {
            plotSellTrades = rawSellTrades.map((v, i) => v != null ? plotData[i] : null);
        }
    } else {
        plotData = rawData;
        plotSpy = rawSpy;
        plotTrades = rawTrades;
        plotSellTrades = rawSellTrades;
        leftLabel = dataLabel || 'Portfolio Value (USD)';
        rightLabel = 'S&P 500 Index';
    }

    const maxVal = plotData.reduce((a, b) => Math.max(a, b ?? 0), 0);
    const useK = !pct && maxVal >= 10000;

    const datasets = [{
        label: leftLabel,
        data: plotData,
        borderColor: '#4e9af1',
        backgroundColor: 'rgba(78,154,241,0.12)',
        borderWidth: 2, pointRadius: 0, pointHoverRadius: 4,
        fill: true, tension: 0.3, yAxisID: 'y'
    }];
    if (plotTrades && plotTrades.length && plotTrades.some(v => v != null)) {
        datasets.push({
            label: 'Buy',
            data: plotTrades,
            borderColor: 'red',
            backgroundColor: 'red',
            showLine: false,
            pointRadius: 5,
            pointHoverRadius: 7,
            pointStyle: 'circle',
            yAxisID: 'y',
            spanGaps: false
        });
    }
    if (plotSellTrades && plotSellTrades.length && plotSellTrades.some(v => v != null)) {
        datasets.push({
            label: 'Sell',
            data: plotSellTrades,
            borderColor: '#00e676',
            backgroundColor: '#00e676',
            showLine: false,
            pointRadius: 5,
            pointHoverRadius: 7,
            pointStyle: 'circle',
            yAxisID: 'y',
            spanGaps: false
        });
    }
    if (plotSpy && plotSpy.length) {
        datasets.push({
            label: rightLabel,
            data: plotSpy,
            borderColor: '#f1a14e',
            backgroundColor: 'rgba(241,161,78,0.0)',
            borderWidth: 2, pointRadius: 0, pointHoverRadius: 4,
            fill: false, tension: 0.3,
            yAxisID: pct && isSecurity ? 'y' : 'y1',
            spanGaps: true
        });
    }

    const isDaily = labels.length > 0 && labels[0].length === 10;
    const xTicks = {
        color: '#a0a0a0', maxRotation: 0, autoSkip: false,
        callback: function (_v, i) {
            const lbl = this.getLabelForValue(i);
            if (!isDaily) {
                const prev = i > 0 ? this.getLabelForValue(i - 1) : null;
                return lbl !== prev ? lbl : '';
            }
            const useMonth = labels.length <= 300;
            const key = useMonth ? lbl.slice(0, 7) : lbl.slice(0, 4);
            const prevLbl = i > 0 ? this.getLabelForValue(i - 1) : null;
            const prevKey = prevLbl ? (useMonth ? prevLbl.slice(0, 7) : prevLbl.slice(0, 4)) : null;
            if (key === prevKey) return '';
            if (useMonth) {
                const [y, m] = key.split('-');
                const months = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
                return months[+m - 1] + ' ' + y;
            }
            return key;
        }
    };

    const scales = {
        x: { ticks: xTicks, grid: { color: 'rgba(255,255,255,0.06)' } },
        y: {
            position: 'left',
            ticks: {
                color: pct && isSecurity ? '#d7c9aa' : '#4e9af1',
                callback: v => pct && isSecurity ? v.toFixed(1) + '%' : (useK ? '$' + (v / 1000).toFixed(0) + 'k' : '$' + v.toFixed(2))
            },
            grid: { color: 'rgba(255,255,255,0.06)' }
        },
        y1: {
            display: !(pct && isSecurity),
            position: 'right',
            ticks: {
                color: '#f1a14e',
                callback: v => v.toFixed(0)
            },
            grid: { drawOnChartArea: false }
        }
    };

    const crosshairPlugin = {
        id: 'crosshair',
        afterDraw(chart) {
            if (!chart.tooltip?._active?.length) return;
            const x = chart.tooltip._active[0].element.x;
            const { top, bottom } = chart.chartArea;
            const ctx = chart.ctx;
            ctx.save();
            ctx.beginPath();
            ctx.moveTo(x, top);
            ctx.lineTo(x, bottom);
            ctx.lineWidth = 1;
            ctx.strokeStyle = 'rgba(255,255,255,0.25)';
            ctx.setLineDash([4, 3]);
            ctx.stroke();
            ctx.restore();
        }
    };

    _marketChart = new Chart(canvas, {
        type: 'line',
        data: { labels, datasets },
        plugins: [crosshairPlugin],
        options: {
            responsive: true, maintainAspectRatio: false,
            interaction: { mode: 'index', intersect: false },
            plugins: {
                legend: { labels: { color: '#d7c9aa' } },
                tooltip: {
                    position: 'topLeft',
                    callbacks: {
                        label: ctx => {
                            const v = ctx.parsed.y;
                            if (v == null) return null;
                            const isRight = ctx.dataset.yAxisID === 'y1';
                            if (pct && isSecurity) return ' ' + ctx.dataset.label + ': ' + v.toFixed(1) + '%';
                            const decimals = isRight ? 0 : (isSecurity ? 2 : 0);
                            const formatted = v.toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
                            return ' ' + ctx.dataset.label + ': ' + (isRight ? '' : '$') + formatted;
                        },
                        footer: items => {
                            if (!pct || !isSecurity) return undefined;
                            const stockItem = items.find(i => i.dataset.label && i.dataset.label.includes('% change') && i.dataset.label !== rightLabel);
                            const spyItem = items.find(i => i.dataset.label === rightLabel);
                            if (!stockItem || !spyItem || stockItem.parsed.y == null || spyItem.parsed.y == null) return undefined;
                            const diff = stockItem.parsed.y - spyItem.parsed.y;
                            return (diff >= 0 ? '▲ +' : '▼ ') + diff.toFixed(1) + '% vs S&P';
                        }
                    }
                }
            },
            scales
        }
    });

    if (_defaultTooltipCanvas && _defaultTooltipHandler) {
        _defaultTooltipCanvas.removeEventListener('mouseleave', _defaultTooltipHandler);
        _defaultTooltipHandler = null;
        _defaultTooltipCanvas = null;
    }
    _defaultTooltipHandler = () => {
        setTimeout(() => {
            if (!_marketChart) return;
            const lastIdx = labels.length - 1;
            _marketChart.tooltip.setActiveElements(
                datasets.map((_, dsIdx) => ({ datasetIndex: dsIdx, index: lastIdx })),
                { x: 0, y: 0 }
            );
            _marketChart.update('none');
        }, 0);
    };
    _defaultTooltipCanvas = canvas;
    canvas.addEventListener('mouseleave', _defaultTooltipHandler);
    _defaultTooltipHandler();

    if (_rebaseCanvas && _rebaseHandler) {
        _rebaseCanvas.removeEventListener('click', _rebaseHandler);
        _rebaseHandler = null;
        _rebaseCanvas = null;
    }
    if (enableRebaseClick) {
        canvas.style.cursor = pct ? 'crosshair' : 'default';
        if (pct) {
            _rebaseHandler = e => {
                const rect = canvas.getBoundingClientRect();
                const x = e.clientX - rect.left;
                const ca = _marketChart.chartArea;
                if (x < ca.left || x > ca.right) return;
                const fraction = (x - ca.left) / (ca.right - ca.left);
                const localIdx = Math.max(0, Math.min(Math.round(fraction * (labels.length - 1)), labels.length - 1));
                if (localIdx === 0) return;
                _displayOffset += localIdx;
                const p = _chartParams;
                const s = _displayOffset;
                buildChart(p.canvasId,
                    p.labels.slice(s),
                    p.data.slice(s),
                    p.spyData ? p.spyData.slice(s) : p.spyData,
                    p.dataLabel, _chartPct,
                    p.tradeData ? p.tradeData.slice(s) : p.tradeData,
                    p.sellTradeData ? p.sellTradeData.slice(s) : p.sellTradeData,
                    true);
            };
            _rebaseCanvas = canvas;
            canvas.addEventListener('click', _rebaseHandler);
        }
    } else {
        canvas.style.cursor = isSecurity ? 'pointer' : 'default';
        canvas.onclick = isSecurity ? () => {
            _chartPct = !_chartPct;
            buildChart(canvasId, labels, rawData, rawSpy, dataLabel, _chartPct, rawTrades, rawSellTrades);
        } : null;
    }
}

window.getElementWidth = id => document.getElementById(id)?.offsetWidth ?? 0;

window.renderMarketValueChart = (canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData) => {
    _chartPct = false;
    _chartParams = { canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData };
    buildChart(canvasId, labels, data, spyData, dataLabel, false, tradeData, sellTradeData);
};

window.renderSecurityChart = (canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData, startPct) => {
    _chartPct = startPct ?? false;
    _displayOffset = 0;
    _chartParams = { canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData };
    buildChart(canvasId, labels, data, spyData, dataLabel, _chartPct, tradeData, sellTradeData, true);
};

window.toggleSecurityChartPct = () => {
    _chartPct = !_chartPct;
    _displayOffset = 0;
    const p = _chartParams;
    buildChart(p.canvasId, p.labels, p.data, p.spyData, p.dataLabel, _chartPct, p.tradeData, p.sellTradeData, true);
};
