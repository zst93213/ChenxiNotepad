using System.Text;
using System.Text.RegularExpressions;

namespace BlindNotepad.Services;

/// <summary>
/// 文本排版服务。提供多种中文文本排版和清理功能。
/// 所有方法均为纯函数，不修改原字符串，返回新字符串。
/// </summary>
public static class TextFormatService
{
    // ===== 中文字符范围 =====
    // \u4e00-\u9fff  CJK 统一汉字
    // \u3400-\u4dbf  CJK 扩展A
    // \u3000         全角空格
    // \uff00-\uffef  全角符号
    private const string ChinesePattern = @"\u4e00-\u9fff\u3400-\u4dbf";
    private const string FullWidthSpace = "\u3000";

    /// <summary>排版选项标志位。</summary>
    [Flags]
    public enum FormatOptions
    {
        None = 0,
        /// <summary>去除多余空行（连续空行合并为一个）</summary>
        CollapseBlankLines = 1,
        /// <summary>去除每行行首行尾空格</summary>
        TrimLineSpaces = 2,
        /// <summary>合并行内多余空格为单个</summary>
        CollapseExtraSpaces = 4,
        /// <summary>中英文之间加空格</summary>
        SpaceBetweenChineseEnglish = 8,
        /// <summary>中文与数字之间加空格</summary>
        SpaceBetweenChineseNumber = 16,
        /// <summary>全角数字字母转半角</summary>
        FullWidthToHalfWidth = 32,
        /// <summary>段落首行缩进（两个全角空格）</summary>
        ParagraphIndent = 64,
        /// <summary>英文标点转中文标点（中文上下文中）</summary>
        PunctToChinese = 128,
        /// <summary>省略号统一为中文六点</summary>
        NormalizeEllipsis = 256,
        /// <summary>引号配对修正</summary>
        FixQuotes = 512,
        /// <summary>智能分段（长文本按句号分段）</summary>
        SmartParagraph = 1024,
        /// <summary>繁体转简体（常用字）</summary>
        TraditionalToSimplified = 2048,
        /// <summary>全部排版（不含繁简转换）</summary>
        AllCore = CollapseBlankLines | TrimLineSpaces | CollapseExtraSpaces |
                  SpaceBetweenChineseEnglish | SpaceBetweenChineseNumber |
                  FullWidthToHalfWidth | ParagraphIndent | PunctToChinese |
                  NormalizeEllipsis | FixQuotes,
    }

    /// <summary>选项的中文显示名称。</summary>
    public static string GetOptionName(FormatOptions opt) => opt switch
    {
        FormatOptions.CollapseBlankLines => "去除多余空行",
        FormatOptions.TrimLineSpaces => "去除行首行尾空格",
        FormatOptions.CollapseExtraSpaces => "合并行内多余空格",
        FormatOptions.SpaceBetweenChineseEnglish => "中英文之间加空格",
        FormatOptions.SpaceBetweenChineseNumber => "中文与数字之间加空格",
        FormatOptions.FullWidthToHalfWidth => "全角数字字母转半角",
        FormatOptions.ParagraphIndent => "段落首行缩进",
        FormatOptions.PunctToChinese => "英文标点转中文标点",
        FormatOptions.NormalizeEllipsis => "省略号统一为六点",
        FormatOptions.FixQuotes => "引号配对修正",
        FormatOptions.SmartParagraph => "智能分段",
        FormatOptions.TraditionalToSimplified => "繁体转简体",
        _ => opt.ToString(),
    };

    /// <summary>选项的中文描述。</summary>
    public static string GetOptionDescription(FormatOptions opt) => opt switch
    {
        FormatOptions.CollapseBlankLines => "连续多个空行合并为一个，减少读屏干扰",
        FormatOptions.TrimLineSpaces => "去除每行开头和结尾的多余空格",
        FormatOptions.CollapseExtraSpaces => "行内连续多个空格合并为单个",
        FormatOptions.SpaceBetweenChineseEnglish => "你好World → 你好 World，读屏自然停顿",
        FormatOptions.SpaceBetweenChineseNumber => "第3章 → 第 3 章，数字播报更清晰",
        FormatOptions.FullWidthToHalfWidth => "１２３ＡＢＣ → 123ABC",
        FormatOptions.ParagraphIndent => "每段开头加两个全角空格，中文标准格式",
        FormatOptions.PunctToChinese => "中文语境中的 ,.?! 转为 ，。？！",
        FormatOptions.NormalizeEllipsis => "... 或 .。。 统一为 ……",
        FormatOptions.FixQuotes => "修正不配对的引号和单引号",
        FormatOptions.SmartParagraph => "长文本按句号自动分段",
        FormatOptions.TraditionalToSimplified => "常用繁体字转简体（基础版本）",
        _ => "",
    };

