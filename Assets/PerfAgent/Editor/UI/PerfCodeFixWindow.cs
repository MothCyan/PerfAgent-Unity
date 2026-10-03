using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;
using PerfAgent.Analysis;
using PerfAgent.Core;

namespace PerfAgent.UI
{
    /// <summary>
    /// 代码修复建议窗口：左边原文、右边建议，由人决定是否应用。
    ///
    /// 为什么单独开一个窗口而不是塞进主面板：这里要对比两段代码，
    /// 需要足够的高度与宽度；挤在结论列表里两边都看不清，等于让人盲目点「应用」。
    ///
    /// 入口：Tools &gt; PerfAgent &gt; 代码修复建议
    /// </summary>
    public class PerfCodeFixWindow : EditorWindow
    {
        class IssueEntry
        {
            public string label;
            public string file;
            public int line;
            public string pattern;
            public string suggestion;

            public override string ToString() { return label; }
        }

        List<IssueEntry> _issues = new List<IssueEntry>();
        IssueEntry _current;

        PerfCodeFixProposal _proposal;
        string _undoContent;
        bool _applied;
        string _pendingFindingId = "";

        DropdownField _picker;
        Label _status;
        TextField _original;
        TextField _proposed;
        TextField _explanation;
        Button _generate;
        Button _apply;
        Button _revert;

        [MenuItem("Tools/PerfAgent/代码修复建议", false, 108)]
        public static void Open()
        {
            var window = GetWindow<PerfCodeFixWindow>("代码修复建议");
            window.minSize = new Vector2(860, 700);
            window.Show();
        }

        /// <summary>
        /// 从结论的「AI 改写」按钮进来。target 形如 "Assets/Scripts/Foo.cs:132"。
        /// 打开后直接定位到那一处并自动请求建议，省掉「再选一次」这个多余步骤。
        /// </summary>
        public static void Open(string target, string findingId)
        {
            string file = target;
            int line = 1;

            int colon = target == null ? -1 : target.LastIndexOf(':');
            if (colon > 0)
            {
                file = target.Substring(0, colon);
                int parsed;
                if (int.TryParse(target.Substring(colon + 1), out parsed)) line = parsed;
            }

            var window = GetWindow<PerfCodeFixWindow>("代码修复建议");
            window.minSize = new Vector2(860, 700);
            window.Show();
            window.FocusOn(file, line, findingId);
        }

        /// <summary>定位到指定位置并立刻请求建议。</summary>
        public void FocusOn(string file, int line, string findingId)
        {
            string full = ToFullPath(file);
            _pendingFindingId = findingId;

            LoadIssues();

            // 优先复用当前快照里已有的那条（它带着反模式 id 与建议，提示词更精确）
            for (int i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].file != full || _issues[i].line != line) continue;
                _current = _issues[i];
                _picker.value = _issues[i].label;
                Generate();
                return;
            }

            // 快照里没有这一条（比如用的是旧快照）——按文件与行号直接生成
            var entry = new IssueEntry();
            entry.file = full;
            entry.line = line;
            entry.pattern = "";
            entry.suggestion = "";
            entry.label = Path.GetFileName(file) + ":" + line;
            _current = entry;
            Generate();
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 8;
            root.style.paddingBottom = 8;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;
            // flexShrink=0 是关键：默认值是 1，空间不够时这一行会被压缩。
            top.style.flexShrink = 0;

            _picker = new DropdownField("代码问题");
            _picker.style.flexGrow = 1;
            _picker.RegisterValueChangedCallback(delegate (ChangeEvent<string> e)
            {
                SelectByName(e.newValue);
            });
            top.Add(_picker);

            var reload = new Button(delegate { LoadIssues(); });
            reload.text = "刷新";
            reload.style.marginLeft = 4;
            top.Add(reload);

            root.Add(top);

            var actions = new VisualElement();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.marginTop = 6;
            actions.style.flexShrink = 0;

            _generate = new Button(delegate { Generate(); });
            _generate.text = "生成建议";
            actions.Add(_generate);

            _apply = new Button(delegate { Apply(); });
            _apply.text = "应用到文件";
            _apply.style.marginLeft = 4;
            actions.Add(_apply);

            _revert = new Button(delegate { Revert(); });
            _revert.text = "撤销";
            _revert.style.marginLeft = 4;
            actions.Add(_revert);

            root.Add(actions);

            _status = new Label();
            _status.style.marginTop = 6;
            _status.style.marginBottom = 6;
            _status.style.whiteSpace = WhiteSpace.Normal;
            _status.style.flexShrink = 0;
            root.Add(_status);

            // 代码区放进滚动容器，并用**固定高度**。
            //
            // 之前让三个文本域 flexGrow=1 去抢剩余空间，一旦窗口高度差一点，
            // 顶部的下拉与按钮行就先被压掉 —— 表现为「按钮点不到」。
            // 现在按钮区完全不参与高度竞争（不在滚动容器里，且 flexShrink=0），
            // 无论窗口多小、代码多长，它都在；代码太长时在各自区域内滚动。
            var scroll = new ScrollView(ScrollViewMode.Vertical);
            scroll.style.flexGrow = 1;
            scroll.style.marginTop = 2;
            root.Add(scroll);

            _original = AddBlock(scroll, "原代码", 240, false);
            _proposed = AddBlock(scroll, "建议代码", 240, false);
            _explanation = AddBlock(scroll, "说明", 90, false);

