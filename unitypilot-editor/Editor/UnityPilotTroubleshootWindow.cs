using UnityEditor;
using UnityEngine;

namespace SkillEditor.Editor.UnityPilot
{
    /// <summary>
    /// 可复制的排查说明（独立弹窗）。
    /// </summary>
    internal class UnityPilotTroubleshootWindow : EditorWindow
    {
        private Vector2 _scroll;
        private GUIStyle _bodySelectableStyle;
        private bool     _stylesReady;

        [MenuItem("UnityPilot/排查说明", false, 201)]
        private static void OpenFromMenu()
        {
            OpenWindow();
        }

        private static readonly string Body =
            "UnityPilot 连接与 MCP 排查（可复制本窗口全部文字）\n\n" +
            "【1】Python MCP 服务\n" +
            "- 确认已运行 run_unitypilot_mcp.py（或 Cursor 已启动 MCP stdio 进程）。\n" +
            "- 命令行中的 --port 须与 Unity「WS 连接配置」里的端口一致。\n" +
            "- Windows 防火墙若拦截本机回环，可临时关闭或放行 python。\n\n" +
            "【2】地址与端口\n" +
            "- 默认 ws://127.0.0.1:8765；修改后请先停止 Bridge，再「应用连接地址」，然后「启动/重启」。\n" +
            "- IP/端口按项目保存在 EditorPrefs（键名含项目路径哈希），多项目互不覆盖。\n\n" +
            "【3】Unity 侧\n" +
            "- 菜单 UnityPilot → UnityPilot 打开状态窗口，查看「状态总览」与「诊断日志」。\n" +
            "- 开启「调试通信日志」可看到 RX/TX 与 RAW JSON（通信量较大）。\n" +
            "- WS 断开后 Bridge 约每 2 秒自动重连；查看日志中的连接失败原因。\n\n" +
            "【4】认证\n" +
            "- 「已认证」为否时，检查服务端是否收到 session.hello 并返回 accepted。\n" +
            "- Session ID 在状态窗口「连接状态」中可见。\n\n" +
            "【5】日志文件\n" +
            "- 项目根目录下 logs/unitypilot-editor.log 记录本会话日志；每次打开 Unity 会清空并写新会话头。\n" +
            "- 「诊断日志」标签页可打开该文件所在位置。\n\n" +
            "【6】仍无法解决\n" +
            "- 在「诊断 / 通信测试」中运行「测试服务器」「检查服务器」。\n" +
            "- 将 unitypilot-editor.log 与 Cursor MCP 输出一并反馈。";

        internal static void OpenWindow()
        {
            var win = GetWindow<UnityPilotTroubleshootWindow>(true, "UnityPilot 排查说明", true);
            win.minSize = new Vector2(420, 320);
            win.ShowUtility();
        }

        private void OnGUI()
        {
            EnsureBodyStyle();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            var w = position.width - 24f;
            var h = Mathf.Max(_bodySelectableStyle.CalcHeight(new GUIContent(Body), w), 120f);
            EditorGUILayout.SelectableLabel(Body, _bodySelectableStyle, GUILayout.Width(w), GUILayout.Height(h));
            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("复制全部说明", GUILayout.Height(22)))
                {
                    GUIUtility.systemCopyBuffer = Body;
                    ShowNotification(new GUIContent("已复制到剪贴板"));
                }
            }
        }

        /// <summary>
        /// 将 focused 等与 normal 对齐，减轻选中时的焦点描边；仍用 SelectableLabel 支持复制。
        /// </summary>
        private void EnsureBodyStyle()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            var s = new GUIStyle(EditorStyles.textArea)
            {
                wordWrap     = true,
                richText     = false,
                stretchWidth = true,
            };
            MirrorNonFocusedStatesFromNormal(s);
            _bodySelectableStyle = s;
        }

        /// <summary>让 focused/hover/active 与 normal 一致，避免选中时出现蓝色焦点框。</summary>
        private static void MirrorNonFocusedStatesFromNormal(GUIStyle s)
        {
            void Mirror(GUIStyleState to)
            {
                if (to == null || s.normal == null) return;
                to.background = s.normal.background;
                to.textColor = s.normal.textColor;
                to.scaledBackgrounds = s.normal.scaledBackgrounds;
            }

            Mirror(s.focused);
            Mirror(s.active);
            Mirror(s.hover);
            Mirror(s.onFocused);
            Mirror(s.onActive);
            Mirror(s.onHover);
        }
    }
}
