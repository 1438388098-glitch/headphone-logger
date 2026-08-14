using System.ComponentModel;
using System.Drawing.Drawing2D;
using HeadphoneLogger.Core;

namespace HeadphoneLogger.UI;

/// <summary>气泡确认结果。</summary>
public sealed record SceneConfirmResult(Scene? MainScene, IReadOnlySet<Scene> ConcurrentScenes, bool Confirmed);

/// <summary>
/// 场景切换确认气泡：无边框 TopMost、不抢焦点、右下角贴托盘。
/// 「✓是」接受检测值；「✕让我改」展开主场景下拉；5 分钟无操作自动按预填落库（未确认）。
/// </summary>
public sealed class SceneConfirmBubble : Form
{
    private const int TimeoutMs = 5 * 60 * 1000;
    private readonly Label _titleLabel;
    private readonly Label _infoLabel;
    private readonly ComboBox _sceneCombo;
    private readonly FlowLayoutPanel _concurrentPanel;
    private readonly Button _primaryButton;
    private readonly Button _secondaryButton;
    private readonly System.Windows.Forms.Timer _timeoutTimer;
    private readonly List<CheckBox> _concurrentBoxes = [];

    private bool _editMode;

    /// <summary>气泡结果回调（确认/改场景/超时都走这里）。</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Action<SceneConfirmResult>? OnResolve { get; set; }

    public SceneConfirmBubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(340, 150);
        BackColor = Color.FromArgb(252, 251, 246); // 米白纸色

        var region = new GraphicsPath();
        region.AddArc(0, 0, 18, 18, 180, 90);
        region.AddArc(Width - 18, 0, 18, 18, 270, 90);
        region.AddArc(Width - 18, Height - 18, 18, 18, 0, 90);
        region.AddArc(0, Height - 18, 18, 18, 90, 90);
        Region = new Region(region);
        region.Dispose();