            LoadIssues();
        }

        /// <summary>加一段「标题 + 只读多行文本框」，返回那个文本框。</summary>
        static TextField AddBlock(VisualElement parent, string title, float minHeight, bool grow)
        {
            var caption = new Label(title);
            caption.style.unityFontStyleAndWeight = FontStyle.Bold;
            caption.style.marginTop = 6;
            caption.style.flexShrink = 0;
            parent.Add(caption);

            var field = new TextField();
            field.multiline = true;
            field.isReadOnly = true;
            field.style.whiteSpace = WhiteSpace.Normal;
            field.style.minHeight = minHeight;
            // flexShrink=0 同样不能省：默认值下文本域会被压到几像素高，
            // 内容看不见。 grow 控制它是否吃掉剩余空间 —— 说明区短，不需要撑开。
            field.style.flexShrink = 0;
            field.style.flexGrow = grow ? 1 : 0;
            parent.Add(field);
            return field;
        }

        // =====================================================================
        // 数据
        // =====================================================================

        void LoadIssues()
        {
            _issues.Clear();
            _current = null;
            _proposal = null;
            _undoContent = null;
            _applied = false;

            var snap = PerfSession.Current;
            if (snap == null || snap.codeIssues == null || snap.codeIssues.Count == 0)
            {
                _status.text = "当前快照里没有代码问题。先跑一次「静态审计」或「抓帧并分析」。";
                _picker.choices = new List<string>();
                Refresh();
                return;
            }

            for (int i = 0; i < snap.codeIssues.Count; i++)
            {
                var c = snap.codeIssues[i];
                var entry = new IssueEntry();
                entry.file = ToFullPath(c.file);
                entry.line = c.line;
                entry.pattern = c.pattern;
                entry.suggestion = c.suggestion;
                entry.label = Path.GetFileName(c.file) + ":" + c.line + "  " + c.pattern;
                _issues.Add(entry);
            }

            var labels = new List<string>(_issues.Count);
            for (int i = 0; i < _issues.Count; i++) labels.Add(_issues[i].label);
            _picker.choices = labels;

            if (_issues.Count > 0)
            {
                _picker.value = _issues[0].label;
                _current = _issues[0];
            }

            _status.text = "共 " + _issues.Count + " 处代码问题。选一处点「生成建议」。";
            Refresh();
        }

        void SelectByName(string label)
        {
            for (int i = 0; i < _issues.Count; i++)
            {
                if (_issues[i].label != label) continue;
                _current = _issues[i];
                _proposal = null;
                _undoContent = null;
                _applied = false;
                _status.text = "已选择：" + label;
                Refresh();
                return;
            }
        }

        static string ToFullPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (Path.IsPathRooted(path)) return path;

            var root = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(root, path);
        }

        // =====================================================================
        // 动作
        // =====================================================================

        void Generate()
        {
            if (_current == null) { SetStatus("先选一处代码问题。", true); return; }

            _proposal = null;
            _undoContent = null;
            _applied = false;
            Refresh();

            SetStatus("正在生成建议…（" + _current.pattern + "）", false);

            PerfCodeFixAdvisor.Propose(_pendingFindingId, _current.file, _current.line, _current.pattern,
                _current.suggestion,
                delegate (PerfCodeFixProposal proposal)
                {
                    _proposal = proposal;

                    if (!proposal.IsUsable)
                        SetStatus(proposal.error + (string.IsNullOrEmpty(proposal.explanation)
                            ? "" : "\n\n模型说明：" + proposal.explanation), true);
                    else
                        SetStatus("建议已生成。请对照两段代码确认后再点「应用到文件」。", false);

                    Refresh();
                },
                delegate (string err)
                {
                    SetStatus("生成失败：" + err, true);
                    Refresh();
                });
        }

        void Apply()
        {
            if (_proposal == null || !_proposal.IsUsable) { SetStatus("还没有可应用的改动。", true); return; }

            string undo;
            string error = PerfCodeFixAdvisor.Apply(_proposal, out undo);
            if (error != null) { SetStatus(error, true); return; }

            _undoContent = undo;
            _applied = true;
            SetStatus("已写入 " + Path.GetFileName(_proposal.file)
                + "。Unity 会重新编译，若报错请点「撤销」。", false);
            Refresh();
        }

        void Revert()
        {
            if (!_applied || _undoContent == null) { SetStatus("没有可撤销的改动。", true); return; }

            string error = PerfCodeFixAdvisor.Revert(_proposal.file, _undoContent);
            if (error != null) { SetStatus(error, true); return; }

            _undoContent = null;
            _applied = false;
            SetStatus("已还原到应用前的内容。", false);
            Refresh();
        }

        void SetStatus(string text, bool warn)
        {
            if (_status == null) return;
            _status.text = text;
            _status.style.color = warn
                ? new Color(1f, 0.78f, 0.42f)
                : new Color(0.72f, 0.78f, 0.86f);
        }

        void Refresh()
        {
            if (_original != null)
                _original.value = _proposal == null || string.IsNullOrEmpty(_proposal.originalCode)
                    ? (_current == null ? "" : (PerfCodeFixAdvisor.ReadContext(_current.file, _current.line, out _) ?? ""))
                    : _proposal.originalCode;

            if (_proposed != null)
                _proposed.value = _proposal == null || string.IsNullOrEmpty(_proposal.proposedCode)
                    ? "(还没生成建议)"
                    : _proposal.proposedCode;

            if (_explanation != null)
                _explanation.value = _proposal == null || string.IsNullOrEmpty(_proposal.explanation)
                    ? ""
                    : _proposal.explanation;

            if (_generate != null)
                _generate.SetEnabled(_current != null && !PerfCodeFixAdvisor.Busy);

            if (_apply != null)
                _apply.SetEnabled(_proposal != null && _proposal.IsUsable && !_applied);

            if (_revert != null)
                _revert.SetEnabled(_applied);
        }
    }
}
