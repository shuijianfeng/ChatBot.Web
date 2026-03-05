using ChatBot.Models;
using System.Text.RegularExpressions;

namespace ChatBot.Web.Services;

/// <summary>
/// 从文件系统加载符合 Claude Code 规范的 Skills
/// 每个 Skill 是一个独立文件夹，内含 SKILL.md 文件（YAML frontmatter + markdown 指令）
/// </summary>
public class SkillLoaderService
{
    private readonly ILogger<SkillLoaderService> _logger;
    private readonly string _skillsDirectory;
    private readonly List<SkillConfig> _skills = new();

    // 匹配 YAML frontmatter 的正则：--- 开头，--- 结尾（兼容 \r\n 和 \n）
    private static readonly Regex FrontmatterRegex = new(
        @"^---\s*\r?\n(.*?)\r?\n---\s*\r?\n",
        RegexOptions.Singleline | RegexOptions.Compiled);

    // 匹配 YAML 中的 name 字段
    private static readonly Regex NameRegex = new(
        @"^name:\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    // 匹配 YAML 中的 description 字段
    private static readonly Regex DescriptionRegex = new(
        @"^description:\s*(.+)$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public string SkillsDirectory => _skillsDirectory;

    public SkillLoaderService(IConfiguration configuration, IWebHostEnvironment env, ILogger<SkillLoaderService> logger)
    {
        _logger = logger;

        var configuredDir = configuration.GetValue<string>("SkillsSettings:SkillsDirectory");
        _skillsDirectory = string.IsNullOrWhiteSpace(configuredDir)
            ? Path.Combine(env.WebRootPath ?? Path.Combine(env.ContentRootPath, "wwwroot"), "skills")
            : Path.IsPathRooted(configuredDir)
                ? configuredDir
                : Path.Combine(env.ContentRootPath, configuredDir);

        _logger.LogInformation("Skills 目录路径: {Directory}", _skillsDirectory);
        LoadSkills();
    }

    /// <summary>
    /// 扫描并加载所有 Skills
    /// </summary>
    private void LoadSkills()
    {
        _skills.Clear();

        if (!Directory.Exists(_skillsDirectory))
        {
            _logger.LogWarning("Skills 目录不存在: {Directory}", _skillsDirectory);
            return;
        }

        var skillFolders = Directory.GetDirectories(_skillsDirectory);
        foreach (var folder in skillFolders)
        {
            var skillMdPath = Path.Combine(folder, "SKILL.md");
            if (!File.Exists(skillMdPath))
            {
                _logger.LogDebug("跳过文件夹 {Folder}，未找到 SKILL.md", folder);
                continue;
            }

            try
            {
                var skill = ParseSkillMd(skillMdPath, Path.GetFileName(folder));
                if (skill != null)
                {
                    _skills.Add(skill);
                    _logger.LogInformation("已加载 Skill: {Name} ({FolderName})", skill.Name, skill.FolderName);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载 Skill 失败: {Path}", skillMdPath);
            }
        }

        _logger.LogInformation("共加载 {Count} 个 Skills", _skills.Count);
    }

    /// <summary>
    /// 解析 SKILL.md 文件，提取 YAML frontmatter 和 markdown 正文
    /// </summary>
    private SkillConfig? ParseSkillMd(string filePath, string folderName)
    {
        var content = File.ReadAllText(filePath);
        var frontmatterMatch = FrontmatterRegex.Match(content);

        if (!frontmatterMatch.Success)
        {
            _logger.LogWarning("SKILL.md 缺少 YAML frontmatter: {Path}", filePath);
            return null;
        }

        var yaml = frontmatterMatch.Groups[1].Value;
        var body = content[frontmatterMatch.Length..].Trim();

        var nameMatch = NameRegex.Match(yaml);
        var descriptionMatch = DescriptionRegex.Match(yaml);

        var name = nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : folderName;
        var description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value.Trim() : string.Empty;

        // 自动生成图标：取名称的前两个字符大写
        var icon = GenerateIcon(name);

        return new SkillConfig
        {
            Name = name,
            FolderName = folderName,
            Description = description,
            Icon = icon,
            SystemPrompt = body
        };
    }

    /// <summary>
    /// 从 Skill 名称生成简单的图标文本
    /// </summary>
    private static string GenerateIcon(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "🔧";

        // 常用 Skill 类型的 Emoji 映射
        var emojiMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["translate"] = "🌐",
            ["code"] = "💻",
            ["review"] = "🔍",
            ["writing"] = "✍️",
            ["design"] = "🎨",
            ["test"] = "🧪",
            ["debug"] = "🐛",
            ["doc"] = "📄",
            ["search"] = "🔎",
            ["data"] = "📊",
            ["math"] = "🔢",
            ["chat"] = "💬",
            ["music"] = "🎵",
            ["image"] = "🖼️",
            ["video"] = "🎬",
            ["pdf"] = "📑",
            ["excel"] = "📊",
            ["ppt"] = "📽️",
            ["frontend"] = "🎨",
            ["backend"] = "⚙️",
            ["api"] = "🔌",
            ["sql"] = "🗃️",
        };

        // 优先匹配包含关键词的名称
        foreach (var pair in emojiMap)
        {
            if (name.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return "🔧";
    }

    /// <summary>
    /// 获取所有已加载的 Skills
    /// </summary>
    public List<SkillConfig> GetSkills() => _skills.ToList();

    /// <summary>
    /// 根据技能名称获取系统提示词
    /// </summary>
    public string GetSkillPrompt(string? skillName)
    {
        if (string.IsNullOrWhiteSpace(skillName))
            return string.Empty;

        var skill = _skills.FirstOrDefault(s =>
            s.Name.Equals(skillName, StringComparison.OrdinalIgnoreCase) ||
            s.FolderName.Equals(skillName, StringComparison.OrdinalIgnoreCase));

        return skill?.SystemPrompt ?? string.Empty;
    }

    /// <summary>
    /// 重新加载所有 Skills（热更新）
    /// </summary>
    public void Reload()
    {
        _logger.LogInformation("重新加载 Skills...");
        LoadSkills();
    }
}
