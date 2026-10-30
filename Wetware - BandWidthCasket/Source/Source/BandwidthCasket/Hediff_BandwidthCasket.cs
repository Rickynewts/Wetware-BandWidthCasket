using System.Collections.Generic;
using RimWorld;
using Verse;

namespace BandwidthCasket
{
    /// <summary>
    /// 带宽 Hediff：协调完成后挂在机械师身上，提供 MechBandwidth 加成。
    /// 关键点：
    ///   - 每 60 tick 重算一次当前总带宽（扫描地图上所有带宽仓）；
    ///   - 总带宽为 0 时 ShouldRemove 返回 true，Hediff 自动消失；
    ///   - CurStage 携带 StatModifier(MechBandwidth)，游戏会自动应用。
    /// </summary>
    public class Hediff_BandwidthCasket : HediffWithComps
    {
        // 缓存的带宽值。为 0 时表示没有任何带宽仓在供应 → 应当被移除。
        private int cachedBandwidth;

        // 缓存的 HediffStage（只有带宽 > 0 时才构建）
        private HediffStage curStage;

        /// <summary>带宽归零时自动移除本 Hediff。</summary>
        public override bool ShouldRemove => cachedBandwidth <= 0;

        /// <summary>动态生成的 Hediff 阶段（携带 MechBandwidth 属性加成）。</summary>
        public override HediffStage CurStage
        {
            get
            {
                if (curStage == null && cachedBandwidth > 0)
                {
                    curStage = new HediffStage
                    {
                        statOffsets = new List<StatModifier>
                        {
                            new StatModifier
                            {
                                stat = StatDefOf.MechBandwidth,
                                value = cachedBandwidth
                            }
                        }
                    };
                }
                return curStage;
            }
        }

        // ──────── 生命周期 ────────
        public override void PostAdd(DamageInfo? dinfo)
        {
            base.PostAdd(dinfo);
            Recache();   // 刚挂上时立即算一次
        }

        public override void PostTick()
        {
            base.PostTick();
            // 每 60 tick 重算一次（约 1 秒）
            if (pawn.IsHashIntervalTick(60))
                Recache();
        }

        /// <summary>
        /// 重算总带宽：
        /// 遍历地图上所有殖民者建筑，找出所有"带宽仓"，如果它正在供应给本 pawn，累加带宽值。
        /// </summary>
        private void Recache()
        {
            int old = cachedBandwidth;
            cachedBandwidth = 0;

            // 关键：遍历所有地图，而不是只遍历 pawn.Map
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                foreach (var b in maps[i].listerBuildings.allBuildingsColonist)
                {
                    if (!(b is Building_BandwidthCasket c)) continue;

                    var comp = c.BandNode;
                    if (comp == null) continue;
                    // IsActive = Casket.HasPawn && (PowerTrader == null || PowerTrader.PowerOn)
                    // 一并完成"有 pawn"和"通电"两个判断
                    if (!comp.IsActive) continue;
                    if (comp.TunedTo != pawn) continue;

                    cachedBandwidth += comp.EffectiveBandwidth;
                }
            }

            if (old != cachedBandwidth)
            {
                curStage = null;
                pawn.mechanitor?.Notify_BandwidthChanged();
            }
        }
    }
}