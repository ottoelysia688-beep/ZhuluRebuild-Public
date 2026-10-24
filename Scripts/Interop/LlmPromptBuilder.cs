using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using ZhuluRebuild.Domain;

namespace ZhuluRebuild.Interop
{
    /// <summary>
    /// 提示词构造（2026-10-24 历代终结 第6步）。
    ///
    /// 两条原则：
    ///   ① **只给事实，不给结论**——若把"他该得美谥"写进输入，评语会千篇一律
    ///   ② **候选集由我们给**——模型只能在其中选，不能造字
    /// </summary>
    public static class LlmPromptBuilder
    {
        /// <summary>
        /// 长评笔法池（LlmV2-11→12）：十种笔法各带两个句式骨架模板，按帝王键稳定哈希轮换——
        /// 同帝恒同笔法（读档不变）、跨帝强制多样。用户要求：结构每种都要有（含纯褒颂体）、
        /// 每种配几个模板；文白档位各异（纯文言/白话夹古都有）。
        /// 实测病灶：无笔法变化时模型篇篇"先褒扬、后'然'转折、结尾感叹"。
        /// </summary>
        public static readonly string[] CommentStyles =
        {
            // 0 颂体（纯褒）——门控：仅美谥类
            "颂体（纯褒）——通篇褒扬、无一贬词，只用于功业实至名归者。句式骨架二选一：\n" +
            "  ① 以功业起、以定论收：「拓地安民，仓廪既实，社稷之安，实基此矣。」\n" +
            "  ② 以承敝起、以赞叹收：「承大乱之后而致小康，虽古之贤主，何以加焉。」",
            // 1 贬体（只贬）——门控：仅恶/悯类
            "贬体（只贬不褒）——通篇贬抑、不设褒词，用于失德遭难之主。句式骨架二选一：\n" +
            "  ① 一句定罪、一句事实：「坐视府藏之竭而不问，驯至群盗执柄。」\n" +
            "  ② 反问起笔：「岂有一岁之勤，可盖十年之怠者乎？」",
            // 2 白描（不评）
            "白描（不评）——只述二三事，全篇不著一字褒贬，留读者自断。句式骨架二选一：\n" +
            "  ① 三组短事实排比，末句戛然而止；\n" +
            "  ②「在位若干年，甲事；乙事；丙事。」纯记录体。",
            // 3 问答起伏
            "问答起伏——以问起笔，以答或再问收束。句式骨架二选一：\n" +
            "  ①「何以谓之某？……而已。」\n" +
            "  ② 双问递进：「……欤？抑……欤？」",
            // 4 咏叹
            "咏叹——以感慨起、以长叹或问句收。句式骨架二选一：\n" +
            "  ①「呜呼」居首或居尾，中间两叙一评；\n" +
            "  ② 以时代起笔「某之世，……」，落于其人命运。",
            // 5 直陈留白
            "直陈留白——平实陈述关键一二事，末句留白。句式骨架二选一：\n" +
            "  ① 事实句后接一句悬而未决之语；\n" +
            "  ② 短句成段，最后一句独立极短。",
            // 6 白话夹古
            "白话夹古——现代白话从容叙述，间以文言短语收束（像今人替古人作传评）。句式骨架二选一：\n" +
            "  ①「他这些年……。后人……」；\n" +
            "  ② 口语起笔（说白了／平心而论），末句文言定调。",
            // 7 对照
            "对照——与其前任或先祖对照而论（材料已附前任谥号庙号）。句式骨架二选一：\n" +
            "  ①「较之某帝，……」；\n" +
            "  ②「昔……，今……，其变如此。」",
            // 8 悲悯
            "悲悯——着眼其人命运与其时代，哀其遇、原其心。句式骨架二选一：\n" +
            "  ①「非其罪也，时为之也」式开脱收束；\n" +
            "  ② 以其末路一景起笔，缓缓道来。",
            // 9 曲笔
            "曲笔——皮里阳秋，讥诮藏于叙事。句式骨架二选一：\n" +
            "  ①「史臣曰：……。至于某二字，读史者宜深思之。」\n" +
            "  ② 明褒暗贬：通篇似颂，末句微转。",
        };

        /// <summary>笔法门控（与 CommentStyles 同序）：0=通用；1=颂体仅美谥类；2=贬体仅恶/悯类。</summary>
        private static readonly byte[] StyleGates = { 1, 2, 0, 0, 0, 0, 0, 0, 0, 0 };

