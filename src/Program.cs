// 脚本坐标精准覆盖插件
//
// 两种用法：
//   1) 跨脚本覆盖：把源脚本（基准）的 <&Text:…:X:Y> 坐标按区块/标签顺序写到目标脚本。
//   2) 同脚本段落覆盖：在一个脚本内找出结构相同的重复段落，用“第一个改好的段落”
//      作模板，把它的坐标（以及 & 绝对坐标写法）覆盖到后续段落。
//
// 只改坐标数字与 <Text: / <&Text: 的 & 写法；文字、颜色、跳转、换行、编码全部原样保留。
//
// 编译：见 build.ps1（Windows 自带 .NET Framework 4.x 的 csc.exe 即可）。
//
// 命令行：
//   --cli      --src A.txt --dst B.txt [--apply] [--report out.txt]
//   --cli-para --file X.txt [--template 水帘洞] [--apply] [--report out.txt]

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace ScriptCoordOverlay
{
    // ---------------------------------------------------------------- 数据模型

    internal enum ElementKind
    {
        Text = 0,     // <Text:…> / <&Text:…> / 简写 <文字/颜色>
        Image = 1,    // <Img:…> / <ImgEx:…>
        ItemBox = 2,  // <ITEMBOX:…>
        Other = 3,
        Plain = 4     // 写在标签外面的纯文字（没有标签，也没有坐标）
    }

    internal sealed class ScriptTag
    {
        public int BlockIndex;
        public int Ordinal;      // 在所属区块/段落内的顺序（从 0 开始）
        public int LineNo;       // 1 起
        public int TagStart;     // '<' 的绝对位置（纯文字时为文字起点）
        public int TagEnd;       // '>' 的绝对位置（纯文字时为最后一个字符）
        public int RunEnd;       // 纯文字结束后的下一个位置
        public bool HasAmp;      // 是否为 <&Text:（绝对坐标）
        public string Name = "Text";
        public ElementKind Kind = ElementKind.Text;
        public bool IsShorthand; // 简写：<文字/颜色>，本身没有坐标
        public bool IsPlain;     // 标签外的纯文字
        public int OptionsStart = -1; // '/' 选项起始的绝对位置（没有则 -1）
        public int OptionsLen;        // 选项长度
        public int TextStart = -1;    // 内容文字的绝对起点
        public int TextLen;           // 内容文字长度
        public string Raw = "";
        public string Text = "";
        public string X = "";
        public string Y = "";
        public string Options = "";
        public int XStart, XLen;  // 在整份文本中的绝对位置
        public int YStart, YLen;
        public bool HasCoord;
    }

    /// 自定义对话框调用（OPENMERCHANTBIGDLG …），只取命令后面的参数部分
    internal sealed class DialogCmd
    {
        public int BlockIndex;
        public int Ordinal;
        public int LineNo;
        public int ArgStart;     // 参数在整份文本中的绝对位置
        public int ArgLen;
        public string Command = "OPENMERCHANTBIGDLG";
        public string Args = ""; // 参数（已去掉首尾空白与行尾注释）
        public string Raw = "";  // 整行原文
    }

    internal sealed class ScriptBlock
    {
        public int Index;
        public string Name = "";
        public int StartLine;
        public bool IsParagraph;          // 是否为由 #IF 切出来的段落（而非 [@] 区块）
        public int SourceBlockIndex = -1;
        public int ParagraphIndex = -1;
        public readonly List<int> IfLines = new List<int>();
        public readonly List<ScriptTag> Tags = new List<ScriptTag>();      // 仅命名 Text（跨脚本模式用）
        public readonly List<ScriptTag> Elements = new List<ScriptTag>();  // 全部控件（段落模式用）
        public readonly List<DialogCmd> Dialogs = new List<DialogCmd>();
    }

    internal sealed class ScriptDoc
    {
        public string Path = "";
        public string Text = "";
        public Encoding Enc = new UTF8Encoding(false);
        public byte[] Bom = new byte[0];
        public readonly List<ScriptBlock> Blocks = new List<ScriptBlock>();
        /// 由 [@] 区块再按 #IF 切出来的段落；段落模式只认这些
        public readonly List<ScriptBlock> Paragraphs = new List<ScriptBlock>();

        /// 只保留真正含可覆盖标签的区块（即 #say 对话框区块）
        public List<ScriptBlock> TagBlocks()
        {
            List<ScriptBlock> r = new List<ScriptBlock>();
            for (int i = 0; i < Blocks.Count; i++)
                if (Blocks[i].Tags.Count > 0) r.Add(Blocks[i]);
            return r;
        }

        /// 含任意对话框控件的区块（跨脚本模式按区块整体配对）
        public List<ScriptBlock> ElementBlocks()
        {
            List<ScriptBlock> r = new List<ScriptBlock>();
            for (int i = 0; i < Blocks.Count; i++)
                if (Blocks[i].Elements.Count > 0) r.Add(Blocks[i]);
            return r;
        }

        public int TagCount()
        {
            int n = 0;
            for (int i = 0; i < Blocks.Count; i++) n += Blocks[i].Tags.Count;
            return n;
        }
    }

    internal sealed class CoordPair
    {
        public ScriptTag Src;
        public ScriptTag Dst;
        public string BlockName = "";
        public int Index;
        public string Status = "";   // 相同 / 覆盖 / 缺失 / 标点 / 编号 / 颜色
        public bool IsMeta;
        public bool IsDialog;
        public string MetaOld = "";
        public string MetaNew = "";
        public DialogCmd SrcDlg;
        public DialogCmd DstDlg;
        public int BlockIndex;
    }

    internal sealed class ScanResult
    {
        public ScriptDoc Src;
        public ScriptDoc Dst;
        public List<CoordPair> Pairs = new List<CoordPair>();
        public readonly List<ParaPlan> Plans = new List<ParaPlan>();
        public int BlockA, BlockB, TagA, TagB;
        public bool StructureOk = true;
        public readonly List<string> Warnings = new List<string>();
        public int SameCount, ChangeCount, MissingCount;
        public int CompleteCount, ButtonChanges, DialogChanges;
        public int PunctChanges, IdChanges, ColorChanges;
        public int TotalChanges
        {
            get { return ChangeCount + PunctChanges + IdChanges + ColorChanges; }
        }
    }

    internal sealed class ParagraphGroup
    {
        public string Signature = "";
        public readonly List<ScriptBlock> Blocks = new List<ScriptBlock>();
    }

    internal sealed class ParaPair
    {
        public ScriptBlock TargetBlock;
        public ScriptTag Src;
        public ScriptTag Dst;
        public DialogCmd SrcDlg;
        public DialogCmd DstDlg;
        public bool IsDialog;
        public bool IsMeta;          // 标点/编号/颜色这类附加变更行
        public string MetaOld = "";
        public string MetaNew = "";
        public string Status = "";   // 相同 / 覆盖 / 缺失 / 结构异常 / 标点 / 编号 / 颜色
    }

    internal sealed class TextStyle
    {
        public bool? ColonFull;   // true=全角 ：，false=半角 :，null=不改
        public bool? SlashFull;   // true=全角 ／，false=半角 /
    }

    internal sealed class ParaPlan
    {
        public ScriptDoc Doc;
        public ScriptBlock Template;
        public List<ScriptBlock> Targets = new List<ScriptBlock>();
        public List<ParaPair> Pairs = new List<ParaPair>();
        public readonly List<string> Warnings = new List<string>();
        public int SameCount, ChangeCount, MissingCount, SkippedBlocks;
        public int DialogChanges;
        public int CompleteCount;   // 简写文本补全为绝对坐标的处数
        public int ButtonChanges;   // 文字按钮替换为图像按钮的处数
        public int KindMismatch;    // 控件种类对不上、跳过的处数
        public int PunctChanges;    // 标点风格变更的控件数
        public int IdChanges;       // 图片编号变更的控件数
        public int ColorChanges;    // 颜色/选项变更的控件数
        public bool FollowAmp = true;
        public bool SyncDialog = true;
        public bool SyncPunctuation = true;
        public bool SyncImageIds = true;
        public bool SyncColors = false;
        public bool VerifyByBlock = false;   // 跨脚本模式：按 [@] 区块校验
        public TextStyle Style = new TextStyle();

        public int TotalChanges
        {
            get { return ChangeCount + PunctChanges + IdChanges + ColorChanges; }
        }
    }

    internal sealed class Edit
    {
        public int Start;
        public int Len;
        public string Value;
    }

    internal static class TextEdits
    {
        /// 从右往左替换，前面的偏移量才不会失效
        public static string Apply(string text, List<Edit> edits)
        {
            edits.Sort(delegate(Edit a, Edit b) { return b.Start.CompareTo(a.Start); });
            StringBuilder sb = new StringBuilder(text);
            foreach (Edit e in edits)
            {
                sb.Remove(e.Start, e.Len);
                sb.Insert(e.Start, e.Value);
            }
            return sb.ToString();
        }

        /// 把 <Text: 变成 <&Text:（或反过来），返回需要执行的编辑
        public static void AddAmpEdits(List<Edit> edits, ScriptTag dst, bool addAmp)
        {
            if (addAmp == dst.HasAmp) return;
            Edit e = new Edit();
            e.Start = dst.TagStart + 1;
            e.Len = dst.HasAmp ? 1 : 0;
            e.Value = addAmp ? "&" : "";
            edits.Add(e);
        }

        public static void AddCoordEdits(List<Edit> edits, ScriptTag src, ScriptTag dst)
        {
            if (src.X != dst.X)
                edits.Add(new Edit { Start = dst.XStart, Len = dst.XLen, Value = src.X });
            if (src.Y != dst.Y)
                edits.Add(new Edit { Start = dst.YStart, Len = dst.YLen, Value = src.Y });
        }

        public static string AmpText(bool hasAmp) { return hasAmp ? "&" : "无"; }

        public static string AmpChange(bool from, bool to)
        {
            if (from == to) return AmpText(from);
            return AmpText(from) + " → " + AmpText(to);
        }
    }

    // ---------------------------------------------------------------- 解析器

    internal static class ScriptParser
    {
        // 命名控件：<&Text:…> / <Img:…> / <ITEMBOX:…> / <ImgEx:…>
        private static readonly Regex NamedTagRegex = new Regex(
            @"^<(?<amp>&?)(?<name>[A-Za-z][A-Za-z0-9_]*):(?<body>.*)>$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 末尾的 :X:Y 或 ：X：Y（可为负数），后面可跟 /SCOLOR=224 或 {AUTOCOLOR=…} 之类选项
        private static readonly Regex CoordRegex = new Regex(
            @"^(?<text>.*?)(?<s1>[:：])(?<x>-?\d+)(?<s2>[:：])(?<y>-?\d+)(?<post>[/{].*)?$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 简写文本 <文字/颜色> 里的选项起点
        private static readonly Regex ShortOptRegex = new Regex(
            @"/(?=(?:FCOLOR|SCOLOR|COLOR|AUTOCOLOR|@))",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // <ITEMBOX:N:F:M:X:Y:W:H:S:T>：坐标是第 4、5 个字段
        private static readonly Regex ItemBoxCoordRegex = new Regex(
            @"^(?<p1>[^:]*):(?<p2>[^:]*):(?<p3>[^:]*):(?<x>-?\d+):(?<y>-?\d+)(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline);

        // 自定义对话框：OPENMERCHANTBIGDLG 参数…
        private static readonly Regex DialogRegex = new Regex(
            @"^(?<pre>\s*)(?<cmd>OPENMERCHANTBIGDLG)(?=\s|$)(?<args>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        public static ScriptDoc Load(string path)
        {
            ScriptDoc doc = new ScriptDoc();
            doc.Path = path;
            byte[] bytes = File.ReadAllBytes(path);
            doc.Text = Decode(bytes, out doc.Enc, out doc.Bom);
            ParseInto(doc);
            return doc;
        }

        public static ScriptDoc ParseText(string text, string path)
        {
            ScriptDoc doc = new ScriptDoc();
            doc.Path = path;
            doc.Text = text;
            ParseInto(doc);
            return doc;
        }

        private static string Decode(byte[] b, out Encoding enc, out byte[] bom)
        {
            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                enc = new UTF8Encoding(false);
                bom = new byte[] { 0xEF, 0xBB, 0xBF };
                return enc.GetString(b, 3, b.Length - 3);
            }
            if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            {
                enc = new UnicodeEncoding(false, false);
                bom = new byte[] { 0xFF, 0xFE };
                return enc.GetString(b, 2, b.Length - 2);
            }
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                enc = new UnicodeEncoding(true, false);
                bom = new byte[] { 0xFE, 0xFF };
                return enc.GetString(b, 2, b.Length - 2);
            }
            UTF8Encoding strict = new UTF8Encoding(false, true);
            try
            {
                string s = strict.GetString(b);
                enc = new UTF8Encoding(false);
                bom = new byte[0];
                return s;
            }
            catch (DecoderFallbackException)
            {
                enc = Encoding.GetEncoding(936);   // GBK
                bom = new byte[0];
                return enc.GetString(b);
            }
        }

        /// 找到与 start 处 '<' 配对的 '>'（跳过 <$STR(...)> 这类嵌套变量）
        private static int FindTagEnd(string line, int start)
        {
            int j = start + 1;
            while (j < line.Length)
            {
                if (line[j] == '<' && j + 1 < line.Length && line[j + 1] == '$')
                {
                    int k = line.IndexOf('>', j + 2);
                    if (k < 0) return -1;
                    j = k + 1;
                    continue;
                }
                if (line[j] == '>') return j;
                j++;
            }
            return -1;
        }

        private static string DirectiveHead(string trimmed)
        {
            string d = trimmed.Substring(1).TrimStart();
            int sp = d.IndexOfAny(new char[] { ' ', '\t' });
            if (sp >= 0) d = d.Substring(0, sp);
            return d.ToLowerInvariant();
        }

        private static void ParseInto(ScriptDoc doc)
        {
            string text = doc.Text;
            List<int> starts = new List<int>();
            List<int> ends = new List<int>();

            int s = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n' || text[i] == '\r')
                {
                    starts.Add(s);
                    ends.Add(i);
                    if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    s = i + 1;
                }
            }
            starts.Add(s);
            ends.Add(text.Length);

            bool inSay = false;

            for (int li = 0; li < starts.Count; li++)
            {
                int ls = starts[li];
                int le = ends[li];
                string line = text.Substring(ls, le - ls);
                string trimmed = line.Trim();

                if (trimmed.StartsWith("[@") && trimmed.EndsWith("]"))
                {
                    ScriptBlock nb = new ScriptBlock();
                    nb.Name = trimmed;
                    nb.StartLine = li + 1;
                    nb.Index = doc.Blocks.Count;
                    doc.Blocks.Add(nb);
                    inSay = false;
                    continue;
                }

                ParseDialogLine(doc, line, ls, li, trimmed);

                if (trimmed.StartsWith("#"))
                {
                    ScriptBlock cur = doc.Blocks.Count > 0 ? doc.Blocks[doc.Blocks.Count - 1] : null;
                    string head = DirectiveHead(trimmed);
                    if (head == "if")
                    {
                        if (cur != null) cur.IfLines.Add(li + 1);
                        inSay = false;
                    }
                    else if (head == "say" || head == "elsesay")
                    {
                        inSay = true;
                    }
                    else
                    {
                        inSay = false;
                    }
                    continue;
                }

                if (!inSay || doc.Blocks.Count == 0) continue;
                ParseElements(doc.Blocks[doc.Blocks.Count - 1], line, ls, li);
            }

            BuildParagraphs(doc);
        }

        /// 扫描一行里的 <…> 控件，并把标签外的纯文字也登记为控件
        /// 扫描一行里的 <…> 控件，并把标签外的纯文字也登记为控件（保持行内顺序）
        private static void ParseElements(ScriptBlock blk, string line, int ls, int li)
        {
            int prev = 0;
            int pos = 0;
            while (pos < line.Length)
            {
                int st = line.IndexOf('<', pos);
                if (st < 0) break;
                int en = FindTagEnd(line, st);
                if (en < 0) break;

                AddPlainRun(blk, line, ls, li, prev, st);
                AddTag(blk, line, ls, li, st, en);
                prev = en + 1;
                pos = en + 1;
            }
            AddPlainRun(blk, line, ls, li, prev, line.Length);
        }

        private static void AddTag(ScriptBlock blk, string line, int ls, int li, int st, int en)
        {
            string raw = line.Substring(st, en - st + 1);
            string inner = raw.Substring(1, raw.Length - 2);
            if (inner.StartsWith("$")) return;   // <$STR(..)> 变量不是控件

            ScriptTag tag = new ScriptTag();
            tag.LineNo = li + 1;
            tag.Raw = raw;
            tag.TagStart = ls + st;
            tag.TagEnd = ls + en;
            tag.BlockIndex = blk.Index;

            Match nm = NamedTagRegex.Match(raw);
            if (nm.Success)
            {
                tag.HasAmp = nm.Groups["amp"].Length == 1;
                tag.Name = nm.Groups["name"].Value;

                string lower = tag.Name.ToLowerInvariant();
                if (lower == "text") tag.Kind = ElementKind.Text;
                else if (lower == "img" || lower == "imgex") tag.Kind = ElementKind.Image;
                else if (lower == "itembox") tag.Kind = ElementKind.ItemBox;
                else tag.Kind = ElementKind.Other;

                string body = nm.Groups["body"].Value;
                int bodyAbs = ls + st + nm.Groups["body"].Index;   // 相对整行
                tag.Text = body;
                tag.TextStart = bodyAbs;
                tag.TextLen = body.Length;

                if (tag.Kind != ElementKind.ItemBox)
                {
                    Match cm = CoordRegex.Match(body);
                    if (cm.Success)
                    {
                        tag.HasCoord = true;
                        tag.Text = cm.Groups["text"].Value;
                        tag.TextStart = bodyAbs + cm.Groups["text"].Index;
                        tag.TextLen = cm.Groups["text"].Length;
                        tag.X = cm.Groups["x"].Value;
                        tag.Y = cm.Groups["y"].Value;
                        tag.XStart = bodyAbs + cm.Groups["x"].Index;
                        tag.XLen = cm.Groups["x"].Length;
                        tag.YStart = bodyAbs + cm.Groups["y"].Index;
                        tag.YLen = cm.Groups["y"].Length;
                        tag.Options = cm.Groups["post"].Value;
                        if (cm.Groups["post"].Success)
                        {
                            tag.OptionsStart = bodyAbs + cm.Groups["post"].Index;
                            tag.OptionsLen = cm.Groups["post"].Length;
                        }
                    }
                }
                else
                {
                    // <ITEMBOX:N:F:M:X:Y:W:H:S:T>：坐标是第 4、5 个字段
                    Match im = ItemBoxCoordRegex.Match(body);
                    if (im.Success)
                    {
                        tag.HasCoord = true;
                        tag.X = im.Groups["x"].Value;
                        tag.Y = im.Groups["y"].Value;
                        tag.XStart = bodyAbs + im.Groups["x"].Index;
                        tag.XLen = im.Groups["x"].Length;
                        tag.YStart = bodyAbs + im.Groups["y"].Index;
                        tag.YLen = im.Groups["y"].Length;
                    }
                }
            }
            else
            {
                // 简写文本：<文字/颜色> 或 <文字/@跳转>
                tag.IsShorthand = true;
                tag.Kind = ElementKind.Text;
                tag.Name = "Text";
                tag.HasAmp = false;

                Match sm = ShortOptRegex.Match(inner);
                tag.TextStart = ls + st + 1;
                if (sm.Success)
                {
                    tag.Options = inner.Substring(sm.Index);
                    tag.Text = inner.Substring(0, sm.Index);
                    tag.TextLen = sm.Index;
                    tag.OptionsStart = ls + st + 1 + sm.Index;
                    tag.OptionsLen = inner.Length - sm.Index;
                }
                else
                {
                    tag.Options = "";
                    tag.Text = inner;
                    tag.TextLen = inner.Length;
                    tag.OptionsStart = -1;
                    tag.OptionsLen = 0;
                }
            }

            tag.Ordinal = blk.Elements.Count;
            blk.Elements.Add(tag);

            // 跨脚本模式仍只用命名 Text
            if (tag.Kind == ElementKind.Text && !tag.IsShorthand)
                blk.Tags.Add(tag);
        }

        private static void AddPlainRun(ScriptBlock blk, string line, int ls, int li, int from, int to)
        {
            int a = from;
            int b = to;
            while (a < b && IsPlainEdge(line[a])) a++;
            while (b > a && IsPlainEdge(line[b - 1])) b--;
            if (b <= a) return;

            string content = line.Substring(a, b - a);
            if (content.StartsWith("<$")) return;   // 变量片段不当作可补全文字
            if (content.IndexOf('<') >= 0 || content.IndexOf('>') >= 0)
                return;                              // 残缺标签（例如漏了 '<'）不要动

            ScriptTag p = new ScriptTag();
            p.IsPlain = true;
            p.Kind = ElementKind.Plain;
            p.Name = "Plain";
            p.LineNo = li + 1;
            p.Text = content;
            p.Raw = content;
            p.Options = "";
            p.TagStart = ls + a;
            p.TagEnd = ls + b - 1;
            p.RunEnd = ls + b;
            p.TextStart = ls + a;
            p.TextLen = content.Length;
            p.BlockIndex = blk.Index;
            p.Ordinal = blk.Elements.Count;
            blk.Elements.Add(p);
        }

        private static bool IsPlainEdge(char c)
        {
            return char.IsWhiteSpace(c) || c == '\\';
        }

        private static void ParseDialogLine(ScriptDoc doc, string line, int ls, int li, string trimmed)
        {
            Match dm = DialogRegex.Match(line);
            if (!dm.Success) return;

            if (doc.Blocks.Count == 0)
            {
                ScriptBlock nb = new ScriptBlock();
                nb.Name = "(未分区)";
                nb.StartLine = li + 1;
                nb.Index = 0;
                doc.Blocks.Add(nb);
            }

            ScriptBlock blk = doc.Blocks[doc.Blocks.Count - 1];
            DialogCmd dcmd = new DialogCmd();
            dcmd.BlockIndex = blk.Index;
            dcmd.LineNo = li + 1;
            dcmd.Raw = trimmed;
            dcmd.Ordinal = blk.Dialogs.Count;

            string rawArgs = dm.Groups["args"].Value;
            int lead = 0;
            while (lead < rawArgs.Length && (rawArgs[lead] == ' ' || rawArgs[lead] == '\t')) lead++;
            string args = rawArgs.Substring(lead);
            int semi = args.IndexOf(';');
            if (semi >= 0) args = args.Substring(0, semi);
            args = args.TrimEnd();

            dcmd.Args = args;
            dcmd.ArgStart = ls + dm.Groups["args"].Index + lead;
            dcmd.ArgLen = args.Length;
            blk.Dialogs.Add(dcmd);
        }

        /// 把每个 [@] 区块按 #IF 切成段落（同一区块里多页对话框就是多个段落）
        private static void BuildParagraphs(ScriptDoc doc)
        {
            for (int bi = 0; bi < doc.Blocks.Count; bi++)
            {
                ScriptBlock b = doc.Blocks[bi];
                if (b.Elements.Count == 0) continue;

                int blockEnd = int.MaxValue;
                if (bi + 1 < doc.Blocks.Count) blockEnd = doc.Blocks[bi + 1].StartLine - 1;

                List<int> bounds = b.IfLines.Count > 0
                    ? b.IfLines
                    : new List<int>(new int[] { b.StartLine });

                for (int i = 0; i < bounds.Count; i++)
                {
                    int st = bounds[i];
                    int en = (i + 1 < bounds.Count) ? bounds[i + 1] - 1 : blockEnd;

                    ScriptBlock pg = new ScriptBlock();
                    pg.IsParagraph = true;
                    pg.SourceBlockIndex = b.Index;
                    pg.StartLine = st;
                    pg.Name = bounds.Count > 1 ? b.Name + " #" + (i + 1) : b.Name;

                    for (int k = 0; k < b.Elements.Count; k++)
                    {
                        ScriptTag t = b.Elements[k];
                        if (t.LineNo < st || t.LineNo > en) continue;
                        t.Ordinal = pg.Elements.Count;
                        pg.Elements.Add(t);
                    }
                    for (int k = 0; k < b.Dialogs.Count; k++)
                    {
                        DialogCmd dcmd = b.Dialogs[k];
                        if (dcmd.LineNo < st || dcmd.LineNo > en) continue;
                        dcmd.Ordinal = pg.Dialogs.Count;
                        pg.Dialogs.Add(dcmd);
                    }

                    if (pg.Elements.Count == 0) continue;
                    pg.ParagraphIndex = doc.Paragraphs.Count;
                    doc.Paragraphs.Add(pg);
                }
            }
        }
    }

    // ---------------------------------------------------------------- 目录/编码

    internal static class FileIO
    {
        public static void Save(ScriptDoc doc, string newText, bool backup)
        {
            string path = doc.Path;
            if (backup)
            {
                string bak = path + ".坐标覆盖备份-" +
                             DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".txt";
                File.Copy(path, bak, false);
            }

            byte[] body = doc.Enc.GetBytes(newText);
            byte[] all = new byte[doc.Bom.Length + body.Length];
            Buffer.BlockCopy(doc.Bom, 0, all, 0, doc.Bom.Length);
            Buffer.BlockCopy(body, 0, all, doc.Bom.Length, body.Length);
            File.WriteAllBytes(path, all);
        }

        public static string FindLatestBackup(string targetPath)
        {
            string dir = Path.GetDirectoryName(targetPath);
            string pat = Path.GetFileName(targetPath) + ".坐标覆盖备份-*.txt";
            string[] files = Directory.GetFiles(dir, pat);
            if (files.Length == 0) return null;
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            return files[files.Length - 1];
        }
    }

    // ---------------------------------------------------------------- 跨脚本覆盖

    internal static class OverlayEngine
    {
        public static ScanResult Scan(string srcPath, string dstPath,
                                      bool syncPunct, bool syncIds, bool syncColors)
        {
            if (string.Equals(Path.GetFullPath(srcPath), Path.GetFullPath(dstPath),
                              StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("源脚本与目标脚本不能是同一个文件。");

            ScanResult r = new ScanResult();
            r.Src = ScriptParser.Load(srcPath);
            r.Dst = ScriptParser.Load(dstPath);

            List<ScriptBlock> ga = r.Src.ElementBlocks();
            List<ScriptBlock> gb = r.Dst.ElementBlocks();
            r.BlockA = ga.Count;
            r.BlockB = gb.Count;
            r.TagA = CountElements(r.Src);
            r.TagB = CountElements(r.Dst);

            if (ga.Count != gb.Count)
            {
                r.StructureOk = false;
                r.Warnings.Add(string.Format(
                    "可同步区块数量不一致：源 {0} 个，目标 {1} 个。", ga.Count, gb.Count));
            }

            int n = Math.Min(ga.Count, gb.Count);
            int seq = 0;
            for (int i = 0; i < n; i++)
            {
                ScriptBlock ba = ga[i];
                ScriptBlock bb = gb[i];

                if (ba.Elements.Count != bb.Elements.Count)
                {
                    r.StructureOk = false;
                    r.Warnings.Add(string.Format(
                        "第 {0} 个区块「{1}」控件数不一致：源 {2} 个，目标 {3} 个，已跳过。",
                        i + 1, bb.Name, ba.Elements.Count, bb.Elements.Count));
                    continue;
                }

                ParaPlan plan = TemplateEngine.BuildPlan(r.Dst, ba,
                    new List<ScriptBlock>(new ScriptBlock[] { bb }),
                    true, false, syncPunct, syncIds, syncColors);
                plan.VerifyByBlock = true;
                r.Plans.Add(plan);

                r.ChangeCount += plan.ChangeCount;
                r.SameCount += plan.SameCount;
                r.MissingCount += plan.MissingCount;
                r.CompleteCount += plan.CompleteCount;
                r.ButtonChanges += plan.ButtonChanges;
                r.PunctChanges += plan.PunctChanges;
                r.IdChanges += plan.IdChanges;
                r.ColorChanges += plan.ColorChanges;

                foreach (ParaPair pp in plan.Pairs)
                {
                    CoordPair cp = new CoordPair();
                    cp.Src = pp.Src;
                    cp.Dst = pp.Dst;
                    cp.BlockName = bb.Name;
                    cp.Index = ++seq;
                    cp.Status = pp.Status;
                    cp.IsMeta = pp.IsMeta;
                    cp.MetaOld = pp.MetaOld;
                    cp.MetaNew = pp.MetaNew;
                    r.Pairs.Add(cp);
                }

                int nd = Math.Min(ba.Dialogs.Count, bb.Dialogs.Count);
                for (int j = 0; j < nd; j++)
                {
                    CoordPair cp = new CoordPair();
                    cp.IsDialog = true;
                    cp.BlockName = bb.Name;
                    cp.BlockIndex = bb.Index;
                    cp.SrcDlg = ba.Dialogs[j];
                    cp.DstDlg = bb.Dialogs[j];
                    cp.Index = ++seq;

                    if (ba.Dialogs[j].Args == bb.Dialogs[j].Args)
                    {
                        cp.Status = "相同";
                        r.SameCount++;
                    }
                    else
                    {
                        cp.Status = "覆盖";
                        r.ChangeCount++;
                        r.DialogChanges++;
                    }
                    r.Pairs.Add(cp);
                }
            }

            return r;
        }

        private static int CountElements(ScriptDoc doc)
        {
            int n = 0;
            for (int i = 0; i < doc.Blocks.Count; i++) n += doc.Blocks[i].Elements.Count;
            return n;
        }

        public static string BuildNewText(ScanResult r)
        {
            List<Edit> edits = new List<Edit>();
            foreach (ParaPlan plan in r.Plans)
                TemplateEngine.CollectEdits(plan, edits);

            foreach (CoordPair p in r.Pairs)
            {
                if (!p.IsDialog || p.SrcDlg == null || p.DstDlg == null) continue;
                if (p.SrcDlg.Args == p.DstDlg.Args) continue;
                edits.Add(new Edit
                {
                    Start = p.DstDlg.ArgStart,
                    Len = p.DstDlg.ArgLen,
                    Value = p.SrcDlg.Args
                });
            }
            return TextEdits.Apply(r.Dst.Text, edits);
        }

        public static int Verify(ScanResult r, string dstText)
        {
            int bad = 0;
            foreach (ParaPlan plan in r.Plans)
                bad += TemplateEngine.Verify(plan, dstText);

            ScriptDoc d2 = ScriptParser.ParseText(dstText, r.Dst.Path);
            foreach (CoordPair p in r.Pairs)
            {
                if (!p.IsDialog || p.SrcDlg == null || p.DstDlg == null) continue;
                if (p.BlockIndex < 0 || p.BlockIndex >= d2.Blocks.Count) { bad++; continue; }
                ScriptBlock nb = d2.Blocks[p.BlockIndex];
                if (p.DstDlg.Ordinal >= nb.Dialogs.Count) { bad++; continue; }
                if (nb.Dialogs[p.DstDlg.Ordinal].Args != p.SrcDlg.Args) bad++;
            }
            return bad;
        }

        public static string BuildReport(ScanResult r, bool applied)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("脚本同步记录  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("源（模板）  ：" + r.Src.Path);
            sb.AppendLine("目标        ：" + r.Dst.Path);
            sb.AppendLine("状态        ：" + (applied ? "已写入" : "仅预览"));
            sb.AppendLine(string.Format(
                "配对 {0} 行：覆盖 {1}（补全 {2}），标点 {3}，编号 {4}，颜色 {5}，已一致 {6}，缺失 {7}",
                r.Pairs.Count, r.ChangeCount, r.CompleteCount,
                r.PunctChanges, r.IdChanges, r.ColorChanges,
                r.SameCount, r.MissingCount));
            if (r.Warnings.Count > 0)
            {
                sb.AppendLine("结构提示：");
                foreach (string w in r.Warnings) sb.AppendLine("  - " + w);
            }
            sb.AppendLine();
            sb.AppendLine("序号\t区块\t行\t源坐标\t目标坐标\t写法\t状态");
            foreach (CoordPair p in r.Pairs)
            {
                if (p.IsMeta)
                {
                    string oldv = p.MetaOld.Length == 0 ? "无" : p.MetaOld;
                    string newv = p.MetaNew.Length == 0 ? "无" : p.MetaNew;
                    sb.AppendLine(string.Format("{0}\t{1}\t-\t-\t-\t-\t{2}",
                        p.Index, p.BlockName,
                        (oldv + " → " + newv).Replace("\t", " ")));
                    continue;
                }

                if (p.IsDialog)
                {
                    string s = p.SrcDlg != null ? p.SrcDlg.Args : "-";
                    string d = p.DstDlg != null ? p.DstDlg.Args : "-";
                    sb.AppendLine(string.Format("{0}\t{1}\t框\t{2}\t{3}\t-\t{4}",
                        p.Index, p.BlockName, s, d, p.Status));
                    continue;
                }

                string sxy = p.Src != null && p.Src.HasCoord ? p.Src.X + ":" + p.Src.Y : "-";
                string dxy = p.Dst != null && p.Dst.HasCoord ? p.Dst.X + ":" + p.Dst.Y : "-";
                int line = p.Src != null ? p.Src.LineNo : (p.Dst != null ? p.Dst.LineNo : 0);
                string amp = p.Src != null && p.Dst != null
                    ? TextEdits.AmpChange(p.Dst.HasAmp, p.Src.HasAmp) : "-";
                sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                    p.Index, p.BlockName, line, sxy, dxy, amp, p.Status));
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- 同脚本段落覆盖

    internal static class TemplateEngine
    {
        public static List<ParagraphGroup> DetectGroups(ScriptDoc doc)
        {
            List<ParagraphGroup> ordered = new List<ParagraphGroup>();
            Dictionary<string, ParagraphGroup> map = new Dictionary<string, ParagraphGroup>();

            foreach (ScriptBlock b in doc.Paragraphs)
            {
                string sig = Signature(b);
                ParagraphGroup g;
                if (!map.TryGetValue(sig, out g))
                {
                    g = new ParagraphGroup();
                    g.Signature = sig;
                    map[sig] = g;
                    ordered.Add(g);
                }
                g.Blocks.Add(b);
            }

            List<ParagraphGroup> res = new List<ParagraphGroup>();

            // 近似归组：只差一两个控件、文字高度相似的组直接合并
            bool merged = true;
            while (merged)
            {
                merged = false;
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Blocks.Count == 0) continue;
                    for (int j = i + 1; j < ordered.Count; j++)
                    {
                        if (ordered[j].Blocks.Count == 0) continue;
                        ScriptBlock a = ordered[i].Blocks[0];
                        ScriptBlock b = ordered[j].Blocks[0];
                        // 不再限制控件总数差：纯文字“。”这类会让页与页差好几个控件，
                        // 能否归组交给 FuzzyScore（行数相同 + 文字匹配比例）判断。
                        if (FuzzyScore(a, b) < 0.55) continue;

                        ordered[i].Blocks.AddRange(ordered[j].Blocks);
                        ordered[j].Blocks.Clear();
                        merged = true;
                    }
                }
            }

            foreach (ParagraphGroup g in ordered)
            {
                if (g.Blocks.Count < 2) continue;

                // 组内按文档顺序排，第一段就是默认模板
                g.Blocks.Sort(delegate(ScriptBlock a, ScriptBlock b)
                {
                    if (a.SourceBlockIndex != b.SourceBlockIndex)
                        return a.SourceBlockIndex.CompareTo(b.SourceBlockIndex);
                    return a.StartLine.CompareTo(b.StartLine);
                });
                res.Add(g);
            }
            return res;
        }

        /// 每行控件数 + 空文字位置 + 带 /@ 跳转的位置，用来判定“同构段落”
        public static string Signature(ScriptBlock b)
        {
            StringBuilder empty = new StringBuilder();
            StringBuilder link = new StringBuilder();
            for (int i = 0; i < b.Elements.Count; i++)
            {
                ScriptTag t = b.Elements[i];
                if (t.Kind == ElementKind.Text && t.HasCoord && t.Text.Trim().Length == 0)
                    empty.Append(i).Append(',');
                if (t.Options.Contains("/@")) link.Append(i).Append(',');
            }
            return LineCounts(b) + "#" + empty + "#" + link;
        }

        public static string LineCounts(ScriptBlock b)
        {
            StringBuilder sb = new StringBuilder();
            int cur = -1, n = 0;
            foreach (ScriptTag t in b.Elements)
            {
                if (t.LineNo != cur)
                {
                    if (cur != -1) sb.Append(n).Append(',');
                    cur = t.LineNo;
                    n = 0;
                }
                n++;
            }
            if (cur != -1) sb.Append(n);
            return sb.ToString();
        }

        public static ParaPlan BuildPlan(ScriptDoc doc, ScriptBlock template,
                                         List<ScriptBlock> targets, bool followAmp,
                                         bool syncDialog, bool syncPunct,
                                         bool syncIds, bool syncColors)
        {
            ParaPlan plan = new ParaPlan();
            plan.Doc = doc;
            plan.Template = template;
            plan.Targets = targets;
            plan.FollowAmp = followAmp;
            plan.SyncDialog = syncDialog;
            plan.SyncPunctuation = syncPunct;
            plan.SyncImageIds = syncIds;
            plan.SyncColors = syncColors;
            plan.Style = DetectStyle(template);

            foreach (ScriptBlock tb in targets)
            {
                bool exact = tb.Elements.Count == template.Elements.Count &&
                             LineCounts(tb) == LineCounts(template);
                List<List<ScriptTag>> tplLines = null;
                List<List<ScriptTag>> dstLines = null;

                if (!exact)
                {
                    // 行数一致就按行 + 文字相似度对齐；控件数允许因纯文字“。”而不同
                    tplLines = LineGroups(template);
                    dstLines = LineGroups(tb);
                    if (tplLines.Count == 0 || tplLines.Count != dstLines.Count)
                    {
                        plan.SkippedBlocks++;
                        plan.Warnings.Add(string.Format(
                            "段落「{0}」与模板结构不同（模板 {1} 个控件 / 本段 {2} 个），已跳过。",
                            tb.Name, template.Elements.Count, tb.Elements.Count));
                        ParaPair bad = new ParaPair();
                        bad.TargetBlock = tb;
                        bad.Src = template.Elements.Count > 0 ? template.Elements[0] : null;
                        bad.Dst = null;
                        bad.Status = "结构异常";
                        plan.Pairs.Add(bad);
                        plan.MissingCount++;
                        continue;
                    }
                }

                // 自定义对话框：让重复段落调用同一套 OPENMERCHANTBIGDLG 参数（对话框 + 坐标）
                if (syncDialog)
                {
                    int nd = Math.Min(template.Dialogs.Count, tb.Dialogs.Count);
                    for (int j = 0; j < nd; j++)
                    {
                        DialogCmd sd = template.Dialogs[j];
                        DialogCmd dd = tb.Dialogs[j];
                        ParaPair p = new ParaPair();
                        p.TargetBlock = tb;
                        p.IsDialog = true;
                        p.SrcDlg = sd;
                        p.DstDlg = dd;

                        if (sd.Args == dd.Args)
                        {
                            p.Status = "相同";
                            plan.SameCount++;
                        }
                        else
                        {
                            p.Status = "覆盖";
                            plan.ChangeCount++;
                            plan.DialogChanges++;
                        }
                        plan.Pairs.Add(p);
                    }

                    for (int j = nd; j < template.Dialogs.Count; j++)
                    {
                        ParaPair p = new ParaPair();
                        p.TargetBlock = tb;
                        p.IsDialog = true;
                        p.SrcDlg = template.Dialogs[j];
                        p.DstDlg = null;
                        p.Status = "缺失";
                        plan.MissingCount++;
                        plan.Pairs.Add(p);
                    }

                    if (tb.Dialogs.Count > template.Dialogs.Count)
                    {
                        plan.Warnings.Add(string.Format(
                            "段落「{0}」的 OPENMERCHANTBIGDLG 比模板多 {1} 处，多余部分未处理。",
                            tb.Name, tb.Dialogs.Count - template.Dialogs.Count));
                    }
                }

                if (exact)
                {
                    for (int j = 0; j < template.Elements.Count; j++)
                        PairElements(plan, tb, template.Elements[j], tb.Elements[j], followAmp);
                }
                else
                {
                    for (int li = 0; li < tplLines.Count; li++)
                    {
                        List<ScriptTag> a = tplLines[li];
                        List<ScriptTag> b = dstLines[li];
                        bool[] matched = new bool[a.Count];
                        bool[] used = new bool[b.Count];

                        List<int[]> pairs = AlignLine(a, b);
                        foreach (int[] pr in pairs)
                        {
                            PairElements(plan, tb, a[pr[0]], b[pr[1]], followAmp);
                            matched[pr[0]] = true;
                            used[pr[1]] = true;
                        }

                        // 剩下没配上的，按顺序补配（同为文字类或同种控件）
                        int q = 0;
                        for (int k = 0; k < a.Count; k++)
                        {
                            if (matched[k]) continue;
                            while (q < b.Count && used[q]) q++;
                            if (q >= b.Count) break;
                            if (!CompatiblePair(a[k], b[q])) continue;

                            PairElements(plan, tb, a[k], b[q], followAmp);
                            matched[k] = true;
                            used[q] = true;
                        }

                        for (int k = 0; k < a.Count; k++)
                        {
                            if (matched[k]) continue;
                            ParaPair miss = new ParaPair();
                            miss.TargetBlock = tb;
                            miss.Src = a[k];
                            miss.Dst = null;
                            miss.Status = "缺失";
                            plan.MissingCount++;
                            plan.Pairs.Add(miss);
                        }
                    }
                }
            }
            return plan;
        }

        /// 文字类控件之间可以互相补全；其它控件要求种类一致
        private static bool CompatiblePair(ScriptTag a, ScriptTag b)
        {
            if (a.Kind == b.Kind) return true;
            bool ta = a.Kind == ElementKind.Text || a.IsPlain;
            bool tb = b.Kind == ElementKind.Text || b.IsPlain;
            if (ta && tb) return true;

            // 一边是带 @ 跳转的图像/其它按钮，另一边是带 @ 跳转的文字按钮：
            // 允许配对，交给“替换”分支把文字按钮整体换成图像按钮
            if (HasLink(a.Options) && HasLink(b.Options) &&
                ((a.Kind != ElementKind.Text && b.Kind == ElementKind.Text) ||
                 (a.Kind == ElementKind.Text && b.Kind != ElementKind.Text)))
                return true;

            return false;
        }

        private static bool IsTextual(ScriptTag t)
        {
            return t.Kind == ElementKind.Text || t.IsPlain;
        }

        private static void PairElements(ParaPlan plan, ScriptBlock tb, ScriptTag src, ScriptTag dst, bool followAmp)
        {
            AddElementPair(plan, tb, src, dst, followAmp);
            ParaPair last = plan.Pairs[plan.Pairs.Count - 1];
            if (last.Status == "缺失" || last.Status == "种类不同" || last.Status == "结构异常") return;
            AddMetaPairs(plan, tb, src, dst);
        }

        /// 标点风格、图片编号、颜色这三类附加变更，单独出一行方便预览
        private static void AddMetaPairs(ParaPlan plan, ScriptBlock tb, ScriptTag src, ScriptTag dst)
        {
            if (plan.SyncPunctuation && IsTextual(src) && IsTextual(dst) && dst.TextStart >= 0)
            {
                string mapped = MapPunctuation(dst.Text, plan.Style);
                if (mapped != dst.Text)
                {
                    ParaPair m = new ParaPair();
                    m.TargetBlock = tb;
                    m.Src = src;
                    m.Dst = dst;
                    m.IsMeta = true;
                    m.Status = "标点";
                    m.MetaOld = dst.Text;
                    m.MetaNew = mapped;
                    plan.Pairs.Add(m);
                    plan.PunctChanges++;
                }
            }

            if (plan.SyncImageIds && src.Kind == ElementKind.Image && dst.Kind == ElementKind.Image &&
                dst.TextStart >= 0 && src.TextStart >= 0 && src.Text != dst.Text)
            {
                ParaPair m = new ParaPair();
                m.TargetBlock = tb;
                m.Src = src;
                m.Dst = dst;
                m.IsMeta = true;
                m.Status = "编号";
                m.MetaOld = dst.Text;
                m.MetaNew = src.Text;
                plan.Pairs.Add(m);
                plan.IdChanges++;
            }

            if (plan.SyncColors && IsTextual(src) && IsTextual(dst))
            {
                string merged = MergeOptions(src.Options, dst.Options);
                if (merged != dst.Options)
                {
                    ParaPair m = new ParaPair();
                    m.TargetBlock = tb;
                    m.Src = src;
                    m.Dst = dst;
                    m.IsMeta = true;
                    m.Status = "颜色";
                    m.MetaOld = dst.Options;
                    m.MetaNew = merged;
                    plan.Pairs.Add(m);
                    plan.ColorChanges++;
                }
            }
        }

        /// 统计模板段落内容文字里的标点风格；打平或没出现就不改
        private static TextStyle DetectStyle(ScriptBlock b)
        {
            int colonFull = 0, colonHalf = 0, slashFull = 0, slashHalf = 0;
            foreach (ScriptTag t in b.Elements)
            {
                if (!IsTextual(t)) continue;
                string s = t.Text ?? "";
                foreach (char c in s)
                {
                    if (c == '：') colonFull++;
                    else if (c == ':') colonHalf++;
                    else if (c == '／') slashFull++;
                    else if (c == '/') slashHalf++;
                }
            }

            TextStyle st = new TextStyle();
            if (colonFull != colonHalf) st.ColonFull = colonFull > colonHalf;
            if (slashFull != slashHalf) st.SlashFull = slashFull > slashHalf;
            return st;
        }

        private static string MapPunctuation(string text, TextStyle st)
        {
            if (text == null || (st.ColonFull == null && st.SlashFull == null)) return text;
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                char o = c;
                if (st.ColonFull.HasValue)
                {
                    if (st.ColonFull.Value && c == ':') o = '：';
                    else if (!st.ColonFull.Value && c == '：') o = ':';
                }
                if (st.SlashFull.HasValue)
                {
                    if (st.SlashFull.Value && c == '/') o = '／';
                    else if (!st.SlashFull.Value && c == '／') o = '/';
                }
                sb.Append(o);
            }
            return sb.ToString();
        }

        /// 选项里的 @跳转 保留目标自己的，其余（颜色等）跟模板
        private static string MergeOptions(string srcOptions, string dstOptions)
        {
            if (srcOptions == null) srcOptions = "";
            if (dstOptions == null) dstOptions = "";
            int si = srcOptions.IndexOf("/@");
            int di = dstOptions.IndexOf("/@");
            string head = si < 0 ? srcOptions : srcOptions.Substring(0, si);
            string link = di < 0 ? "" : dstOptions.Substring(di);
            return head + link;
        }

        private static void AddElementPair(ParaPlan plan, ScriptBlock tb, ScriptTag src, ScriptTag dst, bool followAmp)
        {
            ParaPair p = new ParaPair();
            p.TargetBlock = tb;
            p.Src = src;
            p.Dst = dst;

            bool sameKind = src.Kind == dst.Kind;
            bool convertible = followAmp && dst.IsShorthand && src.HasCoord &&
                               (src.Kind == ElementKind.Text ||
                                (HasLink(src.Options) && HasLink(dst.Options)));

            if (dst.IsPlain)
            {
                // 标签外的纯文字：套上 <&Text:文字:X:Y/选项>
                if (followAmp && src.Kind == ElementKind.Text && src.HasCoord)
                {
                    p.Status = "补全";
                    plan.ChangeCount++;
                    plan.CompleteCount++;
                }
                else
                {
                    p.Status = "跳过";
                }
            }
            else if (dst.Kind == ElementKind.Text && HasLink(dst.Options) &&
                     HasLink(src.Options) && src.Kind != ElementKind.Text && src.HasCoord)
            {
                // 模板把文字按钮换成了图像按钮（<&imgex:…/@跳转>）：
                // 目标按钮整体替换成模板的按钮，只保留目标自己的 @ 跳转
                p.Status = "替换";
                plan.ChangeCount++;
                plan.ButtonChanges++;
            }
            else if (dst.IsShorthand)
            {
                if (convertible)
                {
                    p.Status = "补全";       // 简写文本 → <&Text:文字:X:Y/选项>
                    plan.ChangeCount++;
                    plan.CompleteCount++;
                }
                else if (!src.HasCoord || !followAmp)
                {
                    p.Status = "跳过";       // 模板没有坐标，或未启用“写法跟随模板”
                }
                else
                {
                    p.Status = "种类不同";
                    plan.KindMismatch++;
                }
            }
            else if (!sameKind)
            {
                p.Status = "种类不同";
                plan.KindMismatch++;
            }
            else if (!src.HasCoord || !dst.HasCoord)
            {
                p.Status = "跳过";           // 例如 ITEMBOX 暂不识别坐标
            }
            else if (src.X == dst.X && src.Y == dst.Y &&
                     (!followAmp || src.HasAmp == dst.HasAmp))
            {
                p.Status = "相同";
                plan.SameCount++;
            }
            else
            {
                p.Status = "覆盖";
                plan.ChangeCount++;
            }
            plan.Pairs.Add(p);
        }

        /// 按行分组（同一行的控件放一起）
        private static List<List<ScriptTag>> LineGroups(ScriptBlock b)
        {
            List<List<ScriptTag>> lines = new List<List<ScriptTag>>();
            int cur = -1;
            List<ScriptTag> list = null;
            foreach (ScriptTag t in b.Elements)
            {
                if (t.LineNo != cur)
                {
                    list = new List<ScriptTag>();
                    lines.Add(list);
                    cur = t.LineNo;
                }
                list.Add(t);
            }
            return lines;
        }

        /// 两行控件按文字相似度做序列对齐
        private static List<int[]> AlignLine(List<ScriptTag> a, List<ScriptTag> b)
        {
            int n = a.Count;
            int m = b.Count;
            double[,] dp = new double[n + 1, m + 1];
            bool[,] take = new bool[n + 1, m + 1];

            for (int i = 1; i <= n; i++)
            {
                for (int j = 1; j <= m; j++)
                {
                    double s = Similarity(a[i - 1], b[j - 1]);
                    bool ok = s >= 0.35;
                    double diag = dp[i - 1, j - 1] + (ok ? s : -1000);
                    double up = dp[i - 1, j];
                    double left = dp[i, j - 1];
                    double best = Math.Max(Math.Max(up, left), diag);
                    dp[i, j] = best;
                    take[i, j] = ok && best == diag;
                }
            }

            List<int[]> pairs = new List<int[]>();
            int x = n, y = m;
            while (x > 0 && y > 0)
            {
                if (take[x, y])
                {
                    pairs.Add(new int[] { x - 1, y - 1 });
                    x--;
                    y--;
                }
                else if (dp[x - 1, y] >= dp[x, y - 1]) x--;
                else y--;
            }
            pairs.Reverse();
            return pairs;
        }

        /// 两个段落整体像不像（0~1），只用于“差一两个控件”的近似归组
        private static double FuzzyScore(ScriptBlock a, ScriptBlock b)
        {
            List<List<ScriptTag>> la = LineGroups(a);
            List<List<ScriptTag>> lb = LineGroups(b);
            if (la.Count != lb.Count || la.Count == 0) return 0;

            int matched = 0;
            int minTotal = 0;
            double sum = 0;
            for (int i = 0; i < la.Count; i++)
            {
                minTotal += Math.Min(la[i].Count, lb[i].Count);
                List<int[]> pairs = AlignLine(la[i], lb[i]);
                matched += pairs.Count;
                foreach (int[] p in pairs) sum += Similarity(la[i][p[0]], lb[i][p[1]]);
            }

            if (matched < 2) return 0;
            if (matched < (int)Math.Ceiling(minTotal * 0.6)) return 0;
            // 能配上多少比例的控件，比“平均像不像”更适合判断同构段落
            return (double)matched / Math.Max(a.Elements.Count, b.Elements.Count);
        }

        private static double Similarity(ScriptTag a, ScriptTag b)
        {
            string x = Normalize(a.Text);
            string y = Normalize(b.Text);
            if (x.Length == 0 && y.Length == 0) return 1;
            if (x.Length == 0 || y.Length == 0) return 0;
            if (x == y) return 1;
            int d = Levenshtein(x, y);
            return 1.0 - (double)d / Math.Max(x.Length, y.Length);
        }

        private static string Normalize(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsDigit(c) || char.IsWhiteSpace(c)) continue;
                if ("【】[]()（）{}<>，,。.、：:；;！!？?/\\-_".IndexOf(c) >= 0) continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static int Levenshtein(string a, string b)
        {
            int n = a.Length;
            int m = b.Length;
            int[,] d = new int[n + 1, m + 1];
            for (int i = 0; i <= n; i++) d[i, 0] = i;
            for (int j = 0; j <= m; j++) d[0, j] = j;
            for (int i = 1; i <= n; i++)
                for (int j = 1; j <= m; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            return d[n, m];
        }

        private static bool HasLink(string options)
        {
            return options != null && options.Contains("/@");
        }

        /// 保留模板的其它选项，只把 @跳转 换成目标自己的
        private static string MergeLink(string srcOptions, string dstOptions)
        {
            if (srcOptions == null) srcOptions = "";
            if (dstOptions == null) dstOptions = "";
            int si = srcOptions.IndexOf("/@");
            int di = dstOptions.IndexOf("/@");
            if (si < 0 || di < 0) return srcOptions;
            return srcOptions.Substring(0, si) + dstOptions.Substring(di);
        }

        public static string BuildNewText(ParaPlan plan)
        {
            List<Edit> edits = new List<Edit>();
            CollectEdits(plan, edits);
            return TextEdits.Apply(plan.Doc.Text, edits);
        }

        /// 把本计划产生的修改收集到 edits（跨脚本模式会把多个计划合并后统一应用）
        public static void CollectEdits(ParaPlan plan, List<Edit> edits)
        {
            foreach (ParaPair p in plan.Pairs)
            {
                if (p.IsMeta) continue;   // 附加变更行只用于预览

                if (p.IsDialog)
                {
                    if (p.SrcDlg == null || p.DstDlg == null) continue;
                    if (p.SrcDlg.Args == p.DstDlg.Args) continue;
                    edits.Add(new Edit
                    {
                        Start = p.DstDlg.ArgStart,
                        Len = p.DstDlg.ArgLen,
                        Value = p.SrcDlg.Args
                    });
                    continue;
                }

                if (p.Src == null || p.Dst == null) continue;

                if (p.Status == "替换")
                {
                    // 整个按钮替换成模板的控件（&imgex / &Img…），只保留目标自己的 @ 跳转
                    string newTag = "<" + (p.Src.HasAmp ? "&" : "") + p.Src.Name + ":" +
                                    p.Src.Text + ":" + p.Src.X + ":" + p.Src.Y +
                                    MergeLink(p.Src.Options, p.Dst.Options) + ">";
                    edits.Add(new Edit
                    {
                        Start = p.Dst.TagStart,
                        Len = p.Dst.TagEnd - p.Dst.TagStart + 1,
                        Value = newTag
                    });
                    continue;
                }

                bool textual = IsTextual(p.Src) && IsTextual(p.Dst);
                string mergedOptions = plan.SyncColors && textual
                    ? MergeOptions(p.Src.Options, p.Dst.Options)
                    : p.Dst.Options;

                if (p.Status == "补全")
                {
                    if (p.Dst.IsPlain)
                    {
                        // 标签外纯文字 → <&Text:文字:X:Y/选项>
                        string open = "<" + (p.Src.HasAmp ? "&" : "") + "Text:";
                        edits.Add(new Edit { Start = p.Dst.TagStart, Len = 0, Value = open });
                        edits.Add(new Edit
                        {
                            Start = p.Dst.RunEnd,
                            Len = 0,
                            Value = ":" + p.Src.X + ":" + p.Src.Y + mergedOptions + ">"
                        });
                    }
                    else
                    {
                        // <文字/颜色> → <&Text:文字:X:Y/颜色>，保留原文案
                        string prefix = (p.Src.HasAmp ? "&" : "") + "Text:";
                        edits.Add(new Edit { Start = p.Dst.TagStart + 1, Len = 0, Value = prefix });
                        bool hasOpt = p.Dst.OptionsStart >= 0;
                        edits.Add(new Edit
                        {
                            Start = hasOpt ? p.Dst.OptionsStart : p.Dst.TagEnd - 1,
                            Len = hasOpt ? p.Dst.OptionsLen : 0,
                            Value = ":" + p.Src.X + ":" + p.Src.Y + mergedOptions
                        });
                    }
                }
                else
                {
                    if (p.Src.HasCoord && p.Dst.HasCoord)
                    {
                        if (plan.FollowAmp) TextEdits.AddAmpEdits(edits, p.Dst, p.Src.HasAmp);
                        TextEdits.AddCoordEdits(edits, p.Src, p.Dst);
                    }

                    if (plan.SyncColors && textual)
                        AddOptionEdits(edits, p.Dst, mergedOptions);
                }

                // 标点：文字里的 : / 按模板风格改
                if (plan.SyncPunctuation && textual && p.Dst.TextStart >= 0)
                    AddPunctuationEdits(edits, p.Dst, plan.Style);

                // 图片编号：坐标之前的数字跟模板
                if (plan.SyncImageIds && p.Src.Kind == ElementKind.Image &&
                    p.Dst.Kind == ElementKind.Image && p.Dst.TextStart >= 0 &&
                    p.Src.Text != p.Dst.Text)
                {
                    edits.Add(new Edit
                    {
                        Start = p.Dst.TextStart,
                        Len = p.Dst.TextLen,
                        Value = p.Src.Text
                    });
                }
            }
        }

        private static void AddPunctuationEdits(List<Edit> edits, ScriptTag dst, TextStyle style)
        {
            if (style.ColonFull == null && style.SlashFull == null) return;
            string text = dst.Text ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char o = c;
                if (style.ColonFull.HasValue)
                {
                    if (style.ColonFull.Value && c == ':') o = '：';
                    else if (!style.ColonFull.Value && c == '：') o = ':';
                }
                if (style.SlashFull.HasValue)
                {
                    if (style.SlashFull.Value && c == '/') o = '／';
                    else if (!style.SlashFull.Value && c == '／') o = '/';
                }
                if (o != c)
                    edits.Add(new Edit { Start = dst.TextStart + i, Len = 1, Value = o.ToString() });
            }
        }

        private static void AddOptionEdits(List<Edit> edits, ScriptTag dst, string merged)
        {
            if (merged == dst.Options) return;
            if (dst.OptionsStart >= 0)
            {
                edits.Add(new Edit
                {
                    Start = dst.OptionsStart,
                    Len = dst.OptionsLen,
                    Value = merged
                });
            }
            else if (merged.Length > 0)
            {
                edits.Add(new Edit { Start = dst.TagEnd, Len = 0, Value = merged });
            }
        }

        private static ScriptBlock FindBlock(ParaPlan plan, ScriptDoc d2, ScriptBlock target)
        {
            if (plan.VerifyByBlock)
            {
                if (target.Index < 0 || target.Index >= d2.Blocks.Count) return null;
                return d2.Blocks[target.Index];
            }
            if (target.ParagraphIndex < 0 || target.ParagraphIndex >= d2.Paragraphs.Count) return null;
            return d2.Paragraphs[target.ParagraphIndex];
        }

        public static int Verify(ParaPlan plan, string newText)
        {
            ScriptDoc d2 = ScriptParser.ParseText(newText, plan.Doc.Path);
            int bad = 0;
            foreach (ParaPair p in plan.Pairs)
            {
                if (p.IsMeta) continue;

                if (p.IsDialog)
                {
                    if (p.SrcDlg == null || p.DstDlg == null) continue;
                    ScriptBlock nb = FindBlock(plan, d2, p.TargetBlock);
                    if (nb == null) { bad++; continue; }
                    if (p.DstDlg.Ordinal >= nb.Dialogs.Count) { bad++; continue; }
                    if (nb.Dialogs[p.DstDlg.Ordinal].Args != p.SrcDlg.Args) bad++;
                    continue;
                }

                if (p.Dst == null || p.Src == null) continue;

                ScriptBlock nb2 = FindBlock(plan, d2, p.TargetBlock);
                if (nb2 == null) { bad++; continue; }
                if (p.Dst.Ordinal >= nb2.Elements.Count) { bad++; continue; }
                ScriptTag nt = nb2.Elements[p.Dst.Ordinal];

                bool textual = IsTextual(p.Src) && IsTextual(p.Dst);
                string expectedText = plan.SyncPunctuation && textual
                    ? MapPunctuation(p.Dst.Text, plan.Style) : p.Dst.Text;

                if (p.Status == "替换")
                {
                    if (!nt.HasCoord || nt.X != p.Src.X || nt.Y != p.Src.Y) { bad++; continue; }
                    if (nt.Options != MergeLink(p.Src.Options, p.Dst.Options)) bad++;
                    continue;
                }

                if (p.Status == "补全")
                {
                    if (!nt.HasCoord || nt.X != p.Src.X || nt.Y != p.Src.Y) { bad++; continue; }
                    if (nt.Text != expectedText) bad++;
                    if (plan.SyncColors && textual &&
                        nt.Options != MergeOptions(p.Src.Options, p.Dst.Options)) bad++;
                    continue;
                }

                if (p.Src.HasCoord && p.Dst.HasCoord)
                {
                    if (nt.X != p.Src.X || nt.Y != p.Src.Y) { bad++; continue; }
                    if (plan.FollowAmp && nt.HasAmp != p.Src.HasAmp) { bad++; continue; }
                }

                if (plan.SyncPunctuation && textual && nt.Text != expectedText) bad++;

                if (plan.SyncImageIds && p.Src.Kind == ElementKind.Image &&
                    p.Dst.Kind == ElementKind.Image && nt.Text != p.Src.Text) bad++;

                if (plan.SyncColors && textual &&
                    nt.Options != MergeOptions(p.Src.Options, p.Dst.Options)) bad++;
            }
            return bad;
        }

        private static string StyleDescription(TextStyle st)
        {
            List<string> parts = new List<string>();
            if (st.ColonFull.HasValue) parts.Add(st.ColonFull.Value ? "冒号全角" : "冒号半角");
            if (st.SlashFull.HasValue) parts.Add(st.SlashFull.Value ? "斜杠全角" : "斜杠半角");
            if (parts.Count == 0) return "模板没有可参照的标点，不修改";
            return string.Join(" / ", parts.ToArray());
        }

        public static string BuildReport(ParaPlan plan, bool applied)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("段落坐标覆盖记录  " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("脚本        ：" + plan.Doc.Path);
            sb.AppendLine("模板段落    ：" + plan.Template.Name);
            sb.AppendLine("目标段落    ：" + plan.Targets.Count + " 段"
                          + (plan.SkippedBlocks > 0 ? "（其中 " + plan.SkippedBlocks + " 段因结构不同被跳过）" : ""));
            sb.AppendLine("坐标写法    ：" + (plan.FollowAmp ? "跟随模板（& = 绝对坐标）" : "仅复制数字"));
            sb.AppendLine("对话框参数  ：" + (plan.SyncDialog ? "同步（OPENMERCHANTBIGDLG 与模板一致）" : "不同步"));
            sb.AppendLine("内容标点    ：" + (plan.SyncPunctuation ? StyleDescription(plan.Style) : "不同步"));
            sb.AppendLine("图片编号    ：" + (plan.SyncImageIds ? "同步" : "不同步"));
            sb.AppendLine("颜色/选项   ：" + (plan.SyncColors ? "同步" : "不同步"));
            sb.AppendLine("状态        ：" + (applied ? "已写入" : "仅预览"));
            sb.AppendLine(string.Format(
                "匹配 {0} 处：需覆盖 {1}（简写补全 {2}），标点 {3}，编号 {4}，颜色 {5}，已一致 {6}，缺失 {7}",
                plan.Pairs.Count, plan.ChangeCount, plan.CompleteCount,
                plan.PunctChanges, plan.IdChanges, plan.ColorChanges,
                plan.SameCount, plan.MissingCount));
            if (plan.Warnings.Count > 0)
            {
                sb.AppendLine("提示：");
                foreach (string w in plan.Warnings) sb.AppendLine("  - " + w);
            }
            sb.AppendLine();
            sb.AppendLine("目标段落\t序号\t文本\t模板坐标\t原坐标\t写法\t状态");
            foreach (ParaPair p in plan.Pairs)
            {
                if (p.IsMeta)
                {
                    string oldv = p.MetaOld.Length == 0 ? "无" : p.MetaOld;
                    string newv = p.MetaNew.Length == 0 ? "无" : p.MetaNew;
                    sb.AppendLine(string.Format("{0}\t-\t{1}\t-\t-\t-\t{2}",
                        p.TargetBlock.Name,
                        (oldv + " → " + newv).Replace("\t", " "),
                        p.Status));
                    continue;
                }

                if (p.IsDialog)
                {
                    string s = p.SrcDlg != null ? p.SrcDlg.Args : "-";
                    string d = p.DstDlg != null ? p.DstDlg.Args : "-";
                    sb.AppendLine(string.Format("{0}\t框\t{1}\t{2}\t{3}\t-\t{4}",
                        p.TargetBlock.Name, "OPENMERCHANTBIGDLG", s, d, p.Status));
                    continue;
                }

                string sxy = p.Src != null && p.Src.HasCoord ? p.Src.X + ":" + p.Src.Y : "-";
                string dxy = p.Dst != null && p.Dst.HasCoord ? p.Dst.X + ":" + p.Dst.Y : "无";
                string txt = p.Dst != null ? p.Dst.Text : "";
                string amp;
                if (p.Status == "替换") amp = "按钮 → " + (p.Src != null ? p.Src.Name : "imgex");
                else if (p.Status == "补全") amp = (p.Dst != null && p.Dst.IsPlain) ? "文本 → &Text" : "简写 → &Text";
                else if (p.Src != null && p.Dst != null && p.Src.Kind == ElementKind.Text)
                    amp = TextEdits.AmpChange(p.Dst.HasAmp, p.Src.HasAmp);
                else amp = "-";
                sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}",
                    p.TargetBlock.Name,
                    p.Dst != null ? p.Dst.Ordinal + 1 : 0,
                    txt.Replace("\t", " "),
                    sxy, dxy, amp, p.Status));
            }
            return sb.ToString();
        }
    }

    // ---------------------------------------------------------------- 命令行

    internal static class CliRunner
    {
        public static int Run(string[] args)
        {
            string src = null, report = null, template = null;
            bool apply = false;
            bool noDialog = false;
            bool syncPunct = true, syncIds = true, syncColors = false;
            bool para = args.Length > 0 && args[0] == "--cli-para";
            List<string> dsts = new List<string>();
            List<string> files = new List<string>();

            for (int i = 1; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--apply") { apply = true; continue; }
                if (a == "--no-dialog") { noDialog = true; continue; }
                if (a == "--no-punct") { syncPunct = false; continue; }
                if (a == "--no-ids") { syncIds = false; continue; }
                if (a == "--sync-colors") { syncColors = true; continue; }
                if (i + 1 < args.Length)
                {
                    if (a == "--src") { src = args[++i]; continue; }
                    if (a == "--dst") { dsts.Add(args[++i]); continue; }
                    if (a == "--report") { report = args[++i]; continue; }
                    if (a == "--file") { files.Add(args[++i]); continue; }
                    if (a == "--template") { template = args[++i]; continue; }
                }
            }

            StringBuilder outBuf = new StringBuilder();
            int code;
            try
            {
                code = para
                    ? RunParaBatch(files, template, apply, noDialog, syncPunct, syncIds, syncColors, outBuf)
                    : RunCrossBatch(src, dsts, apply, syncPunct, syncIds, syncColors, outBuf);
            }
            catch (Exception ex)
            {
                outBuf.AppendLine("失败：" + ex.Message);
                code = 1;
            }

            if (report != null)
            {
                try { File.WriteAllText(report, outBuf.ToString(), new UTF8Encoding(true)); }
                catch { }
            }
            Console.Out.Write(outBuf.ToString());
            return code;
        }

        private static void TryWriteReport(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(true)); }
            catch { }
        }

        // ---------------- 跨脚本 ----------------

        private static int RunCrossBatch(string src, List<string> dsts, bool apply,
                                        bool syncPunct, bool syncIds, bool syncColors,
                                        StringBuilder outBuf)
        {
            if (src == null || dsts.Count == 0)
            {
                outBuf.AppendLine("参数不足：需要 --src <源脚本> --dst <目标脚本> [--dst <更多目标> …] [--apply] [--report <文件>]");
                return 2;
            }

            if (dsts.Count == 1)
            {
                string only;
                return RunCrossOne(src, dsts[0], apply, outBuf, true, syncPunct, syncIds, syncColors, out only);
            }

            outBuf.AppendLine(string.Format("批量跨脚本覆盖：源 {0}，目标 {1} 个文件", src, dsts.Count));
            int written = 0, same = 0, skipped = 0, failed = 0, ready = 0;

            for (int i = 0; i < dsts.Count; i++)
            {
                outBuf.AppendLine();
                outBuf.AppendLine(string.Format("[{0}/{1}] {2}", i + 1, dsts.Count, dsts[i]));
                StringBuilder one = new StringBuilder();
                string state;
                int c = RunCrossOne(src, dsts[i], apply, one, false, syncPunct, syncIds, syncColors, out state);
                outBuf.Append(one);
                if (state == "写入") written++;
                else if (state == "已一致") same++;
                else if (state == "可覆盖") ready++;
                else if (state == "跳过") skipped++;
                else failed++;
                if (c == 3) failed++;
            }

            outBuf.AppendLine();
            if (apply)
                outBuf.AppendLine(string.Format("汇总：写入 {0}，已一致 {1}，跳过 {2}，失败 {3}",
                    written, same, skipped, failed));
            else
                outBuf.AppendLine(string.Format("汇总：可覆盖 {0}，已一致 {1}，跳过 {2}，失败 {3}",
                    ready, same, skipped, failed));

            return failed > 0 ? 3 : 0;
        }

        private static int RunCrossOne(string src, string dst, bool apply, StringBuilder outBuf,
                                       bool fullReport, bool syncPunct, bool syncIds, bool syncColors,
                                       out string state)
        {
            state = "失败";
            ScanResult r = OverlayEngine.Scan(src, dst, syncPunct, syncIds, syncColors);
            outBuf.AppendLine(string.Format("源标签 {0}，目标标签 {1}，需覆盖 {2}，已一致 {3}，缺失 {4}",
                r.TagA, r.TagB, r.ChangeCount, r.SameCount, r.MissingCount));
            foreach (string w in r.Warnings) outBuf.AppendLine("提示：" + w);

            if (!r.StructureOk)
            {
                outBuf.AppendLine("结构不一致，已跳过（不会强行按顺序对齐）。");
                if (fullReport) outBuf.Append(OverlayEngine.BuildReport(r, false));
                state = "跳过";
                return 2;
            }

            if (!apply)
            {
                state = r.TotalChanges == 0 ? "已一致" : "可覆盖";
                outBuf.AppendLine(state == "已一致" ? "坐标已经一致。" : "可覆盖。");
                if (fullReport) outBuf.Append(OverlayEngine.BuildReport(r, false));
                return 0;
            }

            if (r.ChangeCount == 0)
            {
                outBuf.AppendLine("坐标已经一致，无需写入。");
                state = "已一致";
                return 0;
            }

            string newText = OverlayEngine.BuildNewText(r);
            FileIO.Save(r.Dst, newText, true);
            int bad = OverlayEngine.Verify(r, newText);
            outBuf.AppendLine(bad == 0 ? "写入完成，校验通过。"
                                       : "写入完成，但校验发现 " + bad + " 处仍不一致。");
            string bak = FileIO.FindLatestBackup(dst);
            if (bak != null) outBuf.AppendLine("备份：" + Path.GetFileName(bak));
            TryWriteReport(dst + ".坐标覆盖记录.txt", OverlayEngine.BuildReport(r, true));
            if (fullReport) outBuf.Append(OverlayEngine.BuildReport(r, true));
            state = "写入";
            return bad == 0 ? 0 : 3;
        }

        // ---------------- 同脚本段落 ----------------

        private static int RunParaBatch(List<string> files, string template, bool apply, bool noDialog,
                                        bool syncPunct, bool syncIds, bool syncColors,
                                        StringBuilder outBuf)
        {
            if (files.Count == 0)
            {
                outBuf.AppendLine("参数不足：需要 --file <脚本文件> [--file <更多文件> …] [--template <段落名>] [--apply] [--report <文件>]");
                return 2;
            }

            if (files.Count == 1)
            {
                string only;
                return RunParaOne(files[0], template, apply, outBuf, true, noDialog, syncPunct, syncIds, syncColors, out only);
            }

            outBuf.AppendLine(string.Format(
                "批量同脚本段落覆盖：{0} 个文件（每个文件默认以自身第一个同构段落为模板）", files.Count));
            int written = 0, same = 0, skipped = 0, failed = 0, ready = 0;

            for (int i = 0; i < files.Count; i++)
            {
                outBuf.AppendLine();
                outBuf.AppendLine(string.Format("[{0}/{1}] {2}", i + 1, files.Count, files[i]));
                StringBuilder one = new StringBuilder();
                string state;
                int c = RunParaOne(files[i], template, apply, one, false, noDialog, syncPunct, syncIds, syncColors, out state);
                outBuf.Append(one);
                if (state == "写入") written++;
                else if (state == "已一致") same++;
                else if (state == "可覆盖") ready++;
                else if (state == "跳过") skipped++;
                else failed++;
                if (c == 3) failed++;
            }

            outBuf.AppendLine();
            if (apply)
                outBuf.AppendLine(string.Format("汇总：写入 {0}，已一致 {1}，跳过 {2}，失败 {3}",
                    written, same, skipped, failed));
            else
                outBuf.AppendLine(string.Format("汇总：可覆盖 {0}，已一致 {1}，跳过 {2}，失败 {3}",
                    ready, same, skipped, failed));

            return failed > 0 ? 3 : 0;
        }

        private static int RunParaOne(string file, string template, bool apply, StringBuilder outBuf,
                                      bool fullReport, bool noDialog,
                                      bool syncPunct, bool syncIds, bool syncColors, out string state)
        {
            state = "失败";
            ScriptDoc doc = ScriptParser.Load(file);
            List<ParagraphGroup> groups = TemplateEngine.DetectGroups(doc);
            if (groups.Count == 0)
            {
                outBuf.AppendLine("没有发现结构相同的重复段落。");
                state = "跳过";
                return 2;
            }

            ParagraphGroup group = groups[0];
            for (int i = 0; i < groups.Count; i++)
                if (groups[i].Blocks.Count > group.Blocks.Count) group = groups[i];

            ScriptBlock tpl = group.Blocks[0];
            if (template != null)
            {
                ScriptBlock found = null;
                foreach (ScriptBlock b in group.Blocks)
                {
                    string nm = b.Name.Trim('[', ']', '@');
                    if (nm == template.Trim('[', ']', '@') || b.Name == template) { found = b; break; }
                }
                if (found != null) tpl = found;
                else outBuf.AppendLine("本文件没有模板段落「" + template + "」，改用首段。");
            }

            List<ScriptBlock> targets = new List<ScriptBlock>();
            foreach (ScriptBlock b in group.Blocks) if (b != tpl) targets.Add(b);

            ParaPlan plan = TemplateEngine.BuildPlan(doc, tpl, targets, true, !noDialog, syncPunct, syncIds, syncColors);
            outBuf.AppendLine(string.Format("组内 {0} 段，模板 {1}，目标 {2} 段；需覆盖 {3}，已一致 {4}，跳过 {5}",
                group.Blocks.Count, tpl.Name, targets.Count,
                plan.ChangeCount, plan.SameCount, plan.SkippedBlocks));
            foreach (string w in plan.Warnings) outBuf.AppendLine("提示：" + w);

            if (!apply)
            {
                state = plan.TotalChanges == 0 ? "已一致" : "可覆盖";
                outBuf.AppendLine(state == "已一致" ? "坐标已经一致。" : "可覆盖。");
                if (fullReport) outBuf.Append(TemplateEngine.BuildReport(plan, false));
                return 0;
            }

            if (plan.TotalChanges == 0)
            {
                outBuf.AppendLine("坐标已经一致，无需写入。");
                state = "已一致";
                return 0;
            }

            string newText = TemplateEngine.BuildNewText(plan);
            FileIO.Save(doc, newText, true);
            int bad = TemplateEngine.Verify(plan, newText);
            outBuf.AppendLine(bad == 0 ? "写入完成，校验通过。"
                                       : "写入完成，但校验发现 " + bad + " 处仍不一致。");
            string bak = FileIO.FindLatestBackup(file);
            if (bak != null) outBuf.AppendLine("备份：" + Path.GetFileName(bak));
            TryWriteReport(file + ".段落覆盖记录.txt", TemplateEngine.BuildReport(plan, true));
            if (fullReport) outBuf.Append(TemplateEngine.BuildReport(plan, true));
            state = "写入";
            return bad == 0 ? 0 : 3;
        }
    }

    // ---------------------------------------------------------------- 批量处理

    internal sealed class BatchItem
    {
        public string Path = "";
        public string Scale = "—";
        public int Changes;
        public int Same;
        public int Missing;
        public string Structure = "—";
        public string Result = "待处理";
        public List<string> Warnings = new List<string>();
        public ScanResult CrossPlan;
        public ParaPlan ParaPlan;
    }

    internal sealed class BatchForm : Form
    {
        private readonly bool _cross;
        private TextBox _tbSrc;
        private ListBox _files;
        private DataGridView _grid;
        private TextBox _log;
        private Label _summary;
        private CheckBox _chkBackup;
        private CheckBox _chkAmp;
        private CheckBox _chkDialog;
        private CheckBox _chkPunct;
        private CheckBox _chkIds;
        private CheckBox _chkColors;
        private readonly List<BatchItem> _rows = new List<BatchItem>();

        public BatchForm(bool cross, string source, string currentFile)
        {
            _cross = cross;
            BuildUi();
            if (_cross) _tbSrc.Text = source ?? "";
            if (!string.IsNullOrEmpty(currentFile) && File.Exists(currentFile))
                _files.Items.Add(currentFile);
            UpdateSummary();
        }

        // ---------------------------------------------------------- 界面

        private void BuildUi()
        {
            Text = _cross ? "批量 · 跨脚本覆盖" : "批量 · 同脚本段落覆盖";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(960, 690);
            MinimumSize = new Size(820, 560);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, _cross ? 44F : 32F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 150F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96F));
            Controls.Add(root);

            // 源脚本 / 说明
            if (_cross)
            {
                TableLayoutPanel srcRow = new TableLayoutPanel();
                srcRow.Dock = DockStyle.Fill;
                srcRow.ColumnCount = 3;
                srcRow.RowCount = 1;
                srcRow.Padding = new Padding(12, 6, 12, 2);
                srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110F));
                srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                srcRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88F));
                srcRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
                root.Controls.Add(srcRow, 0, 0);

                Label lb = new Label();
                lb.Text = "源脚本（基准）";
                lb.Dock = DockStyle.Fill;
                lb.TextAlign = ContentAlignment.MiddleLeft;
                srcRow.Controls.Add(lb, 0, 0);

                _tbSrc = new TextBox();
                _tbSrc.Dock = DockStyle.Fill;
                _tbSrc.Margin = new Padding(0, 6, 8, 6);
                srcRow.Controls.Add(_tbSrc, 1, 0);

                Button b = new Button();
                b.Text = "浏览…";
                b.Dock = DockStyle.Fill;
                b.Margin = new Padding(0, 5, 0, 5);
                b.Click += OnPickSource;
                srcRow.Controls.Add(b, 2, 0);
            }
            else
            {
                Label hint = new Label();
                hint.Dock = DockStyle.Fill;
                hint.Text = "每个文件都以自身第一个同构段落为模板，覆盖该文件内其余同构段落。";
                hint.TextAlign = ContentAlignment.MiddleLeft;
                hint.Padding = new Padding(14, 0, 0, 0);
                hint.ForeColor = Color.FromArgb(70, 70, 70);
                root.Controls.Add(hint, 0, 0);
            }

            // 文件列表
            Panel filePanel = new Panel();
            filePanel.Dock = DockStyle.Fill;
            filePanel.Padding = new Padding(12, 0, 12, 4);
            root.Controls.Add(filePanel, 0, 1);

            _files = new ListBox();
            _files.Dock = DockStyle.Fill;
            _files.SelectionMode = SelectionMode.MultiExtended;
            _files.IntegralHeight = false;
            _files.AllowDrop = true;
            _files.DragEnter += delegate(object s, DragEventArgs e)
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            };
            _files.DragDrop += OnDropFiles;
            filePanel.Controls.Add(_files);

            FlowLayoutPanel fileButtons = new FlowLayoutPanel();
            fileButtons.Dock = DockStyle.Right;
            fileButtons.Width = 120;
            fileButtons.FlowDirection = FlowDirection.TopDown;
            fileButtons.WrapContents = false;
            fileButtons.Padding = new Padding(6, 0, 0, 0);
            filePanel.Controls.Add(fileButtons);

            Button bAdd = MakeButton("添加文件…", 106);
            bAdd.Click += OnAddFiles;
            fileButtons.Controls.Add(bAdd);

            Button bRemove = MakeButton("移除选中", 106);
            bRemove.Click += OnRemoveFiles;
            fileButtons.Controls.Add(bRemove);

            Button bClear = MakeButton("清空列表", 106);
            bClear.Click += OnClearFiles;
            fileButtons.Controls.Add(bClear);

            Label lbFiles = new Label();
            lbFiles.Dock = DockStyle.Top;
            lbFiles.Height = 22;
            lbFiles.Text = _cross ? "目标脚本文件（可一次添加多个，支持拖入）"
                                  : "脚本文件（可一次添加多个，支持拖入）";
            lbFiles.TextAlign = ContentAlignment.MiddleLeft;
            filePanel.Controls.Add(lbFiles);

            // 结果表
            _grid = new DataGridView();
            StyleGrid(_grid);
            _grid.Margin = new Padding(12, 0, 12, 0);
            AddColumn(_grid, "文件", 200, true);
            AddColumn(_grid, "规模", 92, false);
            AddColumn(_grid, "需覆盖", 68, false);
            AddColumn(_grid, "已一致", 68, false);
            AddColumn(_grid, "缺失", 56, false);
            AddColumn(_grid, "结构", 86, false);
            AddColumn(_grid, "结果", 150, false);
            root.Controls.Add(_grid, 0, 2);

            // 操作条
            TableLayoutPanel actionBar = new TableLayoutPanel();
            actionBar.Dock = DockStyle.Fill;
            actionBar.ColumnCount = 2;
            actionBar.RowCount = 3;
            actionBar.Padding = new Padding(12, 4, 12, 4);
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 430F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            root.Controls.Add(actionBar, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actionBar.Controls.Add(actions, 0, 0);

            Button btnScan = MakeButton("① 预览全部", 120);
            btnScan.Font = new Font(Font, FontStyle.Bold);
            btnScan.Click += OnPreview;
            actions.Controls.Add(btnScan);

            Button btnApply = MakeButton("② 应用全部", 120);
            btnApply.Font = new Font(Font, FontStyle.Bold);
            btnApply.Click += OnApply;
            actions.Controls.Add(btnApply);

            Button btnClose = MakeButton("关闭", 76);
            btnClose.Click += delegate(object s, EventArgs e) { Close(); };
            actions.Controls.Add(btnClose);

            _summary = new Label();
            _summary.Dock = DockStyle.Fill;
            _summary.AutoSize = false;
            _summary.TextAlign = ContentAlignment.MiddleRight;
            _summary.Margin = new Padding(0, 3, 0, 0);
            _summary.ForeColor = Color.FromArgb(60, 60, 60);
            actionBar.Controls.Add(_summary, 1, 0);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            options.WrapContents = false;
            options.Margin = new Padding(0);
            actionBar.Controls.Add(options, 0, 1);
            actionBar.SetColumnSpan(options, 2);

            _chkBackup = new CheckBox();
            _chkBackup.Text = "写入前自动备份（可恢复）";
            _chkBackup.AutoSize = true;
            _chkBackup.Checked = true;
            _chkBackup.Margin = new Padding(0);
            options.Controls.Add(_chkBackup);

            if (!_cross)
            {
                _chkAmp = new CheckBox();
                _chkAmp.Text = "坐标写法跟随模板（& = 绝对坐标）";
                _chkAmp.AutoSize = true;
                _chkAmp.Checked = true;
                _chkAmp.Margin = new Padding(24, 0, 0, 0);
                options.Controls.Add(_chkAmp);

                _chkDialog = new CheckBox();
                _chkDialog.Text = "同步 OPENMERCHANTBIGDLG 对话框参数";
                _chkDialog.AutoSize = true;
                _chkDialog.Checked = true;
                _chkDialog.Margin = new Padding(24, 0, 0, 0);
                options.Controls.Add(_chkDialog);
            }

            FlowLayoutPanel options2 = new FlowLayoutPanel();
            options2.Dock = DockStyle.Fill;
            options2.FlowDirection = FlowDirection.LeftToRight;
            options2.WrapContents = false;
            options2.Margin = new Padding(0);
            actionBar.Controls.Add(options2, 0, 2);
            actionBar.SetColumnSpan(options2, 2);

            _chkPunct = new CheckBox();
            _chkPunct.Text = "同步标点风格（冒号、斜杠）";
            _chkPunct.AutoSize = true;
            _chkPunct.Checked = true;
            _chkPunct.Margin = new Padding(0);
            options2.Controls.Add(_chkPunct);

            _chkIds = new CheckBox();
            _chkIds.Text = "同步图片编号";
            _chkIds.AutoSize = true;
            _chkIds.Checked = true;
            _chkIds.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_chkIds);

            _chkColors = new CheckBox();
            _chkColors.Text = "同步颜色/选项";
            _chkColors.AutoSize = true;
            _chkColors.Checked = false;
            _chkColors.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_chkColors);

            _log = new TextBox();
            _log.Multiline = true;
            _log.ReadOnly = true;
            _log.ScrollBars = ScrollBars.Vertical;
            _log.Dock = DockStyle.Fill;
            _log.BackColor = Color.FromArgb(250, 250, 250);
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.Font = new Font("Microsoft YaHei UI", 8.5F);
            _log.Margin = new Padding(12, 6, 12, 10);
            root.Controls.Add(_log, 0, 4);
        }

        private Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 28;
            b.Margin = new Padding(0, 2, 6, 4);
            b.FlatStyle = FlatStyle.System;
            return b;
        }

        private void StyleGrid(DataGridView g)
        {
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.ReadOnly = true;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 249);
            g.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            g.ColumnHeadersHeight = 30;
            g.RowTemplate.Height = 24;
            g.GridColor = Color.FromArgb(230, 232, 235);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        }

        private void AddColumn(DataGridView g, string header, int width, bool fill)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.Width = width;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.ReadOnly = true;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(c);
        }

        // ---------------------------------------------------------- 文件列表

        private void OnPickSource(object sender, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "脚本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                d.Title = "选择源脚本（基准）";
                if (d.ShowDialog(this) == DialogResult.OK) _tbSrc.Text = d.FileName;
            }
        }

        private void OnAddFiles(object sender, EventArgs e)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "脚本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                d.Title = "选择要批量处理的脚本（可多选）";
                d.Multiselect = true;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                AddFiles(d.FileNames);
            }
        }

        private void OnDropFiles(object sender, DragEventArgs e)
        {
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null) AddFiles(f);
        }

        private void AddFiles(string[] paths)
        {
            int added = 0;
            foreach (string p in paths)
            {
                if (!File.Exists(p)) continue;
                bool exists = false;
                for (int i = 0; i < _files.Items.Count; i++)
                    if (string.Equals(_files.Items[i].ToString(), p, StringComparison.OrdinalIgnoreCase))
                    { exists = true; break; }
                if (exists) continue;
                _files.Items.Add(p);
                added++;
            }
            if (added > 0) Log("已添加 " + added + " 个文件。");
            UpdateSummary();
        }

        private void OnRemoveFiles(object sender, EventArgs e)
        {
            List<int> idx = new List<int>();
            foreach (int i in _files.SelectedIndices) idx.Add(i);
            idx.Sort();
            for (int i = idx.Count - 1; i >= 0; i--)
            {
                _rows.RemoveAll(delegate(BatchItem x)
                {
                    return string.Equals(x.Path, _files.Items[idx[i]].ToString(),
                                         StringComparison.OrdinalIgnoreCase);
                });
                _files.Items.RemoveAt(idx[i]);
            }
            FillGrid();
            UpdateSummary();
        }

        private void OnClearFiles(object sender, EventArgs e)
        {
            _files.Items.Clear();
            _rows.Clear();
            _grid.Rows.Clear();
            _summary.Text = "";
            _log.Clear();
        }

        // ---------------------------------------------------------- 评估

        private BatchItem Evaluate(string path)
        {
            BatchItem it = new BatchItem();
            it.Path = path;

            try
            {
                if (_cross)
                {
                    if (!File.Exists(_tbSrc.Text))
                    {
                        it.Result = "跳过：未选源脚本";
                        return it;
                    }

                    ScanResult s = OverlayEngine.Scan(_tbSrc.Text, path, _chkPunct.Checked, _chkIds.Checked, _chkColors.Checked);
                    it.CrossPlan = s;
                    it.Scale = "标签 " + s.TagB;
                    it.Changes = s.ChangeCount;
                    it.Same = s.SameCount;
                    it.Missing = s.MissingCount;
                    it.Structure = s.StructureOk ? "正常" : "不一致";
                    it.Warnings = s.Warnings;

                    if (!s.StructureOk) it.Result = "跳过：结构不一致";
                    else if (s.TotalChanges == 0) it.Result = "已一致";
                    else it.Result = "可覆盖";
                }
                else
                {
                    ScriptDoc doc = ScriptParser.Load(path);
                    List<ParagraphGroup> groups = TemplateEngine.DetectGroups(doc);
                    if (groups.Count == 0)
                    {
                        it.Structure = "无重复段";
                        it.Result = "跳过：无重复段落";
                        return it;
                    }

                    ParagraphGroup g = groups[0];
                    for (int i = 0; i < groups.Count; i++)
                        if (groups[i].Blocks.Count > g.Blocks.Count) g = groups[i];

                    List<ScriptBlock> targets = new List<ScriptBlock>();
                    for (int i = 1; i < g.Blocks.Count; i++) targets.Add(g.Blocks[i]);

                    ParaPlan plan = TemplateEngine.BuildPlan(doc, g.Blocks[0], targets, true, _chkDialog.Checked, _chkPunct.Checked, _chkIds.Checked, _chkColors.Checked);
                    it.ParaPlan = plan;
                    it.Scale = "段落 " + g.Blocks.Count;
                    it.Changes = plan.ChangeCount;
                    it.Same = plan.SameCount;
                    it.Missing = plan.MissingCount;
                    it.Structure = plan.SkippedBlocks > 0 ? "部分不同" : "同构";
                    it.Warnings = plan.Warnings;

                    if (plan.TotalChanges == 0) it.Result = "已一致";
                    else it.Result = "可覆盖";
                }
            }
            catch (Exception ex)
            {
                it.Result = "失败：" + ex.Message;
            }
            return it;
        }

        // ---------------------------------------------------------- 预览 / 应用

        private void OnPreview(object sender, EventArgs e)
        {
            if (_files.Items.Count == 0)
            {
                MessageBox.Show(this, "请先添加要处理的脚本文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (_cross && !File.Exists(_tbSrc.Text))
            {
                MessageBox.Show(this, "请先选择存在的源脚本（基准）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _rows.Clear();
                for (int i = 0; i < _files.Items.Count; i++)
                    _rows.Add(Evaluate(_files.Items[i].ToString()));
                FillGrid();
                UpdateSummary();

                foreach (BatchItem it in _rows)
                {
                    if (it.Warnings.Count > 0)
                        foreach (string w in it.Warnings)
                            Log("⚠ " + System.IO.Path.GetFileName(it.Path) + "：" + w);
                }
                Log("预览完成：" + SummaryText());
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "预览失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnApply(object sender, EventArgs e)
        {
            if (_rows.Count == 0) OnPreview(null, null);
            if (_rows.Count == 0) return;

            int ready = 0;
            foreach (BatchItem it in _rows) if (Evaluate(it.Path).Result == "可覆盖") ready++;
            if (ready == 0)
            {
                MessageBox.Show(this, "没有需要写入的文件（可能都已一致或被跳过）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_chkBackup.Checked)
            {
                if (MessageBox.Show(this,
                        "未勾选自动备份，写入后无法用本工具撤销。\n确实要写入 " + ready + " 个文件吗？",
                        "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            int written = 0, failed = 0, badCount = 0;
            try
            {
                Cursor = Cursors.WaitCursor;
                foreach (BatchItem it in _rows)
                {
                    BatchItem fresh = Evaluate(it.Path);
                    CopyState(it, fresh);
                    if (fresh.Result != "可覆盖") continue;

                    try
                    {
                        int bad = Write(fresh);
                        written++;
                        if (bad > 0) { badCount++; }
                        CopyState(it, Evaluate(it.Path));
                        it.Result = bad == 0 ? "已写入" : "已写入（校验异常）";
                        Log("✔ " + Path.GetFileName(it.Path) + "：写入 " +
                            fresh.Changes + " 处" + (bad == 0 ? "，校验通过。" : "，校验异常 " + bad + " 处。"));
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        it.Result = "失败：" + ex.Message;
                        Log("✘ " + Path.GetFileName(it.Path) + "：" + ex.Message);
                    }
                }
            }
            finally
            {
                Cursor = Cursors.Default;
            }

            FillGrid();
            UpdateSummary();
            Log(string.Format("批量完成：写入 {0}，失败 {1}{2}。",
                written, failed, badCount > 0 ? "，校验异常 " + badCount : ""));
        }

        private static void CopyState(BatchItem dst, BatchItem src)
        {
            dst.Scale = src.Scale;
            dst.Changes = src.Changes;
            dst.Same = src.Same;
            dst.Missing = src.Missing;
            dst.Structure = src.Structure;
            dst.Result = src.Result;
            dst.Warnings = src.Warnings;
            dst.CrossPlan = src.CrossPlan;
            dst.ParaPlan = src.ParaPlan;
        }

        private int Write(BatchItem it)
        {
            if (_cross)
            {
                ScanResult s = it.CrossPlan;
                string newText = OverlayEngine.BuildNewText(s);
                FileIO.Save(s.Dst, newText, _chkBackup.Checked);
                int bad = OverlayEngine.Verify(s, newText);
                TryWriteFile(s.Dst.Path + ".坐标覆盖记录.txt", OverlayEngine.BuildReport(s, true));
                string bak = FileIO.FindLatestBackup(it.Path);
                if (bak != null) Log("   备份：" + Path.GetFileName(bak));
                return bad;
            }
            else
            {
                ParaPlan p = it.ParaPlan;
                string newText = TemplateEngine.BuildNewText(p);
                FileIO.Save(p.Doc, newText, _chkBackup.Checked);
                int bad = TemplateEngine.Verify(p, newText);
                TryWriteFile(p.Doc.Path + ".段落覆盖记录.txt", TemplateEngine.BuildReport(p, true));
                string bak = FileIO.FindLatestBackup(it.Path);
                if (bak != null) Log("   备份：" + Path.GetFileName(bak));
                return bad;
            }
        }

        private static void TryWriteFile(string path, string text)
        {
            try { File.WriteAllText(path, text, new UTF8Encoding(true)); }
            catch { }
        }

        // ---------------------------------------------------------- 表格

        private void FillGrid()
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (BatchItem it in _rows)
            {
                int idx = _grid.Rows.Add(Path.GetFileName(it.Path), it.Scale,
                    it.Changes, it.Same, it.Missing, it.Structure, it.Result);
                DataGridViewRow row = _grid.Rows[idx];
                row.Cells[0].ToolTipText = it.Path;

                if (it.Result == "可覆盖") row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                else if (it.Result == "已写入") row.DefaultCellStyle.BackColor = Color.FromArgb(226, 244, 226);
                else if (it.Result.StartsWith("跳过") || it.Result.StartsWith("失败"))
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 228, 228);
                else row.DefaultCellStyle.ForeColor = Color.FromArgb(110, 110, 110);

                if (it.Warnings.Count > 0)
                    row.Cells[6].ToolTipText = string.Join("\n", it.Warnings.ToArray());
            }
            _grid.ClearSelection();
            _grid.ResumeLayout();
        }

        private string SummaryText()
        {
            int ready = 0, same = 0, skip = 0, fail = 0, done = 0;
            foreach (BatchItem it in _rows)
            {
                if (it.Result == "可覆盖") ready++;
                else if (it.Result == "已一致") same++;
                else if (it.Result == "已写入") done++;
                else if (it.Result.StartsWith("跳过")) skip++;
                else fail++;
            }
            return string.Format("共 {0} 个文件 · 可覆盖 {1} · 已写入 {2} · 已一致 {3} · 跳过 {4} · 失败 {5}",
                _rows.Count, ready, done, same, skip, fail);
        }

        private void UpdateSummary()
        {
            _summary.Text = _rows.Count > 0 ? SummaryText()
                : "共 " + _files.Items.Count + " 个文件 · 尚未预览";
            _summary.ForeColor = Color.FromArgb(60, 60, 60);
        }

        private void Log(string msg)
        {
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }
    }
    // ---------------------------------------------------------------- 界面

    internal sealed class MainForm : Form
    {
        // 跨脚本页
        private TextBox _tbSrc;
        private TextBox _tbDst;
        private TextBox _log;
        private DataGridView _grid;
        private Label _lblSummary;
        private CheckBox _chkLax;
        private CheckBox _chkBackup;
        private CheckBox _cChkPunct;
        private CheckBox _cChkIds;
        private CheckBox _cChkColors;
        private Button _btnScan;
        private Button _btnApply;
        private ScanResult _result;
        private string _scannedSrc;
        private string _scannedDst;

        // 同脚本段落页
        private TextBox _pFile;
        private ComboBox _pGroup;
        private ComboBox _pTemplate;
        private CheckedListBox _pTargets;
        private DataGridView _pGrid;
        private TextBox _pLog;
        private Label _pSummary;
        private CheckBox _pChkAmp;
        private CheckBox _pChkDialog;
        private CheckBox _pChkPunct;
        private CheckBox _pChkIds;
        private CheckBox _pChkColors;
        private CheckBox _pBackup;
        private ScriptDoc _pDoc;
        private List<ParagraphGroup> _pGroups = new List<ParagraphGroup>();
        private bool _pUpdating;

        private static string SettingsPath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                 "ScriptCoordOverlay"),
                    "settings.txt");
            }
        }

        private TabControl _tabs;

        public MainForm(bool paraTab)
        {
            // 窗口/任务栏图标取自 exe 自身嵌入的图标（build.ps1 的 /win32icon）
            try { Icon = System.Drawing.Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            BuildUi();
            if (paraTab) _tabs.SelectedIndex = 1;
            LoadSettings();
            AutoFillDefaults();
        }

        // ---------------------------------------------------------- 骨架

        private void BuildUi()
        {
            Text = "传奇脚本同步器";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1060, 760);
            MinimumSize = new Size(1000, 620);
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            AutoScaleMode = AutoScaleMode.Dpi;

            TabControl tabs = new TabControl();
            _tabs = tabs;
            tabs.Dock = DockStyle.Fill;
            tabs.Font = Font;

            TabPage cross = new TabPage("跨脚本覆盖");
            cross.BackColor = Color.White;
            cross.Padding = new Padding(0);
            cross.Controls.Add(BuildCrossPanel());

            TabPage para = new TabPage("同脚本段落覆盖");
            para.BackColor = Color.White;
            para.Padding = new Padding(0);
            para.Controls.Add(BuildParaPanel());

            tabs.TabPages.Add(cross);
            tabs.TabPages.Add(para);
            Controls.Add(tabs);

            KeyPreview = true;
            KeyDown += OnKeyDown;
            FormClosing += OnFormClosing;
        }

        private Button MakeButton(string text, int width)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Height = 30;
            b.Margin = new Padding(0, 3, 8, 0);
            b.FlatStyle = FlatStyle.System;
            return b;
        }

        private void StyleGrid(DataGridView g)
        {
            g.Dock = DockStyle.Fill;
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.ReadOnly = true;
            g.RowHeadersVisible = false;
            g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            g.MultiSelect = false;
            g.BackgroundColor = Color.White;
            g.BorderStyle = BorderStyle.None;
            g.EnableHeadersVisualStyles = false;
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(244, 246, 249);
            g.ColumnHeadersDefaultCellStyle.Font = new Font(Font, FontStyle.Bold);
            g.ColumnHeadersHeight = 30;
            g.RowTemplate.Height = 24;
            g.GridColor = Color.FromArgb(230, 232, 235);
            g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None;
        }

        private void AddColumn(DataGridView g, string header, int width, bool fill)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header;
            c.Width = width;
            c.SortMode = DataGridViewColumnSortMode.NotSortable;
            c.ReadOnly = true;
            if (fill) c.AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
            g.Columns.Add(c);
        }

        private static string Shorten(string s, int max)
        {
            s = s.Replace("\r", " ").Replace("\n", " ").Replace("\\", " ");
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        // ---------------------------------------------------------- 跨脚本页

        private TableLayoutPanel BuildCrossPanel()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 102F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104F));

            TableLayoutPanel top = new TableLayoutPanel();
            top.Dock = DockStyle.Fill;
            top.ColumnCount = 3;
            top.RowCount = 2;
            top.Padding = new Padding(12, 8, 12, 2);
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            top.RowStyles.Add(new RowStyle(SizeType.Percent, 50F));
            root.Controls.Add(top, 0, 0);

            top.Controls.Add(MakeLabel("源脚本（基准坐标）"), 0, 0);

            _tbSrc = new TextBox();
            _tbSrc.Dock = DockStyle.Fill;
            _tbSrc.Margin = new Padding(0, 8, 8, 8);
            _tbSrc.AllowDrop = true;
            _tbSrc.DragEnter += OnDragEnter;
            _tbSrc.DragDrop += OnDropSrc;
            _tbSrc.TextChanged += OnPathTextChanged;
            top.Controls.Add(_tbSrc, 1, 0);

            Button btnSrc = MakeBrowseButton();
            btnSrc.Margin = new Padding(0, 7, 0, 7);
            btnSrc.Click += OnPickSrc;
            top.Controls.Add(btnSrc, 2, 0);

            top.Controls.Add(MakeLabel("目标脚本（被覆盖）"), 0, 1);

            _tbDst = new TextBox();
            _tbDst.Dock = DockStyle.Fill;
            _tbDst.Margin = new Padding(0, 8, 8, 8);
            _tbDst.AllowDrop = true;
            _tbDst.DragEnter += OnDragEnter;
            _tbDst.DragDrop += OnDropDst;
            _tbDst.TextChanged += OnPathTextChanged;
            top.Controls.Add(_tbDst, 1, 1);

            Button btnDst = MakeBrowseButton();
            btnDst.Margin = new Padding(0, 7, 0, 7);
            btnDst.Click += OnPickDst;
            top.Controls.Add(btnDst, 2, 1);

            TableLayoutPanel actionBar = new TableLayoutPanel();
            actionBar.Dock = DockStyle.Fill;
            actionBar.ColumnCount = 2;
            actionBar.RowCount = 3;
            actionBar.Padding = new Padding(12, 5, 12, 5);
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 380F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            root.Controls.Add(actionBar, 0, 1);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actionBar.Controls.Add(actions, 0, 0);

            Button btnSwap = MakeButton("⇄ 交换源/目标", 132);
            btnSwap.Click += OnSwap;
            actions.Controls.Add(btnSwap);

            _btnScan = MakeButton("① 预览差异", 120);
            _btnScan.Font = new Font(Font, FontStyle.Bold);
            _btnScan.Click += OnScan;
            actions.Controls.Add(_btnScan);

            _btnApply = MakeButton("② 应用覆盖", 120);
            _btnApply.Font = new Font(Font, FontStyle.Bold);
            _btnApply.Click += OnApply;
            actions.Controls.Add(_btnApply);

            Button btnRestore = MakeButton("恢复备份", 96);
            btnRestore.Click += OnRestore;
            actions.Controls.Add(btnRestore);

            Button btnBatchCross = MakeButton("批量…", 84);
            btnBatchCross.Click += OnBatchCross;
            actions.Controls.Add(btnBatchCross);

            _lblSummary = new Label();
            _lblSummary.Dock = DockStyle.Fill;
            _lblSummary.AutoSize = false;
            _lblSummary.TextAlign = ContentAlignment.MiddleRight;
            _lblSummary.Margin = new Padding(0, 3, 0, 0);
            _lblSummary.ForeColor = Color.FromArgb(60, 60, 60);
            actionBar.Controls.Add(_lblSummary, 1, 0);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            options.WrapContents = false;
            options.Margin = new Padding(0);
            actionBar.Controls.Add(options, 0, 1);
            actionBar.SetColumnSpan(options, 2);

            _chkLax = new CheckBox();
            _chkLax.Text = "结构不一致时仍按顺序覆盖";
            _chkLax.AutoSize = true;
            _chkLax.Margin = new Padding(0);
            _chkLax.CheckedChanged += OnPathTextChanged;
            options.Controls.Add(_chkLax);

            _chkBackup = new CheckBox();
            _chkBackup.Text = "写入前自动备份（可恢复）";
            _chkBackup.AutoSize = true;
            _chkBackup.Checked = true;
            _chkBackup.Margin = new Padding(24, 0, 0, 0);
            options.Controls.Add(_chkBackup);

            FlowLayoutPanel options2 = new FlowLayoutPanel();
            options2.Dock = DockStyle.Fill;
            options2.FlowDirection = FlowDirection.LeftToRight;
            options2.WrapContents = false;
            options2.Margin = new Padding(0);
            actionBar.Controls.Add(options2, 0, 2);
            actionBar.SetColumnSpan(options2, 2);

            _cChkPunct = new CheckBox();
            _cChkPunct.Text = "同步标点风格（冒号、斜杠）";
            _cChkPunct.AutoSize = true;
            _cChkPunct.Checked = true;
            _cChkPunct.Margin = new Padding(0);
            options2.Controls.Add(_cChkPunct);

            _cChkIds = new CheckBox();
            _cChkIds.Text = "同步图片编号";
            _cChkIds.AutoSize = true;
            _cChkIds.Checked = true;
            _cChkIds.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_cChkIds);

            _cChkColors = new CheckBox();
            _cChkColors.Text = "同步颜色/选项";
            _cChkColors.AutoSize = true;
            _cChkColors.Checked = false;
            _cChkColors.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_cChkColors);

            _grid = new DataGridView();
            StyleGrid(_grid);
            _grid.Margin = new Padding(12, 0, 12, 0);
            AddColumn(_grid, "序号", 50, false);
            AddColumn(_grid, "区块", 168, false);
            AddColumn(_grid, "行", 46, false);
            AddColumn(_grid, "文本内容（源脚本）", 100, true);
            AddColumn(_grid, "源坐标", 76, false);
            AddColumn(_grid, "目标坐标", 76, false);
            AddColumn(_grid, "写法", 66, false);
            AddColumn(_grid, "状态", 62, false);
            root.Controls.Add(_grid, 0, 2);

            _log = MakeLog();
            _log.Margin = new Padding(12, 8, 12, 12);
            root.Controls.Add(_log, 0, 3);

            return root;
        }

        private Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Fill;
            l.TextAlign = ContentAlignment.MiddleLeft;
            return l;
        }

        private Button MakeBrowseButton()
        {
            Button b = new Button();
            b.Text = "浏览…";
            b.Dock = DockStyle.Fill;
            return b;
        }

        private TextBox MakeLog()
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.Dock = DockStyle.Fill;
            t.BackColor = Color.FromArgb(250, 250, 250);
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Font = new Font("Microsoft YaHei UI", 8.5F);
            return t;
        }

        // ---------------------------------------------------------- 段落页

        private TableLayoutPanel BuildParaPanel()
        {
            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44F));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 100F));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 92F));

            // 文件行
            TableLayoutPanel fileRow = new TableLayoutPanel();
            fileRow.Dock = DockStyle.Fill;
            fileRow.ColumnCount = 3;
            fileRow.RowCount = 1;
            fileRow.Padding = new Padding(12, 8, 12, 4);
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84F));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92F));
            fileRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            root.Controls.Add(fileRow, 0, 0);

            fileRow.Controls.Add(MakeLabel("脚本文件"), 0, 0);

            _pFile = new TextBox();
            _pFile.Dock = DockStyle.Fill;
            _pFile.Margin = new Padding(0, 8, 8, 8);
            _pFile.AllowDrop = true;
            _pFile.DragEnter += OnDragEnter;
            _pFile.DragDrop += OnDropParaFile;
            _pFile.TextChanged += OnParaFileTextChanged;
            fileRow.Controls.Add(_pFile, 1, 0);

            Button btnFile = MakeBrowseButton();
            btnFile.Margin = new Padding(0, 7, 0, 7);
            btnFile.Click += OnPickPFile;
            fileRow.Controls.Add(btnFile, 2, 0);

            // 组 / 模板行
            FlowLayoutPanel pick = new FlowLayoutPanel();
            pick.Dock = DockStyle.Fill;
            pick.FlowDirection = FlowDirection.LeftToRight;
            pick.WrapContents = false;
            pick.Padding = new Padding(12, 4, 12, 0);
            root.Controls.Add(pick, 0, 1);

            Label lbGroup = new Label();
            lbGroup.Text = "同构段落组：";
            lbGroup.AutoSize = true;
            lbGroup.Margin = new Padding(0, 8, 4, 0);
            pick.Controls.Add(lbGroup);

            _pGroup = new ComboBox();
            _pGroup.DropDownStyle = ComboBoxStyle.DropDownList;
            _pGroup.Width = 340;
            _pGroup.Margin = new Padding(0, 4, 16, 0);
            _pGroup.SelectedIndexChanged += OnGroupChanged;
            pick.Controls.Add(_pGroup);

            Label lbTpl = new Label();
            lbTpl.Text = "模板段落：";
            lbTpl.AutoSize = true;
            lbTpl.Margin = new Padding(0, 8, 4, 0);
            pick.Controls.Add(lbTpl);

            _pTemplate = new ComboBox();
            _pTemplate.DropDownStyle = ComboBoxStyle.DropDownList;
            _pTemplate.Width = 200;
            _pTemplate.Margin = new Padding(0, 4, 16, 0);
            _pTemplate.SelectedIndexChanged += OnTemplateChanged;
            pick.Controls.Add(_pTemplate);

            // 内容区：左目标列表 + 右预览表
            TableLayoutPanel body = new TableLayoutPanel();
            body.Dock = DockStyle.Fill;
            body.ColumnCount = 2;
            body.RowCount = 1;
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 236F));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            body.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            body.Padding = new Padding(12, 0, 12, 0);
            root.Controls.Add(body, 0, 2);

            Panel left = new Panel();
            left.Dock = DockStyle.Fill;
            left.Margin = new Padding(0, 0, 10, 0);

            _pTargets = new CheckedListBox();
            _pTargets.Dock = DockStyle.Fill;
            _pTargets.CheckOnClick = true;
            _pTargets.IntegralHeight = false;
            _pTargets.BorderStyle = BorderStyle.FixedSingle;
            _pTargets.ItemCheck += OnTargetItemCheck;
            left.Controls.Add(_pTargets);

            Label lbTargets = new Label();
            lbTargets.Text = "目标段落（勾选要覆盖的）";
            lbTargets.Dock = DockStyle.Top;
            lbTargets.Height = 24;
            lbTargets.TextAlign = ContentAlignment.MiddleLeft;
            left.Controls.Add(lbTargets);
            body.Controls.Add(left, 0, 0);

            _pGrid = new DataGridView();
            StyleGrid(_pGrid);
            _pGrid.Margin = new Padding(0);
            AddColumn(_pGrid, "目标段落", 140, false);
            AddColumn(_pGrid, "序号", 46, false);
            AddColumn(_pGrid, "文本内容（目标脚本）", 100, true);
            AddColumn(_pGrid, "模板坐标", 168, false);
            AddColumn(_pGrid, "原坐标", 168, false);
            AddColumn(_pGrid, "写法", 104, false);
            AddColumn(_pGrid, "状态", 62, false);
            body.Controls.Add(_pGrid, 1, 0);

            // 操作条
            TableLayoutPanel actionBar = new TableLayoutPanel();
            actionBar.Dock = DockStyle.Fill;
            actionBar.ColumnCount = 2;
            actionBar.RowCount = 3;
            actionBar.Padding = new Padding(12, 4, 12, 4);
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 34F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            actionBar.RowStyles.Add(new RowStyle(SizeType.Absolute, 26F));
            root.Controls.Add(actionBar, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.Margin = new Padding(0);
            actionBar.Controls.Add(actions, 0, 0);

            Button btnScan = MakeButton("① 预览差异", 120);
            btnScan.Font = new Font(Font, FontStyle.Bold);
            btnScan.Click += OnParaScan;
            actions.Controls.Add(btnScan);

            Button btnApply = MakeButton("② 应用覆盖", 120);
            btnApply.Font = new Font(Font, FontStyle.Bold);
            btnApply.Click += OnParaApply;
            actions.Controls.Add(btnApply);

            Button btnRestore = MakeButton("恢复备份", 96);
            btnRestore.Click += OnParaRestore;
            actions.Controls.Add(btnRestore);

            Button btnAll = MakeButton("全选", 72);
            btnAll.Click += OnSelectAll;
            actions.Controls.Add(btnAll);

            Button btnNone = MakeButton("全不选", 80);
            btnNone.Click += OnSelectNone;
            actions.Controls.Add(btnNone);

            Button btnBatchPara = MakeButton("批量…", 84);
            btnBatchPara.Click += OnBatchPara;
            actions.Controls.Add(btnBatchPara);

            _pSummary = new Label();
            _pSummary.Dock = DockStyle.Fill;
            _pSummary.AutoSize = false;
            _pSummary.TextAlign = ContentAlignment.MiddleRight;
            _pSummary.Margin = new Padding(0, 3, 0, 0);
            _pSummary.ForeColor = Color.FromArgb(60, 60, 60);
            actionBar.Controls.Add(_pSummary, 1, 0);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.FlowDirection = FlowDirection.LeftToRight;
            options.WrapContents = false;
            options.Margin = new Padding(0);
            actionBar.Controls.Add(options, 0, 1);
            actionBar.SetColumnSpan(options, 2);

            _pChkAmp = new CheckBox();
            _pChkAmp.Text = "坐标写法跟随模板（& = 绝对坐标）";
            _pChkAmp.AutoSize = true;
            _pChkAmp.Checked = true;
            _pChkAmp.Margin = new Padding(0);
            options.Controls.Add(_pChkAmp);

            _pChkDialog = new CheckBox();
            _pChkDialog.Text = "同步 OPENMERCHANTBIGDLG 对话框参数";
            _pChkDialog.AutoSize = true;
            _pChkDialog.Checked = true;
            _pChkDialog.Margin = new Padding(24, 0, 0, 0);
            options.Controls.Add(_pChkDialog);

            _pBackup = new CheckBox();
            _pBackup.Text = "写入前自动备份（可恢复）";
            _pBackup.AutoSize = true;
            _pBackup.Checked = true;
            _pBackup.Margin = new Padding(24, 0, 0, 0);
            options.Controls.Add(_pBackup);

            FlowLayoutPanel options2 = new FlowLayoutPanel();
            options2.Dock = DockStyle.Fill;
            options2.FlowDirection = FlowDirection.LeftToRight;
            options2.WrapContents = false;
            options2.Margin = new Padding(0);
            actionBar.Controls.Add(options2, 0, 2);
            actionBar.SetColumnSpan(options2, 2);

            _pChkPunct = new CheckBox();
            _pChkPunct.Text = "同步标点风格（冒号、斜杠）";
            _pChkPunct.AutoSize = true;
            _pChkPunct.Checked = true;
            _pChkPunct.Margin = new Padding(0);
            options2.Controls.Add(_pChkPunct);

            _pChkIds = new CheckBox();
            _pChkIds.Text = "同步图片编号";
            _pChkIds.AutoSize = true;
            _pChkIds.Checked = true;
            _pChkIds.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_pChkIds);

            _pChkColors = new CheckBox();
            _pChkColors.Text = "同步颜色/选项";
            _pChkColors.AutoSize = true;
            _pChkColors.Checked = false;
            _pChkColors.Margin = new Padding(24, 0, 0, 0);
            options2.Controls.Add(_pChkColors);

            _pLog = MakeLog();
            _pLog.Margin = new Padding(12, 6, 12, 10);
            root.Controls.Add(_pLog, 0, 4);

            return root;
        }

        // ---------------------------------------------------------- 通用事件

        private void OnDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F5) { OnScan(null, null); e.Handled = true; }
            if (e.Control && e.KeyCode == Keys.Enter) { OnApply(null, null); e.Handled = true; }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            SaveSettings();
        }

        private string PickFile(string current)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Filter = "脚本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*";
                d.Title = "选择脚本文件";
                try
                {
                    if (!string.IsNullOrEmpty(current))
                        d.InitialDirectory = Path.GetDirectoryName(current);
                }
                catch { }
                return d.ShowDialog(this) == DialogResult.OK ? d.FileName : null;
            }
        }

        // ---------------------------------------------------------- 跨脚本：事件

        private void OnDropSrc(object sender, DragEventArgs e)
        {
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null && f.Length > 0) { _tbSrc.Text = f[0]; AutoScan(); }
        }

        private void OnDropDst(object sender, DragEventArgs e)
        {
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null && f.Length > 0) { _tbDst.Text = f[0]; AutoScan(); }
        }

        private void OnPickSrc(object sender, EventArgs e)
        {
            string f = PickFile(_tbSrc.Text);
            if (f != null) { _tbSrc.Text = f; AutoScan(); }
        }

        private void OnPickDst(object sender, EventArgs e)
        {
            string f = PickFile(_tbDst.Text);
            if (f != null) { _tbDst.Text = f; AutoScan(); }
        }

        private void OnSwap(object sender, EventArgs e)
        {
            string t = _tbSrc.Text;
            _tbSrc.Text = _tbDst.Text;
            _tbDst.Text = t;
            AutoScan();
        }

        private void OnPathTextChanged(object sender, EventArgs e)
        {
            _result = null;
            _lblSummary.Text = "";
        }

        private void AutoScan()
        {
            if (File.Exists(_tbSrc.Text) && File.Exists(_tbDst.Text)) OnScan(null, null);
        }

        private bool PathsValid(bool quiet)
        {
            if (!File.Exists(_tbSrc.Text))
            {
                if (!quiet) MessageBox.Show(this, "请选择存在的源脚本文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (!File.Exists(_tbDst.Text))
            {
                if (!quiet) MessageBox.Show(this, "请选择存在的目标脚本文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (string.Equals(Path.GetFullPath(_tbSrc.Text), Path.GetFullPath(_tbDst.Text),
                              StringComparison.OrdinalIgnoreCase))
            {
                if (!quiet) MessageBox.Show(this, "源脚本与目标脚本不能是同一个文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private void OnScan(object sender, EventArgs e)
        {
            if (!PathsValid(false)) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                _result = OverlayEngine.Scan(_tbSrc.Text, _tbDst.Text, _cChkPunct.Checked, _cChkIds.Checked, _cChkColors.Checked);
                _scannedSrc = _tbSrc.Text;
                _scannedDst = _tbDst.Text;
                FillGrid(_result);
                Log(string.Format("扫描完成：源 {0} 个标签 / 目标 {1} 个标签；需覆盖 {2} 处，已一致 {3} 处。",
                    _result.TagA, _result.TagB, _result.ChangeCount, _result.SameCount));
                foreach (string w in _result.Warnings) Log("⚠ " + w);
                if (!_result.StructureOk && !_chkLax.Checked)
                    Log("结构不一致：默认不允许写入，请核对预览；确需强行按顺序覆盖请勾选左侧选项。");
            }
            catch (Exception ex)
            {
                _result = null;
                MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void FillGrid(ScanResult r)
        {
            _grid.SuspendLayout();
            _grid.Rows.Clear();
            foreach (CoordPair p in r.Pairs)
            {
                if (p.IsMeta)
                {
                    string detail = p.Status == "颜色"
                        ? (p.MetaOld.Length == 0 ? "无" : p.MetaOld) + " → " + (p.MetaNew.Length == 0 ? "无" : p.MetaNew)
                        : Shorten(p.MetaOld, 30) + " → " + Shorten(p.MetaNew, 30);
                    int mi = _grid.Rows.Add(p.Index, p.BlockName, "—", detail, "—", "—", "—", p.Status);
                    _grid.Rows[mi].DefaultCellStyle.BackColor = Color.FromArgb(228, 240, 255);
                    _grid.Rows[mi].Cells[3].ToolTipText = p.MetaOld + "  →  " + p.MetaNew;
                    continue;
                }

                if (p.IsDialog)
                {
                    string sa = p.SrcDlg != null ? p.SrcDlg.Args : "—";
                    string da = p.DstDlg != null ? p.DstDlg.Args : "—";
                    int di = _grid.Rows.Add(p.Index, p.BlockName, "框", "OPENMERCHANTBIGDLG", sa, da, "—", p.Status);
                    DataGridViewRow drow = _grid.Rows[di];
                    if (p.Status == "覆盖") drow.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                    else drow.DefaultCellStyle.ForeColor = Color.FromArgb(110, 110, 110);
                    drow.Cells[4].ToolTipText = sa;
                    drow.Cells[5].ToolTipText = da;
                    continue;
                }

                string text = p.Src != null ? Shorten(p.Src.Text, 60) : "";
                string sx = p.Src != null && p.Src.HasCoord ? p.Src.X + " : " + p.Src.Y : "—";
                string dx = p.Dst != null && p.Dst.HasCoord ? p.Dst.X + " : " + p.Dst.Y : "—";
                string amp = p.Src != null && p.Dst != null
                    ? TextEdits.AmpChange(p.Dst.HasAmp, p.Src.HasAmp) : "—";
                int line = p.Src != null ? p.Src.LineNo : (p.Dst != null ? p.Dst.LineNo : 0);
                int idx = _grid.Rows.Add(p.Index, p.BlockName, line, text, sx, dx, amp, p.Status);

                DataGridViewRow row = _grid.Rows[idx];
                if (p.Status == "覆盖" || p.Status == "补全" || p.Status == "替换")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                else if (p.Status == "缺失") row.DefaultCellStyle.BackColor = Color.FromArgb(255, 228, 228);
                else if (p.Status == "种类不同") row.DefaultCellStyle.BackColor = Color.FromArgb(255, 226, 196);
                else row.DefaultCellStyle.ForeColor = Color.FromArgb(110, 110, 110);

                row.Cells[3].ToolTipText = p.Src != null ? p.Src.Raw : "";
                if (p.Dst != null) row.Cells[5].ToolTipText = p.Dst.Raw;
            }
            _grid.ClearSelection();
            _grid.ResumeLayout();

            string punctNote = r.PunctChanges > 0 ? " · 标点 " + r.PunctChanges : "";
            string idNote = r.IdChanges > 0 ? " · 编号 " + r.IdChanges : "";
            string colorNote = r.ColorChanges > 0 ? " · 颜色 " + r.ColorChanges : "";
            _lblSummary.Text = string.Format("共 {0} 行 · 覆盖 {1} · 一致 {2} · 缺失 {3}{4}{5}{6}",
                r.Pairs.Count, r.ChangeCount, r.SameCount, r.MissingCount,
                punctNote, idNote, colorNote);
            _lblSummary.ForeColor = r.MissingCount > 0 || !r.StructureOk
                ? Color.FromArgb(190, 40, 40) : Color.FromArgb(60, 60, 60);
        }

        private void OnApply(object sender, EventArgs e)
        {
            if (!PathsValid(false)) return;

            if (_result == null || _scannedSrc != _tbSrc.Text || _scannedDst != _tbDst.Text)
                OnScan(null, null);
            if (_result == null) return;

            if (!_result.StructureOk && !_chkLax.Checked)
            {
                MessageBox.Show(this,
                    "两个脚本的区块/标签结构不一致，为避免坐标错位已停止。\n\n" +
                    string.Join("\n", _result.Warnings.ToArray()),
                    "结构不一致", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_result.TotalChanges == 0)
            {
                MessageBox.Show(this, "所有坐标已经一致，无需覆盖。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_chkBackup.Checked)
            {
                if (MessageBox.Show(this,
                        "未勾选自动备份，写入后无法用本工具撤销。\n确实要直接写入吗？",
                        "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                string newText = OverlayEngine.BuildNewText(_result);
                FileIO.Save(_result.Dst, newText, _chkBackup.Checked);
                int bad = OverlayEngine.Verify(_result, newText);

                Log(string.Format("已写入 {0}：覆盖 {1} 处坐标。", Path.GetFileName(_tbDst.Text),
                    _result.ChangeCount));
                if (_chkBackup.Checked) Log("已备份原文件：" + Path.GetFileName(
                    FileIO.FindLatestBackup(_tbDst.Text)));
                if (bad == 0) Log("校验通过：全部坐标与源脚本一致。");
                else Log("⚠ 校验发现 " + bad + " 处仍不一致，请检查。");

                string reportPath = _tbDst.Text + ".坐标覆盖记录.txt";
                try
                {
                    File.WriteAllText(reportPath, OverlayEngine.BuildReport(_result, true),
                        new UTF8Encoding(true));
                    Log("记录已保存：" + Path.GetFileName(reportPath));
                }
                catch { }

                OnScan(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    ex.Message + "\n\n若目标文件正在脚本编辑器中打开，请先关闭它再试。",
                    "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnRestore(object sender, EventArgs e)
        {
            if (!File.Exists(_tbDst.Text))
            {
                MessageBox.Show(this, "请先选择目标脚本（要恢复的那个文件）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string bak = FileIO.FindLatestBackup(_tbDst.Text);
            if (bak == null)
            {
                MessageBox.Show(this, "目标脚本旁边没有找到本工具生成的备份文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this,
                    "将用备份恢复目标脚本：\n" + Path.GetFileName(bak) + "\n\n继续吗？",
                    "恢复备份", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                File.Copy(bak, _tbDst.Text, true);
                Log("已从备份恢复：" + Path.GetFileName(bak));
                OnScan(null, null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------- 批量入口

        private void OnBatchCross(object sender, EventArgs e)
        {
            using (BatchForm f = new BatchForm(true, _tbSrc.Text, null))
                f.ShowDialog(this);
            if (File.Exists(_tbSrc.Text) && File.Exists(_tbDst.Text)) OnScan(null, null);
        }

        private void OnBatchPara(object sender, EventArgs e)
        {
            using (BatchForm f = new BatchForm(false, null, _pFile.Text))
                f.ShowDialog(this);
            if (File.Exists(_pFile.Text)) LoadParaFile();
        }

        // ---------------------------------------------------------- 段落页：事件

        private void OnDropParaFile(object sender, DragEventArgs e)
        {
            string[] f = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (f != null && f.Length > 0) { _pFile.Text = f[0]; }
        }

        private void OnPickPFile(object sender, EventArgs e)
        {
            string f = PickFile(_pFile.Text);
            if (f != null) _pFile.Text = f;
        }

        private void OnParaFileTextChanged(object sender, EventArgs e)
        {
            _pDoc = null;
            _pGroups = new List<ParagraphGroup>();
            _pGroup.Items.Clear();
            _pTemplate.Items.Clear();
            _pTargets.Items.Clear();
            _pGrid.Rows.Clear();
            _pSummary.Text = "";
        }

        private void LoadParaFile()
        {
            if (!File.Exists(_pFile.Text))
            {
                MessageBox.Show(this, "请选择存在的脚本文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                _pDoc = ScriptParser.Load(_pFile.Text);
                _pGroups = TemplateEngine.DetectGroups(_pDoc);
                _pUpdating = true;
                _pGroup.Items.Clear();
                for (int i = 0; i < _pGroups.Count; i++)
                    _pGroup.Items.Add(string.Format("组 {0}：{1} 段 · {2} …",
                        i + 1, _pGroups[i].Blocks.Count, _pGroups[i].Blocks[0].Name));
                _pUpdating = false;

                if (_pGroups.Count == 0)
                {
                    LogPara("未发现结构相同的重复段落（至少需要 2 段同样布局）。");
                    _pTemplate.Items.Clear();
                    _pTargets.Items.Clear();
                    return;
                }

                int best = 0;
                for (int i = 1; i < _pGroups.Count; i++)
                    if (_pGroups[i].Blocks.Count > _pGroups[best].Blocks.Count) best = i;

                _pGroup.SelectedIndex = best;
                LogPara(string.Format("读取完成：共 {0} 个同构段落组，最大组 {1} 段。",
                    _pGroups.Count, _pGroups[best].Blocks.Count));
                AutoPreviewPara();
            }
            catch (Exception ex)
            {
                _pDoc = null;
                MessageBox.Show(this, ex.Message, "读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private ParagraphGroup CurrentGroup()
        {
            int i = _pGroup.SelectedIndex;
            if (i < 0 || i >= _pGroups.Count) return null;
            return _pGroups[i];
        }

        private void OnGroupChanged(object sender, EventArgs e)
        {
            if (_pUpdating) return;
            ParagraphGroup g = CurrentGroup();
            if (g == null) return;

            _pUpdating = true;
            _pTemplate.Items.Clear();
            foreach (ScriptBlock b in g.Blocks) _pTemplate.Items.Add(b.Name);
            _pUpdating = false;
            if (_pTemplate.Items.Count > 0) _pTemplate.SelectedIndex = 0;
            else RebuildTargets();
            AutoPreviewPara();
        }

        private void OnTemplateChanged(object sender, EventArgs e)
        {
            if (_pUpdating) return;
            RebuildTargets();
            AutoPreviewPara();
        }

        private void AutoPreviewPara()
        {
            if (_pDoc == null || _pTargets.Items.Count == 0) return;
            if (_pTargets.CheckedItems.Count == 0) return;
            OnParaScan(null, null);
        }

        private void RebuildTargets()
        {
            _pUpdating = true;
            _pTargets.Items.Clear();
            ParagraphGroup g = CurrentGroup();
            int ti = _pTemplate.SelectedIndex;
            if (g != null && ti >= 0 && ti < g.Blocks.Count)
            {
                for (int i = 0; i < g.Blocks.Count; i++)
                {
                    if (i == ti) continue;
                    _pTargets.Items.Add(g.Blocks[i].Name, true);
                }
            }
            _pUpdating = false;
        }

        private void OnTargetItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (_pUpdating) return;
        }

        private void OnSelectAll(object sender, EventArgs e)
        {
            for (int i = 0; i < _pTargets.Items.Count; i++) _pTargets.SetItemChecked(i, true);
        }

        private void OnSelectNone(object sender, EventArgs e)
        {
            for (int i = 0; i < _pTargets.Items.Count; i++) _pTargets.SetItemChecked(i, false);
        }

        private List<ScriptBlock> SelectedParaBlocks()
        {
            List<ScriptBlock> res = new List<ScriptBlock>();
            ParagraphGroup g = CurrentGroup();
            int ti = _pTemplate.SelectedIndex;
            if (g == null || ti < 0) return res;

            int k = 0;
            for (int i = 0; i < g.Blocks.Count; i++)
            {
                if (i == ti) continue;
                if (k < _pTargets.Items.Count && _pTargets.GetItemChecked(k)) res.Add(g.Blocks[i]);
                k++;
            }
            return res;
        }

        private ParaPlan BuildCurrentPlan()
        {
            ParagraphGroup g = CurrentGroup();
            int ti = _pTemplate.SelectedIndex;
            if (g == null || ti < 0 || ti >= g.Blocks.Count) return null;

            List<ScriptBlock> targets = SelectedParaBlocks();
            if (targets.Count == 0) return null;
            return TemplateEngine.BuildPlan(_pDoc, g.Blocks[ti], targets, _pChkAmp.Checked, _pChkDialog.Checked, _pChkPunct.Checked, _pChkIds.Checked, _pChkColors.Checked);
        }

        private void OnParaScan(object sender, EventArgs e)
        {
            if (_pDoc == null) LoadParaFile();
            if (_pDoc == null) return;

            ParaPlan plan = BuildCurrentPlan();
            if (plan == null)
            {
                MessageBox.Show(this, "请至少勾选一个目标段落。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            FillParaGrid(plan);
            if (sender != null)
            {
                string extra = "";
                if (plan.CompleteCount > 0) extra += "，简写补全 " + plan.CompleteCount + " 处";
                if (plan.DialogChanges > 0) extra += "，对话框参数 " + plan.DialogChanges + " 处";
                if (plan.ButtonChanges > 0) extra += "，按钮替换 " + plan.ButtonChanges + " 处";
                if (plan.KindMismatch > 0) extra += "，种类不同跳过 " + plan.KindMismatch + " 处";
                LogPara(string.Format("预览：模板 {0} → {1} 段；需覆盖 {2} 处，已一致 {3} 处，跳过 {4} 段{5}。",
                    plan.Template.Name, plan.Targets.Count, plan.ChangeCount,
                    plan.SameCount, plan.SkippedBlocks, extra));
                foreach (string w in plan.Warnings) LogPara("⚠ " + w);
            }
        }

        private void FillParaGrid(ParaPlan plan)
        {
            _pGrid.SuspendLayout();
            _pGrid.Rows.Clear();
            foreach (ParaPair p in plan.Pairs)
            {
                if (p.IsMeta)
                {
                    string detail = p.Status == "颜色"
                        ? (p.MetaOld.Length == 0 ? "无" : p.MetaOld) + " → " + (p.MetaNew.Length == 0 ? "无" : p.MetaNew)
                        : Shorten(p.MetaOld, 28) + " → " + Shorten(p.MetaNew, 28);
                    int mi = _pGrid.Rows.Add(p.TargetBlock.Name, "—", detail, "—", "—", "—", p.Status);
                    DataGridViewRow mrow = _pGrid.Rows[mi];
                    mrow.DefaultCellStyle.BackColor = Color.FromArgb(228, 240, 255);
                    mrow.Cells[2].ToolTipText = p.MetaOld + "  →  " + p.MetaNew;
                    continue;
                }

                if (p.IsDialog)
                {
                    string sa = p.SrcDlg != null ? p.SrcDlg.Args : "—";
                    string da = p.DstDlg != null ? p.DstDlg.Args : "—";
                    string dstatus = p.DstDlg == null ? "缺失" : p.Status;
                    int di = _pGrid.Rows.Add(p.TargetBlock.Name, "框", "OPENMERCHANTBIGDLG", sa, da, "—", dstatus);
                    DataGridViewRow drow = _pGrid.Rows[di];
                    if (dstatus == "覆盖") drow.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                    else if (dstatus == "缺失") drow.DefaultCellStyle.BackColor = Color.FromArgb(255, 228, 228);
                    else drow.DefaultCellStyle.ForeColor = Color.FromArgb(110, 110, 110);
                    drow.Cells[3].ToolTipText = sa;
                    drow.Cells[4].ToolTipText = da;
                    continue;
                }

                string text = p.Dst != null ? Shorten(p.Dst.Text, 54) : "";
                string sx = p.Src != null && p.Src.HasCoord ? p.Src.X + " : " + p.Src.Y : "—";
                string dx = p.Dst != null && p.Dst.HasCoord ? p.Dst.X + " : " + p.Dst.Y : "无";
                string amp;
                if (p.Status == "替换") amp = "按钮 → " + (p.Src != null ? p.Src.Name : "imgex");
                else if (p.Status == "补全") amp = (p.Dst != null && p.Dst.IsPlain) ? "文本 → &Text" : "简写 → &Text";
                else if (p.Src != null && p.Dst != null && p.Src.Kind == ElementKind.Text)
                    amp = TextEdits.AmpChange(p.Dst.HasAmp, p.Src.HasAmp);
                else amp = "—";
                int ord = p.Dst != null ? p.Dst.Ordinal + 1 : 0;
                int idx = _pGrid.Rows.Add(p.TargetBlock.Name, ord, text, sx, dx, amp, p.Status);

                DataGridViewRow row = _pGrid.Rows[idx];
                if (p.Status == "覆盖" || p.Status == "补全")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 248, 214);
                else if (p.Status == "缺失")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 228, 228);
                else if (p.Status == "种类不同")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 226, 196);
                else if (p.Status == "结构异常")
                    row.DefaultCellStyle.BackColor = Color.FromArgb(255, 205, 205);
                else row.DefaultCellStyle.ForeColor = Color.FromArgb(110, 110, 110);

                if (p.Dst != null) row.Cells[2].ToolTipText = p.Dst.Raw;
            }
            _pGrid.ClearSelection();
            _pGrid.ResumeLayout();

            string dialogNote = plan.DialogChanges > 0 ? " · 对话框 " + plan.DialogChanges : "";
            string completeNote = plan.CompleteCount > 0 ? " · 补全 " + plan.CompleteCount : "";
            string buttonNote = plan.ButtonChanges > 0 ? " · 按钮 " + plan.ButtonChanges : "";
            string punctNote = plan.PunctChanges > 0 ? " · 标点 " + plan.PunctChanges : "";
            string idNote = plan.IdChanges > 0 ? " · 编号 " + plan.IdChanges : "";
            string colorNote = plan.ColorChanges > 0 ? " · 颜色 " + plan.ColorChanges : "";
            _pSummary.Text = string.Format("覆盖 {0} · 一致 {1}{2}{3}{4}{5}{6}{7}",
                plan.ChangeCount, plan.SameCount, dialogNote, completeNote, buttonNote,
                punctNote, idNote, colorNote);
            _pSummary.ForeColor = plan.MissingCount > 0
                ? Color.FromArgb(190, 40, 40) : Color.FromArgb(60, 60, 60);
        }

        private void OnParaApply(object sender, EventArgs e)
        {
            if (_pDoc == null) LoadParaFile();
            if (_pDoc == null) return;

            ParaPlan plan = BuildCurrentPlan();
            if (plan == null)
            {
                MessageBox.Show(this, "请至少勾选一个目标段落。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (plan.SkippedBlocks > 0)
            {
                MessageBox.Show(this,
                    "有 " + plan.SkippedBlocks + " 段与模板结构不同，将被跳过（红色行）。\n\n" +
                    string.Join("\n", plan.Warnings.ToArray()),
                    "结构提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (plan.TotalChanges == 0)
            {
                MessageBox.Show(this, "所选段落的坐标已经与模板一致，无需覆盖。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (!_pBackup.Checked)
            {
                if (MessageBox.Show(this,
                        "未勾选自动备份，写入后无法用本工具撤销。\n确实要直接写入吗？",
                        "确认", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;
                string newText = TemplateEngine.BuildNewText(plan);
                FileIO.Save(_pDoc, newText, _pBackup.Checked);
                int bad = TemplateEngine.Verify(plan, newText);

                LogPara(string.Format("已写入 {0}：{1} 段，覆盖 {2} 处（坐标 / 对话框）。",
                    Path.GetFileName(_pFile.Text), plan.Targets.Count, plan.ChangeCount));
                if (_pBackup.Checked) LogPara("已备份原文件：" +
                    Path.GetFileName(FileIO.FindLatestBackup(_pFile.Text)));
                if (bad == 0) LogPara("校验通过：所选段落已与模板一致。");
                else LogPara("⚠ 校验发现 " + bad + " 处仍不一致，请检查。");

                string reportPath = _pFile.Text + ".段落覆盖记录.txt";
                try
                {
                    File.WriteAllText(reportPath, TemplateEngine.BuildReport(plan, true),
                        new UTF8Encoding(true));
                    LogPara("记录已保存：" + Path.GetFileName(reportPath));
                }
                catch { }

                // 写入后重新读取并刷新预览（段落对象要重新解析，不能复用旧的）
                LoadParaFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    ex.Message + "\n\n若脚本正在脚本编辑器中打开，请先关闭它再试。",
                    "写入失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void OnParaRestore(object sender, EventArgs e)
        {
            if (!File.Exists(_pFile.Text))
            {
                MessageBox.Show(this, "请先选择脚本文件（要恢复的那个）。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string bak = FileIO.FindLatestBackup(_pFile.Text);
            if (bak == null)
            {
                MessageBox.Show(this, "脚本旁边没有找到本工具生成的备份文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (MessageBox.Show(this,
                    "将用备份恢复脚本：\n" + Path.GetFileName(bak) + "\n\n继续吗？",
                    "恢复备份", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                File.Copy(bak, _pFile.Text, true);
                LogPara("已从备份恢复：" + Path.GetFileName(bak));
                LoadParaFile();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "恢复失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ---------------------------------------------------------- 杂项

        private void Log(string msg)
        {
            _log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }

        private void LogPara(string msg)
        {
            _pLog.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + "\r\n");
        }

        private void LoadSettings()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string[] lines = File.ReadAllLines(SettingsPath, Encoding.UTF8);
                foreach (string line in lines)
                {
                    if (line.StartsWith("src=")) { string v = line.Substring(4); if (File.Exists(v)) _tbSrc.Text = v; }
                    else if (line.StartsWith("dst=")) { string v = line.Substring(4); if (File.Exists(v)) _tbDst.Text = v; }
                    else if (line.StartsWith("para=")) { string v = line.Substring(5); if (File.Exists(v)) _pFile.Text = v; }
                }
            }
            catch { }
        }

        private void SaveSettings()
        {
            try
            {
                string dir = Path.GetDirectoryName(SettingsPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(SettingsPath, new string[]
                {
                    "src=" + _tbSrc.Text,
                    "dst=" + _tbDst.Text,
                    "para=" + _pFile.Text
                }, Encoding.UTF8);
            }
            catch { }
        }

        private void AutoFillDefaults()
        {
            bool crossLoaded = false;
            if (File.Exists(_tbSrc.Text) && File.Exists(_tbDst.Text))
            {
                OnScan(null, null);
                crossLoaded = true;
            }

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            if (!crossLoaded && string.IsNullOrEmpty(_tbSrc.Text) && string.IsNullOrEmpty(_tbDst.Text))
            {
                string a = Path.Combine(desktop, "第七大陆.txt");
                string b = Path.Combine(desktop, "第八大陆.txt");
                if (File.Exists(a) && File.Exists(b))
                {
                    _tbSrc.Text = a;
                    _tbDst.Text = b;
                    OnScan(null, null);
                }
            }

            if (string.IsNullOrEmpty(_pFile.Text))
            {
                string p = Path.Combine(desktop, "11.txt");
                if (File.Exists(p))
                {
                    _pFile.Text = p;
                    LoadParaFile();
                }
            }
            else if (File.Exists(_pFile.Text))
            {
                LoadParaFile();
            }
        }
    }

    // ---------------------------------------------------------------- 入口

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "--cli" || args[0] == "--cli-para"))
            {
                Environment.Exit(CliRunner.Run(args));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            {
                MessageBox.Show(e.Exception.Message, "出错了", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            bool paraTab = false;
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--tab2") paraTab = true;
            Application.Run(new MainForm(paraTab));
        }
    }
}
