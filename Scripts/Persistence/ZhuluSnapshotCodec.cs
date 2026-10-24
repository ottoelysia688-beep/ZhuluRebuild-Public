using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace ZhuluRebuild.Persistence
{
    /// <summary>
    /// 快照文件编解码：JSON 文本 ↔ 磁盘字节。
    ///
    /// 2026-10-23 [P1-3]：单档快照 JSON 约 543–767KB，自动存档频繁时 IO 压力明显。
    /// 这里只做**存储层压缩**，不动 Schema：JSON 结构、版本号、分区全部原样，
    /// 压缩后通常能降到 1/8 左右。
    ///
    /// 兼容策略（关键）：读取端按**魔数**识别，不依赖扩展名或配置。
    ///   · 以 1F 8B 开头 → GZip 解压
    ///   · 其他        → 按 UTF-8 纯文本读
    /// 因此旧档（纯 JSON）无需迁移即可继续读取，回滚时把开关关掉也只是不再压缩。
    ///
    /// 开关：ZhuluConfig.SnapshotGzip（默认开）。关闭后写出纯 JSON，行为回到修复前。
    /// </summary>
    public static class ZhuluSnapshotCodec
    {
        /// <summary>GZip 魔数：0x1F 0x8B。</summary>
        public static bool LooksGzip(byte[] data)
        {
            return data != null && data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;
        }

        /// <summary>把 JSON 文本编码为待写字节；gzip=false 时按 UTF-8 原样返回。</summary>
        public static byte[] Encode(string json, bool gzip)
        {
            if (json == null) json = "";
            byte[] raw = Encoding.UTF8.GetBytes(json);
            if (!gzip) return raw;

            using (var ms = new MemoryStream())
            {
                using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
                {
                    gz.Write(raw, 0, raw.Length);
                }
                return ms.ToArray();
            }
        }

        /// <summary>把磁盘字节解码为 JSON 文本；自动识别 gzip / 纯文本。</summary>
        public static string Decode(byte[] data)
        {
            if (data == null || data.Length == 0) return "";
            if (!LooksGzip(data)) return Encoding.UTF8.GetString(data);

            using (var input = new MemoryStream(data))
            using (var gz = new GZipStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                gz.CopyTo(output);
                return Encoding.UTF8.GetString(output.ToArray());
            }
        }
    }
}
