using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewWikiAgent.Config;

namespace StardewWikiAgent.UI;

/// <summary>A dependency-free, vanilla-style in-game editor for every <see cref="ModConfig"/> option.</summary>
internal sealed class ModConfigMenu : IClickableMenu
{
    private const int OuterMargin = 32;
    private const int HeaderHeight = 76;
    private const int FooterHeight = 112;
    private const int RowHeight = 76;
    private const int ScrollbarWidth = 48;

    private static readonly Rectangle UpArrowSource = new(421, 459, 11, 12);
    private static readonly Rectangle DownArrowSource = new(421, 472, 11, 12);
    private static readonly Rectangle ScrollThumbSource = new(435, 463, 6, 10);
    private static readonly Rectangle CheckboxOffSource = new(227, 425, 9, 9);
    private static readonly Rectangle CheckboxOnSource = new(236, 425, 9, 9);

    private readonly Action<ModConfig> onSave;
    private readonly Action? onCancel;
    private readonly List<SettingRow> rows = new();

    private ModConfig working;
    private Rectangle contentBounds;
    private Rectangle upArrowBounds;
    private Rectangle downArrowBounds;
    private Rectangle scrollTrackBounds;
    private Rectangle scrollThumbBounds;
    private Rectangle defaultsButtonBounds;
    private Rectangle cancelButtonBounds;
    private Rectangle saveButtonBounds;
    private int firstVisibleRow;
    private int visibleRowCount;
    private bool draggingScrollbar;
    private bool saved;
    private bool closed;
    private string? validationMessage;
    private bool validationMessageIsError;
    private string? hoverHelp;

    /// <param name="current">The live configuration. The menu never mutates this instance.</param>
    /// <param name="onSave">Receives a validated copy when the player clicks Save.</param>
    /// <param name="onCancel">Optional callback invoked when the menu closes without saving.</param>
    public ModConfigMenu(ModConfig current, Action<ModConfig> onSave, Action? onCancel = null)
        : base(0, 0, 0, 0, showUpperRightCloseButton: true)
    {
        this.onSave = onSave ?? throw new ArgumentNullException(nameof(onSave));
        this.onCancel = onCancel;
        this.working = CopyOf(current ?? throw new ArgumentNullException(nameof(current)));

        this.BuildRows();
        this.LayoutMenu();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        this.LayoutMenu();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (this.upperRightCloseButton?.containsPoint(x, y) == true)
        {
            this.CloseWithoutSaving();
            return;
        }

        if (this.upArrowBounds.Contains(x, y))
        {
            this.ScrollBy(-1);
            return;
        }
        if (this.downArrowBounds.Contains(x, y))
        {
            this.ScrollBy(1);
            return;
        }
        if (this.scrollThumbBounds.Contains(x, y))
        {
            this.draggingScrollbar = true;
            return;
        }
        if (this.scrollTrackBounds.Contains(x, y))
        {
            this.ScrollToMouse(y);
            return;
        }

        if (this.defaultsButtonBounds.Contains(x, y))
        {
            Game1.playSound("smallSelect");
            this.working = new ModConfig();
            this.RefreshControlsFromWorkingCopy();
            this.validationMessage = "已恢复默认值；点击保存后才会生效。";
            this.validationMessageIsError = false;
            return;
        }
        if (this.cancelButtonBounds.Contains(x, y))
        {
            this.CloseWithoutSaving();
            return;
        }
        if (this.saveButtonBounds.Contains(x, y))
        {
            this.TrySaveAndClose();
            return;
        }

        SettingRow? clicked = this.FindVisibleRowAt(x, y);
        if (clicked is null)
        {
            this.SelectTextBox(null);
            return;
        }

        switch (clicked.Kind)
        {
            case SettingKind.Text:
            case SettingKind.Integer:
                this.SelectTextBox(clicked);
                break;

            case SettingKind.Boolean:
                this.SelectTextBox(null);
                clicked.BoolValue = !clicked.BoolValue;
                Game1.playSound("drumkit6");
                break;

            case SettingKind.Choice:
                this.SelectTextBox(null);
                clicked.CycleChoice();
                Game1.playSound("smallSelect");
                break;
        }
    }

