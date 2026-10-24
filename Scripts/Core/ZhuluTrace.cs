using System.IO;
using System.Text;

namespace ZhuluRebuild.Core
{
    /// <summary>
    /// 轻量数据插桩写盘工具（D 批 2026-10-06）：war_trace / faction_trace / rating_trace 共用。
    /// 与 power_trace（RegimeManager 内联）同口径：追加模式、UTF-8 无 BOM、>2MB 停写守卫、全 try/catch 静默。
    /// 仅在 .runtime/ 落盘，不入快照、不影响游戏逻辑；关闭后可整体移除调用点。
    /// </summary>
    public static class ZhuluTrace
    {
        private const long MAX_BYTES = 2L * 1024 * 1024; // 2MB 守卫：超限停写，防无限膨胀
        private static bool _overflowWarned = false;

        public static void Write(string fileName, string line)
        {
            try
            {
                string dir = Path.Combine(ZhuluContext.Instance.ModPath, ".runtime");
                string path = Path.Combine(dir, fileName);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MAX_BYTES)
                {
                    if (!_overflowWarned)
                    {
                        _overflowWarned = true;
                        try { ZhuluContext.Instance.LogWarning($"[ZhuluTrace] {fileName} 超 2MB，停止写入"); } catch { }
                    }
                    return;
                }
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(path, line + System.Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
