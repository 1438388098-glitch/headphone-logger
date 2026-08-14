using System.ComponentModel;
using System.Drawing.Drawing2D;
using HeadphoneLogger.Core;

namespace HeadphoneLogger.UI;

/// <summary>气泡确认结果。</summary>
public sealed record SceneConfirmResult(Scene? MainScene, IReadOnlySet<Scene> ConcurrentScenes, bool Confirmed);

/// <summary>
/// 场景切换确认气泡：无边框 TopMost、不抢焦点、右下角贴托盘。
/// 「✓是」接受检测值；「✕让我改」展开主场景下拉；「稍后标」立即按检测值落库并标未确认；
/// 3 分钟无操作自动按预填落库（未确认）。
/// </summary>
public sealed class SceneConfirmBubble : Form
{
    private const int TimeoutMs = 3 * 60 * 1000; // 显示 3 分钟，期间有操作则重新起算
    private const int BubbleWidth = 420;
    private const int Margin = 24;
    private readonly Label _titleLabel;
    private readonly Label _infoLabel;
    private readonly ComboBox _sceneCombo;
    private readonly FlowLayoutPanel _concurrentPanel;
    private readonly Button _primaryButton;
    private readonly Button _secondaryButton;
    private readonly Button _laterButton;
    private readonly System.Windows.Forms.Timer _timeoutTimer;
    private readonly List<CheckBox> _concurrentBoxes = [];
    private readonly Font _tagFont; // 共享字体，避免每次重建 CheckBox 时泄漏 GDI 字体句柄

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
        ClientSize = new Size(BubbleWidth, 176);
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
            Location = new Point(Margin, 16),
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
            ForeColor = Color.FromArgb(40, 40, 40),
            Text = string.Empty,
        };

        _infoLabel = new Label
        {
            AutoSize = true,
            Location = new Point(Margin, 48),
            MaximumSize = new Size(BubbleWidth - Margin * 2, 0),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ForeColor = Color.FromArgb(110, 110, 110),
            Text = string.Empty,
        };

        _sceneCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Location = new Point(Margin, 78),
            Width = 240,
            Visible = false,
            Font = new Font("Microsoft YaHei UI", 10f),
        };
        foreach (var scene in Enum.GetValues<Scene>())
        {
            if (scene != Scene.Unmarked)
                _sceneCombo.Items.Add(scene);
        }

        _concurrentPanel = new FlowLayoutPanel
        {
            Location = new Point(Margin, 82),
            Size = new Size(BubbleWidth - Margin * 2, 30),
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        _primaryButton = MakeButton("✓ 是", Color.FromArgb(52, 120, 78), Color.White, ClickedPrimary);
        _secondaryButton = MakeButton("✕ 让我改", Color.Transparent, Color.FromArgb(80, 80, 80), ClickedSecondary);
        _laterButton = MakeButton("稍后标", Color.Transparent, Color.FromArgb(150, 120, 60), ClickedLater);
        _primaryButton.Location = new Point(BubbleWidth - Margin - _primaryButton.Width, 132);
        _secondaryButton.Location = new Point(_primaryButton.Left - _secondaryButton.Width - 8, 132);
        _laterButton.Location = new Point(_secondaryButton.Left - _laterButton.Width - 8, 132);

        Controls.AddRange(new Control[] { _titleLabel, _infoLabel, _sceneCombo, _concurrentPanel, _primaryButton, _secondaryButton, _laterButton });

        _timeoutTimer = new System.Windows.Forms.Timer { Interval = TimeoutMs };
        _timeoutTimer.Tick += (_, _) =>
        {
            _timeoutTimer.Stop();
            OnResolve?.Invoke(new SceneConfirmResult(null, CheckedConcurrentScenes(), Confirmed: false));
            HideBubble();
        };

        _tagFont = new Font("Microsoft YaHei UI", 8.5f);
        // 用户改主场景下拉时，重置 5 分钟超时计时（符合「无操作才超时」语义）
        _sceneCombo.SelectedIndexChanged += (_, _) => ResetTimeout();
    }

    /// <summary>用最新草稿刷新气泡内容（pending 合并时调用）。编辑态下保留用户选择，不重置。</summary>
    public void UpdateDraft(SceneDraft draft)
    {
        _titleLabel.Text = $"切到「{draft.MainScene.DisplayName()}」了？";
        var detail = draft.WindowTitle is { Length: > 0 } ? draft.WindowTitle : draft.AppName ?? "未知应用";
        _infoLabel.Text = detail;

        // 用户正在「✕ 让我改」编辑中：保留其勾选与下拉选择，避免被合并刷新覆盖
        if (_editMode)
            return;

        _sceneCombo.SelectedItem = draft.MainScene;
        // 先释放旧 CheckBox（Controls.Clear 不 Dispose，会泄漏句柄），再重建
        foreach (Control c in _concurrentPanel.Controls)
            c.Dispose();
        _concurrentPanel.Controls.Clear();
        _concurrentBoxes.Clear();
        foreach (var scene in draft.ConcurrentScenes.OrderBy(s => s.DisplayName()))
        {
            var box = new CheckBox
            {
                Text = scene.DisplayName(),
                Checked = true,
                AutoSize = true,
                Font = _tagFont,
                Margin = new Padding(0, 2, 10, 0),
                Tag = scene,
            };
            box.CheckedChanged += (_, _) => ResetTimeout();
            _concurrentPanel.Controls.Add(box);
            _concurrentBoxes.Add(box);
        }
        LayoutBubble();
    }

    /// <summary>显示气泡并开始计时（重复调用只重置计时与位置）。</summary>
    public void ShowBubble(SceneDraft draft)
    {
        ResetEditMode(); // 上一条气泡可能遗留在编辑态
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
        ResetEditMode();
    }

    /// <summary>重置超时计时（仅气泡可见时），用户有操作则重新起算。</summary>
    private void ResetTimeout()
    {
        _timeoutTimer.Stop();
        if (Visible)
            _timeoutTimer.Start();
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
        ResetTimeout();
        if (!_editMode)
        {
            _editMode = true;
            _sceneCombo.Visible = true;
            _secondaryButton.Text = "取消";
            _primaryButton.Text = "✓ 确认";
            _laterButton.Visible = false;
            LayoutBubble();
            return;
        }

        ResetEditMode();
        LayoutBubble();
    }

    /// <summary>「稍后标」：不打断当前专注，立即按检测值落库并标未确认，关闭气泡。</summary>
    private void ClickedLater(object? sender, EventArgs e)
    {
        _timeoutTimer.Stop();
        OnResolve?.Invoke(new SceneConfirmResult(null, CheckedConcurrentScenes(), Confirmed: false));
        HideBubble();
    }

    private void ResetEditMode()
    {
        _editMode = false;
        _sceneCombo.Visible = false;
        _secondaryButton.Text = "✕ 让我改";
        _primaryButton.Text = "✓ 是";
        _laterButton.Visible = true;
    }

    private void LayoutBubble()
    {
        _concurrentPanel.Visible = _concurrentBoxes.Count > 0;
        var concurrentY = _editMode ? 106 : 82;
        _concurrentPanel.Location = new Point(Margin, concurrentY);

        var buttonsY = _editMode ? 164 : 132;
        if (_editMode)
            _sceneCombo.Location = new Point(Margin, 78);
        _primaryButton.Location = new Point(BubbleWidth - Margin - _primaryButton.Width, buttonsY);
        _secondaryButton.Location = new Point(_primaryButton.Left - _secondaryButton.Width - 8, buttonsY);
        _laterButton.Location = new Point(_secondaryButton.Left - _laterButton.Width - 8, buttonsY);

        var height = _editMode ? 200 : (buttonsY + 36);
        ClientSize = new Size(BubbleWidth, height);
        _titleLabel.Top = 16;
        _infoLabel.Top = 48;
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
            Padding = new Padding(16, 6, 16, 6),
            BackColor = back,
            ForeColor = fore,
            Font = new Font("Microsoft YaHei UI", 10f),
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
            _tagFont.Dispose();
            foreach (Control c in _concurrentPanel.Controls)
                c.Dispose();
        }
        base.Dispose(disposing); // 基类负责释放 Region 与所有子控件
    }
}