        /// <summary>
        /// 按帝王键确定性选笔法（跨帝多样、同帝恒定），并按基础谥类门控——
        /// 颂体只发给美谥类、贬体只发给恶/悯类，免得昏君被塞颂体逼模型写违心话。
        /// </summary>
        public static string PickStyleOf(string key, ShiCategory baseCategory = ShiCategory.Plain)
        {
            bool praise = baseCategory == ShiCategory.Fine;
            bool blame = baseCategory == ShiCategory.Evil || baseCategory == ShiCategory.Pity;
            var eligible = new List<int>();
            for (int i = 0; i < CommentStyles.Length; i++)
            {
                byte g = StyleGates[i];
                if (g == 1 && !praise) continue;
                if (g == 2 && !blame) continue;
                eligible.Add(i);
            }
            if (eligible.Count == 0) return CommentStyles[0];
            int h = string.IsNullOrEmpty(key) ? 0 : PosthumousNameGenerator.StableHash32(key);
            // (uint)h % int 会提升为 long——List 索引只收 int，须显式回落
            return CommentStyles[eligible[(int)((uint)h % eligible.Count)]];
        }

        /// <summary>
        /// 系统提示词（LlmV2-11 三改）：只留"防胡编"硬约束 + 反八股明令 + 笔法字段 +
        /// 三体反差示例（原单示例是"褒—然—贬—叹"结构，模型篇篇仿其骨架，是结构八股根因）。
        /// </summary>
        public static string SystemPromptFor(int maxChars)
        {
            return
                "你是《逐鹿》的史官，为帝王定谥并作盖棺之评。\n" +
                "【定谥】只能从给定候选集中各选一个，不得自造：\n" +
                "  - 谥字：从 shi_candidates 中选一字，须与帝王事迹相称\n" +
                "  - 庙号：从 temple_candidates 中选一个；候选为空则返回空串\n" +
                "  - 优先避开 used 中的字\n" +
                "【实录】（comment 字段）后世官修正史定调之评：\n" +
                "  - 8 至 " + maxChars + " 字，句数不限\n" +
                "  - 文体：材料中的「文体」字段指定本篇笔法——遵其气韵，不拘泥字面\n" +
                "  - 结构随人而变：可纯褒（颂）、可只贬、可白描不评、可问答起伏、可直陈留白——" +
                "严禁每篇都\"先褒扬、后'然'转折、结尾感叹\"的套路，也不必面面俱到\n" +
                "  - 「文体」字段自带该笔法的句式骨架——二选一参用，亦可略变其形\n" +
                "  - 平庸之君就写平庸，不必硬找亮点\n" +
                "【野史】（wild 字段）民间传闻口吻的另说：\n" +
                "  - 里巷之谈、疑案存疑、戏谑惋惜皆可，笔法与实录错开更佳\n" +
                "  - 事实仍限于给定材料——不得编造材料中没有的具体事件\n" +
                "【铁律】\n" +
                "  - 只用给定事实，不得新增任何事实性事件或数字\n" +
                "  - 白话可以，术语腔不行（经济/政策/数据/系统/管理/效率/优化等词禁用）\n" +
                "  - 不得复述谥号庙号本身（不说\"武帝\"\"太宗\"）\n" +
                "  - 实录与野史都只评人，不复述数字（不说\"城池增加三座\"，说\"拓地千里\"）\n" +
                "只输出 JSON：{\"shi\":\"文\",\"temple\":\"太宗\",\"shift\":0,\"shift_reason\":\"\",\"comment\":\"实录\",\"wild\":\"野史\"}\n" +
                "shift 表示相对规则判定的褒贬位移，只能取 -1 / 0 / +1；非 0 时须在 shift_reason 说明。\n" +
                "【风格示例·三体各一，仅示差异——勿仿其事，勿仿其人，勿仿其结构】\n" +
                "· 文言峻断：「聚敛三十七年，府库盈而闾阎空。利归于上，怨归于下——国之衰，自此始矣。」\n" +
                "· 白话夹古：「他这五十年没打过一场像样的仗，也没闹出什么大乱子。后人记不住他的年号，只记得那几十年难得的太平。」\n" +
                "· 咏叹设问：「何以为中兴之主？城复矣，民归矣，然宝鼎犹轻——轻在何处？轻在人心未附，社稷如寄。」";
        }

        // ═══════════════════════ 断代史（LlmV2-4）═══════════════════════

        /// <summary>断代史段内一朝的事实摘要（由 OrthodoxMandateManager 采集，纯数据便于单测）。</summary>
        public sealed class DynastyDigestFact
        {
            public string Name;
            public int StartYear;
            public int EndYear;     // -1 = 延续至今
            public int Emperors;    // 段内在位过的帝数
            public int Wars;        // 段内宣战次数（王国史册）
            public int Policies;    // 段内施政次数
            public string EndReason;
        }

        /// <summary>断代史系统提示词（只留防胡编硬约束，文风同长评放开）。</summary>
        public static string ChronicleSystemPromptFor(int maxChars)
        {
            return
                "你是《逐鹿》的史官，为一段年期的天下兴亡作断代史论。\n" +
                "- 60 至 " + maxChars + " 字，文言底色文白随宜，可感叹设问\n" +
                "- 写出这一段的天命大势：有兴有亡、有因有果；平庸之世就写平庸\n" +
                "- 只用给定事实，不得新增任何事实性事件或数字\n" +
                "- 禁用现代词汇（经济/政策/数据/系统/管理/效率/优化 等）\n" +
                "只输出 JSON：{\"digest\":\"断代史论\"}";
        }

