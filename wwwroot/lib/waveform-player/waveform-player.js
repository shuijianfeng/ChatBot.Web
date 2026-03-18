/**
 * ══════════════════════════════════════════════════════
 *  <waveform-player>  Web Component v3.0 (Stream)
 *
 *  用法：
 *    <waveform-player src="audio.mp3"></waveform-player>
 *    <waveform-player stream src="https://tts-api/speak?text=hello"></waveform-player>
 *    <waveform-player no-download src="audio.mp3"></waveform-player>
 *
 *  属性：
 *    src         - 音频 URL
 *    stream      - 流模式（布尔属性，启用后边收边播）
 *    theme       - 主题强调色（如 #e06050）
 *    dark        - 暗色模式（布尔属性）
 *    auto-dark   - 跟随系统暗色偏好（布尔属性）
 *    label       - 自定义标题（不设则显示文件名，设为空字符串则隐藏）
 *    proxy       - CORS 代理前缀（如 https://proxy.com/?url=）
 *    no-download - 隐藏下载按钮（布尔属性）
 *
 *  API ：play() / pause() / stop() / download() / currentTime / volume
 *  事件：waveform-ready / stream-end
 * ══════════════════════════════════════════════════════
 */
;(function () {
  'use strict';

  var _cssURL = null;
  var _cssCache = null;
  function _loadCSS() {
    if (_cssCache !== null) return Promise.resolve(_cssCache);
    if (_cssURL) {
      return fetch(_cssURL).then(function (r) {
        if (r.ok) return r.text().then(function (t) { _cssCache = t; return t; });
        throw new Error(r.status);
      }).catch(function () { _cssCache = FALLBACK_CSS; return FALLBACK_CSS; });
    }
    _cssCache = FALLBACK_CSS;
    return Promise.resolve(FALLBACK_CSS);
  }

  /* ───────────────────────────────
     HTML 模板
  ─────────────────────────────── */
  var TPL = [
    '<div class="file-zone" part="file-zone">',
    '  <input type="file" accept="audio/*" class="file-input">',
    '  <div class="hint"><b>\uD83C\uDFB5 点击或拖拽音频文件到此处</b>支持 MP3 / WAV / OGG / FLAC / AAC</div>',
    '</div>',
    '<div class="file-name" part="file-name"></div>',
    '<div class="player" part="player">',
    '  <button class="play-btn" part="play-btn" title="播放/暂停">',
    '    <svg class="play-icon" viewBox="0 0 24 24" width="17" height="17" fill="#fff">',
    '      <polygon points="6,4 20,12 6,20"/>',
    '    </svg>',
    '  </button>',
    '  <div class="wave-wrap">',
    '    <canvas class="wave-canvas"></canvas>',
    '    <div class="mask"><div class="spin"></div> 正在解析波形…</div>',
    '  </div>',
    '  <span class="time-label">--:-- / --:--</span>',
    '  <div class="vol-wrap" title="音量">',
    '    <svg viewBox="0 0 24 24" width="15" height="15">',
    '      <path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.05c1.48-.73 2.5-2.25 2.5-4.02z"/>',
    '    </svg>',
    '    <input type="range" class="vol-slider" min="0" max="1" step="0.01" value="1">',
    '  </div>',
    '  <button class="dl-btn" part="dl-btn" title="下载音频">',
    '    <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">',
    '      <path d="M12 3v13m0 0l-4.5-4.5M12 16l4.5-4.5M5 20h14"/>',
    '    </svg>',
    '  </button>',
    '</div>'
  ].join('\n');


  class WaveformPlayer extends HTMLElement {

    static get observedAttributes() {
      return ['src', 'theme', 'proxy', 'label', 'no-download', 'stream'];  /* ★ STREAM: 增加 stream */
    }

    constructor() {
      super();
      this._shadow    = this.attachShadow({ mode: 'open' });
      this._peakData  = [];
      this._hoverX    = -1;
      this._dragging  = false;
      this._dpr       = window.devicePixelRatio || 1;
      this._objectUrl = null;
      this._currentFile = null;

      this._liveCtx       = null;
      this._liveAnalyser  = null;
      this._liveSource    = null;
      this._livePeaks     = [];
      this._liveMode      = false;
      this._liveRAF       = null;
      this._liveCollected = false;
      this._zeroFrames    = 0;

      /* ★ STREAM: 流模式状态 */
      this._streamMode       = false;   // 是否处于流模式
      this._streamChunks     = [];      // 收集的原始分块
      this._streamTotalBytes = 0;       // 已接收总字节
      this._streamComplete   = false;   // 流是否传输完成
      this._streamBlob       = null;    // 流完成后的完整 Blob
      this._streamAbort      = null;    // AbortController
      this._mediaSource      = null;    // MediaSource 实例
      this._mediaSourceUrl   = null;    // MediaSource object URL
      this._streamAutoPlay   = false;   // 流模式下是否自动播放
      this._streamDuration   = 0;       // 流模式下的已知时长
      this._streamFinalizeTimer = null; // 流完成判定的延迟定时器
      this._streamLoading    = false;   // 后台正在获取音频数据
      this._streamPendingPlay = false;  // 用户在加载期间点击了播放
      this._streamUiTimer    = null;    // 接收期间刷新时长/响应播放意图
      this._streamStarted    = false;   // 是否已开始实际接收流数据
      this._loadedSrc        = null;    // 当前已加载的 src，避免重复初始化
      this._streamMaskLocked = false;   // 流式加载阶段锁定遮罩显示，避免闪烁

      this._ready = this._init();
    }

    async _init() {
      var css = await _loadCSS();

      if ('adoptedStyleSheets' in Document.prototype) {
        var sheet = new CSSStyleSheet();
        sheet.replaceSync(css);
        this._shadow.adoptedStyleSheets = [sheet];
      } else {
        var s = document.createElement('style');
        s.textContent = css;
        this._shadow.appendChild(s);
      }

      var wrap = document.createElement('div');
      wrap.innerHTML = TPL;
      while (wrap.firstChild) this._shadow.appendChild(wrap.firstChild);

      this.$audio = document.createElement('audio');
      this.$audio.preload = 'auto';
      this._shadow.appendChild(this.$audio);

      this._query();
      this._bindEvents();
      this._syncDownloadVisible();
      this._ensureSrcLoaded();
    }

    _ensureSrcLoaded() {
      var src = this.getAttribute('src');
      if (!src || this._loadedSrc === src) return;
      this._loadedSrc = src;
      this._loadSrc(src);
    }

    _query() {
      var s = this._shadow;
      this.$fileZone  = s.querySelector('.file-zone');
      this.$fileInput = s.querySelector('.file-input');
      this.$fileName  = s.querySelector('.file-name');
      this.$player    = s.querySelector('.player');
      this.$playBtn   = s.querySelector('.play-btn');
      this.$playIcon  = s.querySelector('.play-icon');
      this.$waveWrap  = s.querySelector('.wave-wrap');
      this.$canvas    = s.querySelector('.wave-canvas');
      this.$ctx       = this.$canvas.getContext('2d');
      this.$mask      = s.querySelector('.mask');
      this.$timeLabel = s.querySelector('.time-label');
      this.$volSlider = s.querySelector('.vol-slider');
      this.$dlBtn     = s.querySelector('.dl-btn');

      if (this.getAttribute('src')) {
        this.$fileZone.classList.add('hidden');
        this.$player.classList.add('show');
      }
    }

    _bindEvents() {
      var self = this;

      this.$fileInput.addEventListener('change', function (e) {
        if (e.target.files[0]) self._loadFile(e.target.files[0]);
      });

      this.$fileZone.addEventListener('dragover', function (e) {
        e.preventDefault(); self.$fileZone.classList.add('over');
      });
      this.$fileZone.addEventListener('dragleave', function () {
        self.$fileZone.classList.remove('over');
      });
      this.$fileZone.addEventListener('drop', function (e) {
        e.preventDefault(); self.$fileZone.classList.remove('over');
        var f = e.dataTransfer.files[0];
        if (f) self._loadFile(f);
      });

      this.$playBtn.addEventListener('click', function () {
        self._togglePlay();
      });

      this.$dlBtn.addEventListener('click', function () {
        self._doDownload();
      });

      this.$audio.addEventListener('play', function () {
        if (self._streamMode) {
          self._streamPendingPlay = false;
        }
        self._updateIcon();
        /* ★ STREAM: 播放开始时确保遮罩隐藏（作为 canplay 的后备） */
        if (self._streamMode && !self._streamMaskLocked) {
          self.$mask.classList.add('hidden');
        }
        if (self._liveMode && !self._liveCollected) self._startLiveCapture();
      });
      this.$audio.addEventListener('pause', function () {
        self._updateIcon();
        self._stopLiveCapture();
        /* ★ STREAM: 暂停时如果完整数据已就绪，切换到 Blob URL 启用拖拽进度 */
        if (self._streamMode && self._streamBlob && !self._streamBlobSwitched) {
          self._switchToBlobUrl();
        }
      });
      this.$audio.addEventListener('ended', function () {
        self._updateIcon();
        self._stopLiveCapture();
        if (self._liveMode && !self._liveCollected) {
          self._liveCollected = true;
          self._finalizeLivePeaks();
        }
        /* ★ STREAM: 流模式播放结束后获取完整数据，启用拖拽进度和真实波形 */
        if (self._streamMode) {
          if (!self._streamComplete) {
            self._finalizeStreamPlayback();
          } else if (self._streamBlob && !self._streamBlobSwitched) {
            self._switchToBlobUrl();
          }
        }
        self._draw();
      });
      this.$audio.addEventListener('timeupdate', function () {
        self._updateTimeLabel();
        self._draw();
      });
      this.$audio.addEventListener('loadedmetadata', function () {
        self._updateTimeLabel();
      });
      this.$audio.addEventListener('canplay', function () {
        self._updateTimeLabel();
        if (self._streamMode && self._streamPendingPlay && self.$audio.paused) {
          self.$audio.play().catch(function () {});
        }
      });
      this.$audio.addEventListener('durationchange', function () {
        self._updateTimeLabel();
      });
      this.$audio.addEventListener('progress', function () {
        if (self._streamMode) {
          self._updateTimeLabel();
        }
      });

      this.$canvas.addEventListener('click', function (e) {
        self._seekByEvent(e);
      });
      this.$canvas.addEventListener('mousedown', function (e) {
        self._dragging = true; self._seekByEvent(e);
      });
      window.addEventListener('mouseup', function () {
        self._dragging = false;
      });
      window.addEventListener('mousemove', function (e) {
        if (self._dragging) self._seekByEvent(e);
      });

      this.$canvas.addEventListener('mousemove', function (e) {
        self._hoverX = e.clientX - self.$canvas.getBoundingClientRect().left;
        self._draw();
      });
      this.$canvas.addEventListener('mouseleave', function () {
        self._hoverX = -1; self._draw();
      });

      this.$volSlider.addEventListener('input', function (e) {
        self.$audio.volume = parseFloat(e.target.value);
      });

      this.setAttribute('tabindex', '0');
      this.addEventListener('keydown', function (e) {
        if (e.code === 'Space')      { e.preventDefault(); self._togglePlay(); }
        if (e.code === 'ArrowLeft')  self.$audio.currentTime -= 5;
        if (e.code === 'ArrowRight') self.$audio.currentTime += 5;
      });

      new ResizeObserver(function () {
        self._dpr = window.devicePixelRatio || 1;
        self._initCanvas(); self._draw();
      }).observe(this.$waveWrap);
    }

    attributeChangedCallback(name, oldVal, val) {
      var self = this;
      this._ready.then(function () {
        if (name === 'src'   && val && val !== oldVal) {
          self._ensureSrcLoaded();
        }
        if (name === 'theme')        self.style.setProperty('--wp-color', val);
        if (name === 'label')        self._updateLabel();
        if (name === 'no-download')  self._syncDownloadVisible();
        if (name === 'stream')       { /* 仅标记，下次 _loadSrc 生效 */ }
      });
    }

    connectedCallback() {
      var self = this;
      this._ready.then(function () {
        self._ensureSrcLoaded();
      });
    }

    /* ════════════════════════════════
       ★ STREAM: 下载按钮显隐 — 流未完成时也隐藏
    ════════════════════════════════ */
    _syncDownloadVisible() {
      if (!this.$dlBtn) return;
      var forceHide = this.hasAttribute('no-download');
      /* ★ STREAM: 流模式下未完成则隐藏下载按钮 */
      var streamPending = this._streamMode && !this._streamComplete;
      this.$dlBtn.classList.toggle('hidden', forceHide || streamPending);
    }

    /* ════════════════════════════════
       ★ STREAM: 获取用于显示的 duration
       流模式下 audio.duration 可能是 Infinity
    ════════════════════════════════ */
    _getBufferedEnd() {
      if (!this.$audio) return 0;
      var buffered = this.$audio.buffered;
      if (!buffered || buffered.length === 0) return 0;

      try {
        return buffered.end(buffered.length - 1) || 0;
      } catch (_) {
        return 0;
      }
    }

    _getDisplayDuration() {
      var d = this.$audio.duration;
      if (!this._streamMode || this._streamComplete || this._streamBlobSwitched) {
        if (isFinite(d) && d > 0) {
          this._streamDuration = Math.max(d, this.$audio.currentTime || 0, this._streamDuration || 0);
          return this._streamDuration;
        }

        if (this._streamMode && this._streamDuration > 0) {
          return Math.max(this._streamDuration, this.$audio.currentTime || 0);
        }

        return d;
      }

      var bufferedEnd = this._getBufferedEnd();
      var receivedDuration = Math.max(bufferedEnd, this.$audio.currentTime || 0, this._streamDuration || 0);

      if (receivedDuration > 0) {
        this._streamDuration = receivedDuration;
        return receivedDuration;
      }

      if (isFinite(d) && d > 0) {
        this._streamDuration = d;        // 缓存有限时长
        return d;
      }

      if (this._streamDuration > 0) return this._streamDuration;
      return this.$audio.currentTime || 0;
    }

    _updateTimeLabel() {
      if (!this.$timeLabel || !this.$audio) return;

      var currentTime = this.$audio.currentTime || 0;
      var duration = this._getDisplayDuration();

      this.$timeLabel.textContent =
        this._fmt(currentTime) + ' / ' + this._fmt(duration);
    }

    _clearStreamFinalizeTimer() {
      if (this._streamFinalizeTimer) {
        clearTimeout(this._streamFinalizeTimer);
        this._streamFinalizeTimer = null;
      }
    }

    _startStreamUiTimer() {
      var self = this;
      this._stopStreamUiTimer();
      this._streamUiTimer = setInterval(function () {
        if (!self._streamMode) return;
        self._updateStreamTimeLabel();
        if (self._streamPendingPlay && self.$audio && self.$audio.src && self.$audio.paused) {
          self.$audio.play().catch(function () {});
        }
      }, 250);
    }

    _stopStreamUiTimer() {
      if (this._streamUiTimer) {
        clearInterval(this._streamUiTimer);
        this._streamUiTimer = null;
      }
    }

    _scheduleStreamFinalize() {
      var self = this;
      this._clearStreamFinalizeTimer();
      this._streamFinalizeTimer = setTimeout(function () {
        self._streamFinalizeTimer = null;
        if (!self._streamComplete) {
          self._finalizeStreamPlayback();
        }
      }, 1200);
    }

    /* ════════════════════════════════
       标题管理
    ════════════════════════════════ */
    _updateLabel(fallback) {
      var label = this.getAttribute('label');

      if (label !== null && label === '') {
        this.$fileName.classList.remove('show');
        return;
      }

      var text = (label !== null) ? label : (fallback || '');
      this.$fileName.textContent = text;
      if (text) {
        this.$fileName.classList.add('show');
      } else {
        this.$fileName.classList.remove('show');
      }
    }

    /* ════════════════════════════════
       多策略获取音频数据（非流）
    ════════════════════════════════ */
    async _fetchAudioBuffer(url) {
      var errors = [];

      try {
        var r1 = await fetch(url);
        if (!r1.ok) throw new Error('HTTP ' + r1.status);
        return await r1.arrayBuffer();
      } catch (e) { errors.push('fetch: ' + e.message); }

      try {
        var buf = await new Promise(function (resolve, reject) {
          var xhr = new XMLHttpRequest();
          xhr.open('GET', url, true);
          xhr.responseType = 'arraybuffer';
          xhr.timeout = 5000;
          xhr.onload = function () {
            (xhr.status === 0 || xhr.status === 200)
              ? resolve(xhr.response)
              : reject(new Error('XHR ' + xhr.status));
          };
          xhr.onerror   = function () { reject(new Error('XHR 网络错误')); };
          xhr.ontimeout = function () { reject(new Error('XHR 超时')); };
          xhr.send();
        });
        return buf;
      } catch (e) { errors.push('XHR: ' + e.message); }

      var proxy = this.getAttribute('proxy');
      if (proxy) {
        try {
          var r3 = await fetch(proxy + encodeURIComponent(url));
          if (!r3.ok) throw new Error('Proxy HTTP ' + r3.status);
          return await r3.arrayBuffer();
        } catch (e) { errors.push('proxy: ' + e.message); }
      }

      var publicProxies = [
        'https://api.allorigins.win/raw?url=',
        'https://corsproxy.io/?'
      ];
      for (var pi = 0; pi < publicProxies.length; pi++) {
        try {
          var opts = {};
          if (typeof AbortSignal !== 'undefined' && AbortSignal.timeout) {
            opts.signal = AbortSignal.timeout(10000);
          }
          var r4 = await fetch(publicProxies[pi] + encodeURIComponent(url), opts);
          if (!r4.ok) throw new Error('HTTP ' + r4.status);
          var buf4 = await r4.arrayBuffer();
          if (buf4.byteLength > 0) return buf4;
        } catch (e) { errors.push('公共代理: ' + e.message); }
      }

      throw new Error(errors.join('; '));
    }

    /* ════════════════════════════════
       ★ STREAM: 从 URL 加载 — 分流处理
    ════════════════════════════════ */
    async _loadSrc(url) {
      /* ★ STREAM: 如果设置了 stream 属性，走流模式 */
      if (this.hasAttribute('stream')) {
        if (url) {
          try {
            var headResponse = await fetch(url, { method: 'HEAD' });
            if (headResponse.status === 204 || headResponse.ok) {
              return this._loadSrcNormal(url);
            }
          } catch (_) {}
        }
        return this._loadSrcStream(url);
      }
      return this._loadSrcNormal(url);
    }

    /* ════════════════════════════════
       原有的常规加载（改名）
    ════════════════════════════════ */
    async _loadSrcNormal(url) {
      this._resetState();
      this._currentFile = null;

      this.$player.classList.add('show');
      this.$fileZone.classList.add('hidden');

      this._updateLabel('\uD83C\uDFB5 ' + decodeURIComponent(url.split('/').pop()));

      /* 立即生成模拟波形并显示，无需等待音频加载和解码 */
      this._generateSimulatedPeaks();
      this._initCanvas();
      this._draw();
      this.$mask.classList.add('hidden');

      /* 立即设置 audio src，浏览器开始加载元数据和音频数据，
         用户可立即看到时长、可立即点击播放 */
      this.$audio.removeAttribute('crossOrigin');
      this.$audio.src = url;
      this.$audio.load();

      /* 并行获取音频数据用于真实波形解码 */
      try {
        var buf = await this._fetchAudioBuffer(url);
        /* 必须在 decodeAudioData 之前创建 Blob，
           因为 decodeAudioData 会 detach ArrayBuffer 导致其 byteLength 变为 0 */
        var blob = new Blob([buf]);
        if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
        this._objectUrl = URL.createObjectURL(blob);

        this._peakData = await this._decodeBuffer(buf);
        this._draw();
        this._updateTimeLabel();

        /* 用 Blob URL 替换原始 URL，避免播放时重复下载 */
        var wasPlaying = !this.$audio.paused && !this.$audio.ended;
        var restoreTime = this.$audio.currentTime || 0;
        this.$audio.src = this._objectUrl;
        this.$audio.load();
        var self = this;
        function onMeta() {
          self.$audio.removeEventListener('loadedmetadata', onMeta);
          if (restoreTime > 0 && isFinite(restoreTime)) {
            self.$audio.currentTime = Math.min(restoreTime, self.$audio.duration || restoreTime);
          }
          if (wasPlaying) self.$audio.play().catch(function () {});
          self._updateTimeLabel();
        }
        this.$audio.addEventListener('loadedmetadata', onMeta);
        return;
      } catch (e) {
        console.warn('[waveform-player] 原始数据获取失败:', e.message);
      }

      /* audio.src 已设置，即使波形解码失败，播放仍可用。
         尝试 CORS 探测以启用实时波形分析。 */
      try {
        var corsOK = await this._testCORS(url);
        if (corsOK) {
          this.$audio.crossOrigin = 'anonymous';
          this.$audio.src = url;
          this.$audio.load();
          this._enterLiveMode();
          return;
        }
      } catch (_) {}
    }

    /* ════════════════════════════════════════════════════
       ★ STREAM: 流模式核心加载
       服务端 GetSharedMediaStream 使用分块传输（无 Content-Length），
       <audio> 元素无法直接加载此类流式响应。

       使用 fetch + ReadableStream 逐块读取音频数据：
       · 传输期间持续刷新已接收大小显示（如 "⬇ 156 KB"）
       · 用户可在传输期间点击播放（记录意图）
       · 全部数据到达后创建完整 Blob URL 设为 audio.src
       · 若用户已点击播放则自动开始
    ════════════════════════════════════════════════════ */
    async _loadSrcStream(url) {
      this._resetState();
      this._currentFile      = null;
      this._streamMode       = true;
      this._streamChunks     = [];
      this._streamTotalBytes = 0;
      this._streamComplete   = false;
      this._streamBlob       = null;
      this._streamDuration   = 0;

      this.$player.classList.add('show');
      this.$fileZone.classList.add('hidden');
      this._updateLabel('\uD83C\uDFB5 ' + decodeURIComponent(url.split('/').pop()));
      this._syncDownloadVisible();

      /* 立即生成模拟波形，无需等待音频加载 */
      this._generateSimulatedPeaks();
      this._initCanvas();
      this._draw();
      this._streamMaskLocked = true;
      this.$mask.classList.remove('hidden');
      this.$mask.innerHTML = '<div class="spin"></div> 点击播放后开始接收音频…';

      this.$audio.removeAttribute('crossOrigin');
      this.$audio.removeAttribute('src');
      this.$audio.load();
      this.$audio.preload = 'metadata';
      this._streamLoading = false;
      this._streamPendingPlay = false;
      this._streamStarted = false;
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 后台获取音频数据
       使用 fetch + ReadableStream 逐块读取。
       传输期间在时间标签显示已接收大小；
       全部数据到达后创建完整 Blob URL 设为 audio.src，
       若用户在传输期间点击了播放则自动开始。
    ────────────────────────────────────────────────── */
    async _tryPreloadStreamAudio(url) {
      try {
        var response = await fetch(url);
        if (!response.ok) {
          this._streamMaskLocked = false;
          this._streamLoading = false;
          this.$mask.classList.remove('hidden');
          this.$mask.innerHTML =
            '<div style="text-align:center;line-height:1.6;font-size:11px">' +
            '\u26A0\uFE0F 音频加载失败 (HTTP ' + response.status + ')</div>';
          return;
        }

        var ct = (response.headers.get('content-type') || 'audio/mpeg').split(';')[0].trim();
        var reader = response.body.getReader();

        if (ct === 'text/event-stream') {
          await this._streamFromSSE(reader);
          this._streamLoading = false;
          return;
        }

        var mseMime = (typeof MediaSource !== 'undefined') ? this._detectMSEMime(url, ct) : null;
        if (mseMime) {
          await this._streamViaMediaSource(reader, mseMime);
          this._streamLoading = false;
          return;
        }

        await this._streamCollectThenPlay(reader, url, ct);
        this._streamLoading = false;
        return;
      } catch (e) {
        this._streamMaskLocked = false;
        this._streamLoading = false;
        this.$mask.classList.remove('hidden');
        this.$mask.innerHTML =
          '<div style="text-align:center;line-height:1.6;font-size:11px">' +
          '\u26A0\uFE0F 音频加载失败</div>';
      }
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 传输期间更新时间标签，持续刷新数字时长
    ────────────────────────────────────────────────── */
    _updateStreamTimeLabel() {
      if (this._streamMode) {
        var bufferedEnd = this._getBufferedEnd();
        var currentTime = this.$audio && this.$audio.currentTime ? this.$audio.currentTime : 0;
        if (bufferedEnd > 0 || currentTime > 0) {
          this._streamDuration = Math.max(this._streamDuration || 0, bufferedEnd, currentTime);
        }
      }
      this._updateTimeLabel();
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 流接收完成后获取完整数据
       服务端在首次流式响应后会缓存音频，再次请求返回完整数据。
       据此解码真实波形并切换到可拖拽的 Blob URL。
    ────────────────────────────────────────────────── */
    async _finalizeStreamPlayback() {
      if (this._streamComplete) return;
      this._streamComplete = true;
      this._streamMaskLocked = false;
      this._clearStreamFinalizeTimer();
      this._stopStreamUiTimer();
      var decodedPeaks = null;

      /* 移除缓冲监测监听器 */
      if (this._streamBufferCheck) {
        this.$audio.removeEventListener('progress', this._streamBufferCheck);
        this.$audio.removeEventListener('durationchange', this._streamBufferCheck);
        this.$audio.removeEventListener('canplaythrough', this._streamBufferCheck);
        this.$audio.removeEventListener('suspend', this._streamBufferCheck);
        this._streamBufferCheck = null;
      }

      /* 缓存当前时长 */
      if (isFinite(this.$audio.duration)) {
        this._streamDuration = Math.max(this.$audio.duration, this.$audio.currentTime || 0, this._streamDuration || 0);
      }

      var url = this.getAttribute('src');
      if (url) {
        try {
          var response = await fetch(url);
          if (!response.ok) throw new Error('HTTP ' + response.status);
          var buf = await response.arrayBuffer();
          if (buf.byteLength > 0) {
            var ct = (response.headers.get('content-type') || 'audio/mpeg').split(';')[0].trim();

            /* 创建 Blob 用于 audio src；复制一份 buf 用于 decodeAudioData（它会 detach 传入的 ArrayBuffer） */
            var bufCopy = buf.slice(0);
            this._streamBlob = new Blob([buf], { type: ct });

            /* 解码真实波形 */
            try {
              decodedPeaks = await this._decodeBuffer(bufCopy);
            } catch (e) {
              console.warn('[waveform-player] 流数据波形解码失败:', e);
            }

            /* 切换到 Blob URL（可任意拖拽进度、显示真实时长） */
            this._switchToBlobUrl(undefined, decodedPeaks);
          }
        } catch (e) {
          console.warn('[waveform-player] 流数据重新获取失败:', e);
        }
      }

      if (!this._streamBlobSwitched && decodedPeaks && decodedPeaks.length) {
        this._applyPeakData(decodedPeaks);
      }

      this._syncDownloadVisible();

      /* 更新时间标签 */
      this._updateTimeLabel();

      var dur = this._getDisplayDuration();

      this.dispatchEvent(new CustomEvent('stream-end', {
        bubbles: true,
        detail: { duration: dur }
      }));

      this.dispatchEvent(new CustomEvent('waveform-ready', {
        bubbles: true,
        detail: { duration: dur }
      }));
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 切换到 Blob URL（启用完整拖拽进度）
       保留当前播放位置和播放状态。
    ────────────────────────────────────────────────── */
    _switchToBlobUrl(seekTo, finalPeakData) {
      if (!this._streamBlob || this._streamBlobSwitched) return;
      this._streamBlobSwitched = true;
      this._streamMaskLocked = false;

      var wasPlaying = !this.$audio.paused && !this.$audio.ended;
      var wasEnded   = this.$audio.ended;
      var restoreTime = (seekTo !== undefined) ? seekTo
                      : wasEnded ? 0
                      : this.$audio.currentTime;

      if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
      this._objectUrl = URL.createObjectURL(this._streamBlob);
      this.$audio.preload = 'auto';
      this.$audio.src = this._objectUrl;

      var self = this;
      function onLoaded() {
        self.$audio.removeEventListener('loadedmetadata', onLoaded);

        /* 缓存真实时长并刷新显示 */
        if (isFinite(self.$audio.duration) && self.$audio.duration > 0) {
          self._streamDuration = Math.max(self.$audio.duration, restoreTime || 0, self._streamDuration || 0);
        }

        if (restoreTime > 0 && isFinite(restoreTime)) {
          self.$audio.currentTime = Math.min(restoreTime, self.$audio.duration || restoreTime);
        }
        if (finalPeakData && finalPeakData.length) {
          self._applyPeakData(finalPeakData);
        }
        self._updateTimeLabel();
        if (wasPlaying || self._streamPendingPlay) {
          self._streamPendingPlay = false;
          self.$audio.play().catch(function () {});
        }
        self._draw();
      }
      this.$audio.addEventListener('loadedmetadata', onLoaded);
      this.$audio.load();
    }

    _applyPeakData(peaks) {
      if (!peaks || !peaks.length) return;

      var self = this;
      requestAnimationFrame(function () {
        self._peakData = peaks;
        self._draw();
      });
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 探测 MediaSource 可用的 MIME
    ────────────────────────────────────────────────── */
    _detectMSEMime(url, contentType) {
      /* 优先根据 Content-Type */
      var ct = (contentType || '').toLowerCase().split(';')[0].trim();
      var candidates = [];

      /* WAV 不受 MSE 支持，直接降级 */
      if (ct === 'audio/wav' || ct === 'audio/x-wav' || ct === 'audio/wave')
        return null;

      /* 非音频类型不尝试 MSE */
      if (ct && ct.indexOf('audio/') !== 0)
        return null;

      if (ct === 'audio/mpeg' || ct === 'audio/mp3')
        candidates.push('audio/mpeg');
      else if (ct === 'audio/mp4' || ct === 'audio/aac' || ct === 'audio/x-m4a')
        candidates.push('audio/mp4; codecs="mp4a.40.2"');
      else if (ct === 'audio/webm')
        candidates.push('audio/webm; codecs="opus"', 'audio/webm; codecs="vorbis"');
      else if (ct === 'audio/ogg')
        candidates.push('audio/ogg; codecs="opus"', 'audio/ogg; codecs="vorbis"');

      /* 根据扩展名补充 */
      var ext = '';
      try { ext = new URL(url, location.href).pathname.split('.').pop().toLowerCase(); } catch (_) {}
      if (ext === 'mp3' && !candidates.length) candidates.push('audio/mpeg');
      if ((ext === 'mp4' || ext === 'm4a' || ext === 'aac') && !candidates.length)
        candidates.push('audio/mp4; codecs="mp4a.40.2"');
      if (ext === 'webm' && !candidates.length)
        candidates.push('audio/webm; codecs="opus"', 'audio/webm; codecs="vorbis"');
      if (ext === 'ogg' && !candidates.length)
        candidates.push('audio/ogg; codecs="opus"', 'audio/ogg; codecs="vorbis"');

      /* 通用猜测 */
      if (!candidates.length) candidates.push('audio/mpeg');

      for (var i = 0; i < candidates.length; i++) {
        try {
          if (MediaSource.isTypeSupported(candidates[i])) return candidates[i];
        } catch (_) {}
      }
      return null;
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM 路径 A：MediaSource 边收边播
    ────────────────────────────────────────────────── */
    _streamViaMediaSource(reader, mime) {
      var self = this;
      return new Promise(function (resolve, reject) {
        var ms = new MediaSource();
        self._mediaSource = ms;
        if (self._mediaSourceUrl) URL.revokeObjectURL(self._mediaSourceUrl);
        self._mediaSourceUrl = URL.createObjectURL(ms);

        self.$audio.removeAttribute('crossOrigin');
        self.$audio.src = self._mediaSourceUrl;
        self.$audio.load();

        /* ★ STREAM: 设置 AnalyserNode，播放时即可采集实时波形 */
        self._setupStreamAnalyser();

        ms.addEventListener('sourceopen', function onOpen() {
          ms.removeEventListener('sourceopen', onOpen);

          var sb;
          try {
            sb = ms.addSourceBuffer(mime);
          } catch (e) {
            reject(new Error('addSourceBuffer 失败: ' + e.message));
            return;
          }

          var queue    = [];
          var appending = false;
          var done     = false;

          function flushQueue() {
            if (appending || queue.length === 0) return;
            if (ms.readyState !== 'open') return;
            appending = true;
            try {
              sb.appendBuffer(queue.shift());
            } catch (e) {
              appending = false;
              console.warn('[waveform-player] appendBuffer error:', e);
            }
          }

          sb.addEventListener('updateend', function () {
            appending = false;

            /* ★ STREAM: 首次可播后响应用户的播放意图 */
            if (!self._streamAutoPlay && self._streamPendingPlay && self.$audio.paused) {
              self._streamAutoPlay = true;
              self.$mask.classList.add('hidden');
              self.$audio.play().catch(function () {});
            }

            self._updateTimeLabel();
            self._draw();

            if (queue.length > 0) {
              flushQueue();
            } else if (done && ms.readyState === 'open') {
              try { ms.endOfStream(); } catch (_) {}
              self._onStreamFinished();
              resolve();
            }
          });

          /* 读取流 */
          function pump() {
            reader.read().then(function (result) {
              if (result.done) {
                done = true;
                if (!appending && queue.length === 0 && ms.readyState === 'open') {
                  try { ms.endOfStream(); } catch (_) {}
                  self._onStreamFinished();
                  resolve();
                }
                return;
              }
              var chunk = result.value;
              self._streamChunks.push(chunk);
              self._streamTotalBytes += chunk.byteLength;
              if (self.$audio && isFinite(self.$audio.duration) && self.$audio.duration > 0) {
                self._streamDuration = Math.max(self._streamDuration || 0, self.$audio.duration, self.$audio.currentTime || 0);
              } else {
                var bufferedEnd = self._getBufferedEnd();
                if (bufferedEnd > 0) {
                  self._streamDuration = Math.max(self._streamDuration || 0, bufferedEnd, self.$audio.currentTime || 0);
                }
              }
              self._updateStreamTimeLabel();

              queue.push(chunk);
              flushQueue();
              pump();
            }).catch(function (e) {
              if (e.name === 'AbortError') { resolve(); return; }
              reject(e);
            });
          }
          pump();
        });

        ms.addEventListener('error', function () {
          reject(new Error('MediaSource error'));
        });
      });
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM 路径 B：收完再播（降级）
    ────────────────────────────────────────────────── */
    async _streamCollectThenPlay(reader, url, contentType) {
      this.$mask.classList.add('hidden');

      while (true) {
        var result = await reader.read();
        if (result.done) break;
        this._streamChunks.push(result.value);
        this._streamTotalBytes += result.value.byteLength;
        if (this.$audio && isFinite(this.$audio.duration) && this.$audio.duration > 0) {
          this._streamDuration = Math.max(this._streamDuration || 0, this.$audio.duration, this.$audio.currentTime || 0);
        } else {
          var bufferedEnd = this._getBufferedEnd();
          if (bufferedEnd > 0) {
            this._streamDuration = Math.max(this._streamDuration || 0, bufferedEnd, this.$audio.currentTime || 0);
          }
        }
        this._updateStreamTimeLabel();
      }

      /* 构建 Blob 并播放 */
      var blobType = contentType ? contentType.split(';')[0].trim() : 'audio/mpeg';
      this._streamBlob = new Blob(this._streamChunks, { type: blobType });
      if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
      this._objectUrl = URL.createObjectURL(this._streamBlob);

      this.$audio.removeAttribute('crossOrigin');
      this.$audio.src = this._objectUrl;
      this.$audio.load();

      this._onStreamFinished();
    }

    /* ──────────────────────────────────────────────────
       ★ SSE: 从 text/event-stream 响应中提取 base64 音频并播放
       支持 DashScope 等 TTS 服务返回的 SSE 格式：
       data:{"output":{"audio":{"data":"<base64>"}}}
       支持增量分块（多个事件各含一段音频）和单次完整返回两种模式。
    ────────────────────────────────────────────────── */
    async _streamFromSSE(reader) {
      this.$mask.innerHTML = '<div class="spin"></div> 正在接收语音数据…';

      var decoder = new TextDecoder();
      var sseText = '';
      while (true) {
        var result = await reader.read();
        if (result.done) break;
        sseText += decoder.decode(result.value, { stream: true });
        this._streamTotalBytes += result.value.byteLength;
        this._updateStreamProgress();
      }
      sseText += decoder.decode();

      /* 解析 SSE data: 行，收集所有 base64 音频分块 */
      var audioChunks = [];
      var lines = sseText.split('\n');
      for (var i = 0; i < lines.length; i++) {
        var line = lines[i].trim();
        if (line.indexOf('data:') !== 0) continue;
        var jsonStr = line.substring(5).trim();
        if (!jsonStr || jsonStr === '[DONE]') continue;
        try {
          var obj = JSON.parse(jsonStr);
          var b64 = obj && obj.output && obj.output.audio && obj.output.audio.data;
          if (b64) {
            try {
              var binStr = atob(b64);
              var chunk = new Uint8Array(binStr.length);
              for (var j = 0; j < binStr.length; j++) chunk[j] = binStr.charCodeAt(j);
              if (chunk.length > 0) audioChunks.push(chunk);
            } catch (_) {}
          }
        } catch (_) {}
      }

      if (audioChunks.length === 0) throw new Error('SSE 响应中未找到音频数据');

      /* 合并所有分块 */
      var totalLen = 0;
      for (var i = 0; i < audioChunks.length; i++) totalLen += audioChunks[i].length;
      var bytes = new Uint8Array(totalLen);
      var offset = 0;
      for (var i = 0; i < audioChunks.length; i++) {
        bytes.set(audioChunks[i], offset);
        offset += audioChunks[i].length;
      }

      /* 检测 MIME */
      var mime = 'audio/mpeg';
      if (bytes.length >= 4) {
        if (bytes[0] === 0x52 && bytes[1] === 0x49 && bytes[2] === 0x46 && bytes[3] === 0x46) mime = 'audio/wav';
          else if (bytes[0] === 0x4F && bytes[1] === 0x67 && bytes[2] === 0x67 && bytes[3] === 0x53) mime = 'audio/ogg';
          else if (bytes[0] === 0x66 && bytes[1] === 0x4C && bytes[2] === 0x61 && bytes[3] === 0x43) mime = 'audio/flac';
        }

        this._fixWavHeader(bytes);

        this._streamBlob = new Blob([bytes], { type: mime });
      if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
      this._objectUrl = URL.createObjectURL(this._streamBlob);

      this.$audio.removeAttribute('crossOrigin');
      this.$audio.src = this._objectUrl;
      this.$audio.load();

      this._streamChunks = [bytes];
      this._streamTotalBytes = bytes.byteLength;

      this._onStreamFinished();
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 进度显示
    ────────────────────────────────────────────────── */
    _updateStreamProgress() {
      var kb = (this._streamTotalBytes / 1024).toFixed(0);
      var mb = (this._streamTotalBytes / (1024 * 1024)).toFixed(1);
      var sizeText = this._streamTotalBytes > 1048576 ? (mb + ' MB') : (kb + ' KB');

      /* 仅在 mask 可见时更新（MediaSource 自动播放后 mask 已隐藏） */
      if (!this.$mask.classList.contains('hidden')) {
        this.$mask.innerHTML = '<div class="spin"></div> 正在接收流数据… ' + sizeText;
      }
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 流传输完成
    ────────────────────────────────────────────────── */
    async _onStreamFinished() {
      this._streamComplete = true;
      this._stopLiveCapture();

      /* ★ STREAM: 标记实时波形采集完成 */
      if (this._liveMode && !this._liveCollected) {
        this._liveCollected = true;
        this._finalizeLivePeaks();
      }

      /* 合并 Blob（如果有收集到的分块数据） */
      if (!this._streamBlob && this._streamChunks.length > 0) {
        this._streamBlob = new Blob(this._streamChunks);
      }

      /* 仅在有 Blob 数据时尝试精确波形解码 */
      if (this._streamBlob && this._streamBlob.size > 0) {
        /* 修正 WAV 头部（仅在 RIFF 大小字段不正确时才重建） */
        try {
          var headerBuf = await this._streamBlob.slice(0, 44).arrayBuffer();
          var hdr = new Uint8Array(headerBuf);
          if (hdr[0] === 0x52 && hdr[1] === 0x49 && hdr[2] === 0x46 && hdr[3] === 0x46 && hdr.length >= 8) {
            var hdv = new DataView(headerBuf);
            var riffSize = hdv.getUint32(4, true);
            if (riffSize !== this._streamBlob.size - 8) {
              this.$audio.pause();
              var fullBuf = new Uint8Array(await this._streamBlob.arrayBuffer());
              this._fixWavHeader(fullBuf);
              this._streamBlob = new Blob([fullBuf], { type: 'audio/wav' });
              if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
              this._objectUrl = URL.createObjectURL(this._streamBlob);
              this.$audio.src = this._objectUrl;
              this.$audio.load();
            }
          }
        } catch (_) {}

        /* 解码精确波形 */
        try {
          var buf = await this._streamBlob.arrayBuffer();
          this._peakData = await this._decodeBuffer(buf);
          this.$mask.classList.add('hidden');
          this._draw();
        } catch (e) {
          console.warn('[waveform-player] 流数据波形解码失败:', e);
          if (this._peakData.length) {
            this.$mask.classList.add('hidden');
          } else {
            this.$mask.innerHTML = '\u26A0\uFE0F 波形解码失败';
          }
        }
      } else {
        /* 原生流模式：无 Blob 数据，直接使用实时波形 */
        this.$mask.classList.add('hidden');
        this._draw();
      }

      /* 显示下载按钮 */
      this._syncDownloadVisible();

      /* 缓存时长 */
      if (isFinite(this.$audio.duration)) {
        this._streamDuration = Math.max(this.$audio.duration, this.$audio.currentTime || 0, this._streamDuration || 0);
      }

      this._updateTimeLabel();

      /* 派发事件 */
      this.dispatchEvent(new CustomEvent('stream-end', {
        bubbles: true,
        detail: {
          size: this._streamTotalBytes,
          duration: this._getDisplayDuration()
        }
      }));

      this.dispatchEvent(new CustomEvent('waveform-ready', {
        bubbles: true,
        detail: { duration: this._getDisplayDuration() }
      }));
    }

    /* ──────────────────────────────────────────────────
       修正 WAV 文件头中的 RIFF/data 块大小字段。
       流式 WAV（如 DashScope TTS）使用占位值 0x7FFFFFBF，
       浏览器 audio 元素无法播放此类文件。
    ────────────────────────────────────────────────── */
    _fixWavHeader(bytes) {
      if (!bytes || bytes.length < 44) return;
      if (bytes[0] !== 0x52 || bytes[1] !== 0x49 || bytes[2] !== 0x46 || bytes[3] !== 0x46) return;
      if (bytes[8] !== 0x57 || bytes[9] !== 0x41 || bytes[10] !== 0x56 || bytes[11] !== 0x45) return;

      var dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
      dv.setUint32(4, bytes.length - 8, true);

      var pos = 12;
      while (pos + 8 <= bytes.length) {
        var id = String.fromCharCode(bytes[pos], bytes[pos + 1], bytes[pos + 2], bytes[pos + 3]);
        if (id === 'data') {
          dv.setUint32(pos + 4, bytes.length - pos - 8, true);
          break;
        }
        var size = dv.getUint32(pos + 4, true);
        if (size > bytes.length) break;
        pos += 8 + size;
        if (size & 1) pos++;
      }
    }

    /* ──────────────────────────────────────────────────
       ★ STREAM: 为流模式创建 AnalyserNode（实时波形）
       使用 crossOrigin-safe 的 MediaElement 方式
    ────────────────────────────────────────────────── */
    _setupStreamAnalyser() {
      try {
        if (!this._liveCtx) {
          this._liveCtx      = new (window.AudioContext || window.webkitAudioContext)();
          this._liveAnalyser = this._liveCtx.createAnalyser();
          this._liveAnalyser.fftSize = 2048;
          this._liveSource   = this._liveCtx.createMediaElementSource(this.$audio);
          this._liveSource.connect(this._liveAnalyser);
          this._liveAnalyser.connect(this._liveCtx.destination);
        }
        this._liveMode      = true;
        this._livePeaks     = [];
        this._liveCollected = false;
        this._zeroFrames    = 0;
      } catch (e) {
        console.warn('[waveform-player] AnalyserNode 创建失败:', e);
      }
    }

    /* ════════════════════════════════
       从 File 加载
    ════════════════════════════════ */
    async _loadFile(file) {
      if (!file.type.startsWith('audio')) {
        alert('请选择音频文件！');
        return;
      }
      this._resetState();
      this._currentFile = file;

      this._updateLabel('\uD83C\uDFB5 ' + file.name);

      this.$player.classList.add('show');
      this.$fileZone.classList.add('hidden');

      if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
      this._objectUrl = URL.createObjectURL(file);
      this.$audio.removeAttribute('crossOrigin');
      this.$audio.src = this._objectUrl;
      this.$audio.load();

      /* 立即生成模拟波形并显示，无需等待解码 */
      this._generateSimulatedPeaks();
      this._initCanvas();
      this._draw();
      this.$mask.classList.add('hidden');

      try {
        this._peakData = await this._decodeBuffer(await file.arrayBuffer());
        this._draw();
        this.dispatchEvent(new CustomEvent('waveform-ready', {
          bubbles: true,
          detail: { file: file, duration: this.$audio.duration }
        }));
      } catch (err) {
        this.$mask.classList.remove('hidden');
        this.$mask.innerHTML = '\u26A0\uFE0F 解析失败：' + err.message;
      }
    }

    /* ════════════════════════════════
       ★ STREAM: 下载功能 — 流完成后才执行
    ════════════════════════════════ */
    _doDownload() {
      /* ★ STREAM: 从收集的 Blob 下载（如有）；否则走后续 fetch 下载 */
      if (this._streamMode && this._streamBlob) {
        var blobUrl = URL.createObjectURL(this._streamBlob);
        var filename = this._guessFilename(this.getAttribute('src') || '');
        this._triggerDownload(blobUrl, filename);
        setTimeout(function () { URL.revokeObjectURL(blobUrl); }, 1000);
        return;
      }

      if (this._currentFile) {
        var blobUrl = URL.createObjectURL(this._currentFile);
        this._triggerDownload(blobUrl, this._currentFile.name);
        setTimeout(function () { URL.revokeObjectURL(blobUrl); }, 1000);
        return;
      }

      var src = this.getAttribute('src');
      if (!src) return;

      var filename = this._guessFilename(src);
      var self = this;

      this.$dlBtn.classList.add('downloading');
      fetch(src)
        .then(function (r) {
          if (!r.ok) throw new Error(r.status);
          return r.blob();
        })
        .then(function (blob) {
          var blobUrl = URL.createObjectURL(blob);
          self._triggerDownload(blobUrl, filename);
          setTimeout(function () { URL.revokeObjectURL(blobUrl); }, 1000);
        })
        .catch(function () {
          self._triggerDownload(src, filename);
        })
        .finally(function () {
          self.$dlBtn.classList.remove('downloading');
        });
    }

    _triggerDownload(url, filename) {
      var a = document.createElement('a');
      a.href = url;
      a.download = filename || 'audio';
      a.style.display = 'none';
      document.body.appendChild(a);
      a.click();
      setTimeout(function () { document.body.removeChild(a); }, 200);
    }

    _guessFilename(url) {
      try {
        var pathname = new URL(url, location.href).pathname;
        var name = decodeURIComponent(pathname.split('/').pop());
        if (name && /\.\w{2,5}$/.test(name)) return name;
      } catch (_) {}
      return 'audio.mp3';
    }

    download() {
      var self = this;
      this._ready.then(function () { self._doDownload(); });
    }

    /* ════════════════════════════════
       CORS 探测
    ════════════════════════════════ */
    _testCORS(url) {
      return new Promise(function (resolve) {
        var a = document.createElement('audio');
        a.crossOrigin = 'anonymous';
        a.preload = 'metadata';
        var timer = setTimeout(function () { cleanup(); resolve(false); }, 4000);
        function cleanup() {
          a.removeAttribute('src');
          a.load();
          clearTimeout(timer);
        }
        a.addEventListener('loadedmetadata', function () {
          cleanup(); resolve(true);
        }, { once: true });
        a.addEventListener('error', function () {
          cleanup(); resolve(false);
        }, { once: true });
        a.src = url;
        a.load();
      });
    }

    /* ════════════════════════════════
       解码 ArrayBuffer → 真实峰值
    ════════════════════════════════ */
    async _decodeBuffer(buf) {
      var ctx = new (window.AudioContext || window.webkitAudioContext)();
      var ab;
      try {
        ab = await ctx.decodeAudioData(buf);
      } finally {
        ctx.close();
      }

      var numCh  = ab.numberOfChannels;
      var len    = ab.length;
      var merged = new Float32Array(len);

      for (var ch = 0; ch < numCh; ch++) {
        var d = ab.getChannelData(ch);
        for (var i = 0; i < len; i++) merged[i] += Math.abs(d[i]);
      }
      for (var i = 0; i < len; i++) merged[i] /= numCh;

      var N    = 1000;
      var step = Math.max(1, Math.floor(len / N));
      var peaks = new Float32Array(N);
      var maxV  = 0;

      for (var i = 0; i < N; i++) {
        var sum = 0, cnt = 0, s = i * step;
        for (var j = 0; j < step && s + j < len; j++, cnt++) {
          sum += merged[s + j] * merged[s + j];
        }
        peaks[i] = cnt ? Math.sqrt(sum / cnt) : 0;
        if (peaks[i] > maxV) maxV = peaks[i];
      }
      for (var i = 0; i < N; i++) {
        peaks[i] = maxV > 0 ? peaks[i] / maxV : 0;
        peaks[i] = Math.max(0.04, peaks[i]);
      }
      return peaks;
    }

    /* ════════════════════════════════
       生成模拟波形数据（流模式用）
       使用确定性伪随机 + 平滑，产生自然的波形外观
    ════════════════════════════════ */
    _generateSimulatedPeaks() {
      var N = 1000;
      var raw = new Float32Array(N);
      var seed = 12345;
      for (var i = 0; i < N; i++) {
        seed = (seed * 16807) % 2147483647;
        raw[i] = 0.10 + 0.80 * ((seed & 0xffff) / 0xffff);
      }
      var peaks = new Float32Array(N);
      for (var i = 0; i < N; i++) {
        var s = 0, c = 0;
        for (var j = Math.max(0, i - 3); j <= Math.min(N - 1, i + 3); j++) {
          s += raw[j]; c++;
        }
        peaks[i] = Math.max(0.04, s / c);
      }
      this._peakData = peaks;
    }

    /* ════════════════════════════════
       实时分析（非流 CORS 降级用）
    ════════════════════════════════ */
    _enterLiveMode() {
      this._liveMode      = true;
      this._livePeaks     = [];
      this._liveCollected = false;
      this._zeroFrames    = 0;

      this.$mask.innerHTML = '\uD83C\uDFA7 点击播放，实时生成真实波形';
      this.$mask.classList.remove('hidden');

      try {
        if (!this._liveCtx) {
          this._liveCtx      = new (window.AudioContext || window.webkitAudioContext)();
          this._liveAnalyser = this._liveCtx.createAnalyser();
          this._liveAnalyser.fftSize = 2048;
          this._liveSource   = this._liveCtx.createMediaElementSource(this.$audio);
          this._liveSource.connect(this._liveAnalyser);
          this._liveAnalyser.connect(this._liveCtx.destination);
        }
      } catch (e) {
        this.$mask.innerHTML = '\u26A0\uFE0F 无法创建音频分析节点';
      }
    }

    _startLiveCapture() {
      if (!this._liveAnalyser) return;
      if (this._liveCtx.state === 'suspended') this._liveCtx.resume();
      if (this._liveCollected) return;
      this.$mask.classList.add('hidden');

      var self   = this;
      var bufLen = this._liveAnalyser.fftSize;
      var data   = new Float32Array(bufLen);
      var N      = 1000;

      function loop() {
        if (self.$audio.paused || self.$audio.ended) return;
        self._liveAnalyser.getFloatTimeDomainData(data);

        var sum = 0;
        for (var i = 0; i < bufLen; i++) sum += data[i] * data[i];
        var rms = Math.sqrt(sum / bufLen);

        if (rms < 1e-10) {
          self._zeroFrames++;
          if (self._zeroFrames > 30) {
            self._stopLiveCapture();
            self.$mask.classList.remove('hidden');
            self.$mask.innerHTML = '\u26A0\uFE0F 跨域限制，无法分析波形';
            return;
          }
        } else {
          self._zeroFrames = 0;
        }

        var dur = self._getDisplayDuration() || 1;       /* ★ STREAM: 使用安全时长 */
        var idx = Math.min(
          Math.floor((self.$audio.currentTime / dur) * N), N - 1
        );
        if (!self._livePeaks[idx] || rms > self._livePeaks[idx]) {
          self._livePeaks[idx] = rms;
        }

        self._buildLivePeakData();
        self._draw();
        self._liveRAF = requestAnimationFrame(loop);
      }

      this._liveRAF = requestAnimationFrame(loop);
    }

    _stopLiveCapture() {
      if (this._liveRAF) {
        cancelAnimationFrame(this._liveRAF);
        this._liveRAF = null;
      }
    }

    _buildLivePeakData() {
      var N = 1000, mx = 0;
      for (var i = 0; i < this._livePeaks.length; i++) {
        if (this._livePeaks[i] > mx) mx = this._livePeaks[i];
      }
      var p = new Float32Array(N);
      for (var i = 0; i < N; i++) {
        p[i] = mx > 0 ? (this._livePeaks[i] || 0) / mx : 0;
        if (this._livePeaks[i] !== undefined) p[i] = Math.max(0.04, p[i]);
      }
      this._peakData = p;
    }

    _finalizeLivePeaks() {
      var N = 1000;
      for (var i = 0; i < N; i++) {
        if (this._livePeaks[i] === undefined) {
          var p = this._nearest(i, -1);
          var n = this._nearest(i,  1);
          if (p !== null && n !== null) {
            this._livePeaks[i] = (p + n) / 2;
          } else {
            this._livePeaks[i] = (p !== null ? p : n !== null ? n : 0);
          }
        }
      }
      this._buildLivePeakData();
      this._draw();
    }

    _nearest(idx, dir) {
      for (var i = idx + dir; i >= 0 && i < 1000; i += dir) {
        if (this._livePeaks[i] !== undefined) return this._livePeaks[i];
      }
      return null;
    }

    /* ════════════════════════════════
       Canvas 初始化 & 绘制
    ════════════════════════════════ */
    _initCanvas() {
      var w = this.$waveWrap.offsetWidth;
      var h = this.$waveWrap.offsetHeight;
      this.$canvas.width  = Math.round(w * this._dpr);
      this.$canvas.height = Math.round(h * this._dpr);
      this.$ctx.setTransform(this._dpr, 0, 0, this._dpr, 0, 0);
    }

    _draw() {
      var W   = this.$canvas.width / this._dpr;
      var H   = this.$canvas.height / this._dpr;
      var ctx = this.$ctx;
      ctx.clearRect(0, 0, W, H);
      if (!this._peakData.length) return;

      var BAR_W = 3, GAP = 1.5, UNIT = BAR_W + GAP;
      var barCount = Math.floor(W / UNIT);
      var dur  = this._getDisplayDuration() || 0;       /* ★ STREAM: 安全时长 */
      var cur  = this.$audio.currentTime || 0;
      var prog = dur ? cur / dur : 0;
      var midY = H / 2;

      var cs      = getComputedStyle(this);
      var accent  = cs.getPropertyValue('--_accent').trim()   || '#5a8f85';
      var waveRGB = cs.getPropertyValue('--_wave-off').trim() || '184,208,205';
      var cursor  = cs.getPropertyValue('--_cursor').trim()   || '#2d6b62';

      if (this._hoverX >= 0) {
        ctx.fillStyle = this._hexToRgba(accent, 0.08);
        ctx.fillRect(0, 0, this._hoverX, H);
      }

      for (var i = 0; i < barCount; i++) {
        var idx  = Math.floor((i / barCount) * this._peakData.length);
        var peak = this._peakData[idx] || 0;

        if (this._liveMode && !this._liveCollected && peak === 0) {
          ctx.fillStyle = 'rgba(' + waveRGB + ',0.20)';
          ctx.beginPath();
          var x0 = i * UNIT;
          if (ctx.roundRect) ctx.roundRect(x0, midY - 1, BAR_W, 2, 1);
          else ctx.rect(x0, midY - 1, BAR_W, 2);
          ctx.fill();
          continue;
        }

        var barH = Math.max(2, peak * H * 0.88);
        var x    = i * UNIT;
        var played = (x / W) < prog;

        ctx.fillStyle = played
          ? accent
          : 'rgba(' + waveRGB + ',' + (0.35 + 0.65 * peak).toFixed(2) + ')';

        ctx.beginPath();
        if (ctx.roundRect) ctx.roundRect(x, midY - barH / 2, BAR_W, barH, 1.5);
        else ctx.rect(x, midY - barH / 2, BAR_W, barH);
        ctx.fill();
      }

      if (dur > 0) {
        var cx = prog * W;
        ctx.fillStyle = cursor;
        ctx.fillRect(cx - 1, 0, 2, H);
        ctx.beginPath();
        ctx.arc(cx, midY, 4.5, 0, Math.PI * 2);
        ctx.fill();
      }
    }

    _hexToRgba(hex, alpha) {
      if (!hex || hex.charAt(0) !== '#') return 'rgba(90,143,133,' + alpha + ')';
      hex = hex.replace('#', '');
      if (hex.length === 3) hex = hex[0]+hex[0]+hex[1]+hex[1]+hex[2]+hex[2];
      var r = parseInt(hex.substring(0, 2), 16);
      var g = parseInt(hex.substring(2, 4), 16);
      var b = parseInt(hex.substring(4, 6), 16);
      if (isNaN(r)) return 'rgba(90,143,133,' + alpha + ')';
      return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')';
    }

    /* ★ STREAM: 重置状态 — 包含流相关清理 */
    _resetState() {
      /* 停止当前播放，防止切换音源时出现多重声音 */
      if (this.$audio) {
        this.$audio.pause();
        this.$audio.preload = 'auto';  /* 恢复默认预加载（流模式会设为 none） */
      }

      this._liveMode      = false;
      this._liveCollected = false;
      this._livePeaks     = [];
      this._zeroFrames    = 0;
      this._destroyLiveNodes();

      /* ★ STREAM: 中止进行中的流 */
      if (this._streamAbort) {
        this._streamAbort.abort();
        this._streamAbort = null;
      }
      this._streamMode       = false;
      this._streamChunks     = [];
      this._streamTotalBytes = 0;
      this._streamComplete   = false;
      this._streamBlob       = null;
      this._streamAutoPlay   = false;
      this._streamDuration   = 0;
      this._streamBlobSwitched = false;
      this._streamLoading    = false;
      this._streamPendingPlay = false;
      this._streamStarted    = false;
      this._streamMaskLocked = false;
      this._clearStreamFinalizeTimer();
      this._stopStreamUiTimer();

      /* 清理流缓冲监测监听器 */
      if (this._streamBufferCheck && this.$audio) {
        this.$audio.removeEventListener('progress', this._streamBufferCheck);
        this.$audio.removeEventListener('durationchange', this._streamBufferCheck);
        this.$audio.removeEventListener('canplaythrough', this._streamBufferCheck);
        this.$audio.removeEventListener('suspend', this._streamBufferCheck);
        this._streamBufferCheck = null;
      }

      /* 清理 MediaSource */
      if (this._mediaSourceUrl) {
        URL.revokeObjectURL(this._mediaSourceUrl);
        this._mediaSourceUrl = null;
      }
      this._mediaSource = null;
    }

    _destroyLiveNodes() {
      this._stopLiveCapture();
      try { if (this._liveSource)   this._liveSource.disconnect();   } catch (_) {}
      try { if (this._liveAnalyser) this._liveAnalyser.disconnect(); } catch (_) {}
      try { if (this._liveCtx)      this._liveCtx.close();           } catch (_) {}
      this._liveSource = this._liveAnalyser = this._liveCtx = null;
    }

    _togglePlay() {
      if (this._streamMode && !this._streamStarted) {
        this._streamPendingPlay = true;
        this._streamStarted = true;
        this._streamLoading = true;
        this._streamMaskLocked = true;
        this._startStreamUiTimer();
        this.$mask.classList.remove('hidden');
        this.$mask.innerHTML = '<div class="spin"></div> 正在接收流数据…';
        this._updateIcon();
        this._tryPreloadStreamAudio(this.getAttribute('src'));
        return;
      }

      /* ★ STREAM: 接收期间点击播放，始终保留播放意图。
         若此时已有可用 src，则立即尝试播放；否则等后续数据到达后自动开始。 */
      if (this._streamMode && this.$audio.paused) {
        this._streamPendingPlay = true;
        if (this._streamMode && !this._peakData.length) {
          this._generateSimulatedPeaks();
          this.$mask.classList.add('hidden');
          this._draw();
        }
        if (this._liveCtx && this._liveCtx.state === 'suspended') {
          this._liveCtx.resume();
        }
        if (!this.$audio.src) {
          this._updateIcon();
          return;
        }
      }
      if (!this.$audio.src || this.$audio.error) return;
      /* ★ STREAM: 首次点击时生成模拟波形（canplay 未触发时的后备） */
      if (this._streamMode && !this._peakData.length) {
        this._generateSimulatedPeaks();
        this.$mask.classList.add('hidden');
        this._draw();
      }
      if (this._liveCtx && this._liveCtx.state === 'suspended') {
        this._liveCtx.resume();
      }
      if (this.$audio.paused) {
        this.$audio.play().catch(function () {});
      } else {
        this._streamPendingPlay = false;
        this.$audio.pause();
      }
    }

    _updateIcon() {
      this.$playIcon.innerHTML = (this.$audio.paused && !(this._streamMode && this._streamPendingPlay))
        ? '<polygon points="6,4 20,12 6,20"/>'
        : '<rect x="5" y="4" width="4" height="16"/><rect x="15" y="4" width="4" height="16"/>';
    }

    _seekByEvent(e) {
      var dur = this._getDisplayDuration();
      if (!dur) return;
      var rect  = this.$canvas.getBoundingClientRect();
      var ratio = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      var targetTime = ratio * dur;

      /* ★ STREAM: 完整数据已就绪时切换到 Blob URL 再 seek（支持任意位置拖拽） */
      if (this._streamMode && this._streamBlob && !this._streamBlobSwitched) {
        this._switchToBlobUrl(targetTime);
        return;
      }

      /* ★ STREAM: 流式播放中，限制 seek 在浏览器已缓冲的范围内 */
      if (this._streamMode && !this._streamBlobSwitched) {
        var buffered = this.$audio.buffered;
        if (buffered.length > 0) {
          targetTime = Math.min(targetTime, buffered.end(buffered.length - 1) - 0.1);
          targetTime = Math.max(0, targetTime);
        }
      }

      this.$audio.currentTime = targetTime;
      this._updateTimeLabel();
      this._draw();
    }

    _fmt(s) {
      if (!isFinite(s) || s <= 0) return '--:--';
      var m   = Math.floor(s / 60);
      var sec = Math.floor(s % 60).toString();
      if (sec.length < 2) sec = '0' + sec;
      return m + ':' + sec;
    }

    /* ════════════════════════════════
       公开 API
    ════════════════════════════════ */
    play() {
      var self = this;
      this._ready.then(function () { self.$audio.play(); });
    }
    pause() {
      var self = this;
      this._ready.then(function () { self.$audio.pause(); });
    }
    stop() {
      var self = this;
      this._ready.then(function () {
        self.$audio.pause();
        self.$audio.currentTime = 0;
      });
    }

    get currentTime()  { return this.$audio ? this.$audio.currentTime : 0; }
    set currentTime(v) { if (this.$audio) this.$audio.currentTime = v; }
    get volume()       { return this.$audio ? this.$audio.volume : 1; }
    set volume(v) {
      if (this.$audio) {
        this.$audio.volume = v;
        this.$volSlider.value = v;
      }
    }

    /* ★ STREAM: 只读属性 — 流是否完成 */
    get streamComplete() { return this._streamComplete; }

    disconnectedCallback() {
      if (this._objectUrl) URL.revokeObjectURL(this._objectUrl);
      if (this._mediaSourceUrl) URL.revokeObjectURL(this._mediaSourceUrl);
      if (this._streamAbort) this._streamAbort.abort();
      this._destroyLiveNodes();
    }
  }


  /* ─── 兜底 CSS ─── */
  var FALLBACK_CSS = ':host{display:block;width:100%;font-family:Arial,sans-serif;--_accent:var(--wp-color,#5a8f85);--_bg:var(--wp-bg,#f0f5f4);--_bg-hover:var(--wp-bg-hover,#e0efed);--_border:var(--wp-border,#5a8f85);--_text:var(--wp-text,#555);--_text-hint:var(--wp-text-hint,#5a8f85);--_wave-off:var(--wp-wave-off,184,208,205);--_mask-bg:var(--wp-mask-bg,rgba(240,245,244,.92));--_shadow:var(--wp-shadow,0 3px 12px rgba(0,0,0,.14));--_slider-bg:var(--wp-slider-bg,#b8d0cd);--_cursor:var(--wp-cursor,#2d6b62);--_icon-fill:var(--wp-icon-fill,#555);--_spin-ring:var(--wp-spin-ring,#b8cecc)}:host([dark]){--_accent:var(--wp-color,#5eead4);--_bg:var(--wp-bg,#1e2530);--_bg-hover:var(--wp-bg-hover,#283040);--_border:var(--wp-border,#3a4a5a);--_text:var(--wp-text,#b0bec5);--_text-hint:var(--wp-text-hint,#5eead4);--_wave-off:var(--wp-wave-off,60,80,95);--_mask-bg:var(--wp-mask-bg,rgba(25,32,42,.92));--_shadow:var(--wp-shadow,0 3px 16px rgba(0,0,0,.4));--_slider-bg:var(--wp-slider-bg,#3a4a5a);--_cursor:var(--wp-cursor,#5eead4);--_icon-fill:var(--wp-icon-fill,#90a4ae);--_spin-ring:var(--wp-spin-ring,#3a4a5a)}@media(prefers-color-scheme:dark){:host([auto-dark]){--_accent:var(--wp-color,#5eead4);--_bg:var(--wp-bg,#1e2530);--_bg-hover:var(--wp-bg-hover,#283040);--_border:var(--wp-border,#3a4a5a);--_text:var(--wp-text,#b0bec5);--_text-hint:var(--wp-text-hint,#5eead4);--_wave-off:var(--wp-wave-off,60,80,95);--_mask-bg:var(--wp-mask-bg,rgba(25,32,42,.92));--_shadow:var(--wp-shadow,0 3px 16px rgba(0,0,0,.4));--_slider-bg:var(--wp-slider-bg,#3a4a5a);--_cursor:var(--wp-cursor,#5eead4);--_icon-fill:var(--wp-icon-fill,#90a4ae);--_spin-ring:var(--wp-spin-ring,#3a4a5a)}}.file-zone{border:2px dashed var(--_border);border-radius:10px;padding:20px;text-align:center;cursor:pointer;background:var(--_bg);position:relative;transition:background .2s,border-color .2s}.file-zone:hover,.file-zone.over{background:var(--_bg-hover);border-color:var(--_accent)}.file-zone input[type=file]{position:absolute;inset:0;opacity:0;cursor:pointer;width:100%;height:100%}.file-zone .hint{pointer-events:none;font-size:13px;color:var(--_text-hint)}.file-zone .hint b{display:block;font-size:15px;margin-bottom:4px}.file-zone.hidden{display:none}.file-name{font-size:12px;color:var(--_text);text-align:center;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;padding:4px 0;display:none}.file-name.show{display:block}.player{display:none;align-items:center;gap:12px;background:var(--_bg);border-radius:10px;padding:12px 18px;box-shadow:var(--_shadow)}.player.show{display:flex}.play-btn{width:40px;height:40px;border-radius:50%;border:none;background:var(--_accent);cursor:pointer;flex-shrink:0;display:flex;align-items:center;justify-content:center;transition:background .2s,transform .1s;box-shadow:0 2px 6px rgba(0,0,0,.2)}.play-btn:hover{filter:brightness(1.15)}.play-btn:active{transform:scale(.92)}.wave-wrap{flex:1;height:56px;position:relative;cursor:pointer;min-width:0}.wave-wrap canvas{width:100%;height:100%;display:block}.mask{position:absolute;inset:0;background:var(--_mask-bg);display:flex;align-items:center;justify-content:center;font-size:12px;color:var(--_accent);gap:7px;border-radius:4px;transition:opacity .3s}.mask.hidden{opacity:0;pointer-events:none}.spin{width:14px;height:14px;border:2px solid var(--_spin-ring);border-top-color:var(--_accent);border-radius:50%;animation:wp-spin .7s linear infinite;flex-shrink:0}@keyframes wp-spin{to{transform:rotate(360deg)}}.time-label{font-size:12px;color:var(--_text);white-space:nowrap;min-width:90px;text-align:right;font-family:monospace}.vol-wrap{display:flex;align-items:center;gap:5px;flex-shrink:0}.vol-wrap svg{opacity:.65;flex-shrink:0;fill:var(--_icon-fill)}input[type=range]{-webkit-appearance:none;width:64px;height:4px;background:var(--_slider-bg);border-radius:2px;cursor:pointer;accent-color:var(--_accent)}input[type=range]::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:12px;border-radius:50%;background:var(--_accent);cursor:pointer}' +
  '.dl-btn{background:none;border:none;padding:4px;cursor:pointer;flex-shrink:0;display:flex;align-items:center;justify-content:center;color:var(--_icon-fill);opacity:.55;transition:opacity .2s,color .2s,transform .1s;border-radius:4px}.dl-btn:hover{opacity:1;color:var(--_accent)}.dl-btn:active{transform:scale(.88)}.dl-btn.downloading{pointer-events:none;opacity:.3}.dl-btn.hidden{display:none}';


  customElements.define('waveform-player', WaveformPlayer);

})();
