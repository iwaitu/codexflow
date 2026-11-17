// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Globalization;
using System.Text;
using CodexFlow.Contracts.BusinessSkills;

namespace CodexFlow.Core.BusinessSkills;

/// <summary>
/// 由 Skill JSON v1 唯一真相单向派生的只读 SKILL.md 渲染器（不含任何凭据或不可信原始样本）。
/// </summary>
public static class SkillMarkdownRenderer
{
    public static string RenderReadOnlyMarkdown(SkillDefinitionV1 definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# {definition.Name}");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(definition.Description))
        {
            sb.AppendLine(definition.Description);
            sb.AppendLine();
        }

        sb.AppendLine("> 本文档由受控 Skill JSON v1 定义单向生成，仅供只读审阅，不作为独立执行真相。");
        sb.AppendLine();
        sb.AppendLine("## 元信息与绑定");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Skill ID**：`{definition.SkillId}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **版本**：`v{definition.Version}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **来源插件**：`{definition.PluginId}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **绑定环境**：`{definition.ConnectionBinding.Environment}` (连接修订 `{definition.ConnectionBinding.ConnectionRevision}` / 目录快照 `{definition.CatalogSnapshotId}`)");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **内容哈希**：`{definition.ContentHash}`");
        sb.AppendLine();

        sb.AppendLine("## 授权使用能力（Selection 子集）");
        foreach (var capId in definition.UsedCapabilityIds)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"- `{capId}`");
        }
        sb.AppendLine();

        sb.AppendLine("## 业务输入参数");
        if (definition.Inputs.Properties.Count == 0)
        {
            sb.AppendLine("- 无必填或声明输入参数");
        }
        else
        {
            var requiredSet = new HashSet<string>(definition.Inputs.Required, StringComparer.Ordinal);
            foreach (var prop in definition.Inputs.Properties)
            {
                var reqText = requiredSet.Contains(prop.Key) ? "必填" : "可选";
                var fmt = string.IsNullOrWhiteSpace(prop.Value.Format) ? string.Empty : $" / {prop.Value.Format}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"- `{prop.Key}` (`{prop.Value.Type}{fmt}`，{reqText})：{prop.Value.Description ?? "-"}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## 执行步骤流水线");
        for (var i = 0; i < definition.Steps.Count; i++)
        {
            var step = definition.Steps[i];
            var kind = step switch
            {
                SkillToolStep t => $"tool (`{t.CapabilityId}`)",
                SkillTransformStep tr => $"transform ({tr.Operation})",
                SkillBranchStep => "branch",
                SkillForeachStep fe => $"foreach (max={fe.MaxItems}, sequential={fe.Sequential})",
                SkillGenerateTextStep gt => $"generateText (allowedFields={string.Join(", ", gt.AllowedInputFields)})",
                SkillApprovalStep ap => $"approval (`{ap.TargetCapabilityId}`)",
                SkillOutputStep => "output",
                _ => "step"
            };
            sb.AppendLine(CultureInfo.InvariantCulture, $"{i + 1}. **{step.Id}** — {kind}：{step.Title ?? "-"}");
        }
        sb.AppendLine();

        sb.AppendLine("## 安全预算与数据边界");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 最大外部请求数：`{definition.Budgets.MaxExternalRequests}`，最大读取记录：`{definition.Budgets.MaxReadRecords}`，最大关联输出行：`{definition.Budgets.MaxJoinOutputRows}`，单批写入上限：`{definition.Budgets.MaxWriteBatchItems}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 模型允许读取字段白名单：{(definition.DataPolicy.AllowedModelFields.Count == 0 ? "（未开放任何业务数据字段）" : string.Join(", ", definition.DataPolicy.AllowedModelFields.Select(static f => $"`{f}`")))}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- 日志与导出脱敏：`{definition.DataPolicy.RedactLogsAndExports}`");
        return sb.ToString();
    }
}