        public static string BuildChroniclePayload(int spanStart, int spanEnd,
            IList<DynastyDigestFact> dynasties, int worldDynastyTotal)
        {
            var o = new JObject
            {
                ["年期"] = spanStart + "~" + spanEnd + "年",
                ["本段朝代数"] = dynasties != null ? dynasties.Count : 0,
                ["开朝以来朝代总数"] = worldDynastyTotal
            };
            var arr = new JArray();
            if (dynasties != null)
            {
                foreach (var d in dynasties)
                {
                    if (d == null) continue;
                    arr.Add(new JObject
                    {
                        ["国号"] = d.Name ?? "",
                        ["存续"] = d.StartYear + "~" + (d.EndYear < 0 ? "今" : d.EndYear.ToString()),
                        ["帝数"] = d.Emperors,
                        ["战事"] = d.Wars,
                        ["施政"] = d.Policies,
                        ["结局"] = d.EndReason ?? ""
                    });
                }
            }
            o["朝代"] = arr;
            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        public static string BuildUserPayload(
            EmperorFacts f, string kingdom, string name, string era,
            int reignIndex, string prevShi, string prevTemple,
            IList<string> shiCandidates, IList<string> templeCandidates,
            IList<string> usedShi, IList<EmperorSample> track, string style = null)
        {
            var o = new JObject
            {
                ["国号"] = kingdom ?? "",
                ["帝王"] = name ?? "",
                ["年号"] = era ?? "",
                ["本朝第几帝"] = reignIndex,
                ["前任谥号"] = prevShi ?? "",
                ["前任庙号"] = prevTemple ?? "",
                ["shi_candidates"] = ToArr(shiCandidates),
                ["temple_candidates"] = ToArr(templeCandidates),
                ["used"] = ToArr(usedShi)
            };
            // LlmV2-11：笔法指令随帝王轮换（同帝恒定），强制跨帝结构多样
            if (!string.IsNullOrEmpty(style)) o["文体"] = style;

            o["在位年数"] = f.ReignYears;
            o["城数变化"] = Signed(f.CityDelta);
            o["国库变化"] = Signed((int)f.TreasDelta);
            o["天命变化"] = Signed((int)f.MandDelta);
            o["稳定变化"] = Signed((int)f.StabDelta);
            o["附庸变化"] = Signed(f.VassalDelta);
            o["人口变化"] = Signed((int)f.PopPct) + "%";
            // LlmFix-4→6：原"战争次数"填的是施政次数（假数据）——现接 EmperorRecord.WarCount
            // （王国史册回溯的真实攻伐数；-1=未采集时整个省略，不臆造）
            if (f.WarCount >= 0) o["战争次数"] = f.WarCount;
            o["施政次数"] = f.PolicyCount;

            if (f.Unified) o["大一统"] = true;
            if (f.LostThrone) o["结局"] = "失国";
            else if (f.Usurped) o["结局"] = "被篡";
            else if (f.Abdicated) o["结局"] = "禅让";
            else o["结局"] = "善终";

            var marks = new JArray();
            if (f.Tyrant) marks.Add("暴");
            if (f.Foolish) marks.Add("昏");
            if (f.Benevolent) marks.Add("仁");
            if (f.Unified) marks.Add("混一");
            if (marks.Count > 0) o["德行标记"] = marks;

            if (track != null && track.Count > 0)
            {
                var condensed = EmperorTrackSampler.Condense(track, 8);
                var arr = new JArray();
                foreach (var s in condensed)
                {
                    arr.Add(new JObject
                    {
                        ["年"] = s.Year,
                        ["城"] = s.Cities,
                        ["人口"] = s.Population,
                        ["天命"] = (int)s.Mandate,
                        ["稳定"] = (int)s.Stability,
                        ["排名"] = s.Rank
                    });
                }
                o["轨迹"] = arr;

                var peak = EmperorTrackSampler.Peak(condensed);
                if (peak != null) o["极盛之年"] = peak.Year;

                var shape = EmperorTrackSampler.Shape(condensed);
                if (shape != EmperorShape.Unknown)
                    o["轨迹形态"] = ShapeText(shape);
            }

            return o.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string ShapeText(EmperorShape s)
        {
            switch (s)
            {
                case EmperorShape.Rising: return "一路向上";
                case EmperorShape.Falling: return "一路下滑";
                case EmperorShape.RiseThenFall: return "先升后降（中叶极盛，晚节不终）";
                case EmperorShape.Flat: return "平稳";
                default: return "";
            }
        }

        private static string Signed(int v)
        {
            return (v >= 0 ? "+" : "") + v;
        }

        private static JArray ToArr(IList<string> list)
        {
            var a = new JArray();
            if (list != null) foreach (var s in list) a.Add(s);
            return a;
        }
    }
}
