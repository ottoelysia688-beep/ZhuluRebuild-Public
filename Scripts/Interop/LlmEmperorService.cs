using System;
using System.Collections.Generic;
using System.Threading;

namespace ZhuluRebuild.Interop
{
    /// <summary>大模型接入配置（第8步的窗口会读写它）。</summary>
    public sealed class LlmConfig
    {
        public bool Enabled { get; set; }
        public bool AutoEval { get; set; } = true;
        public LlmProtocolKind Protocol { get; set; } = LlmProtocolKind.ChatCompletions;
        public string Endpoint { get; set; } = "";
        public string Model { get; set; } = "";
        public string ApiKey { get; set; } = "";
        public int TimeoutSeconds { get; set; } = 30;   // LlmFix-2：12s 对推理模型太短（单次常 15~40s）
        public int MaxChars { get; set; } = 200;   // LlmV2-1：B 案全部长评（原 120）
        public int MaxRequestsPerGame { get; set; } = 100;
    }

    /// <summary>一位帝王的 AI 评价状态（窗口状态点四态，LlmFix-3）。</summary>
    public enum LlmAiStatus
    {
        None = 0,     // ○ 未生成
        Queued = 1,   // ◐ 排队或生成中
        Done = 2,     // ● 已生成
        Failed = 3    // × 上次失败
    }

    /// <summary>
    /// 请求类型（LlmV2-3 服务泛化）：评语之外的 LLM 任务共用队列/熔断/额度。
    /// 二期预留：Edict（圣旨神谕）、Audience（奏对独白）——到时新增枚举值即可接入。
    /// </summary>
    public enum LlmRequestKind
    {
        Emperor = 0,      // 帝王盖棺评价（写 LlmCommentStore + 谥号回写）
        Chronicle = 1     // 断代史摘要（结果经 OnResult 交给订阅方）
    }

    /// <summary>通用 LLM 任务（非 Emperor 类）：调用方给出提示词与主线程回调。</summary>
    public sealed class LlmTask
    {
        public LlmRequestKind Kind = LlmRequestKind.Chronicle;
        /// <summary>幂等键：同键在途去重；空键不收。</summary>
        public string Key;
        public string SystemPrompt;
        public string UserPayload;
        /// <summary>主线程回调（Pump 内触发）：失败时 Success=false 携带 Error，调用方自行兜底。</summary>
        public Action<LlmCallResult> OnResult;
    }

    /// <summary>一次评价请求（含候选集——模型只能在其中选）。</summary>
    public sealed class LlmEmperorRequest
    {
        public string Key { get; set; }
        public string SystemPrompt { get; set; }
        public string UserPayload { get; set; }
        public List<string> ShiCandidates { get; set; }
        public List<string> TempleCandidates { get; set; }
        public int EndYear { get; set; }
        public bool Manual { get; set; }
    }

    /// <summary>
    /// 评价服务（2026-10-24 历代天命接入 第6步）。
    ///
    /// 职责：
    ///   · 入队（幂等：已生成的不再请求，除非手动重写）
    ///   · 串行执行（同时只允许 1 个在途请求，避免撞 RPM）
    ///   · 每局请求上限（防额度被一次性烧光）
    ///   · 结果经线程安全队列回主线程，由 Pump() 写库
    ///
    /// 线程模型：Enqueue 在主线程；后台线程只做 HTTP 与解析；写 Store 只在 Pump（主线程）。
    /// </summary>
    public sealed class LlmEmperorService
    {
        public static LlmEmperorService Instance { get; } = new LlmEmperorService();

        private readonly object _lock = new object();
        /// <summary>队列统一项（LlmV2-3）：Emperor 走既有写库路径，其余 Kind 走任务回调。</summary>
        private sealed class WorkItem
        {
            public LlmRequestKind Kind;
            public LlmEmperorRequest Req;   // Kind=Emperor 时非空
            public LlmTask Task;            // 其余 Kind 时非空
            public string Key;
            public string System;
            public string User;
        }
        private readonly Queue<WorkItem> _queue = new Queue<WorkItem>();
        private readonly Queue<KeyValuePair<WorkItem, LlmCallResult>> _completed
            = new Queue<KeyValuePair<WorkItem, LlmCallResult>>();
        private readonly HashSet<string> _inflightOrQueued = new HashSet<string>(StringComparer.Ordinal);
        // LlmFix-3：失败帝王集合（内存级，成功即清除）——窗口状态点据此显示 ×
        private readonly HashSet<string> _failedKeys = new HashSet<string>(StringComparer.Ordinal);

