

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
let _show50ma = false;
let _show200ma = false;

function sma(arr, n) {
    const out = new Array(arr.length).fill(null);
    for (let i = n - 1; i < arr.length; i++) {
        let sum = 0, cnt = 0;
        for (let j = i - n + 1; j <= i; j++) {
            const v = arr[j];
            if (v != null) { sum += v; cnt++; }
        }
        if (cnt === n) out[i] = sum / n;
    }
    return out;
}

function buildChart(canvasId, labels, rawData, rawSpy, dataLabel, pct, rawTrades, rawSellTrades, enableRebaseClick = false, costBase = null) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    if (_marketChart) { _marketChart.destroy(); _marketChart = null; }
    Chart.Tooltip.positioners.topLeft = function(_el, _pos) {
        const ca = this.chart.chartArea;
        // Anchor at the top-left INSIDE the plot area; with yAlign:'top' the
        // tooltip body grows downward from this point, so the box never
        // extends above the chart's top edge.
        return { x: ca.left + 10, y: ca.top + 10 };
    };
    const isSecurity = !!dataLabel;

    let plotData, plotSpy, plotTrades, plotSellTrades, leftLabel, rightLabel;
    const pctFromBase = (arr, base) => {
        const b = base ?? arr.find(v => v != null);
        if (b == null || b === 0) return arr.map(() => null);
        return arr.map(v => v != null ? +((v / b - 1) * 100).toFixed(2) : null);
    };
    if (pct) {
        plotData = pctFromBase(rawData, null);
        plotSpy = rawSpy ? pctFromBase(rawSpy, null) : rawSpy;
        leftLabel = (dataLabel || 'Portfolio') + ' (% change)';
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
            yAxisID: pct ? 'y' : 'y1',
            spanGaps: true
        });
    }
    if (_show50ma || _show200ma) {
        // MAs are computed against the FULL underlying series (e.g. from 2015),
        // not the visible window — otherwise rebasing would leave the first
        // 50/200 points of the visible range blank. The full MA is then sliced
        // to the visible window and, in pct mode, rebased to the same baseline
        // the main series uses.
        const fullData = (_chartParams && _chartParams.data) ? _chartParams.data : rawData;
        const offset = Math.max(0, fullData.length - rawData.length);
        const pctBase = (pct && isSecurity) ? rawData.find(v => v != null) : null;
        const sliceAndRebase = maFull => {
            const sliced = maFull.slice(offset);
            if (pctBase == null || pctBase === 0) return sliced;
            return sliced.map(v => v != null ? +((v / pctBase - 1) * 100).toFixed(2) : null);
        };
        if (_show50ma && fullData.length >= 50) {
            datasets.push({
                label: '50 DMA',
                data: sliceAndRebase(sma(fullData, 50)),
                borderColor: '#ffd54f',
                backgroundColor: 'rgba(255,213,79,0)',
                borderWidth: 1.5, pointRadius: 0, pointHoverRadius: 3,
                fill: false, tension: 0.2,
                yAxisID: 'y',
                spanGaps: true
            });
        }
        if (_show200ma && fullData.length >= 200) {
            datasets.push({
                label: '200 DMA',
                data: sliceAndRebase(sma(fullData, 200)),
                borderColor: '#ce93d8',
                backgroundColor: 'rgba(206,147,216,0)',
                borderWidth: 1.5, pointRadius: 0, pointHoverRadius: 3,
                fill: false, tension: 0.2,
                yAxisID: 'y',
                spanGaps: true
            });
        }
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
                color: pct ? '#d7c9aa' : '#4e9af1',
                callback: v => pct ? v.toFixed(1) + '%' : (useK ? '$' + (v / 1000).toFixed(0) + 'k' : '$' + v.toFixed(2))
            },
            grid: { color: 'rgba(255,255,255,0.06)' }
        },
        y1: {
            display: !pct,
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
                    yAlign: 'top',
                    xAlign: 'left',
                    caretSize: 0,
                    callbacks: {
                        label: ctx => {
                            const v = ctx.parsed.y;
                            if (v == null) return null;
                            const isRight = ctx.dataset.yAxisID === 'y1';
                            if (pct) return ' ' + ctx.dataset.label + ': ' + v.toFixed(1) + '%';
                            const decimals = isRight ? 0 : 2;
                            const formatted = v.toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
                            return ' ' + ctx.dataset.label + ': ' + (isRight ? '' : '$') + formatted;
                        },
                        footer: items => {
                            if (!pct) return undefined;
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
        // Click anywhere on the plot area to make that date the new x-axis start.
        // Mode (Percentage/Price) is preserved — only the toggle button changes mode.
        canvas.style.cursor = 'crosshair';
        canvas.onclick = null;
        _rebaseHandler = e => {
            if (!_marketChart) return;
            const rect = canvas.getBoundingClientRect();
            const x = e.clientX - rect.left;
            const ca = _marketChart.chartArea;
            if (x < ca.left || x > ca.right) return;
            const p = _chartParams;
            if (!p) return;
            const remaining = p.labels.length - _displayOffset;
            if (remaining <= 1) return;
            const fraction = (x - ca.left) / (ca.right - ca.left);
            const localIdx = Math.max(0, Math.min(Math.round(fraction * (remaining - 1)), remaining - 1));
            if (localIdx === 0) return;
            _displayOffset += localIdx;
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
    } else {
        canvas.style.cursor = isSecurity ? 'pointer' : 'default';
        canvas.onclick = isSecurity ? () => {
            _chartPct = !_chartPct;
            buildChart(canvasId, labels, rawData, rawSpy, dataLabel, _chartPct, rawTrades, rawSellTrades);
        } : null;
    }
}

window.getElementWidth = id => document.getElementById(id)?.offsetWidth ?? 0;
window.getViewportChartWidth = () => Math.min(window.innerWidth - 80, 1400);

window.renderMarketValueChart = (canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData) => {
    _chartPct = false;
    _show50ma = false;
    _show200ma = false;
    _chartParams = { canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData };
    buildChart(canvasId, labels, data, spyData, dataLabel, false, tradeData, sellTradeData);
};

window.renderSecurityChart = (canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData, startPct) => {
    _chartPct = startPct ?? false;
    _displayOffset = 0;
    _show50ma = false;
    _show200ma = false;
    _chartParams = { canvasId, labels, data, spyData, dataLabel, tradeData, sellTradeData };
    buildChart(canvasId, labels, data, spyData, dataLabel, _chartPct, tradeData, sellTradeData, true);
};

window.toggleSecurityChartPct = () => {
    _chartPct = !_chartPct;
    _displayOffset = 0;
    const p = _chartParams;
    buildChart(p.canvasId, p.labels, p.data, p.spyData, p.dataLabel, _chartPct, p.tradeData, p.sellTradeData, true);
};

function _rebuildVisible() {
    const p = _chartParams;
    if (!p) return;
    const s = _displayOffset;
    buildChart(p.canvasId,
        p.labels.slice(s),
        p.data.slice(s),
        p.spyData ? p.spyData.slice(s) : p.spyData,
        p.dataLabel, _chartPct,
        p.tradeData ? p.tradeData.slice(s) : p.tradeData,
        p.sellTradeData ? p.sellTradeData.slice(s) : p.sellTradeData,
        true, p.costBase ?? null);
}

window.toggleMA50 = () => {
    _show50ma = !_show50ma;
    _rebuildVisible();
    return _show50ma;
};

window.toggleMA200 = () => {
    _show200ma = !_show200ma;
    _rebuildVisible();
    return _show200ma;
};
