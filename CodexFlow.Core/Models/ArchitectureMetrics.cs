// Copyright (c) 2026 iwaitu
// This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
// If a copy of the MPL was not distributed with this file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.ObjectModel;

namespace CodexFlow.Core.Models;

public class ArchitectureMetrics
{
    public string FilePath { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;

    // 耦合度度量
    public int AfferentCoupling { get; set; } // Ca: 入度
    public int EfferentCoupling { get; set; } // Ce: 出度

    // 稳定性度量 (0: 极度稳定, 1: 极度不稳定)
    public double Instability => (AfferentCoupling + EfferentCoupling) == 0
        ? 0
        : (double)EfferentCoupling / (AfferentCoupling + EfferentCoupling);

    // 坏味道标记
    public Collection<string> DetectedSmells { get; } = new ();

    // 重构价值评分 (0-100)
    public double RefactorPriority { get; set; }

    public string Summary => $"[Arch] {FilePath} (I: {Instability:F2}, Priority: {RefactorPriority:F0}) - {string.Join(", ", DetectedSmells)}";
}