        private LlmClient _client;
        private int _workerRunning;

        public LlmConfig Config { get; private set; } = new LlmConfig();

        /// <summary>
        /// 是否自动起后台线程执行队列。生产环境 true；
        /// 单测设 false，改用 ProcessInline 同步执行（否则会真的联网）。
        /// </summary>
        public bool AutoStartWorker { get; set; } = true;

        public int RequestsUsed { get; private set; }

        /// <summary>当前是否可用（未熔断且已配置）。窗口状态条用。</summary>
        public bool IsAvailable
        {
            get { return _client != null && !_client.Breaker.IsOpen; }
        }

        /// <summary>不可用原因（熔断原因）；可用时返回 null。</summary>
        public string UnavailableReason
        {
            get
            {
                if (_client == null) return "未配置";
                return _client.Breaker.IsOpen ? (_client.Breaker.Reason ?? "已停止请求") : null;
            }
        }
        public int QueuedCount { get { lock (_lock) return _queue.Count; } }
        public bool IsBusy { get { return Interlocked.CompareExchange(ref _workerRunning, 1, 1) == 1; } }

        /// <summary>结果回调（窗口刷新用，批末一次——见 Pump）。参数=帝王键。</summary>
        public event Action<string> OnCommentReady;

        /// <summary>
        /// 单条评语成功落库后触发（每条一次，主线程，LlmFix-5）——
        /// 域名层回写谥字/庙号用；与 OnCommentReady（UI 刷新，批末一次）分开，互不拖累。
        /// </summary>
        public event Action<string> OnCommentApplied;

        public void Configure(LlmConfig cfg, ILlmTransport transportOverride = null)
        {
            Config = cfg ?? new LlmConfig();
            _client = new LlmClient(
                transportOverride ?? (ILlmTransport)new HttpLlmTransport(),
                LlmProtocolFactory.Create(Config.Protocol));
        }

        /// <summary>世界切换/读档：丢弃未完成请求与计数。</summary>
        public void ResetForNewWorld()
        {
            lock (_lock)
            {
                _queue.Clear();
                _completed.Clear();
                _inflightOrQueued.Clear();
                _failedKeys.Clear();
            }
            RequestsUsed = 0;
        }

        /// <summary>帝王当前评价状态（LlmFix-3：窗口状态点四态——●已生成/◐生成中/×失败/○未生成）。</summary>
        public LlmAiStatus AiStatusOf(string key)
        {
            if (string.IsNullOrEmpty(key)) return LlmAiStatus.None;
            var c = LlmCommentStore.Instance.Get(key);
            if (c != null && !string.IsNullOrEmpty(c.Comment)) return LlmAiStatus.Done;
            lock (_lock)
            {
                if (_inflightOrQueued.Contains(key)) return LlmAiStatus.Queued;
                if (_failedKeys.Contains(key)) return LlmAiStatus.Failed;
            }
            return LlmAiStatus.None;
        }

        /// <summary>最近一次入队被拒的原因（窗口用它给出可见反馈，避免"点了没反应"）。</summary>
        public string LastRejectReason { get; private set; }

        /// <summary>最近一次请求/校验失败的原因（窗口状态条显示，便于定位"入队了但没结果"）。</summary>
        public string LastError { get; private set; }

        /// <summary>
        /// 入队（帝王评价）。返回 false 表示未入队（未启用/已达上限/已有缓存/重复）。
        /// force=true 用于手动重写（覆盖已有评语）。
        /// </summary>
        public bool Enqueue(LlmEmperorRequest req, bool force = false)
        {
            LastRejectReason = null;
            if (req == null || string.IsNullOrEmpty(req.Key))
            {
                LastRejectReason = "请求为空";
                return false;
            }
            return EnqueueCore(new WorkItem
            {
                Kind = LlmRequestKind.Emperor,
                Req = req,
                Key = req.Key,
                System = req.SystemPrompt,
                User = req.UserPayload
            }, force, queuedReject: "该帝王已在队列中");
        }

        /// <summary>通用任务入队（LlmV2-3）：断代史等非评语请求，结果经 OnResult 回调。</summary>
        public bool Enqueue(LlmTask task)
        {
            LastRejectReason = null;
            if (task == null || string.IsNullOrEmpty(task.Key) || task.OnResult == null)
            {
                LastRejectReason = "任务为空（键或回调缺失）";
                return false;
            }
            return EnqueueCore(new WorkItem
            {
                Kind = task.Kind,
                Task = task,
                Key = task.Key,
                System = task.SystemPrompt,
                User = task.UserPayload
            }, false, queuedReject: "该任务已在队列中");
        }