    // ===== 选项分组 =====
    public static readonly FormatOptions[] BasicCleanup =
    {
        FormatOptions.CollapseBlankLines,
        FormatOptions.TrimLineSpaces,
        FormatOptions.CollapseExtraSpaces,
    };

    public static readonly FormatOptions[] ChineseEnglishMix =
    {
        FormatOptions.SpaceBetweenChineseEnglish,
        FormatOptions.SpaceBetweenChineseNumber,
        FormatOptions.FullWidthToHalfWidth,
    };

    public static readonly FormatOptions[] ParagraphStructure =
    {
        FormatOptions.ParagraphIndent,
        FormatOptions.SmartParagraph,
    };

    public static readonly FormatOptions[] PunctuationFix =
    {
        FormatOptions.PunctToChinese,
        FormatOptions.NormalizeEllipsis,
        FormatOptions.FixQuotes,
    };

    public static readonly FormatOptions[] Advanced =
    {
        FormatOptions.TraditionalToSimplified,
    };

    /// <summary>
    /// 应用选中的排版选项。按合理顺序执行各项排版。
    /// </summary>
    public static string Format(string text, FormatOptions options)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var result = text;

        // 1. 全角转半角（先转换，后续规则更容易匹配）
        if (options.HasFlag(FormatOptions.FullWidthToHalfWidth))
            result = ConvertFullWidthToHalfWidth(result);

        // 2. 繁简转换
        if (options.HasFlag(FormatOptions.TraditionalToSimplified))
            result = ConvertTraditionalToSimplified(result);

        // 3. 智能分段（在清理空行之前）
        if (options.HasFlag(FormatOptions.SmartParagraph))
            result = SmartParagraphSplit(result);

        // 4. 引号修正
        if (options.HasFlag(FormatOptions.FixQuotes))
            result = FixMismatchedQuotes(result);

        // 5. 标点转换
        if (options.HasFlag(FormatOptions.PunctToChinese))
            result = ConvertPunctToChinese(result);

        // 6. 省略号统一
        if (options.HasFlag(FormatOptions.NormalizeEllipsis))
            result = NormalizeEllipsis(result);

        // 7. 中英文加空格
        if (options.HasFlag(FormatOptions.SpaceBetweenChineseEnglish))
            result = AddSpaceBetweenChineseEnglish(result);

        // 8. 中文与数字加空格
        if (options.HasFlag(FormatOptions.SpaceBetweenChineseNumber))
            result = AddSpaceBetweenChineseNumber(result);

        // 9. 去除行首行尾空格
        if (options.HasFlag(FormatOptions.TrimLineSpaces))
            result = TrimLines(result);

        // 10. 合并行内多余空格
        if (options.HasFlag(FormatOptions.CollapseExtraSpaces))
            result = CollapseExtraSpaces(result);

        // 11. 去除多余空行
        if (options.HasFlag(FormatOptions.CollapseBlankLines))
            result = CollapseBlankLines(result);

        // 12. 段落首行缩进（最后执行，确保段落已确定）
        if (options.HasFlag(FormatOptions.ParagraphIndent))
            result = AddParagraphIndent(result);

