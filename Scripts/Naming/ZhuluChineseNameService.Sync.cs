using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
using ZhuluRebuild.Adapters;
using ZhuluRebuild.Core;
using ZhuluRebuild.Domain;
using ZhuluRebuild.Persistence;

// ── 由 ZhuluChineseNameService.cs 拆出的 partial 部分：王国名同步与权威 Tick ──
// 2026-10-23 [P1-1] 纯文件级拆分，编译后仍为同一个类型，无任何行为改动。
namespace ZhuluRebuild.Naming
{
    public sealed partial class ZhuluChineseNameService
    {
        /// trackedBase 为空时从 _kingdomBaseNames / GetBaseName 兜底。
        /// </summary>
        public void SyncKingdomDataName(Kingdom kingdom, string trackedBase)
        {
            if (kingdom == null || !kingdom.isAlive() || kingdom.data == null) return;
            // 叛乱命名的王国不覆盖（保持"义军"等名称）
            if (Persistence.KingdomDataExt.GetStr(kingdom, "zlr_rebel_named") == "1")
            {
                _kingdomObservedNames[kingdom] = kingdom.data.name ?? "";
                return;
            }
            string kId = kingdom.id.ToString();
            if (string.IsNullOrWhiteSpace(trackedBase))
            {
                if (!_kingdomBaseNames.TryGetValue(kingdom, out string tb))
                    tb = KingdomExt.GetBaseName(kId, kingdom.data.name);
                trackedBase = tb;
            }

            string current = kingdom.data.name ?? "";
            string expected = KingdomExt.GetStoredName(kId, trackedBase);
            if (!_kingdomObservedNames.TryGetValue(kingdom, out string observed))
            {
                _kingdomObservedNames[kingdom] = current;
                observed = current;
            }
            if (current != observed && current != expected && !string.IsNullOrWhiteSpace(current))
            {
                // 与上次逐鹿看到的名字不同，且并非当前机制计算出的系统名：认定为玩家改名。
                KingdomExt.SetManualName(kId, current);
                _kingdomObservedNames[kingdom] = current;
                return;
            }
            KingdomExt.SetBaseName(kId, trackedBase);
            kingdom.data.name = expected;
            _kingdomObservedNames[kingdom] = expected;
        }

        /// <summary>阶段3 改名功能：改名 UI 入口——玩家主动改国号（含天命国）。立即可见且不被任何同步覆盖。</summary>
        public void NotifyManualKingdomRename(Kingdom kingdom, string newName)
        {
            if (kingdom == null || string.IsNullOrWhiteSpace(newName)) return;
            string kId = kingdom.id.ToString();
            KingdomExt.SetManualName(kId, newName.Trim());
            if (kingdom.data != null) kingdom.data.name = newName.Trim();
            _kingdomObservedNames[kingdom] = newName.Trim();
            ZhuluContext.Instance.LogInfo($"[ChineseName] 玩家改国号: {kId} → {newName.Trim()}");
        }