        private bool EnqueueCore(WorkItem item, bool force, string queuedReject)
        {
            if (!Config.Enabled)
            {
                LastRejectReason = "AI 未启用（检查「大模型接入」里的启用开关）";
                return false;
            }
            if (_client == null)
            {
                LastRejectReason = "客户端未初始化（配置未加载）";
                return false;
            }
            // LlmFix-1 M2：熔断中直接拒绝并给可见原因——原来会照常入队，
            // worker 秒回失败还每次 RequestsUsed++，把每局额度零请求烧满
            if (_client.Breaker.IsOpen)
            {
                LastRejectReason = "AI 熔断中：" + (_client.Breaker.Reason ?? "已停止请求");
                return false;
            }
            if (!force && item.Kind == LlmRequestKind.Emperor
                && LlmCommentStore.Instance.Has(item.Key))
            {
                LastRejectReason = "该帝王已有评语";
                return false;
            }
            if (RequestsUsed >= Config.MaxRequestsPerGame)
            {
                LastRejectReason = $"已达每局上限（{Config.MaxRequestsPerGame}）";
                return false;
            }

            lock (_lock)
            {
                if (_inflightOrQueued.Contains(item.Key))
                {
                    LastRejectReason = queuedReject;
                    return false;
                }
                _inflightOrQueued.Add(item.Key);
                _queue.Enqueue(item);
            }

            TryStartWorker();
            return true;
        }

        private void TryStartWorker()
        {
            if (!AutoStartWorker) return;
            if (Interlocked.CompareExchange(ref _workerRunning, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    while (true)
                    {
                        WorkItem req;
                        lock (_lock)
                        {
                            if (_queue.Count == 0) break;
                            req = _queue.Dequeue();
                        }

                        LlmCallResult res;
                        try
                        {
                            // LlmFix-2（用户决策）：不再传 max_tokens——deepseek-flash 映射到
                            // deepseek-reasoner（推理模型）时，小额度全被 reasoning 烧光、
                            // content 恒空；不传则用服务端默认额度。协议实现自行忽略该参数
                            res = _client.Call(Config.Endpoint, Config.ApiKey, Config.Model,
                                req.System ?? "", req.User ?? "", 0, Config.TimeoutSeconds);
                        }
                        catch (Exception ex)
                        {
                            res = new LlmCallResult { Failure = LlmFailureKind.Timeout, Error = ex.Message };
                        }

                        // LlmFix-1 M2：只有真发出过请求（Attempts>0）才计额度；熔断短路不计
                        if (res.Attempts > 0) RequestsUsed++;
                        lock (_lock) _completed.Enqueue(new KeyValuePair<WorkItem, LlmCallResult>(req, res));

                        // 熔断后不再继续处理队列：剩余项转入完成队列按失败处理，
                        // 让 Pump 清掉它们的在途标记——否则这些帝王永远卡在"已在队列中"（LlmFix-1 M2）
                        if (_client.Breaker.IsOpen)
                        {
                            DrainQueuedAsFailed("熔断中断（未发送）");
                            break;
                        }
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _workerRunning, 0);
                }
            });
        }

        /// <summary>把队列中剩余请求全部标记为失败完成（熔断时用），由 Pump 统一清在途标记。</summary>
        private void DrainQueuedAsFailed(string error)
        {
            lock (_lock)
            {
                while (_queue.Count > 0)
                {
                    var dropped = _queue.Dequeue();
                    _completed.Enqueue(new KeyValuePair<WorkItem, LlmCallResult>(
                        dropped, new LlmCallResult { Failure = LlmFailureKind.AuthFailed, Error = error }));
                }
            }
        }

        /// <summary>安全触发结果回调（成功/失败都通知，窗口据此刷新状态与错误）。</summary>
        private void Notify(string key)
        {
            var cb = OnCommentReady;
            if (cb != null) { try { cb(key); } catch { } }
        }

