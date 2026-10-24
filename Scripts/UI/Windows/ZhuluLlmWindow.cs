using System;
using UnityEngine;
using UnityEngine.UI;
using ZhuluRebuild.Core;
using ZhuluRebuild.Interop;

namespace ZhuluRebuild.UI.Windows
{
    /// <summary>
    /// 大模型接入窗口（2026-10-24 第8步；同日重写布局）。
    ///
    /// 首版三个问题（用户截图反馈）：
    ///   ① 开关/协议只是给 Text 挂 Button，没有 Graphic 作点击目标 → 点不动
    ///   ② 标题「大模型接入 · 史官评配置」超出牌匾宽度 → 被裁切
    ///   ③ 行距与内容区原点自算不一致 → 控件错位、窗口过大
    ///
    /// 本版照搬 ZhuluSettingsWindow 的结构（卡片 + 绝对 y 偏移），
    /// 控件一律用游戏自带九宫格底图（backgroundKingdomElement）+ Image + Button，
    /// 并显式设置 targetGraphic —— 这是本 mod 里已验证可点的写法。
    /// </summary>
    public class ZhuluLlmWindow : ZhuluScrollWindowBase
    {
        public static ZhuluLlmWindow Instance { get; private set; }

        private LlmConfig _cfg = new LlmConfig();

        private Text _statusText;
        private Text _resultText;
        private Image _enableImg;
        private Text _enableText;
        private Image _autoImg;
        private Text _autoText;
        private Text _protocolText;
        private Text _sampleText;

        private InputField _endpointInput;
        private InputField _modelInput;
        private InputField _keyInput;
        private InputField _timeoutInput;
        private InputField _maxReqInput;

        private int _sampleInterval = 3;   // LlmV2-5：默认 3 年（Reload 时以 ZhuluConfig 为准）

        private volatile string _pendingResult;

        protected override string WindowTitle => "<b>大模型接入</b>";
        protected override float WindowWidth => 340f;
        protected override float WindowHeight => 420f;
        protected override float PlaqueWidth => 200f;
        protected override float ContentTopBand => 68f;
        protected override bool WantsScrollbar => true;

        public void Initialize()
        {
            Instance = this;
            ZhuluContext.Instance.LogInfo("[ZhuluLlmWindow] 初始化完成（首次打开时构建）");
        }

        private void OnEnable()
        {
            if (World.world == null) return;
            EnsureBuilt();
            Reload();
        }

        public void Show()
        {
            if (World.world != null && gameObject.activeInHierarchy)
            {
                EnsureBuilt();
                Reload();
            }
        }

        // ═══════════════════════ 构建 ═══════════════════════

        protected override void BuildContent()
        {
            Text sub = CreateText(Bg, $"<color={ZhuluUIStyle.Hex.PaperDim}>AI 评价服务 · 密钥仅存本地</color>", 9);
            sub.alignment = TextAnchor.MiddleCenter;
            sub.raycastTarget = false;
            var subRect = sub.GetComponent<RectTransform>();
            subRect.anchorMin = new Vector2(0.5f, 1);
            subRect.anchorMax = new Vector2(0.5f, 1);
            subRect.pivot = new Vector2(0.5f, 1);
            subRect.anchoredPosition = new Vector2(-8, -38);
            subRect.sizeDelta = new Vector2(InnerWidth, 12f);

            CreateStatusCard();
            CreateConnectCard();
            CreateParamCard();
            CreateActionCard();

            RefreshAll();
        }

        private Transform CreateCard(float height)
        {
            GameObject row = new GameObject("Card", typeof(RectTransform));
            row.transform.SetParent(ContentRoot, false);
            row.GetComponent<RectTransform>().sizeDelta = new Vector2(InnerWidth, height);

            GameObject bgObj = new GameObject("Bg", typeof(RectTransform), typeof(Image));
            bgObj.transform.SetParent(row.transform, false);
            var bgRect = bgObj.GetComponent<RectTransform>();
            bgRect.anchorMin = Vector2.zero;
            bgRect.anchorMax = Vector2.one;
            bgRect.offsetMin = new Vector2(-36f, 0f);
            bgRect.offsetMax = Vector2.zero;
            var bgImg = bgObj.GetComponent<Image>();
            bgImg.color = new Color(0.16f, 0.17f, 0.21f, 0.9f);
            bgImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bgImg.type = Image.Type.Sliced;
            bgImg.raycastTarget = false;

            return row.transform;
        }

