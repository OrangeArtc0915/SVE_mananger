namespace StardewLauncher.Core.Mods;

/// <summary>安装规划里一步的动作类型。</summary>
public enum PlanStepKind
{
    /// <summary>把 Source 目录/文件复制到游戏 Mods 目录（Source 是相对压缩包根目录的路径）。</summary>
    CopyToMods,

    /// <summary>把 Source 覆盖到游戏根目录（用于 XNB 替换、Content 覆盖、Reshade）。</summary>
    CopyToGame,

    /// <summary>检查 Target（相对游戏根目录）是否存在，不存在则该步失败。</summary>
    CheckExists,

    /// <summary>检查游戏根目录下 Source 指向的 dll/exe 文件版本是否 ≥ Condition。</summary>
    CheckVersion,

    /// <summary>运行 Source 指向的可执行文件（必须经用户确认后才执行）。</summary>
    RunProgram,

    /// <summary>删除 Target（相对游戏根目录）——必须经用户确认后才执行。</summary>
    DeleteFromGame
}

/// <summary>安装规划里的一步。</summary>
public sealed class PlanStep
{
    public PlanStepKind Kind { get; set; }

    /// <summary>相对压缩包根目录的路径。</summary>
    public string Source { get; set; } = "";

    /// <summary>相对游戏根目录（或 Mods 目录）的路径。</summary>
    public string Target { get; set; } = "";

    /// <summary>版本号等附加条件。</summary>
    public string? Condition { get; set; }

    /// <summary>给人看的说明。</summary>
    public string? Description { get; set; }

    /// <summary>失败是否可忽略。</summary>
    public bool Optional { get; set; }
}

/// <summary>
/// 一份安装规划：把非标准结构的包（文件替换 / XNB 替换 / Content 覆盖 / Reshade）
/// 描述成一组可复现的步骤。规划可以随包携带（install-plan.json），也可以由程序推断。
/// </summary>
public sealed class InstallPlan
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>适用条件：包内必须存在这个相对路径才套用本规划（为空表示无条件）。</summary>
    public string? Match { get; set; }

    /// <summary>含 RunProgram / DeleteFromGame 时必须为 true。</summary>
    public bool RequiresConfirmation { get; set; }

    public List<PlanStep> Steps { get; set; } = [];
}