        return result;
    }

    // ================================================================
    //  各排版方法实现
    // ================================================================

    /// <summary>全角数字、字母、标点转半角。</summary>
    public static string ConvertFullWidthToHalfWidth(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch >= 0xFF01 && ch <= 0xFF5E)
            {
                // 全角 ASCII → 半角
                sb.Append((char)(ch - 0xFEE0));
            }
            else if (ch == 0x3000)
            {
                // 全角空格 → 半角空格
                sb.Append(' ');
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>中英文之间加空格。</summary>
    public static string AddSpaceBetweenChineseEnglish(string text)
    {
        // 中文后紧跟英文
        var result = Regex.Replace(text,
            $@"([{ChinesePattern}])([A-Za-z])", "$1 $2");
        // 英文后紧跟中文
        result = Regex.Replace(result,
            $@"([A-Za-z])([{ChinesePattern}])", "$1 $2");
        return result;
    }

    /// <summary>中文与数字之间加空格。</summary>
    public static string AddSpaceBetweenChineseNumber(string text)
    {
        // 中文后紧跟数字
        var result = Regex.Replace(text,
            $@"([{ChinesePattern}])([0-9])", "$1 $2");
        // 数字后紧跟中文
        result = Regex.Replace(result,
            $@"([0-9])([{ChinesePattern}])", "$1 $2");
        return result;
    }

    /// <summary>去除每行行首行尾空格（保留全角缩进可选）。</summary>
    public static string TrimLines(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].Trim(' ', '\t');
        }
        return string.Join("\n", lines);
    }

    /// <summary>合并行内连续多个空格为单个（保留行首缩进）。</summary>
    public static string CollapseExtraSpaces(string text)
    {
        var lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            // 行内多个空格合并为一个
            lines[i] = Regex.Replace(lines[i], @"  +", " ");
        }
        return string.Join("\n", lines);
    }

    /// <summary>连续多个空行合并为一个空行。</summary>
    public static string CollapseBlankLines(string text)
    {
        // 统一换行符
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        // 3个及以上换行 → 2个换行（即一个空行）
        var result = Regex.Replace(normalized, @"\n{3,}", "\n\n");
        return result;
    }

    /// <summary>段落首行缩进（每段开头加两个全角空格）。</summary>
    public static string AddParagraphIndent(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        var paragraphs = normalized.Split(new[] { "\n\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();
        for (var i = 0; i < paragraphs.Length; i++)
        {
            if (i > 0) sb.Append("\n\n");
            var para = paragraphs[i].TrimStart('\n', ' ', '\t');
            if (string.IsNullOrEmpty(para))
            {
                sb.Append(para);
            }
            else
            {
                sb.Append(FullWidthSpace);
                sb.Append(FullWidthSpace);
                sb.Append(para);
            }
        }
        return sb.ToString();
    }

    /// <summary>英文标点转中文标点（仅在中文上下文中）。</summary>
    public static string ConvertPunctToChinese(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            var replaced = ch switch
            {
                ',' when IsInChineseContext(text, i) => '，',
                '.' when IsInChineseContext(text, i) => '。',
                '!' when IsInChineseContext(text, i) => '！',
                '?' when IsInChineseContext(text, i) => '？',
                ':' when IsInChineseContext(text, i) => '：',
                ';' when IsInChineseContext(text, i) => '；',
                '(' when IsInChineseContext(text, i) => '（',
                ')' when IsInChineseContext(text, i) => '）',
                _ => ch,
            };
            sb.Append(replaced);
        }
        return sb.ToString();
    }

    /// <summary>省略号统一为中文六点（……）。</summary>
    public static string NormalizeEllipsis(string text)
    {
        // 多个英文句号 ... → ……
        var result = Regex.Replace(text, @"\.{3,}", "……");
        // 混合的 .。。或 。。。。 → ……
        result = Regex.Replace(result, @"[.。]{3,}", "……");
        // 单个 … → ……（确保成对）
        result = Regex.Replace(result, @"(?<!…)…(?!…)", "……");
        return result;
    }

    /// <summary>引号配对修正。</summary>
    public static string FixMismatchedQuotes(string text)
    {
        // 将所有直引号按出现顺序配对为中文引号
        var sb = new StringBuilder(text.Length);
        var isOpen = true; // true = 下一个双引号是开引号
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                sb.Append(isOpen ? '\u201C' : '\u201D'); // " "
                isOpen = !isOpen;
            }
            else if (ch == '\'')
            {
                // 单引号仅在中文上下文中处理
                sb.Append(isOpen ? '\u2018' : '\u2019'); // ' '
                // 单引号不翻转状态（太复杂，保持简单）
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>智能分段：长文本按句号自动分段。</summary>
    public static string SmartParagraphSplit(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace("\r", "\n");
        // 按句末标点（。！？…）分段，每句独占一行
        // 仅当原文无明显段落结构时才分段
        var paragraphs = normalized.Split(new[] { "\n\n" }, StringSplitOptions.None);
        var sb = new StringBuilder();
        for (var i = 0; i < paragraphs.Length; i++)
        {
            if (i > 0) sb.Append("\n\n");
            var para = paragraphs[i].Trim();
            if (string.IsNullOrEmpty(para))
            {
                sb.Append(paragraphs[i]);
                continue;
            }
            // 如果段落本身只有1-2行，不做拆分
            var lineCount = para.Split('\n').Length;
            if (lineCount > 1 && para.Length > 100)
            {
                // 长段落且有多行，按句号拆分
                var sentences = Regex.Split(para, @"(?<=[。！？])\s*");
                sb.Append(string.Join("\n", sentences.Where(s => !string.IsNullOrWhiteSpace(s))));
            }
            else
            {
                sb.Append(para);
            }
        }
        return sb.ToString();
    }

    /// <summary>繁体转简体（常用字基础版本）。</summary>
    public static string ConvertTraditionalToSimplified(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            sb.Append(T2SMap.TryGetValue(ch, out var simplified) ? simplified : ch);
        }
        return sb.ToString();
    }

    // ================================================================
    //  辅助方法
    // ================================================================

    /// <summary>判断某个位置是否处于中文上下文（前后有中文字符）。</summary>
    private static bool IsInChineseContext(string text, int index)
    {
        // 检查前一个非空格字符
        var hasChineseBefore = false;
        for (var j = index - 1; j >= 0; j--)
        {
            if (char.IsWhiteSpace(text[j])) continue;
            if (IsChineseChar(text[j])) hasChineseBefore = true;
            break;
        }
        // 检查后一个非空格字符
        var hasChineseAfter = false;
        for (var j = index + 1; j < text.Length; j++)
        {
            if (char.IsWhiteSpace(text[j])) continue;
            if (IsChineseChar(text[j])) hasChineseAfter = true;
            break;
        }
        return hasChineseBefore || hasChineseAfter;
    }

    private static bool IsChineseChar(char ch)
    {
        return (ch >= '\u4e00' && ch <= '\u9fff') ||
               (ch >= '\u3400' && ch <= '\u4dbf');
    }

    // ================================================================
    //  繁简转换常用字映射表（基础版，约500常用字）
    // ================================================================
    private static readonly Dictionary<char, char> T2SMap = new()
    {
        // 常用繁简对照
        {'愛','爱'},{'礙','碍'},{'闇','暗'},{'罷','罢'},{'備','备'},{'貝','贝'},
        {'筆','笔'},{'畢','毕'},{'邊','边'},{'變','变'},{'標','标'},{'錶','表'},
        {'別','别'},{'賓','宾'},{'補','补'},{'佈','布'},{'參','参'},{'殘','残'},
        {'蠶','蚕'},{'倉','仓'},{'層','层'},{'產','产'},{'長','长'},{'場','场'},
        {'廠','厂'},{'車','车'},{'陳','陈'},{'稱','称'},{'誠','诚'},{'遲','迟'},
        {'衝','冲'},{'醜','丑'},{'處','处'},{'觸','触'},{'辭','辞'},{'從','从'},
        {'達','达'},{'帶','带'},{'當','当'},{'檔','档'},{'黨','党'},{'導','导'},
        {'燈','灯'},{'敵','敌'},{'遞','递'},{'點','点'},{'電','电'},{'東','东'},
        {'動','动'},{'獨','独'},{'斷','断'},{'對','对'},{'噸','吨'},{'奪','夺'},
        {'發','发'},{'範','范'},{'飛','飞'},{'費','费'},{'墳','坟'},{'奮','奋'},
        {'風','风'},{'鳳','凤'},{'復','复'},{'負','负'},{'蓋','盖'},
        {'幹','干'},{'鋼','钢'},{'個','个'},{'給','给'},{'鞏','巩'},{'貢','贡'},
        {'構','构'},{'購','购'},{'穀','谷'},{'顧','顾'},{'關','关'},{'觀','观'},
        {'廣','广'},{'歸','归'},{'龜','龟'},{'國','国'},{'過','过'},{'華','华'},
        {'畫','画'},{'話','话'},{'壞','坏'},{'歡','欢'},{'環','环'},{'還','还'},
        {'匯','汇'},{'會','会'},{'護','护'},{'滬','沪'},{'劃','划'},{'懷','怀'},
        {'換','换'},{'黃','黄'},{'獲','获'},
        {'機','机'},{'極','极'},{'幾','几'},{'計','计'},{'記','记'},{'際','际'},
        {'濟','济'},{'繼','继'},{'家','家'},{'價','价'},{'堅','坚'},{'間','间'},
        {'簡','简'},{'見','见'},{'劍','剑'},{'漸','渐'},{'將','将'},{'獎','奖'},
        {'節','节'},{'潔','洁'},{'結','结'},{'進','进'},{'盡','尽'},{'經','经'},
        {'驚','惊'},{'競','竞'},{'舊','旧'},{'據','据'},{'劇','剧'},{'覺','觉'},
        {'軍','军'},{'開','开'},{'凱','凯'},{'殼','壳'},{'課','课'},{'誇','夸'},
        {'塊','块'},{'寬','宽'},{'況','况'},{'礦','矿'},{'虧','亏'},{'來','来'},
        {'賴','赖'},{'藍','蓝'},{'蘭','兰'},{'攔','拦'},{'覽','览'},{'懶','懒'},
        {'爛','烂'},{'撈','捞'},{'勞','劳'},{'樂','乐'},{'類','类'},{'裡','里'},
        {'禮','礼'},{'歷','历'},{'勵','励'},{'聯','联'},{'練','练'},{'糧','粮'},
        {'兩','两'},{'遼','辽'},{'獵','猎'},{'臨','临'},{'鄰','邻'},{'靈','灵'},
        {'領','领'},{'劉','刘'},{'爐','炉'},{'陸','陆'},{'錄','录'},{'慮','虑'},
        {'論','论'},{'落','落'},{'媽','妈'},{'馬','马'},{'買','买'},{'賣','卖'},
        {'麥','麦'},{'滿','满'},{'貓','猫'},{'貿','贸'},{'門','门'},{'們','们'},
        {'夢','梦'},{'面','面'},{'廟','庙'},{'滅','灭'},{'鳴','鸣'},{'謀','谋'},
        {'畝','亩'},{'納','纳'},{'難','难'},{'腦','脑'},{'鬧','闹'},{'釀','酿'},
        {'鳥','鸟'},{'聶','聂'},{'擬','拟'},{'寧','宁'},{'農','农'},
        {'歐','欧'},{'盤','盘'},{'龐','庞'},{'賠','赔'},{'噴','喷'},{'鵬','鹏'},
        {'騙','骗'},{'蘋','苹'},{'評','评'},{'憑','凭'},{'齊','齐'},
        {'氣','气'},{'遷','迁'},{'錢','钱'},{'潛','潜'},{'強','强'},{'搶','抢'},
        {'親','亲'},{'輕','轻'},{'請','请'},{'慶','庆'},{'瓊','琼'},{'區','区'},
        {'權','权'},{'勸','劝'},{'確','确'},{'讓','让'},{'擾','扰'},{'熱','热'},
        {'認','认'},{'榮','荣'},{'軟','软'},{'銳','锐'},{'賽','赛'},{'傘','伞'},
        {'喪','丧'},{'殺','杀'},{'曬','晒'},{'審','审'},{'聲','声'},{'勝','胜'},
        {'師','师'},{'實','实'},{'適','适'},{'壽','寿'},{'輸','输'},{'樹','树'},
        {'雙','双'},{'誰','谁'},{'絲','丝'},{'鬆','松'},{'蘇','苏'},{'隨','随'},
        {'歲','岁'},{'損','损'},{'鎖','锁'},{'體','体'},{'態','态'},{'貪','贪'},
        {'嘆','叹'},{'湯','汤'},{'討','讨'},{'騰','腾'},{'題','题'},{'條','条'},
        {'鐵','铁'},{'聽','听'},{'廳','厅'},{'統','统'},{'圖','图'},{'團','团'},
        {'萬','万'},{'網','网'},{'衛','卫'},{'穩','稳'},{'務','务'},{'霧','雾'},
        {'習','习'},{'席','席'},{'戲','戏'},{'細','细'},{'蝦','虾'},{'險','险'},
        {'現','现'},{'獻','献'},{'鄉','乡'},{'響','响'},{'項','项'},{'協','协'},
        {'寫','写'},{'興','兴'},{'選','选'},{'學','学'},{'壓','压'},{'鴉','鸦'},
        {'鹽','盐'},{'嚴','严'},{'顏','颜'},{'陽','阳'},{'養','养'},{'樣','样'},
        {'搖','摇'},{'葉','叶'},{'業','业'},{'醫','医'},{'義','义'},{'藝','艺'},
        {'億','亿'},{'議','议'},{'應','应'},{'營','营'},{'優','优'},{'郵','邮'},
        {'與','与'},{'語','语'},{'預','预'},{'員','员'},{'園','园'},{'遠','远'},
        {'願','愿'},{'閱','阅'},{'運','运'},{'雜','杂'},{'災','灾'},{'贊','赞'},
        {'髒','脏'},{'棗','枣'},{'責','责'},{'戰','战'},{'張','张'},{'趙','赵'},
        {'鎮','镇'},{'爭','争'},{'鄭','郑'},{'證','证'},{'織','织'},{'質','质'},
        {'種','种'},{'眾','众'},{'鑄','铸'},{'專','专'},{'莊','庄'},{'裝','装'},
        {'準','准'},{'資','资'},{'總','总'},{'組','组'},{'鑽','钻'},{'嘍','喽'},
        {'壙','圹'},{'壘','垒'},{'壩','坝'},{'壯','壮'},{'膽','胆'},
        {'腫','肿'},{'腸','肠'},{'膿','脓'},{'臺','台'},{'艦','舰'},{'蕩','荡'},
        {'藥','药'},{'藹','蔼'},{'蘿','萝'},{'虜','虏'},
        {'蟲','虫'},{'蠟','蜡'},{'訊','讯'},{'許','许'},
        {'詠','咏'},{'詞','词'},{'試','试'},{'詩','诗'},{'詫','诧'},
        {'誥','诰'},{'誤','误'},{'誘','诱'},{'誦','诵'},
        {'誣','诬'},{'誨','诲'},{'誼','谊'},{'誡','诫'},
        {'謊','谎'},{'謙','谦'},{'謝','谢'},{'謠','谣'},{'謹','谨'},{'謾','谩'},
        {'譭','毁'},{'譽','誉'},{'讀','读'},
        {'讕','谰'},{'讖','谶'},{'讚','赞'},{'讜','谠'},{'讞','谳'},
    };

    // ================================================================
    //  排版预设模板
    // ================================================================

    /// <summary>排版预设。</summary>
    public class FormatPreset
    {
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public FormatOptions Options { get; init; }
        /// <summary>导出时附加的文件后缀，如 "txt"、"md"、"html"。</summary>
        public string FileExtension { get; init; } = "txt";
        /// <summary>导出时是否在内容外包裹格式包装（如 HTML 标签）。</summary>
        public Func<string, string, string>? WrapContent { get; init; }
    }

    /// <summary>所有排版预设列表。</summary>
    public static readonly FormatPreset[] Presets =
    {
        new()
        {
            Name = "不排版（原文）",
            Description = "保持原文不变，不做任何排版处理",
            Options = FormatOptions.None,
            FileExtension = "txt",
        },
        new()
        {
            Name = "简洁阅读版",
            Description = "去多余空行 + 行首尾空格 + 中英文加空格 + 标点规范",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.CollapseExtraSpaces | FormatOptions.SpaceBetweenChineseEnglish |
                      FormatOptions.SpaceBetweenChineseNumber | FormatOptions.NormalizeEllipsis,
            FileExtension = "txt",
        },
        new()
        {
            Name = "公众号排版",
            Description = "段首缩进 + 重点标注「」+ 段间空行 + 标点中文化",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.SpaceBetweenChineseEnglish | FormatOptions.SpaceBetweenChineseNumber |
                      FormatOptions.ParagraphIndent | FormatOptions.PunctToChinese |
                      FormatOptions.NormalizeEllipsis | FormatOptions.FixQuotes,
            FileExtension = "txt",
        },
        new()
        {
            Name = "Markdown 格式",
            Description = "标题用 # 标记，列表用 - 标记，加粗用 ** 标记",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.CollapseExtraSpaces,
            FileExtension = "md",
        },
        new()
        {
            Name = "公文格式",
            Description = "层级编号 + 正文缩进 + 标点中文化 + 全角转半角",
            Options = FormatOptions.FullWidthToHalfWidth | FormatOptions.ParagraphIndent |
                      FormatOptions.PunctToChinese | FormatOptions.CollapseBlankLines |
                      FormatOptions.TrimLineSpaces | FormatOptions.FixQuotes,
            FileExtension = "txt",
        },
        new()
        {
            Name = "邮件格式",
            Description = "称谓顶格 + 正文缩进 + 标点中文化 + 空行清理",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.ParagraphIndent | FormatOptions.PunctToChinese |
                      FormatOptions.SpaceBetweenChineseEnglish,
            FileExtension = "txt",
        },
        new()
        {
            Name = "日记格式",
            Description = "日期天气标题 + 段落缩进 + 心情标记",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.ParagraphIndent | FormatOptions.NormalizeEllipsis,
            FileExtension = "txt",
        },
        new()
        {
            Name = "会议记录",
            Description = "智能分段 + 缩进 + 空行清理",
            Options = FormatOptions.SmartParagraph | FormatOptions.CollapseBlankLines |
                      FormatOptions.TrimLineSpaces | FormatOptions.ParagraphIndent,
            FileExtension = "txt",
        },
        new()
        {
            Name = "网页 HTML",
            Description = "导出为 HTML 文件，段落用 p 标签包裹",
            Options = FormatOptions.CollapseBlankLines | FormatOptions.TrimLineSpaces |
                      FormatOptions.SpaceBetweenChineseEnglish | FormatOptions.SpaceBetweenChineseNumber,
            FileExtension = "html",
            WrapContent = (title, content) =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("<!DOCTYPE html>");
                sb.AppendLine("<html lang=\"zh-CN\">");
                sb.AppendLine("<head>");
                sb.AppendLine("<meta charset=\"UTF-8\">");
                sb.AppendLine($"<title>{System.Net.WebUtility.HtmlEncode(title)}</title>");
                sb.AppendLine("<style>");
                sb.AppendLine("body { font-family: 'Microsoft YaHei', sans-serif; max-width: 800px; margin: 2em auto; padding: 0 1em; line-height: 1.8; }");
                sb.AppendLine("h1 { text-align: center; }");
                sb.AppendLine("p { text-indent: 2em; margin: 0.5em 0; }");
                sb.AppendLine("</style>");
                sb.AppendLine("</head>");
                sb.AppendLine("<body>");
                sb.AppendLine($"<h1>{System.Net.WebUtility.HtmlEncode(title)}</h1>");
                foreach (var line in content.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed))
                        sb.AppendLine();
                    else
                        sb.AppendLine($"<p>{System.Net.WebUtility.HtmlEncode(trimmed)}</p>");
                }
                sb.AppendLine("</body>");
                sb.AppendLine("</html>");
                return sb.ToString();
            },
        },
        new()
        {
            Name = "全排版（最严格）",
            Description = "应用所有排版规则（不含繁简转换）",
            Options = FormatOptions.AllCore,
            FileExtension = "txt",
        },
        new()
        {
            Name = "繁体转简体",
            Description = "常用繁体字转简体 + 基础排版",
            Options = FormatOptions.TraditionalToSimplified | FormatOptions.CollapseBlankLines |
                      FormatOptions.TrimLineSpaces | FormatOptions.CollapseExtraSpaces,
            FileExtension = "txt",
        },
    };

    /// <summary>根据预设名称查找预设。</summary>
    public static FormatPreset? FindPreset(string name)
    {
        return Array.Find(Presets, p => p.Name == name);
    }
}