        private void CreateCardTitle(Transform card, string text, string colorHex)
        {
            Text t = CreateText(card, $"<color={colorHex}>{text}</color>", 10);
            t.alignment = TextAnchor.MiddleLeft;
            var rt = t.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(10, -4);
            rt.sizeDelta = new Vector2(InnerWidth - 20, 14f);
            t.raycastTarget = false;
        }

        private void CreateStatusCard()
        {
            // LlmV2-9：状态改细条（单行换行），不再是占两行的大卡
            Transform card = CreateCard(32f);
            _statusText = CreateText(card, "", 9);
            _statusText.alignment = TextAnchor.MiddleLeft;
            _statusText.horizontalOverflow = HorizontalWrapMode.Wrap;
            var rt = _statusText.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(10, -6);
            rt.sizeDelta = new Vector2(InnerWidth - 20, 20f);
            _statusText.raycastTarget = false;
        }

        private void CreateConnectCard()
        {
            Transform card = CreateCard(152f);
            CreateCardTitle(card, "连 接", ZhuluUIStyle.Hex.Gold);

            CreateSwitchRow(card, -34, "启用", out _enableImg, out _enableText, () =>
            {
                _cfg.Enabled = !_cfg.Enabled;
                RefreshAll();
            });

            CreateCycleRow(card, -58, "协议", out _protocolText, () =>
            {
                _cfg.Protocol = NextProtocol(_cfg.Protocol);
                RefreshAll();
            });

            // LlmFix-3：上限放宽防长密钥截断；LlmV2-9：输入框右栏定宽（与开关/步进器同一右缘线）
            _endpointInput = CreateInputRow(card, -82, "接口地址", 200, 168f);
            _modelInput = CreateInputRow(card, -106, "模型", 64, 118f);
            _keyInput = CreateInputRow(card, -130, "API Key", 256, 168f, masked: true);
        }

        private void CreateParamCard()
        {
            Transform card = CreateCard(132f);
            CreateCardTitle(card, "参 数", ZhuluUIStyle.Hex.Gold);

            _timeoutInput = CreateInputRow(card, -34, "超时（秒）", 4, 84f);
            _maxReqInput = CreateInputRow(card, -58, "每局上限", 4, 84f);

            CreateSwitchRow(card, -82, "自动评价", out _autoImg, out _autoText, () =>
            {
                _cfg.AutoEval = !_cfg.AutoEval;
                RefreshAll();
            });

            // LlmV2-5：采样间隔改 −/+ 步进器（最低 2 年、2 再减=关闭、封顶 50），默认 3
            CreateStepperRow(card, -106, "采样间隔", () => _sampleInterval, v =>
            {
                _sampleInterval = v;
                try
                {
                    ZhuluConfig.Instance.EmperorSampleInterval = v;
                    ZhuluConfig.Instance.Save();
                }
                catch { }
            });
        }

        private void CreateActionCard()
        {
            // LlmV2-9：操作卡——等宽三按钮一行 + 固定高结果框（不再跳动撑卡）
            Transform card = CreateCard(112f);
            CreateCardTitle(card, "操 作", ZhuluUIStyle.Hex.Gold);

            float btnW = (InnerWidth - 16f - 16f) / 3f;
            CreateButton(card, "测试连接", 8, -22, btnW, 22f, TestConnection);
            CreateButton(card, "保 存", 8 + btnW + 8f, -22, btnW, 22f, Save);
            CreateButton(card, "重 载", 8 + (btnW + 8f) * 2f, -22, btnW, 22f, Reload);

            GameObject box = new GameObject("ResultBg", typeof(RectTransform), typeof(Image));
            box.transform.SetParent(card, false);
            var br = box.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(0, 1);
            br.anchorMax = new Vector2(0, 1);
            br.pivot = new Vector2(0, 1);
            br.anchoredPosition = new Vector2(8, -50);
            br.sizeDelta = new Vector2(InnerWidth - 16f, 44f);
            var bImg = box.GetComponent<Image>();
            bImg.color = new Color(0.10f, 0.10f, 0.12f, 0.95f);
            bImg.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            bImg.type = Image.Type.Sliced;
            bImg.raycastTarget = false;

            _resultText = CreateText(card, "", 9);
            _resultText.alignment = TextAnchor.MiddleLeft;
            _resultText.horizontalOverflow = HorizontalWrapMode.Wrap;
            _resultText.verticalOverflow = VerticalWrapMode.Truncate;
            var rr = _resultText.GetComponent<RectTransform>();
            rr.anchorMin = new Vector2(0, 1);
            rr.anchorMax = new Vector2(0, 1);
            rr.pivot = new Vector2(0, 1);
            rr.anchoredPosition = new Vector2(14, -53);
            rr.sizeDelta = new Vector2(InnerWidth - 28f, 38f);
            _resultText.raycastTarget = false;
        }

