using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZhuluRebuild.Interop
{
    /// <summary>一次 HTTP 往返的原始结果。</summary>
    public sealed class LlmHttpResponse
    {
        public int StatusCode;
        public string Body;
        public bool TimedOut;
        public string TransportError;
    }

    /// <summary>传输层抽象——单测注入假实现，不真联网。</summary>
    public interface ILlmTransport
    {
        LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds);
    }

    /// <summary>调用结果。</summary>
    public sealed class LlmCallResult
    {
        public bool Success;
        public string Text;
        public LlmFailureKind Failure;
        public string Error;
        public int Attempts;
    }

    /// <summary>
    /// 会话级熔断器：一旦密钥无效/封号，本次会话不再发请求。
    /// 理由：401/403 重试只会加速封禁，且每次都在消耗时间。
    /// </summary>
    public sealed class LlmCircuitBreaker
    {
        public bool IsOpen { get; private set; }
        public string Reason { get; private set; }

        public void Trip(string reason)
        {
            IsOpen = true;
            Reason = reason;
        }

        public void Reset()
        {
            IsOpen = false;
            Reason = null;
        }
    }

    /// <summary>
    /// 大模型调用客户端（2026-10-24 历代天命接入 第4步）。
    ///
    /// 设计要点：
    ///   · 协议、传输、延时、抖动全部可注入 → 无需联网即可单测退避与熔断
    ///   · 401/403 立即熔断且不重试（社区最常见错误是"无脑重试"，只会加速封号）
    ///   · 解析失败不重试（换请求也一样，直接本地兜底）
    ///   · 绝不抛异常给调用方：任何失败都变成 LlmCallResult
    /// </summary>
    public sealed class LlmClient
    {
        private readonly ILlmTransport _transport;
        private readonly ILlmProtocol _protocol;
        private readonly Action<int> _sleepMs;
        private readonly Func<double> _jitter;
        private readonly LlmCircuitBreaker _breaker;

        // LlmFix-2：net48 的 new Random() 用时间做种子，快速连续调用会得到相同序列——
        // 抖动只有一个全局实例（加锁，退避毫秒级精度无所谓竞争）
        private static readonly Random _jitterRnd = new Random();
        private static double NextJitter()
        {
            lock (_jitterRnd) return _jitterRnd.NextDouble();
        }

        public LlmClient(ILlmTransport transport, ILlmProtocol protocol,
                         Action<int> sleepMs = null, Func<double> jitter = null,
                         LlmCircuitBreaker breaker = null)
        {
            _transport = transport;
            _protocol = protocol;
            _sleepMs = sleepMs ?? (ms => Thread.Sleep(ms));
            _jitter = jitter ?? NextJitter;
            _breaker = breaker ?? new LlmCircuitBreaker();
        }

        public LlmCircuitBreaker Breaker { get { return _breaker; } }

        /// <summary>同步调用（调用方应在后台线程执行，切勿在主线程直接调）。</summary>
        public LlmCallResult Call(string baseUrl, string apiKey, string model,
                                  string system, string user, int maxTokens, int timeoutSeconds)
        {
            var result = new LlmCallResult();

            if (_breaker.IsOpen)
            {
                result.Failure = LlmFailureKind.AuthFailed;
                result.Error = "熔断中：" + (_breaker.Reason ?? "已停止请求");
                return result;
            }
            // LlmFix-4：密钥允许为空（本地 vLLM/Ollama 无鉴权端点），只要求接口地址
            if (string.IsNullOrEmpty(baseUrl))
            {
                result.Failure = LlmFailureKind.BadRequest;
                result.Error = "未配置接口地址";
                return result;
            }

            string url = _protocol.BuildPath(baseUrl);
            var headers = _protocol.BuildHeaders(apiKey);
            string body = _protocol.BuildBody(model, system, user, maxTokens);

            for (int attempt = 1; attempt <= LlmRetryPolicy.MaxAttempts; attempt++)
            {
                result.Attempts = attempt;

                LlmHttpResponse resp;
                try
                {
                    resp = _transport.Send(url, headers, body, timeoutSeconds);
                }
                catch (Exception ex)
                {
                    resp = new LlmHttpResponse { StatusCode = 0, TransportError = ex.Message };
                }

                LlmFailureKind kind;
                if (resp == null)
                {
                    kind = LlmFailureKind.Timeout;
                }
                else if (resp.TimedOut || !string.IsNullOrEmpty(resp.TransportError))
                {
                    kind = LlmFailureKind.Timeout;
                }
                else
                {
                    kind = LlmRetryPolicy.Classify(resp.StatusCode);
                }

                if (kind == LlmFailureKind.None)
                {
                    string text = _protocol.ExtractText(resp.Body);
                    if (string.IsNullOrEmpty(text))
                    {
                        result.Failure = LlmFailureKind.ParseFailed;
                        // LlmFix-2：区分「额度耗尽」与「结构不符」。推理模型（如 deepseek-reasoner）
                        // 会把输出额度烧在 reasoning 上：content 为空、finish_reason=length——
                        // 这种错误要能自解释，不能只给一句"无可取文本"
                        string finish = _protocol.ExtractFinishReason(resp.Body);
                        if (finish == "length" || finish == "max_tokens" || finish == "max_output_tokens")
                        {
                            result.Error = "模型未返回正文：输出额度被耗尽（finish_reason=" + finish +
                                           "）。推理模型易发，建议换非推理模型或调大服务端额度上限";
                        }
                        else
                        {
                            result.Error = "无可取文本｜原始返回：" + Snip(resp.Body);
                        }
                        return result;
                    }
                    result.Success = true;
                    result.Text = text;
                    result.Failure = LlmFailureKind.None;
                    return result;
                }

                result.Failure = kind;
                result.Error = BuildError(kind, resp);

                // 鉴权失败 → 立即熔断，本次会话不再请求
                if (kind == LlmFailureKind.AuthFailed)
                {
                    _breaker.Trip(result.Error);
                    return result;
                }

                if (!LlmRetryPolicy.ShouldRetry(kind)) return result;
                if (attempt >= LlmRetryPolicy.MaxAttempts) return result;

                _sleepMs(LlmRetryPolicy.DelayMs(attempt, _jitter()));
            }

            return result;
        }

        /// <summary>截取返回体片段用于错误提示（去掉换行，避免撑爆状态条）。</summary>
        private static string Snip(string s, int max = 110)
        {
            if (string.IsNullOrEmpty(s)) return "（空）";
            string one = s.Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length <= max ? one : one.Substring(0, max) + "…";
        }

        private string BuildError(LlmFailureKind kind, LlmHttpResponse resp)
        {
            string detail = resp != null && !string.IsNullOrEmpty(resp.TransportError)
                ? resp.TransportError
                : (resp != null ? _protocol.ExtractError(resp.Body) : null);

            switch (kind)
            {
                case LlmFailureKind.RateLimited: return "触发限流（429）" + (detail != null ? "：" + detail : "");
                case LlmFailureKind.Timeout: return "连接超时或网络中断" + (detail != null ? "：" + detail : "");
                case LlmFailureKind.ServerError: return "服务端错误（" + (resp != null ? resp.StatusCode : 0) + "）";
                case LlmFailureKind.AuthFailed: return "密钥无效或无权限（" + (resp != null ? resp.StatusCode : 0) + "）";
                case LlmFailureKind.BadRequest: return "请求被拒（" + (resp != null ? resp.StatusCode : 0) + "）" + (detail != null ? "：" + detail : "");
                default: return detail ?? "未知错误";
            }
        }
    }

    /// <summary>
    /// 真实 HTTP 传输（net48 + HttpClient）。仅在游戏内使用，单测走假传输。
    /// LlmFix-2：HttpClient 静态复用（每请求 new 会累积 TIME_WAIT 套接字）；
    /// 按请求超时用 CancellationTokenSource 实现，实例级 Timeout 设为配置上限之上。
    /// </summary>
    public sealed class HttpLlmTransport : ILlmTransport
    {
        private static readonly HttpClient _shared = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(130)   // 配置上限 120s + 余量
        };

        public LlmHttpResponse Send(string url, Dictionary<string, string> headers, string body, int timeoutSeconds)
        {
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(3, timeoutSeconds))))
                {
                    req.Content = new StringContent(body ?? "", Encoding.UTF8, "application/json");
                    if (headers != null)
                    {
                        foreach (var kv in headers)
                        {
                            if (string.Equals(kv.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
                            req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                        }
                    }

                    using (var resp = _shared.SendAsync(req, cts.Token).GetAwaiter().GetResult())
                    {
                        // SendAsync 默认先缓冲完整响应，这里读取不再受 CTS 影响
                        string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        return new LlmHttpResponse
                        {
                            StatusCode = (int)resp.StatusCode,
                            Body = text
                        };
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return new LlmHttpResponse { TimedOut = true };
            }
            catch (Exception ex)
            {
                return new LlmHttpResponse { TransportError = ex.GetType().Name + ": " + ex.Message };
            }
        }
    }
}