        /// <summary>每次主循环调用。只做轻量操作：首次恢复 + 王国名同步 + 定期清理。</summary>
        public void TickAuthority()
        {
            EnsureLoaded();
            if (World.world == null) return;

            if (_world == null)
            {
                _world = World.world;
                _rehydrated = false;
            }
            else if (!ReferenceEquals(_world, World.world))
            {
                _world = World.world;
                ClearSessionTracking();
                _rehydrated = false;
            }

            // 只在世界首次加载时执行一次全量恢复（极其昂贵，不能每帧跑）
            if (!_rehydrated)
            {
                RehydratePersistentActorsAndKingdoms();
                _rehydrated = true;
            }

            _tickCounter++;

            // 每 10 次 Tick（约30秒）清理已死亡对象
            if (_tickCounter % 10 == 0)
                CleanupDeadEntries();

            // 批次14：全量名字同步——分片轮询（每次处理1/3单位，3轮=90秒覆盖全部）。不写诊断文件。
            if (_tickCounter % 10 == 1)
            {
                int synced = 0;
                try
                {
                    var all = World.world?.units?.getSimpleList();
                    if (all != null && all.Count > 0)
                    {
                        int shard = Mathf.Max(1, all.Count / 3 + 1);
                        int phase = (_tickCounter / 10) % 3;
                        for (int i = phase * shard; i < Math.Min((phase + 1) * shard, all.Count); i++)
                        {
                            var u = all[i];
                            if (u == null || !u.isAlive() || !IsEligibleActor(u)) continue;
                            if (!_actorNames.TryGetValue(u, out string tracked))
                                tracked = EnsureActorName(u, true);
                            else
                                // 2026-10-23：已接管角色也要检测外部改名。
                                // 此前只有 ActorSetNamePostfix 一条捕获路径；若编辑器/其他 mod 直接写
                                // data.name 而不经 set_name，玩家改名仍会被权威名覆盖回去。
                                // 这里做第二道保险（分片执行，每 90 秒覆盖全部单位，开销可忽略）。
                                tracked = ObserveManualActorRename(u, tracked);
                            if (string.IsNullOrEmpty(tracked)) continue;
                            string cur = ActorAccess.GetRawName(u);
                            if (cur != tracked) { ActorAccess.SetName(u, tracked); synced++; }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 批次18-B3：扫尾静默失败=外部mod单位永远无中文名且无从排查——限频留痕
                    ZhuluContext.Instance.LogRateLimited("NameSync", "[NameSync] 名字同步扫尾异常(限频): " + ex.Message, 60f);
                }
            }

            // 只重写王国名字。P1-7（2026-08-23）分片轮询：跟踪条目 >12 时每 tick 仅处理 idx%3 相位片，
            // 全量覆盖周期从 3 秒放宽到约 9 秒——SyncKingdomDataName 幂等，延迟一轮无正确性影响；
            // 小世界（≤12 国）保持全量（开销可忽略）。
            if (_kingdomBaseNames.Count <= 12)
            {
                foreach (var pair in _kingdomBaseNames)
                {
                    try
                    {
                        SyncKingdomDataName(pair.Key, pair.Value);
                    }
                    catch { }
                }
            }
            else
            {
                int phase = _syncPhase % 3;
                _syncPhase++;
                int idx = 0;
                foreach (var pair in _kingdomBaseNames)
                {
                    if (idx % 3 == phase)
                    {
                        try
                        {
                            SyncKingdomDataName(pair.Key, pair.Value);
                        }
                        catch { }
                    }
                    idx++;
                }
            }

            // 接受玩家对氏族名的修改，并把新氏族姓同步到全族。氏族数量远小于人物数，低频执行可控。
            foreach (var pair in new List<KeyValuePair<Clan, string>>(_clanNames))
            {
                try
                {
                    Clan clan = pair.Key;
                    if (clan == null || !clan.isAlive() || clan.data == null) continue;
                    string current = clan.data.name ?? "";
                    // P1-7b 存量自愈：旧版生成器产出的"氏氏/部部"双后缀名，读档扫描即修复并持久化
                    if (current.Contains("氏氏") || current.Contains("部部"))
                    {
                        string fixedClanName = current.Replace("氏氏", "氏").Replace("部部", "部");
                        clan.data.name = fixedClanName;
                        WritePersistentName(clan.data, fixedClanName);
                        _clanNames[clan] = fixedClanName;
                        ZhuluContext.Instance.LogInfo($"[ChineseName] 双后缀氏族名自愈: {current} → {fixedClanName}");
                        continue;
                    }
                    if (!string.IsNullOrWhiteSpace(current) && current != pair.Value)
                    {
                        _clanNames[clan] = current;
                        WritePersistentName(clan.data, current);
                        string surname = ExtractClanSurname(current);
                        if (!string.IsNullOrEmpty(surname))
                        {
                            WritePersistentValue(clan.data, ClanSurnameKey, surname);
                            SynchronizeClanSurname(clan, surname);
                        }
                    }
                }
                catch { }
            }
        }

    }
}
