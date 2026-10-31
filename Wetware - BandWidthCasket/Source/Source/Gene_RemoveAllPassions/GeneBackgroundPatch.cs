using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BandwidthCasket
{
    /// <summary>
    /// XML层开关
    /// </summary>
    public class WetwareGeneBackgroundExtension : DefModExtension
    {
        public bool useCustomBackground = true;

        ///<summary>可选：单独指定这个基因的背景贴图路径</summary>
        public string backgroundPath;
    }

    /// <summary>
    /// 自定义基因的背景图
    /// </summary>
    public static class WetwareGeneBackground
    {
        /// <summary>背景图</summary>
        public const string CustomBackgroundPath = "UI/Gene/GeneBackground_WetwareGene";

        /// <summary>需要添加的白名单</summary>
        private static readonly string[] CoveredGeneDefNames =
        {
            "BandwidthCasket_Booster",
        };

        ///<summary>
        ///NeuroMortar 单独运行时不会受影响
        ///</summary>
        private static readonly string[] CoveredPackageIds =
        {
            "Yushiren.BandWidthCasket", // Wetware 本体
            "Yushiren.NeuroMortar",     //mod id
        };

        // CachedTexture 延迟加载纹理
        private static readonly CachedTexture CustomTexture = new CachedTexture(CustomBackgroundPath);
        private static readonly HashSet<string> ReportedGenes = new HashSet<string>();
        private static readonly Dictionary<Type, FieldInfo[]> vefBackgroundFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly Dictionary<string, CachedTexture> perGeneTextures = new Dictionary<string, CachedTexture>();
        private static bool warnedMissingTexture;

        public static bool IsCoveredGene(GeneDef gene)
        {
            if (gene == null)
                return false;

            for (int i = 0; i < CoveredGeneDefNames.Length; i++)
            {
                if (string.Equals(CoveredGeneDefNames[i], gene.defName, StringComparison.Ordinal))
                    return true;
            }

            ModContentPack pack = gene.modContentPack;
            if (pack == null)
                return false;

            for (int i = 0; i < CoveredPackageIds.Length; i++)
            {
                if (string.Equals(CoveredPackageIds[i], pack.PackageId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检查 VEF 的 GeneExtension 是否给这个基因显式配置了背景（backgroundPathEndogenes 等）。
        /// 用反射/字段名按需探测，不依赖 VEF 程序集，因此 Wetware 不需要硬性依赖 VEF。
        /// </summary>
        private static bool HasVefBackground(GeneDef gene)
        {
            List<DefModExtension> extensions = gene?.modExtensions;
            if (extensions == null || extensions.Count == 0)
                return false;

            for (int i = 0; i < extensions.Count; i++)
            {
                DefModExtension extension = extensions[i];
                if (extension == null)
                    continue;

                FieldInfo[] fields = GetVefBackgroundFields(extension.GetType());
                if (fields == null)
                    continue;

                for (int j = 0; j < fields.Length; j++)
                {
                    if (!(fields[j].GetValue(extension) is string path))
                        continue;
                    if (!string.IsNullOrEmpty(path))
                        return true;
                }
            }
            return false;
        }

        private static FieldInfo[] GetVefBackgroundFields(Type extensionType)
        {
            if (vefBackgroundFields.TryGetValue(extensionType, out FieldInfo[] cached))
                return cached;

            FieldInfo endo = AccessTools.Field(extensionType, "backgroundPathEndogenes");
            FieldInfo xeno = AccessTools.Field(extensionType, "backgroundPathXenogenes");
            FieldInfo archite = AccessTools.Field(extensionType, "backgroundPathArchite");

            List<FieldInfo> result = null;
            if (endo != null || xeno != null || archite != null)
            {
                result = new List<FieldInfo>(3);
                if (endo != null) result.Add(endo);
                if (xeno != null) result.Add(xeno);
                if (archite != null) result.Add(archite);
            }

            FieldInfo[] value = result == null ? null : result.ToArray();
            vefBackgroundFields[extensionType] = value;
            return value;
        }

        /// <summary>由 Transpiler 插入调用：拿到原版/VEF 选出的背景，返回最终应该绘制的背景。</summary>
        public static CachedTexture OverrideBackground(object current, GeneDef gene)
        {
            if (gene == null)
                return current as CachedTexture;

            // 1. XML 扩展优先：可以单独关闭，或给某个基因指定别的背景。
            WetwareGeneBackgroundExtension extension = gene.GetModExtension<WetwareGeneBackgroundExtension>();
            if (extension != null)
            {
                if (!extension.useCustomBackground)
                {
                    Report(gene, "XML 扩展要求关闭，回退原版背景");
                    return current as CachedTexture;
                }

                if (!string.IsNullOrEmpty(extension.backgroundPath))
                {
                    Report(gene, "XML 扩展指定背景 " + extension.backgroundPath);
                    return TextureFor(extension.backgroundPath);
                }

                Report(gene, "XML 扩展指定使用湿件背景");
                return Custom();
            }

            bool covered = IsCoveredGene(gene);
            if (!covered)
                return current as CachedTexture;

            // 2. 尊重 VEF 的 GeneExtension 显式背景配置。
            if (HasVefBackground(gene))
            {
                Report(gene, "VEF GeneExtension 已配置，保留 VEF 背景");
                return current as CachedTexture;
            }

            // 3. 按 defName / packageId 命中的基因使用湿件背景。
            Report(gene, "使用湿件背景");
            return Custom();
        }

        private static void Report(GeneDef gene, string message)
        {
            if (gene != null && ReportedGenes.Add(gene.defName))
                Log.Message("[BandwidthCasket] gene background: " + gene.defName
                    + " (pack=" + (gene.modContentPack != null ? gene.modContentPack.PackageId : "null") + ")"
                    + " -> " + message);
        }

        /// <summary>按路径取一张背景（缓存 CachedTexture，支持 XML 里给单个基因指定背景）。</summary>
        private static CachedTexture TextureFor(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Custom();

            if (!perGeneTextures.TryGetValue(path, out CachedTexture texture))
            {
                texture = new CachedTexture(path);
                perGeneTextures[path] = texture;
            }
            return texture;
        }

        private static CachedTexture Custom()
        {
            Texture2D tex = CustomTexture.Texture;
            if (!warnedMissingTexture && (tex == null || tex == BaseContent.BadTex))
            {
                warnedMissingTexture = true;
                Log.Warning("[BandwidthCasket] 基因背景贴图加载失败：Textures/" + CustomBackgroundPath + ".png");
            }
            return CustomTexture;
        }
    }

    [HarmonyPatch]
    public static class Patch_GeneUIUtility_DrawGeneBasics
    {
        /// <summary>Transpiler 是否成功插入了背景覆盖（调试用）。</summary>
        public static bool LastTranspilerMatched;

        // DrawGeneBasics 是私有静态方法：static void DrawGeneBasics(GeneDef gene, Rect geneRect, GeneType geneType,
        // bool doBackground, bool clickable, bool overridden)
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(GeneUIUtility),
                "DrawGeneBasics",
                new[] { typeof(GeneDef), typeof(Rect), typeof(GeneType), typeof(bool), typeof(bool), typeof(bool) });
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            FieldInfo endoField = AccessTools.Field(typeof(GeneUIUtility), "GeneBackground_Endogene");
            FieldInfo xenoField = AccessTools.Field(typeof(GeneUIUtility), "GeneBackground_Xenogene");
            FieldInfo architeField = AccessTools.Field(typeof(GeneUIUtility), "GeneBackground_Archite");

            // VEF（如果加载）的 Choose* 方法：它可能先于我们执行，把 ldsfld 替换成 call Choose*。
            Type vefPatchType = AccessTools.TypeByName("VEF.Genes.VanillaExpandedFramework_GeneUIUtility_DrawGeneBasics_Patch");
            MethodInfo vefEndo = vefPatchType != null ? AccessTools.Method(vefPatchType, "ChooseEndogeneBackground") : null;
            MethodInfo vefXeno = vefPatchType != null ? AccessTools.Method(vefPatchType, "ChooseXenogeneBackground") : null;
            MethodInfo vefArchite = vefPatchType != null ? AccessTools.Method(vefPatchType, "ChooseArchiteBackground") : null;

            MethodInfo overrideMethod = AccessTools.Method(typeof(WetwareGeneBackground), nameof(WetwareGeneBackground.OverrideBackground));

            int inserted = 0;
            foreach (CodeInstruction code in instructions)
            {
                yield return code;

                bool vanillaField = code.opcode == OpCodes.Ldsfld
                    && ((endoField != null && code.LoadsField(endoField))
                        || (xenoField != null && code.LoadsField(xenoField))
                        || (architeField != null && code.LoadsField(architeField)));

                MethodInfo operand = code.operand as MethodInfo;
                bool vefCall = code.opcode == OpCodes.Call && operand != null
                    && ((vefEndo != null && operand.Equals(vefEndo))
                        || (vefXeno != null && operand.Equals(vefXeno))
                        || (vefArchite != null && operand.Equals(vefArchite)));

                if (vanillaField || vefCall)
                {
                    // 栈上已有"原版/VEF 选好的背景"，压入 gene 后调用 OverrideBackground(object, GeneDef) 返回 CachedTexture。
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, overrideMethod);
                    inserted++;
                }
            }

            LastTranspilerMatched = inserted >= 1;
            if (inserted < 1)
            {
                Log.Warning("[BandwidthCasket] 未能在 GeneUIUtility.DrawGeneBasics 中定位基因背景字段，自定义基因背景未生效（游戏版本可能已变更）。");
            }
            else if (vefPatchType == null)
            {
                Log.Message("[BandwidthCasket] 基因背景补丁已插入 " + inserted + " 处（未检测到 VEF，使用原版字段路径）。");
            }
            else
            {
                Log.Message("[BandwidthCasket] 基因背景补丁已插入 " + inserted + " 处（检测到 VEF，兼容 VEF 的 GeneExtension 背景配置）。");
            }
        }
    }

    /// <summary>
    /// 启动时：
    ///  1. 检查 Harmony 是否已经自动应用了本补丁；如果没有（例如被 PurePatcher 这类预编译 mod 干扰），手动补挂一次；
    ///  2. 输出补丁状态，便于排查（日志里搜 BandwidthCasket 即可）。
    /// </summary>
    [StaticConstructorOnStartup]
    public static class WetwareGeneBackgroundBootstrap
    {
        static WetwareGeneBackgroundBootstrap()
        {
            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(GeneUIUtility),
                    "DrawGeneBasics",
                    new[] { typeof(GeneDef), typeof(Rect), typeof(GeneType), typeof(bool), typeof(bool), typeof(bool) });

                if (target == null)
                {
                    Log.Error("[BandwidthCasket] 找不到 GeneUIUtility.DrawGeneBasics，自定义基因背景无法生效。");
                    return;
                }

                if (!HasOurTranspiler(target))
                {
                    Harmony harmony = new Harmony("yushiren.bandwidthcasket.genebackground");
                    MethodInfo transpiler = AccessTools.Method(typeof(Patch_GeneUIUtility_DrawGeneBasics), "Transpiler");
                    harmony.Patch(target, transpiler: new HarmonyMethod(transpiler));
                    Log.Message("[BandwidthCasket] Harmony 自动 patch 未生效，已手动应用基因背景 Transpiler。");
                }

                Patches patches = Harmony.GetPatchInfo(target);
                int transpilers = patches == null || patches.Transpilers == null ? 0 : patches.Transpilers.Count;
                Log.Message("[BandwidthCasket] DrawGeneBasics transpilers=" + transpilers
                    + " oursMatched=" + Patch_GeneUIUtility_DrawGeneBasics.LastTranspilerMatched
                    + " texture=" + WetwareGeneBackground.CustomBackgroundPath);
            }
            catch (Exception exception)
            {
                Log.Error("[BandwidthCasket] 基因背景补丁初始化失败: " + exception);
            }
        }

        private static bool HasOurTranspiler(MethodBase target)
        {
            Patches patches = Harmony.GetPatchInfo(target);
            if (patches == null || patches.Transpilers == null)
                return false;

            foreach (Patch patch in patches.Transpilers)
            {
                if (patch.PatchMethod != null && patch.PatchMethod.DeclaringType == typeof(Patch_GeneUIUtility_DrawGeneBasics))
                    return true;
            }
            return false;
        }
    }
}
