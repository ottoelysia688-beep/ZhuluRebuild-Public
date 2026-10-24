namespace ZhuluRebuild.Persistence
{
    /// <summary>
    /// 逐鹿存档快照 Schema v5（批次10-A1，2026-08-23）。
    /// 与 v4 的本质区别：载荷不再是大平铺的强类型字段+竖线串，而是注册表驱动的分区字典——
    /// 新增持久化数据 = 系统声明 State 类 + 实现分区接口 + Register 一次（SaveCore 零改动）。
    /// 序列化：Newtonsoft 原生；Sections 值写入时为强类型字典、读回时为 JToken，
    /// 由 ZhuluSaveSchema.RestoreAll 按注册表显式转换（不依赖 $type 内嵌类型名，格式稳定可读）。
    /// Version&lt;5 的旧快照：明确拒绝恢复（本版本不承担旧档迁移——设计稿 §2.5）。
    /// </summary>
    public class ZhuluSnapshotV5
    {
        /// <summary>快照格式版本。</summary>
        public int Version { get; set; } = 5;

        /// <summary>世界唯一 ID（新世界生成 GUID；读档校验防串档——与 v4 同机制原样保留）。</summary>
        public string WorldId { get; set; } = "";

        /// <summary>保存时世界年。</summary>
        public int SavedAtYear { get; set; } = 0;

        /// <summary>分区载荷：SectionName → 该系统状态字典（键=王国ID 或 "__global__"）。</summary>
        public System.Collections.Generic.Dictionary<string, object> Sections { get; set; }
            = new System.Collections.Generic.Dictionary<string, object>();

        public string Serialize()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
        }

        public static ZhuluSnapshotV5 Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var snap = Newtonsoft.Json.JsonConvert.DeserializeObject<ZhuluSnapshotV5>(json);
                if (snap != null && snap.Version < 5) return null;   // 旧版格式：明确拒绝（由调用方给出提示）
                return snap;
            }
            catch (System.Exception)
            {
                return null;
            }
        }
    }
}