        /// <summary>主线程每帧调用：把已完成的结果写库/回调并通知窗口。</summary>
        public int Pump()
        {
            int applied = 0;
            string lastTouchedKey = null;   // LlmFix-1 L7：批末只通知一次（N 帝重写 = 一次窗口重建）
            while (true)
            {
                KeyValuePair<WorkItem, LlmCallResult> item;
                lock (_lock)
                {
                    if (_completed.Count == 0) break;
                    item = _completed.Dequeue();
                }

                var req = item.Key;
                var res = item.Value;
                lock (_lock) _inflightOrQueued.Remove(req.Key);

                // LlmV2-3：通用任务（断代史等）——结果直接交调用方回调，成败兜底由其负责
                if (req.Kind != LlmRequestKind.Emperor)
                {
                    if (res == null || !res.Success || string.IsNullOrEmpty(res.Text))
                        LastError = res == null ? "无返回" : (res.Error ?? "请求失败");
                    else
                        LastError = null;
                    var tcb = req.Task != null ? req.Task.OnResult : null;
                    if (tcb != null) { try { tcb(res); } catch { } }
                    lastTouchedKey = req.Key;
                    continue;
                }

                if (res == null || !res.Success || string.IsNullOrEmpty(res.Text))
                {
                    LastError = res == null ? "无返回" : (res.Error ?? "请求失败");
                    lock (_lock) _failedKeys.Add(req.Key);   // LlmFix-3：状态点 ×
                    lastTouchedKey = req.Key;      // 失败也通知，窗口才能把原因显示出来
                    continue;
                }

                var parsed = LlmResponseValidator.Parse(res.Text, req.Req.ShiCandidates, req.Req.TempleCandidates,
                                                        8, Config.MaxChars);
                if (!parsed.AnyUsable)
                {
                    LastError = parsed.FailReason ?? "返回内容不合格";
                    lock (_lock) _failedKeys.Add(req.Key);
                    lastTouchedKey = req.Key;
                    continue;
                }
                LastError = null;
                lock (_lock) _failedKeys.Remove(req.Key);

                var prev = LlmCommentStore.Instance.Get(req.Key);
                var entry = new LlmEmperorComment
                {
                    Shi = parsed.Shi ?? (prev != null ? prev.Shi : ""),
                    Temple = parsed.Temple ?? (prev != null ? prev.Temple : ""),
                    Comment = parsed.Comment ?? (prev != null ? prev.Comment : ""),
                    WildComment = parsed.WildComment ?? (prev != null ? prev.WildComment : ""),
                    Shift = parsed.Shift,
                    ShiftReason = parsed.ShiftReason ?? "",
                    Source = req.Req.Manual ? "manual" : "llm",
                    GeneratedAtYear = req.Req.EndYear
                };
                LlmCommentStore.Instance.Put(req.Key, entry);
                applied++;
                lastTouchedKey = req.Key;

                // LlmFix-5：回写事件逐条触发（谥字/庙号要写回每条帝王记录）
                var ap = OnCommentApplied;
                if (ap != null) { try { ap(req.Key); } catch { } }
            }
            if (lastTouchedKey != null) Notify(lastTouchedKey);
            return applied;
        }

        /// <summary>单测入口：同步跑一遍队列（用注入的执行器，不联网、不起线程）。非 Emperor 项跳过。</summary>
        public int ProcessInline(Func<LlmEmperorRequest, LlmCallResult> executor)
        {
            int applied = 0;
            while (true)
            {
                WorkItem req;
                lock (_lock)
                {
                    if (_queue.Count == 0) break;
                    req = _queue.Dequeue();
                }
                if (req.Kind != LlmRequestKind.Emperor || req.Req == null)
                {
                    lock (_lock) _inflightOrQueued.Remove(req.Key);
                    continue;
                }
                var res = executor != null ? executor(req.Req) : null;
                if (res == null) continue;

            var parsed2 = LlmResponseValidator.Parse(res.Text ?? "", req.Req.ShiCandidates, req.Req.TempleCandidates,
                                                        8, Config.MaxChars);
            if (!parsed2.AnyUsable) continue;

            var prev = LlmCommentStore.Instance.Get(req.Key);
            LlmCommentStore.Instance.Put(req.Key, new LlmEmperorComment
            {
                Shi = parsed2.Shi ?? (prev != null ? prev.Shi : ""),
                Temple = parsed2.Temple ?? (prev != null ? prev.Temple : ""),
                Comment = parsed2.Comment ?? (prev != null ? prev.Comment : ""),
                WildComment = parsed2.WildComment ?? (prev != null ? prev.WildComment : ""),
                Shift = parsed2.Shift,
                ShiftReason = parsed2.ShiftReason ?? "",
                Source = req.Req.Manual ? "manual" : "llm",
                GeneratedAtYear = req.Req.EndYear
            });
                lock (_lock) _inflightOrQueued.Remove(req.Key);
                applied++;
            }
            return applied;
        }
    }
}
