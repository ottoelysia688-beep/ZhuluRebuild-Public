using System;
using System.Text;
using ZhuluRebuild.Persistence;

namespace ZhuluRebuild.Tests
{
    /// <summary>
    /// 2026-10-23 [P1-3] 快照存储层压缩的契约守护。
    ///
    /// 快照是**存档数据**，任何编码改动都必须证明三件事：
    ///   1. 压缩后再解压，内容逐字一致（不能丢字符/改换行）
    ///   2. 旧档（纯 JSON）能被新读取端识别——否则老玩家读档即炸
    ///   3. 关闭开关时写出的确实是纯文本（回滚路径可用）
    ///
    /// 注意：这里只测编解码，不涉及 Schema——压缩属存储层，不改变 JSON 结构。
    /// </summary>
    public static class SnapshotCodecTests
    {
        public static void Run()
        {
            // 1. 往返一致（含中文、换行、转义字符、大文本）
            var sb = new StringBuilder();
            sb.Append("{\"Version\":5,\"WorldId\":\"abc\",\"Sections\":{");
            for (int i = 0; i < 500; i++)
                sb.Append($"\"s{i}\":\"逐鹿重建 · 第{i}条\\n含\\\"引号\\\"与制表\\t\",");
            sb.Append("\"end\":0}}");
            string json = sb.ToString();

            byte[] gz = ZhuluSnapshotCodec.Encode(json, true);
            TestRunner.AssertTrue(ZhuluSnapshotCodec.LooksGzip(gz), "压缩输出带 GZip 魔数");
            TestRunner.AssertEqual(json, ZhuluSnapshotCodec.Decode(gz), "gzip 往返逐字一致");

            // 2. 压缩必须真的变小（否则白折腾）
            byte[] plain = ZhuluSnapshotCodec.Encode(json, false);
            TestRunner.AssertTrue(gz.Length < plain.Length, $"压缩后体积下降（{gz.Length} < {plain.Length}）");

            // 3. 旧档兼容：纯 JSON 字节必须能被解码（不依赖魔数）
            TestRunner.AssertTrue(!ZhuluSnapshotCodec.LooksGzip(plain), "纯文本不带 GZip 魔数");
            TestRunner.AssertEqual(json, ZhuluSnapshotCodec.Decode(plain), "旧档纯 JSON 可读");

            // 4. 关闭开关 → 写出纯文本（回滚路径）
            byte[] off = ZhuluSnapshotCodec.Encode(json, false);
            TestRunner.AssertEqual(json, Encoding.UTF8.GetString(off), "关闭开关时写 UTF-8 原文");

            // 5. 边界：空内容 / 极短内容不得抛异常
            TestRunner.AssertEqual("", ZhuluSnapshotCodec.Decode(null), "null 解码为空串");
            TestRunner.AssertEqual("", ZhuluSnapshotCodec.Decode(new byte[0]), "空字节解码为空串");
            TestRunner.AssertEqual("{}", ZhuluSnapshotCodec.Decode(ZhuluSnapshotCodec.Encode("{}", true)), "极短 JSON 往返");

            // 6. 中文不得被截断（UTF-8 多字节边界）
            string cjk = "逐鹿中原，问鼎天下";
            TestRunner.AssertEqual(cjk, ZhuluSnapshotCodec.Decode(ZhuluSnapshotCodec.Encode(cjk, true)), "中文往返一致");
        }
    }
}