        // ═══════════════════════ 行控件（均可点） ═══════════════════════

        private void CreateSwitchRow(Transform card, float y, string name, out Image img, out Text val, Action onClick)
        {
            Text label = CreateText(card, name, 10);
            label.alignment = TextAnchor.MiddleLeft;
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 1);
            lr.anchorMax = new Vector2(0, 1);
            lr.pivot = new Vector2(0, 1);
            lr.anchoredPosition = new Vector2(10, y);
            lr.sizeDelta = new Vector2(120f, 18f);
            label.raycastTarget = false;

            GameObject swObj = new GameObject("Switch", typeof(RectTransform), typeof(Image), typeof(Button));
            swObj.transform.SetParent(card, false);
            var swRect = swObj.GetComponent<RectTransform>();
            swRect.anchorMin = new Vector2(1, 1);
            swRect.anchorMax = new Vector2(1, 1);
            swRect.pivot = new Vector2(1, 1);
            swRect.anchoredPosition = new Vector2(-10, y);
            swRect.sizeDelta = new Vector2(52f, 18f);

            img = swObj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.raycastTarget = true;

            var btn = swObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { try { onClick(); } catch { } });

            val = CreateText(swObj.transform, "", 9);
            val.alignment = TextAnchor.MiddleCenter;
            var vr = val.GetComponent<RectTransform>();
            vr.anchorMin = Vector2.zero;
            vr.anchorMax = Vector2.one;
            vr.offsetMin = Vector2.zero;
            vr.offsetMax = Vector2.zero;
            val.raycastTarget = false;
        }

        private void CreateCycleRow(Transform card, float y, string name, out Text val, Action onClick)
        {
            Text label = CreateText(card, name, 10);
            label.alignment = TextAnchor.MiddleLeft;
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 1);
            lr.anchorMax = new Vector2(0, 1);
            lr.pivot = new Vector2(0, 1);
            lr.anchoredPosition = new Vector2(10, y);
            lr.sizeDelta = new Vector2(120f, 18f);
            label.raycastTarget = false;

            GameObject btnObj = new GameObject("Cycle", typeof(RectTransform), typeof(Image), typeof(Button));
            btnObj.transform.SetParent(card, false);
            var br = btnObj.GetComponent<RectTransform>();
            br.anchorMin = new Vector2(1, 1);
            br.anchorMax = new Vector2(1, 1);
            br.pivot = new Vector2(1, 1);
            br.anchoredPosition = new Vector2(-10, y);
            br.sizeDelta = new Vector2(150f, 18f);

            var img = btnObj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = new Color(1f, 1f, 1f, 0.9f);
            img.raycastTarget = true;

            var btn = btnObj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { try { onClick(); } catch { } });

            val = CreateText(btnObj.transform, "", 9);
            val.alignment = TextAnchor.MiddleCenter;
            var vr = val.GetComponent<RectTransform>();
            vr.anchorMin = Vector2.zero;
            vr.anchorMax = Vector2.one;
            vr.offsetMin = Vector2.zero;
            vr.offsetMax = Vector2.zero;
            val.raycastTarget = false;
        }

        /// <summary>
        /// LlmV2-5→V2-7：[−] 值 [+] 步进行。规则在 ZhuluConfig.StepSampleInterval
        /// （开启最低 2、2 再减=关闭、关闭加=2、封顶 50）。
        /// V2-7 修复：原误用左锚定的 CreateButton 传负 x，按钮跑出卡片左侧压到标签——
        /// 本版步进按钮自建**右锚定**（x 为距右缘偏移），三件套 [−] 值 [+] 右对齐一组。
        /// </summary>
        private void CreateStepperRow(Transform card, float y, string name,
            Func<int> get, Action<int> set)
        {
            Text label = CreateText(card, name, 10);
            label.alignment = TextAnchor.MiddleLeft;
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 1);
            lr.anchorMax = new Vector2(0, 1);
            lr.pivot = new Vector2(0, 1);
            lr.anchoredPosition = new Vector2(10, y);
            lr.sizeDelta = new Vector2(96f, 18f);
            label.raycastTarget = false;

            _sampleText = CreateText(card, "", 10);
            _sampleText.alignment = TextAnchor.MiddleCenter;
            var vr = _sampleText.GetComponent<RectTransform>();
            vr.anchorMin = new Vector2(1, 1);
            vr.anchorMax = new Vector2(1, 1);
            vr.pivot = new Vector2(1, 1);
            vr.anchoredPosition = new Vector2(-54, y);
            vr.sizeDelta = new Vector2(48f, 18f);
            _sampleText.raycastTarget = false;

            CreateStepButton(card, "−", -106, y, get, set, -1);
            CreateStepButton(card, "+", -10, y, get, set, 1);
        }

        /// <summary>右锚定步进按钮（距右缘 rightOffset 处，36×18——与值文本同一纵向线）。</summary>
        private void CreateStepButton(Transform card, string label, float rightOffset, float y,
            Func<int> get, Action<int> set, int delta)
        {
            GameObject obj = new GameObject("Step", typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(card, false);
            var rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(rightOffset, y);
            rt.sizeDelta = new Vector2(36f, 18f);
            var img = obj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = new Color(0.22f, 0.20f, 0.16f, 0.95f);
            img.raycastTarget = true;
            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() =>
            {
                try { set(ZhuluConfig.StepSampleInterval(get(), delta)); RefreshAll(); }
                catch { }
            });

            Text t = CreateText(obj.transform, label, 10);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.98f, 0.77f, 0.27f);
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero;
            tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero;
            tr.offsetMax = Vector2.zero;
            t.raycastTarget = false;
        }

        private InputField CreateInputRow(Transform card, float y, string name, int limit, float width, bool masked = false)
        {
            Text label = CreateText(card, name, 10);
            label.alignment = TextAnchor.MiddleLeft;
            var lr = label.GetComponent<RectTransform>();
            lr.anchorMin = new Vector2(0, 1);
            lr.anchorMax = new Vector2(0, 1);
            lr.pivot = new Vector2(0, 1);
            lr.anchoredPosition = new Vector2(10, y);
            lr.sizeDelta = new Vector2(74f, 18f);
            label.raycastTarget = false;

            // LlmV2-9：右栏定宽——与开关/步进器同一右缘线（原拉伸式与其它控件不对齐）
            GameObject box = new GameObject("Input", typeof(RectTransform), typeof(Image), typeof(InputField));
            box.transform.SetParent(card, false);
            var rt = box.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(1, 1);
            rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(1, 1);
            rt.anchoredPosition = new Vector2(-10, y);
            rt.sizeDelta = new Vector2(width, 18f);

            var img = box.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = new Color(0.10f, 0.10f, 0.12f, 0.95f);
            img.raycastTarget = true;

            GameObject textObj = new GameObject("Text", typeof(RectTransform), typeof(Text));
            textObj.transform.SetParent(box.transform, false);
            var txR = textObj.GetComponent<RectTransform>();
            txR.anchorMin = Vector2.zero; txR.anchorMax = Vector2.one;
            txR.offsetMin = new Vector2(6f, 1f); txR.offsetMax = new Vector2(-6f, -1f);
            Text tx = textObj.GetComponent<Text>();
            tx.font = LocalizedTextManager.current_font;
            tx.fontSize = 9;
            tx.color = new Color(0.91f, 0.89f, 0.83f);
            tx.alignment = TextAnchor.MiddleLeft;
            tx.supportRichText = false;

            InputField input = box.GetComponent<InputField>();
            input.textComponent = tx;
            input.targetGraphic = img;
            input.characterLimit = limit;
            input.lineType = InputField.LineType.SingleLine;

            // 密钥类字段用 Password 掩码显示（2026-10-24：截图发现 Key 明文暴露在界面上）
            if (masked) input.contentType = InputField.ContentType.Password;

            return input;
        }

        private void CreateButton(Transform card, string label, float x, float y, float w, float h, Action onClick)
        {
            GameObject obj = new GameObject("Btn", typeof(RectTransform), typeof(Image), typeof(Button));
            obj.transform.SetParent(card, false);
            var rt = obj.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0, 1);
            rt.anchorMax = new Vector2(0, 1);
            rt.pivot = new Vector2(0, 1);
            rt.anchoredPosition = new Vector2(x, y);
            rt.sizeDelta = new Vector2(w, h);

            var img = obj.GetComponent<Image>();
            img.sprite = SpriteTextureLoader.getSprite("ui/special/backgroundKingdomElement");
            img.type = Image.Type.Sliced;
            img.color = new Color(0.22f, 0.20f, 0.16f, 0.95f);
            img.raycastTarget = true;

            var btn = obj.GetComponent<Button>();
            btn.targetGraphic = img;
            btn.onClick.AddListener(() => { try { onClick(); } catch { } });

            Text t = CreateText(obj.transform, label, 9);
            t.alignment = TextAnchor.MiddleCenter;
            t.color = new Color(0.98f, 0.77f, 0.27f);
            var tr = t.GetComponent<RectTransform>();
            tr.anchorMin = Vector2.zero; tr.anchorMax = Vector2.one;
            tr.offsetMin = Vector2.zero; tr.offsetMax = Vector2.zero;
            t.raycastTarget = false;
        }

        // ═══════════════════════ 行为 ═══════════════════════

        private void Reload()
        {
            _cfg = LlmConfigStore.Load();
            try { _sampleInterval = ZhuluConfig.Instance.EmperorSampleInterval; } catch { }
            RefreshAll();
        }

        private void RefreshAll()
        {
            if (_statusText != null)
            {
                _statusText.text = _cfg.Enabled
                    ? $"<color={ZhuluUIStyle.Hex.Jade}>● 已启用</color>　<color={ZhuluUIStyle.TextColor.Secondary}>盖棺时自动请 AI 作评</color>"
                    : $"<color={ZhuluUIStyle.TextColor.Tertiary}>○ 未启用</color>　<color={ZhuluUIStyle.TextColor.Secondary}>评价走本地规则</color>";
            }

            if (_enableText != null) _enableText.text = _cfg.Enabled ? "开" : "关";
            if (_enableImg != null) _enableImg.color = _cfg.Enabled
                ? new Color(0.24f, 0.42f, 0.22f, 0.95f) : new Color(0.24f, 0.22f, 0.20f, 0.95f);

            if (_autoText != null) _autoText.text = _cfg.AutoEval ? "开" : "关";
            if (_autoImg != null) _autoImg.color = _cfg.AutoEval
                ? new Color(0.24f, 0.42f, 0.22f, 0.95f) : new Color(0.24f, 0.22f, 0.20f, 0.95f);

            if (_protocolText != null) _protocolText.text = LlmConfigStore.ProtocolDisplay(_cfg.Protocol);
            if (_sampleText != null) _sampleText.text = _sampleInterval <= 0 ? "关闭" : (_sampleInterval + " 年");

            if (_endpointInput != null) _endpointInput.text = _cfg.Endpoint ?? "";
            if (_modelInput != null) _modelInput.text = _cfg.Model ?? "";
            if (_keyInput != null) _keyInput.text = _cfg.ApiKey ?? "";
            if (_timeoutInput != null) _timeoutInput.text = _cfg.TimeoutSeconds.ToString();
            if (_maxReqInput != null) _maxReqInput.text = _cfg.MaxRequestsPerGame.ToString();
        }

        private void Save()
        {
            CollectInputs();
            bool ok = LlmConfigStore.Save(_cfg);
            LlmConfigStore.Apply(_cfg);
            SetResult(ok
                ? $"<color={ZhuluUIStyle.Hex.Jade}>✓ 已保存</color>"
                : $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 保存失败</color>");
            RefreshAll();
        }

        private void CollectInputs()
        {
            if (_endpointInput != null) _cfg.Endpoint = (_endpointInput.text ?? "").Trim();
            if (_modelInput != null) _cfg.Model = (_modelInput.text ?? "").Trim();
            if (_keyInput != null) _cfg.ApiKey = (_keyInput.text ?? "").Trim();

            int t;
            if (_timeoutInput != null && int.TryParse(_timeoutInput.text, out t))
                _cfg.TimeoutSeconds = Mathf.Clamp(t, 3, 120);
            int m;
            if (_maxReqInput != null && int.TryParse(_maxReqInput.text, out m))
                _cfg.MaxRequestsPerGame = Mathf.Clamp(m, 0, 1000);
        }

        private void SetResult(string text)
        {
            if (_resultText != null) _resultText.text = text;
        }

        private void TestConnection()
        {
            CollectInputs();
            LlmConfigStore.Save(_cfg);
            LlmConfigStore.Apply(_cfg);

            // LlmFix-2：密钥允许为空（本地 vLLM/Ollama 无鉴权端点），只要求地址
            if (string.IsNullOrEmpty(_cfg.Endpoint))
            {
                SetResult($"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 请先填写接口地址</color>");
                return;
            }

            SetResult($"<color={ZhuluUIStyle.Hex.Warn}>◐ 正在测试……</color>");

            var cfg = _cfg;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string msg;
                try
                {
                    // LlmFix-2：测试只判连通与鉴权（HTTP 2xx 即成功），不再要求模型返回文本——
                    // 推理模型对小请求会交白卷（额度进了 reasoning），那不代表连不上
                    var protocol = LlmProtocolFactory.Create(cfg.Protocol);
                    string url = protocol.BuildPath(cfg.Endpoint);
                    var headers = protocol.BuildHeaders(cfg.ApiKey);
                    string body = protocol.BuildBody(cfg.Model, "你是连通性测试助手。", "回复 OK", 8);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var resp = new HttpLlmTransport().Send(url, headers, body, cfg.TimeoutSeconds);
                    sw.Stop();

                    if (resp.TimedOut)
                    {
                        msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 连接超时（>{cfg.TimeoutSeconds}s）</color>";
                    }
                    else if (!string.IsNullOrEmpty(resp.TransportError))
                    {
                        msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 网络错误：{resp.TransportError}</color>";
                    }
                    else
                    {
                        var kind = LlmRetryPolicy.Classify(resp.StatusCode);
                        if (kind == LlmFailureKind.None)
                        {
                            bool emptyText = string.IsNullOrEmpty(protocol.ExtractText(resp.Body));
                            string note = emptyText
                                ? $" <color={ZhuluUIStyle.TextColor.Tertiary}>（推理模型可能不回短文本，不影响评价）</color>"
                                : "";
                            msg = $"<color={ZhuluUIStyle.Hex.Jade}>✓ 连通 · {sw.ElapsedMilliseconds}ms</color>{note}";
                        }
                        else if (kind == LlmFailureKind.AuthFailed)
                        {
                            msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 密钥无效或无权限（{resp.StatusCode}）</color>";
                        }
                        else if (kind == LlmFailureKind.RateLimited)
                        {
                            msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 触发限流（429），稍后再试</color>";
                        }
                        else
                        {
                            string detail = protocol.ExtractError(resp.Body);
                            msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ 服务端错误（{resp.StatusCode}）" +
                                  (detail != null ? "：" + detail : "") + "</color>";
                        }
                    }
                }
                catch (Exception ex)
                {
                    msg = $"<color={ZhuluUIStyle.Hex.Vermilion}>✗ {ex.Message}</color>";
                }
                _pendingResult = msg;
            });
        }

        private void Update()
        {
            if (_pendingResult == null) return;
            string msg = _pendingResult;
            _pendingResult = null;
            SetResult(msg);
        }

        private static LlmProtocolKind NextProtocol(LlmProtocolKind k)
        {
            switch (k)
            {
                case LlmProtocolKind.ChatCompletions: return LlmProtocolKind.AnthropicMessages;
                case LlmProtocolKind.AnthropicMessages: return LlmProtocolKind.Responses;
                default: return LlmProtocolKind.ChatCompletions;
            }
        }
    }
}