        _titleLabel = new Label
        {
            AutoSize = true,
            Location = new Point(18, 14),
            Font = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40),
            Text = string.Empty,
        };

        _infoLabel = new Label
        {
            AutoSize = true,
            Location = new Point(18, 44),
            MaximumSize = new Size(304, 0),
            Font = new Font("Microsoft YaHei UI", 8.5f),
            ForeColor = Color.FromArgb(110, 110, 110),
            Text = string.Empty,
        };

        _sceneCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(18, 70),
            Width = 190,
            Visible = false,
            Font = new Font("Microsoft YaHei UI", 9f),
        };
        foreach (var scene in Enum.GetValues<Scene>())
        {
            if (scene != Scene.Unmarked)
                _sceneCombo.Items.Add(scene);
        }

        _concurrentPanel = new FlowLayoutPanel
        {
            Location = new Point(18, 74),
            Size = new Size(304, 26),
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        _primaryButton = MakeButton("✓ 是", Color.FromArgb(52, 120, 78), Color.White, ClickedPrimary);
        _secondaryButton = MakeButton("✕ 让我改", Color.Transparent, Color.FromArgb(80, 80, 80), ClickedSecondary);
        _primaryButton.Location = new Point(340 - 18 - _primaryButton.Width, 108);
        _secondaryButton.Location = new Point(_primaryButton.Left - _secondaryButton.Width - 8, 108);

        Controls.AddRange(new Control[] { _titleLabel, _infoLabel, _sceneCombo, _concurrentPanel, _primaryButton, _secondaryButton });

        _timeoutTimer = new System.Windows.Forms.Timer { Interval = TimeoutMs };
        _timeoutTimer.Tick += (_, _) =>
        {
            _timeoutTimer.Stop();
            OnResolve?.Invoke(new SceneConfirmResult(null, CheckedConcurrentScenes(), Confirmed: false));
            HideBubble();
        };
    }

    /// <summary>用最新草稿刷新气泡内容（pending 合并时调用）。</summary>
    public void UpdateDraft(SceneDraft draft)
    {
        _titleLabel.Text = $"切到「{draft.MainScene.DisplayName()}」了？";
        var detail = draft.WindowTitle is { Length: > 0 } ? draft.WindowTitle : draft.AppName ?? "未知应用";
        _infoLabel.Text = detail;

        _sceneCombo.SelectedItem = draft.MainScene;
        _concurrentPanel.Controls.Clear();
        _concurrentBoxes.Clear();
        foreach (var scene in draft.ConcurrentScenes.OrderBy(s => s.DisplayName()))
        {
            var box = new CheckBox
            {
                Text = scene.DisplayName(),
                Checked = true,
                AutoSize = true,
                Font = new Font("Microsoft YaHei UI", 8.5f),
                Margin = new Padding(0, 2, 10, 0),
                Tag = scene,
            };
            _concurrentPanel.Controls.Add(box);
            _concurrentBoxes.Add(box);
        }
        LayoutBubble();
    }

    /// <summary>显示气泡并开始计时（重复调用只重置计时与位置）。</summary>
    public void ShowBubble(SceneDraft draft)
    {
        UpdateDraft(draft);
        PositionNearTray();
        if (!Visible)
            Show();
        else
            Refresh();
        _timeoutTimer.Stop();
        _timeoutTimer.Start();
    }

    public void HideBubble()
    {
        _timeoutTimer.Stop();
        Hide();
    }

    private IReadOnlySet<Scene> CheckedConcurrentScenes() =>
        _concurrentBoxes.Where(b => b.Checked).Select(b => (Scene)b.Tag!).ToHashSet();

    private void ClickedPrimary(object? sender, EventArgs e)
    {
        if (_editMode)
        {
            var chosen = _sceneCombo.SelectedItem is Scene s ? s : (Scene?)null;
            _timeoutTimer.Stop();
            OnResolve?.Invoke(new SceneConfirmResult(chosen, CheckedConcurrentScenes(), Confirmed: true));
            HideBubble();
            return;
        }

        // 「✓是」：接受检测值（以 SessionManager 最新 pending 为准，气泡标签可能滞后）
        _timeoutTimer.Stop();
        OnResolve?.Invoke(new SceneConfirmResult(null, CheckedConcurrentScenes(), Confirmed: true));
        HideBubble();
    }

    private void ClickedSecondary(object? sender, EventArgs e)
    {
        if (!_editMode)
        {
            _editMode = true;
            _sceneCombo.Visible = true;
            _secondaryButton.Text = "取消";
            _primaryButton.Text = "✓ 确认";
            LayoutBubble();
            return;
        }

        _editMode = false;
        _sceneCombo.Visible = false;
        _secondaryButton.Text = "✕ 让我改";
        _primaryButton.Text = "✓ 是";
        LayoutBubble();
    }

    private void LayoutBubble()
    {
        _concurrentPanel.Visible = _concurrentBoxes.Count > 0;
        var concurrentY = _editMode ? 96 : 74;
        _concurrentPanel.Location = new Point(18, concurrentY);

        var buttonsY = _editMode ? 130 : 108;
        if (_editMode)
        {
            _sceneCombo.Location = new Point(18, 70);
        }
        _primaryButton.Location = new Point(340 - 18 - _primaryButton.Width, buttonsY);
        _secondaryButton.Location = new Point(_primaryButton.Left - _secondaryButton.Width - 8, buttonsY);

        var height = _editMode ? 168 : (buttonsY + 30);
        ClientSize = new Size(340, height);
        _titleLabel.Top = 14;
        _infoLabel.Top = 44;
    }

    private void PositionNearTray()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.AllScreens[0].WorkingArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;
    }

    private static Button MakeButton(string text, Color back, Color fore, EventHandler onClick)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 4),
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Microsoft YaHei UI", 9f),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = back == Color.Transparent ? 1 : 0;
        btn.FlatAppearance.BorderColor = Color.FromArgb(190, 190, 190);
        btn.Click += onClick;
        return btn;
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeoutTimer.Dispose();
            Region?.Dispose();
        }
        base.Dispose(disposing);
    }
}