    public override void leftClickHeld(int x, int y)
    {
        if (this.draggingScrollbar)
            this.ScrollToMouse(y);
    }

    public override void releaseLeftClick(int x, int y)
    {
        this.draggingScrollbar = false;
        base.releaseLeftClick(x, y);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        this.ScrollBy(direction > 0 ? -1 : 1);
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            this.CloseWithoutSaving();
            return;
        }

        if (key == Keys.Tab)
        {
            this.FocusNextTextBox(Keyboard.GetState().IsKeyDown(Keys.LeftShift)
                || Keyboard.GetState().IsKeyDown(Keys.RightShift) ? -1 : 1);
            return;
        }

        if (!this.rows.Any(row => row.TextBox?.Selected == true))
        {
            if (key == Keys.Up)
            {
                this.ScrollBy(-1);
                return;
            }
            if (key == Keys.Down)
            {
                this.ScrollBy(1);
                return;
            }
        }

        base.receiveKeyPress(key);
    }

    public override void performHoverAction(int x, int y)
    {
        this.hoverHelp = this.FindVisibleRowAt(x, y)?.Help;
        base.performHoverAction(x, y);
    }

    protected override void cleanupBeforeExit()
    {
        this.SelectTextBox(null);
        if (!this.closed && !this.saved)
        {
            this.closed = true;
            this.onCancel?.Invoke();
        }
        base.cleanupBeforeExit();
    }

    public override void draw(SpriteBatch b)
    {
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.55f);
        drawTextureBox(b, this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, Color.White);

        string title = "Stardew Wiki AI 助手设置";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(
            Game1.dialogueFont,
            title,
            new Vector2(this.xPositionOnScreen + (this.width - titleSize.X) / 2f, this.yPositionOnScreen + 20),
            Game1.textColor
        );

        drawTextureBox(
            b,
            this.contentBounds.X - 12,
            this.contentBounds.Y - 12,
            this.contentBounds.Width + 24,
            this.contentBounds.Height + 24,
            Color.White
        );

        this.hoverHelp = null;
        int mouseX = Game1.getOldMouseX();
        int mouseY = Game1.getOldMouseY();
        int lastRow = Math.Min(this.rows.Count, this.firstVisibleRow + this.visibleRowCount);
        for (int index = this.firstVisibleRow; index < lastRow; index++)
        {
            SettingRow row = this.rows[index];
            int visibleIndex = index - this.firstVisibleRow;
            Rectangle rowBounds = this.GetRowBounds(visibleIndex);
            this.DrawRow(b, row, rowBounds, rowBounds.Contains(mouseX, mouseY));
            if (rowBounds.Contains(mouseX, mouseY))
                this.hoverHelp = row.Help;
        }

        this.DrawScrollbar(b);
        this.DrawButton(b, this.defaultsButtonBounds, "恢复默认", this.defaultsButtonBounds.Contains(mouseX, mouseY));
        this.DrawButton(b, this.cancelButtonBounds, "取消", this.cancelButtonBounds.Contains(mouseX, mouseY));
        this.DrawButton(b, this.saveButtonBounds, "保存", this.saveButtonBounds.Contains(mouseX, mouseY));

        string footerText = this.validationMessage
            ?? this.hoverHelp
            ?? "鼠标滚轮或方向键滚动；Tab 切换输入框。保存后重启 SMAPI 即可全部生效。";
        Color footerColor = this.validationMessageIsError ? Color.Firebrick : Game1.unselectedOptionColor;
        b.DrawString(
            Game1.smallFont,
            TruncateToWidth(footerText, this.width - 72, Game1.smallFont),
            new Vector2(this.xPositionOnScreen + 36, this.saveButtonBounds.Y - 36),
            footerColor
        );

        base.draw(b);
        this.drawMouse(b);
    }

    private void BuildRows()
    {
        this.rows.Clear();
        this.rows.Add(SettingRow.Text("LLM 服务地址", "OpenAI 兼容接口的基础地址。", value => this.working.BaseUrl = value));
        this.rows.Add(SettingRow.Text("API Key", "仅保存在本机 config.json；输入内容会被遮罩。", value => this.working.ApiKey = value, password: true));
        this.rows.Add(SettingRow.Text("模型", "发送请求时使用的模型名称。", value => this.working.Model = value));
        this.rows.Add(SettingRow.Text("Wiki API 地址", "中文 Stardew Valley Wiki 的 MediaWiki API 地址。", value => this.working.WikiApiUrl = value));
        this.rows.Add(SettingRow.Integer("请求超时（秒）", "允许范围：5–300。", value => this.working.RequestTimeoutSeconds = value, 5, 300));
        this.rows.Add(SettingRow.Integer("最多工具步骤", "单次问题允许范围：1–12。", value => this.working.MaxAgentSteps = value, 1, 12));
        this.rows.Add(SettingRow.Integer("回答最多字符", "允许范围：200–8000。", value => this.working.MaxAnswerCharacters = value, 200, 8000));
        this.rows.Add(SettingRow.Integer("最大响应 Token", "允许范围：1024–131072。", value => this.working.MaxResponseTokens = value, 1024, 131072));
        this.rows.Add(SettingRow.Choice("思考等级", "低 / 中 / 高；更高等级通常更慢且消耗更多。", new[] { "low", "medium", "high" }, value => this.working.ReasoningEffort = value));
        this.rows.Add(SettingRow.Boolean("包含游戏状态", "允许助手在提问时读取基础游戏状态。", value => this.working.IncludeGameContext = value));
        this.rows.Add(SettingRow.Boolean("启用任务日志工具", "允许助手按需读取当前任务日志。", value => this.working.EnableQuestLogTool = value));
        this.rows.Add(SettingRow.Boolean("允许完整背包读取", "仅影响 mode=all；关闭后仍可查询容量和筛选物品。", value => this.working.AllowFullInventoryRead = value));
        this.rows.Add(SettingRow.Boolean("启用语音输入", "启用本地中文语音识别。", value => this.working.EnableVoiceInput = value));
        this.rows.Add(SettingRow.Text("语音快捷键", "SMAPI 按键名称，例如 V、F8 或 LeftShift。", value => this.working.VoiceHotkey = value));
        this.rows.Add(SettingRow.Integer("语音最长秒数", "单次录音允许范围：3–120 秒。", value => this.working.VoiceMaxSeconds = value, 3, 120));
        this.rows.Add(SettingRow.Integer("语音识别线程", "允许范围：1–8；线程越多，占用的 CPU 越高。", value => this.working.VoiceNumThreads = value, 1, 8));

        this.RefreshControlsFromWorkingCopy();
    }

    private void RefreshControlsFromWorkingCopy()
    {
        string[] textValues =
        {
            this.working.BaseUrl,
            this.working.ApiKey,
            this.working.Model,
            this.working.WikiApiUrl,
            this.working.RequestTimeoutSeconds.ToString(),
            this.working.MaxAgentSteps.ToString(),
            this.working.MaxAnswerCharacters.ToString(),
            this.working.MaxResponseTokens.ToString(),
        };

        int textIndex = 0;
        foreach (SettingRow row in this.rows)
        {
            if (row.TextBox is not null)
            {
                string value = textIndex < textValues.Length
                    ? textValues[textIndex++]
                    : row.Label switch
                    {
                        "语音快捷键" => this.working.VoiceHotkey,
                        "语音最长秒数" => this.working.VoiceMaxSeconds.ToString(),
                        "语音识别线程" => this.working.VoiceNumThreads.ToString(),
                        _ => "",
                    };
                row.TextBox.Text = value;
                row.TextBox.Selected = false;
            }
        }

        this.rows.Single(row => row.Label == "思考等级").ChoiceValue = this.working.ReasoningEffort.ToLowerInvariant();
        this.rows.Single(row => row.Label == "包含游戏状态").BoolValue = this.working.IncludeGameContext;
        this.rows.Single(row => row.Label == "启用任务日志工具").BoolValue = this.working.EnableQuestLogTool;
        this.rows.Single(row => row.Label == "允许完整背包读取").BoolValue = this.working.AllowFullInventoryRead;
        this.rows.Single(row => row.Label == "启用语音输入").BoolValue = this.working.EnableVoiceInput;
    }

    private void LayoutMenu()
    {
        int viewportWidth = Game1.uiViewport.Width;
        int viewportHeight = Game1.uiViewport.Height;
        this.width = Math.Max(560, Math.Min(1080, viewportWidth - OuterMargin * 2));
        this.height = Math.Max(480, Math.Min(820, viewportHeight - OuterMargin * 2));
        this.xPositionOnScreen = (viewportWidth - this.width) / 2;
        this.yPositionOnScreen = (viewportHeight - this.height) / 2;

        this.contentBounds = new Rectangle(
            this.xPositionOnScreen + 36,
            this.yPositionOnScreen + HeaderHeight + 12,
            this.width - 72 - ScrollbarWidth,
            this.height - HeaderHeight - FooterHeight - 24
        );
        this.visibleRowCount = Math.Max(1, this.contentBounds.Height / RowHeight);
        this.firstVisibleRow = Math.Clamp(this.firstVisibleRow, 0, this.MaxScroll);

        int scrollbarX = this.contentBounds.Right + 18;
        this.upArrowBounds = new Rectangle(scrollbarX, this.contentBounds.Y, 44, 48);
        this.downArrowBounds = new Rectangle(scrollbarX, this.contentBounds.Bottom - 48, 44, 48);
        this.scrollTrackBounds = new Rectangle(scrollbarX + 8, this.upArrowBounds.Bottom + 4, 28, Math.Max(16, this.downArrowBounds.Top - this.upArrowBounds.Bottom - 8));

        int buttonY = this.yPositionOnScreen + this.height - 68;
        int buttonWidth = Math.Max(112, Math.Min(176, (this.width - 120) / 3));
        this.defaultsButtonBounds = new Rectangle(this.xPositionOnScreen + 36, buttonY, buttonWidth, 52);
        this.saveButtonBounds = new Rectangle(this.xPositionOnScreen + this.width - 36 - buttonWidth, buttonY, buttonWidth, 52);
        this.cancelButtonBounds = new Rectangle(this.saveButtonBounds.X - 18 - buttonWidth, buttonY, buttonWidth, 52);

        if (this.upperRightCloseButton is not null)
            this.upperRightCloseButton.bounds = new Rectangle(this.xPositionOnScreen + this.width - 36, this.yPositionOnScreen - 8, 48, 48);

        this.PositionVisibleTextBoxes();
        this.UpdateScrollThumb();
    }

    private void PositionVisibleTextBoxes()
    {
        foreach (SettingRow row in this.rows)
        {
            if (row.TextBox is not null)
            {
                row.TextBox.X = -10000;
                row.TextBox.Y = -10000;
            }
        }

        int lastRow = Math.Min(this.rows.Count, this.firstVisibleRow + this.visibleRowCount);
        for (int index = this.firstVisibleRow; index < lastRow; index++)
        {
            SettingRow row = this.rows[index];
            if (row.TextBox is null)
                continue;

            Rectangle rowBounds = this.GetRowBounds(index - this.firstVisibleRow);
            Rectangle controlBounds = this.GetControlBounds(rowBounds);
            row.TextBox.X = controlBounds.X;
            row.TextBox.Y = controlBounds.Y + 4;
            row.TextBox.Width = controlBounds.Width;
            // Keep limitWidth off (see CreateTextBox): the box scrolls to reveal the caret
            // while typing and never truncates the stored value.
            row.TextBox.limitWidth = false;
        }
    }

    private Rectangle GetRowBounds(int visibleIndex) => new(
        this.contentBounds.X,
        this.contentBounds.Y + visibleIndex * RowHeight,
        this.contentBounds.Width,
        RowHeight - 4
    );

    private Rectangle GetControlBounds(Rectangle rowBounds)
    {
        int labelWidth = Math.Clamp((int)(rowBounds.Width * 0.36f), 190, 340);
        return new Rectangle(rowBounds.X + labelWidth, rowBounds.Y + 8, rowBounds.Width - labelWidth - 8, 52);
    }

    private void DrawRow(SpriteBatch b, SettingRow row, Rectangle rowBounds, bool hovered)
    {
        if (hovered)
            b.Draw(Game1.staminaRect, rowBounds, Color.Wheat * 0.35f);

        b.DrawString(Game1.smallFont, row.Label, new Vector2(rowBounds.X + 12, rowBounds.Y + 19), Game1.textColor);
        Rectangle controlBounds = this.GetControlBounds(rowBounds);

        switch (row.Kind)
        {
            case SettingKind.Text:
            case SettingKind.Integer:
                row.TextBox!.Draw(b);
                break;

            case SettingKind.Boolean:
                Rectangle checkboxBounds = new(controlBounds.X + 8, controlBounds.Y + 7, 36, 36);
                b.Draw(
                    Game1.mouseCursors,
                    checkboxBounds,
                    row.BoolValue ? CheckboxOnSource : CheckboxOffSource,
                    Color.White
                );
                b.DrawString(
                    Game1.smallFont,
                    row.BoolValue ? "开启" : "关闭",
                    new Vector2(checkboxBounds.Right + 12, controlBounds.Y + 12),
                    row.BoolValue ? Game1.textColor : Game1.unselectedOptionColor
                );
                break;

            case SettingKind.Choice:
                this.DrawButton(b, controlBounds, LocalizeChoice(row.ChoiceValue), hovered);
                break;
        }
    }

    private void DrawScrollbar(SpriteBatch b)
    {
        Color upColor = this.firstVisibleRow > 0 ? Color.White : Color.Gray * 0.7f;
        Color downColor = this.firstVisibleRow < this.MaxScroll ? Color.White : Color.Gray * 0.7f;
        b.Draw(Game1.mouseCursors, this.upArrowBounds, UpArrowSource, upColor);
        b.Draw(Game1.mouseCursors, this.downArrowBounds, DownArrowSource, downColor);
        drawTextureBox(
            b,
            this.scrollTrackBounds.X,
            this.scrollTrackBounds.Y,
            this.scrollTrackBounds.Width,
            this.scrollTrackBounds.Height,
            Color.White
        );
        b.Draw(Game1.mouseCursors, this.scrollThumbBounds, ScrollThumbSource, Color.White);
    }

    private void DrawButton(SpriteBatch b, Rectangle bounds, string text, bool hovered)
    {
        drawTextureBox(b, bounds.X, bounds.Y, bounds.Width, bounds.Height, hovered ? Color.Wheat : Color.White);
        Vector2 size = Game1.smallFont.MeasureString(text);
        b.DrawString(
            Game1.smallFont,
            text,
            new Vector2(bounds.Center.X - size.X / 2f, bounds.Center.Y - size.Y / 2f),
            Game1.textColor
        );
    }

    private SettingRow? FindVisibleRowAt(int x, int y)
    {
        if (!this.contentBounds.Contains(x, y))
            return null;

        int visibleIndex = (y - this.contentBounds.Y) / RowHeight;
        int index = this.firstVisibleRow + visibleIndex;
        return visibleIndex < this.visibleRowCount && index < this.rows.Count ? this.rows[index] : null;
    }

    private void SelectTextBox(SettingRow? target)
    {
        foreach (SettingRow row in this.rows)
            if (row.TextBox is not null)
                row.TextBox.Selected = ReferenceEquals(row, target);
    }

    private void FocusNextTextBox(int direction)
    {
        List<int> textRows = this.rows
            .Select((row, index) => (row, index))
            .Where(pair => pair.row.TextBox is not null)
            .Select(pair => pair.index)
            .ToList();
        if (textRows.Count == 0)
            return;

        int currentRow = this.rows.FindIndex(row => row.TextBox?.Selected == true);
        int currentTextIndex = textRows.IndexOf(currentRow);
        int nextTextIndex = currentTextIndex < 0
            ? (direction > 0 ? 0 : textRows.Count - 1)
            : (currentTextIndex + direction + textRows.Count) % textRows.Count;
        int nextRow = textRows[nextTextIndex];

        if (nextRow < this.firstVisibleRow)
            this.firstVisibleRow = nextRow;
        else if (nextRow >= this.firstVisibleRow + this.visibleRowCount)
            this.firstVisibleRow = Math.Min(this.MaxScroll, nextRow - this.visibleRowCount + 1);

        this.PositionVisibleTextBoxes();
        this.UpdateScrollThumb();
        this.SelectTextBox(this.rows[nextRow]);
    }

    private void ScrollBy(int amount)
    {
        int next = Math.Clamp(this.firstVisibleRow + amount, 0, this.MaxScroll);
        if (next == this.firstVisibleRow)
            return;

        this.SelectTextBox(null);
        this.firstVisibleRow = next;
        this.PositionVisibleTextBoxes();
        this.UpdateScrollThumb();
        this.validationMessage = null;
        this.validationMessageIsError = false;
        Game1.playSound("shiny4");
    }

    private void ScrollToMouse(int mouseY)
    {
        if (this.MaxScroll <= 0)
            return;

        int available = Math.Max(1, this.scrollTrackBounds.Height - this.scrollThumbBounds.Height);
        float fraction = Math.Clamp((mouseY - this.scrollTrackBounds.Y - this.scrollThumbBounds.Height / 2f) / available, 0f, 1f);
        int next = (int)Math.Round(fraction * this.MaxScroll);
        if (next != this.firstVisibleRow)
        {
            this.SelectTextBox(null);
            this.firstVisibleRow = next;
            this.PositionVisibleTextBoxes();
            this.UpdateScrollThumb();
        }
    }

    private void UpdateScrollThumb()
    {
        int thumbHeight = this.MaxScroll == 0
            ? this.scrollTrackBounds.Height
            : Math.Max(28, (int)(this.scrollTrackBounds.Height * (this.visibleRowCount / (float)this.rows.Count)));
        int travel = Math.Max(0, this.scrollTrackBounds.Height - thumbHeight);
        int y = this.scrollTrackBounds.Y + (this.MaxScroll == 0 ? 0 : (int)Math.Round(travel * (this.firstVisibleRow / (float)this.MaxScroll)));
        this.scrollThumbBounds = new Rectangle(this.scrollTrackBounds.X, y, this.scrollTrackBounds.Width, thumbHeight);
    }

    private int MaxScroll => Math.Max(0, this.rows.Count - this.visibleRowCount);

    private void TrySaveAndClose()
    {
        this.validationMessage = null;
        this.validationMessageIsError = false;
        foreach (SettingRow row in this.rows)
        {
            if (!row.TryApply(out string? error))
            {
                this.validationMessage = error;
                this.validationMessageIsError = true;
                int rowIndex = this.rows.IndexOf(row);
                this.firstVisibleRow = Math.Clamp(rowIndex, 0, this.MaxScroll);
                this.PositionVisibleTextBoxes();
                this.UpdateScrollThumb();
                this.SelectTextBox(row);
                Game1.playSound("cancel");
                return;
            }
        }

        this.saved = true;
        this.closed = true;
        Game1.playSound("bigSelect");
        this.SelectTextBox(null);
        this.onSave(CopyOf(this.working));
        this.exitThisMenu(playSound: false);
    }

    private void CloseWithoutSaving()
    {
        if (!this.closed)
        {
            this.closed = true;
            this.onCancel?.Invoke();
        }
        Game1.playSound("cancel");
        this.SelectTextBox(null);
        this.exitThisMenu(playSound: false);
    }

    private static string LocalizeChoice(string value) => value.ToLowerInvariant() switch
    {
        "low" => "低",
        "high" => "高",
        _ => "中",
    };

    private static string TruncateToWidth(string text, int maxWidth, SpriteFont font)
    {
        if (font.MeasureString(text).X <= maxWidth)
            return text;

        const string suffix = "…";
        int length = text.Length;
        while (length > 0 && font.MeasureString(text[..length] + suffix).X > maxWidth)
            length--;
        return text[..length] + suffix;
    }

    private static ModConfig CopyOf(ModConfig source) => new()
    {
        BaseUrl = source.BaseUrl,
        ApiKey = source.ApiKey,
        Model = source.Model,
        WikiApiUrl = source.WikiApiUrl,
        RequestTimeoutSeconds = source.RequestTimeoutSeconds,
        MaxAgentSteps = source.MaxAgentSteps,
        MaxAnswerCharacters = source.MaxAnswerCharacters,
        MaxResponseTokens = source.MaxResponseTokens,
        ReasoningEffort = source.ReasoningEffort,
        IncludeGameContext = source.IncludeGameContext,
        EnableQuestLogTool = source.EnableQuestLogTool,
        AllowFullInventoryRead = source.AllowFullInventoryRead,
        EnableVoiceInput = source.EnableVoiceInput,
        VoiceHotkey = source.VoiceHotkey,
        VoiceMaxSeconds = source.VoiceMaxSeconds,
        VoiceNumThreads = source.VoiceNumThreads,
    };

    private enum SettingKind
    {
        Text,
        Integer,
        Boolean,
        Choice,
    }

    private sealed class SettingRow
    {
        private Action<string>? setText;
        private Action<int>? setInteger;
        private Action<bool>? setBoolean;
        private Action<string>? setChoice;
        private IReadOnlyList<string> choices = Array.Empty<string>();
        private int min;
        private int max;

        private SettingRow(string label, string help, SettingKind kind)
        {
            this.Label = label;
            this.Help = help;
            this.Kind = kind;
        }

        public string Label { get; }
        public string Help { get; }
        public SettingKind Kind { get; }
        public TextBox? TextBox { get; private init; }
        public bool BoolValue { get; set; }
        public string ChoiceValue { get; set; } = "medium";

        public static SettingRow Text(string label, string help, Action<string> setter, bool password = false)
        {
            TextBox textBox = CreateTextBox(password, numbersOnly: false);
            return new SettingRow(label, help, SettingKind.Text)
            {
                TextBox = textBox,
                setText = setter,
            };
        }

        public static SettingRow Integer(string label, string help, Action<int> setter, int min, int max)
        {
            TextBox textBox = CreateTextBox(password: false, numbersOnly: true);
            return new SettingRow(label, help, SettingKind.Integer)
            {
                TextBox = textBox,
                setInteger = setter,
                min = min,
                max = max,
            };
        }

        public static SettingRow Boolean(string label, string help, Action<bool> setter) => new(label, help, SettingKind.Boolean)
        {
            setBoolean = setter,
        };

        public static SettingRow Choice(string label, string help, IReadOnlyList<string> choices, Action<string> setter) => new(label, help, SettingKind.Choice)
        {
            choices = choices,
            setChoice = setter,
        };

        public void CycleChoice()
        {
            int current = -1;
            for (int index = 0; index < this.choices.Count; index++)
            {
                if (string.Equals(this.choices[index], this.ChoiceValue, StringComparison.OrdinalIgnoreCase))
                {
                    current = index;
                    break;
                }
            }
            this.ChoiceValue = this.choices[(current + 1 + this.choices.Count) % this.choices.Count];
        }

        public bool TryApply(out string? error)
        {
            error = null;
            switch (this.Kind)
            {
                case SettingKind.Text:
                    this.setText!(this.TextBox!.Text.Trim());
                    return true;

                case SettingKind.Integer:
                    if (!int.TryParse(this.TextBox!.Text, out int value) || value < this.min || value > this.max)
                    {
                        error = $"“{this.Label}”必须是 {this.min}–{this.max} 之间的整数。";
                        return false;
                    }
                    this.setInteger!(value);
                    return true;

                case SettingKind.Boolean:
                    this.setBoolean!(this.BoolValue);
                    return true;

                case SettingKind.Choice:
                    this.setChoice!(this.ChoiceValue);
                    return true;

                default:
                    throw new InvalidOperationException("Unknown config row type.");
            }
        }

        private static TextBox CreateTextBox(bool password, bool numbersOnly)
        {
            TextBox textBox = new(
                Game1.content.Load<Texture2D>("LooseSprites\\textBox"),
                null,
                Game1.smallFont,
                Game1.textColor
            )
            {
                PasswordBox = password,
                numbersOnly = numbersOnly,
                // The vanilla Text setter trims characters off the end whenever limitWidth is
                // true and the string is wider than the box, which silently corrupts long
                // values (e.g. the Wiki/LLM URLs) the moment they are assigned. Keep it off so
                // the box scrolls horizontally and preserves the full text instead.
                limitWidth = false,
            };
            return textBox;
        }
    }
}
