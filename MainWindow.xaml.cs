using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using BlindNotepad.Models;
using BlindNotepad.Services;
using Microsoft.Win32;

namespace BlindNotepad;

/// <summary>
/// 主窗口。负责网址收藏、记事本、密码收藏、日记与证件保存五个模块的展示、纯键盘操作与读屏播报。
/// 集成自动锁定、审计日志、防截屏、备份恢复、导入、重复检测、网址健康检查、
/// 自定义快捷键、收藏置顶、批量操作等增强功能。
/// </summary>
public partial class MainWindow : Window
{
    private enum Module { Url, Snippet, Password, Note, IdDocument, Accounting }

    private Module _currentModule = Module.Url;
    private bool _isLoaded;

    // 网址数据
    private UrlCollectionData _urlData = new();

    // 密码数据（仅解锁后有效）
    private VaultData? _vault;
    private bool _isUnlocked;
    private string? _masterPassword;
    private bool _firstTimeSetup;

    // 当前分类/标签筛选（null 表示全部）
    private string? _currentFilter;

    // 过滤后的列表缓存
    private List<UrlEntry> _filteredUrls = new();
    private List<PasswordEntry> _filteredPasswords = new();
    private List<NoteEntry> _filteredNotes = new();
    private List<SnippetEntry> _filteredSnippets = new();
    private List<IdDocumentEntry> _filteredIdDocuments = new();
    private List<AccountingEntry> _filteredAccountings = new();
    private SnippetCollectionData _snippetData = new();

    // 无障碍与剪贴板服务
    private readonly AccessibilityService _a11y = new();
    private readonly ClipboardService _clipboard = new();

    // 自动锁定计时器
    private DispatcherTimer? _autoLockTimer;
    private DateTime _lastActivityTime = DateTime.Now;

    // 快捷键配置
    private List<ShortcutBinding> _shortcuts = new();

    // 防截屏：原始窗口样式
    private bool _antiScreenshotActive;

    // 排序模式：0=按名称, 1=按创建时间, 2=按修改时间
    private int _sortMode;

    // 全局热键
    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID_TOGGLE = 9001;
    private HwndSource? _hwndSource;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // MOD_CONTROL=0x0002, MOD_SHIFT=0x0004, VK_F2=0x71

    // 草稿自动保存计时器
    private DispatcherTimer? _draftSaveTimer;
    private string? _draftKey;

    public MainWindow()
    {
        InitializeComponent();
        _shortcuts = ShortcutConfigService.Load();
    }

    // =========================================================================
    // 窗口加载与关闭
    // =========================================================================

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _isLoaded = true;
        LoadUrlModule();
        SwitchToModule(Module.Url);
        SetupAutoLockTimer();
        SetupDraftSaveTimer();

        // 注册全局热键 Ctrl+Shift+F2
        var helper = new WindowInteropHelper(this);
        _hwndSource = HwndSource.FromHwnd(helper.Handle);
        _hwndSource?.AddHook(WndProc);
        RegisterHotKey(helper.Handle, HOTKEY_ID_TOGGLE, 0x0002 | 0x0004, 0x71); // Ctrl+Shift+F2

        // 日记连续记录提醒
        CheckDiaryStreak();

        // 启动时静默检查更新
        _ = CheckForUpdateAsync(silent: true);

        var screenReader = _a11y.IsScreenReaderRunning();
        Announce(screenReader
            ? "欢迎使用随心记，已检测到读屏软件。当前：网址收藏。按Ctrl+Shift+F2可随时显示或隐藏窗口。"
            : "欢迎使用随心记。当前：网址收藏。按Ctrl+Shift+F2可随时显示或隐藏窗口。");
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // 注销全局热键
        var helper = new WindowInteropHelper(this);
        UnregisterHotKey(helper.Handle, HOTKEY_ID_TOGGLE);
        _hwndSource?.RemoveHook(WndProc);

        _clipboard.StopAutoClearTimer();
        _clipboard.ClearClipboard();
        _autoLockTimer?.Stop();
        _draftSaveTimer?.Stop();
        DisableAntiScreenshot();
    }

    /// <summary>全局热键消息处理：Ctrl+Shift+F2 切换窗口显示/隐藏。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID_TOGGLE)
        {
            ToggleWindowVisibility();
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>切换窗口可见性：可见时隐藏，隐藏时显示。</summary>
    private void ToggleWindowVisibility()
    {
        if (IsVisible && WindowState != WindowState.Minimized)
        {
            // 窗口当前可见 → 隐藏到托盘
            Hide();
        }
        else
        {
            // 窗口隐藏或最小化 → 显示并激活
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Focus();
        }
    }

    // =========================================================================
    // 自动锁定
    // =========================================================================

    private void SetupAutoLockTimer()
    {
        _autoLockTimer?.Stop();
        var minutes = _vault?.Settings.AutoLockMinutes ?? 5;
        if (minutes <= 0)
            return;

        _autoLockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _autoLockTimer.Tick += OnAutoLockTick;
        _autoLockTimer.Start();
    }

    // =========================================================================
    // 草稿自动保存
    // =========================================================================

    /// <summary>设置草稿自动保存计时器，每 30 秒触发一次。</summary>
    private void SetupDraftSaveTimer()
    {
        _draftSaveTimer?.Stop();
        _draftSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _draftSaveTimer.Tick += OnDraftSaveTick;
        _draftSaveTimer.Start();
    }

    private void OnDraftSaveTick(object? sender, EventArgs e)
    {
        // 草稿由编辑对话框自行管理定时保存
        // 此计时器保留用于窗口级别的心跳检查
    }

    /// <summary>检查指定模块是否有未恢复的草稿，有则提示用户。</summary>
    private void CheckAndOfferDraftRestore(string moduleKey)
    {
        if (!DraftService.Exists(moduleKey)) return;
        var draft = DraftService.Load(moduleKey);
        if (draft is null) return;

        var moduleLabel = moduleKey switch
        {
            "snippet" => "记事本",
            "note" => "日记",
            _ => moduleKey
        };

        var timeStr = draft.SaveTime.ToString("yyyy-MM-dd HH:mm");
        var preview = string.IsNullOrEmpty(draft.Title) ? "（无标题）" : draft.Title;
        var result = MessageBox.Show(this,
            $"检测到{moduleLabel}模块有未保存的草稿：\n\n" +
            $"标题：{preview}\n" +
            $"保存时间：{timeStr}\n\n" +
            $"是否恢复草稿？\n（点击\"否\"将丢弃草稿）",
            "恢复草稿", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result == MessageBoxResult.Yes)
        {
            // 打开编辑对话框并恢复草稿内容
            RestoreDraftToDialog(moduleKey, draft);
        }
        else
        {
            DraftService.Clear(moduleKey);
            Announce("草稿已丢弃。");
        }
    }

    /// <summary>将草稿恢复到对应模块的编辑对话框。</summary>
    private void RestoreDraftToDialog(string moduleKey, DraftService.DraftData draft)
    {
        if (moduleKey == "snippet")
        {
            var dialog = new SnippetEditDialog(null, _snippetData.Categories) { Owner = this };
            dialog.SetDraftContent(draft.Title, draft.Category, draft.Content);
            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                var result = dialog.Result;
                _snippetData.Entries.Add(result);
                EnsureSnippetCategoryExists(result.Category);
                StorageService.SaveSnippets(_snippetData);
                RefreshTree(); RefreshList(); SelectSnippetById(result.Id);
                Announce($"已从草稿恢复笔记：{result.Title}。");
            }
            DraftService.Clear(moduleKey);
        }
        else if (moduleKey == "note")
        {
            if (!EnsureUnlocked()) return;
            var dialog = new NoteEditDialog(null) { Owner = this };
            dialog.SetDraftContent(draft.Title, draft.Category, draft.Weather, draft.Mood, draft.Content);
            if (dialog.ShowDialog() == true && dialog.Result is not null)
            {
                var result = dialog.Result;
                _vault!.Notes.Add(result);
                SavePasswordVault();
                RefreshTree(); RefreshList(); SelectNoteById(result.Id);
                Announce($"已从草稿恢复日记：{result.Title}。");
            }
            DraftService.Clear(moduleKey);
        }
    }

    private void OnAutoLockTick(object? sender, EventArgs e)
    {
        var minutes = _vault?.Settings.AutoLockMinutes ?? 5;
        if (minutes <= 0 || !_isUnlocked)
            return;

        var idle = (DateTime.Now - _lastActivityTime).TotalMinutes;
        if (idle >= minutes)
        {
            DoLock();
            Announce($"已闲置 {minutes} 分钟，密码库自动锁定。");
        }
    }

    private void ResetActivityTimer()
    {
        _lastActivityTime = DateTime.Now;
    }

    // =========================================================================
    // 防截屏保护
    // =========================================================================

    private void EnableAntiScreenshot()
    {
        if (_vault is null || !_vault.Settings.AntiScreenshot)
            return;

        try
        {
            // 使用 SetWindowDisplayAffinity 防止截屏
            var helper = new WindowInteropHelper(this);
            SetWindowDisplayAffinity(helper.Handle, 1); // WDA_MONITOR = 1
            _antiScreenshotActive = true;
        }
        catch
        {
            // 非Windows环境或API不可用时忽略
        }
    }

    private void DisableAntiScreenshot()
    {
        if (!_antiScreenshotActive)
            return;

        try
        {
            var helper = new WindowInteropHelper(this);
            SetWindowDisplayAffinity(helper.Handle, 0); // WDA_NONE = 0
            _antiScreenshotActive = false;
        }
        catch
        {
            // 忽略
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

    // =========================================================================
    // 数据加载与模块切换
    // =========================================================================

    private void LoadUrlModule()
    {
        _urlData = StorageService.LoadUrls();
        if (_urlData.Categories.Count == 0)
        {
            _urlData.Categories.Add("默认");
        }
        _snippetData = StorageService.LoadSnippets();
        if (_snippetData.Categories.Count == 0)
        {
            _snippetData.Categories.Add("默认");
        }
    }

    private void OnUrlModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.Url);
    }

    private void OnPasswordModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.Password);
    }

    private void OnNoteModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.Note);
    }

    private void OnSnippetModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.Snippet);
    }

    private void OnIdDocumentModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.IdDocument);
    }

    private void OnAccountingModuleChecked(object sender, RoutedEventArgs e)
    {
        if (_isLoaded) SwitchToModule(Module.Accounting);
    }

    private void SwitchToModule(Module module)
    {
        try
        {
            _currentModule = module;

            urlListBox.Visibility = Visibility.Collapsed;
            snippetListBox.Visibility = Visibility.Collapsed;
            passwordListBox.Visibility = Visibility.Collapsed;
            noteListBox.Visibility = Visibility.Collapsed;
            idDocumentListBox.Visibility = Visibility.Collapsed;
            accountingListBox.Visibility = Visibility.Collapsed;
            unlockPanel.Visibility = Visibility.Collapsed;
            masterPasswordConfirmBox.Visibility = Visibility.Collapsed;

            if (module == Module.Url)
            {
                urlListBox.Visibility = Visibility.Visible;
                addButton.Visibility = Visibility.Visible;
                RefreshTree();
                RefreshList();
                UpdateDetail();
                FocusList();
                UpdateStatus();
            }
            else if (module == Module.Snippet)
            {
                snippetListBox.Visibility = Visibility.Visible;
                addButton.Visibility = Visibility.Visible;
                RefreshTree();
                RefreshList();
                UpdateDetail();
                FocusList();
                UpdateStatus();
                CheckAndOfferDraftRestore("snippet");
            }
            else if (module == Module.Password)
            {
                RefreshTree();
                if (_isUnlocked && _vault is not null)
                {
                    passwordListBox.Visibility = Visibility.Visible;
                    addButton.Visibility = Visibility.Visible;
                    RefreshList();
                    UpdateDetail();
                    FocusList();
                    UpdateStatus();
                }
                else
                {
                    ShowUnlockPanel();
                }
            }
            else if (module == Module.Note)
            {
                RefreshTree();
                if (_isUnlocked && _vault is not null)
                {
                    noteListBox.Visibility = Visibility.Visible;
                    addButton.Visibility = Visibility.Visible;
                    RefreshList();
                    UpdateDetail();
                    FocusList();
                    UpdateStatus();
                    CheckAndOfferDraftRestore("note");
                }
                else
                {
                    ShowUnlockPanel();
                }
            }
            else if (module == Module.IdDocument)
            {
                RefreshTree();
                if (_isUnlocked && _vault is not null)
                {
                    idDocumentListBox.Visibility = Visibility.Visible;
                    addButton.Visibility = Visibility.Visible;
                    RefreshList();
                    UpdateDetail();
                    FocusList();
                    UpdateStatus();
                }
                else
                {
                    ShowUnlockPanel();
                }
            }
            else // Accounting
            {
                RefreshTree();
                if (_isUnlocked && _vault is not null)
                {
                    accountingListBox.Visibility = Visibility.Visible;
                    addButton.Visibility = Visibility.Visible;
                    RefreshList();
                    UpdateDetail();
                    FocusList();
                    UpdateStatus();
                    AnnounceMonthlySummary();
                }
                else
                {
                    ShowUnlockPanel();
                }
            }

            ResetActivityTimer();
        }
        catch (Exception ex)
        {
            HandleModuleSwitchException(ex);
        }
    }

    // =========================================================================
    // 主菜单显示/隐藏（按 ALT 弹出，再次按 ALT 或 ESC 隐藏）
    // =========================================================================
    private void ToggleMainMenu()
    {
        if (mainMenu is null) return;
        if (mainMenu.Visibility == Visibility.Visible)
            HideMainMenu();
        else
            ShowMainMenu();
    }

    private void ShowMainMenu()
    {
        if (mainMenu is null) return;
        mainMenu.Visibility = Visibility.Visible;
        // 聚焦第一个菜单项，使方向键和字母助记键可用
        if (mainMenu.Items.Count > 0 && mainMenu.Items[0] is MenuItem firstItem)
        {
            firstItem.Focus();
        }
    }

    private void HideMainMenu()
    {
        if (mainMenu is null) return;
        mainMenu.Visibility = Visibility.Collapsed;
        // 焦点回到当前模块的列表
        FocusList();
    }

    // =========================================================================
    // 兜底：模块切换/快捷键异常不再崩溃
    // =========================================================================
    private static void SafeSet(Action action)
    {
        if (action is null) return;
        try { action(); }
        catch (Exception ex) { HandleModuleSwitchException(ex); }
    }

    private static void HandleModuleSwitchException(Exception ex)
    {
        try
        {
            var log = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 模块切换/快捷键异常：\n{ex}\n\n";
            var logPath = Path.Combine(StorageService.AppDataDir, "error.log");
            File.AppendAllText(logPath, log);
        }
        catch { /* 写日志失败就放弃，不允许冒泡 */ }
    }

    private void ShowUnlockPanel()
    {
        passwordListBox.Visibility = Visibility.Collapsed;
        noteListBox.Visibility = Visibility.Collapsed;
        idDocumentListBox.Visibility = Visibility.Collapsed;
        accountingListBox.Visibility = Visibility.Collapsed;
        unlockPanel.Visibility = Visibility.Visible;
        addButton.Visibility = Visibility.Collapsed;

        _firstTimeSetup = !StorageService.VaultExists();
        if (_firstTimeSetup)
        {
            unlockPromptText.Text = "首次使用，请设置主密码：";
            masterPasswordConfirmBox.Visibility = Visibility.Visible;
            unlockButton.Content = "设置并进入(_S)";
            unlockHintText.Text = "请输入主密码并确认，按回车提交。主密码无法找回，请务必妥善保管。";
        }
        else
        {
            unlockPromptText.Text = "请输入主密码解锁：";
            masterPasswordConfirmBox.Visibility = Visibility.Collapsed;
            unlockButton.Content = "解锁(_U)";
            unlockHintText.Text = "输入主密码后按回车解锁。";
        }

        masterPasswordBox.Clear();
        masterPasswordConfirmBox.Clear();
        Dispatcher.BeginInvoke(new Action(() => masterPasswordBox.Focus()), DispatcherPriority.Loaded);
        UpdateStatus();
    }

    private void AttemptUnlock()
    {
        var password = masterPasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            Announce("请输入主密码。");
            masterPasswordBox.Focus();
            return;
        }

        if (_firstTimeSetup)
        {
            var confirm = masterPasswordConfirmBox.Password;
            if (!string.Equals(password, confirm, StringComparison.Ordinal))
            {
                Announce("两次输入的主密码不一致，请重新输入。");
                masterPasswordBox.Clear();
                masterPasswordConfirmBox.Clear();
                masterPasswordBox.Focus();
                return;
            }

            if (password.Length < 6)
            {
                Announce("主密码至少需要 6 位，请重新输入。");
                masterPasswordBox.Focus();
                return;
            }

            _vault = new VaultData();
            _masterPassword = password;
            _isUnlocked = true;
            StorageService.SaveVault(CryptoService.EncryptVault(_vault, password));
            SetupAutoLockTimer();
            EnableAntiScreenshot();
            Announce("主密码已设置，密码库已创建。当前共 0 条。");
            SwitchToModule(Module.Password);
            return;
        }

        var encrypted = StorageService.LoadVault();
        if (encrypted is null || !CryptoService.VerifyPassword(encrypted, password))
        {
            Announce("主密码错误，请重试。");
            AuditLogService.Log(_vault, "解锁", "主密码验证失败", false);
            masterPasswordBox.Clear();
            masterPasswordBox.Focus();
            return;
        }

        var vault = CryptoService.DecryptVault(encrypted, password);
        if (vault is null)
        {
            Announce("解密失败，请重试。");
            masterPasswordBox.Clear();
            masterPasswordBox.Focus();
            return;
        }

        _vault = vault;
        _masterPassword = password;
        _isUnlocked = true;
        SetupAutoLockTimer();
        EnableAntiScreenshot();
        AuditLogService.Log(_vault, "解锁", "密码库解锁成功");
        Announce($"密码库已解锁，共 {vault.Entries.Count} 条密码、{vault.Notes.Count} 条日记。");

        // 密码到期提醒
        CheckPasswordExpiry();

        SwitchToModule(Module.Password);
    }

    /// <summary>检查密码到期情况并播报提醒。</summary>
    private void CheckPasswordExpiry()
    {
        if (_vault is null || _vault.Settings.PasswordExpiryDays <= 0)
            return;

        var expired = new List<string>();
        foreach (var entry in _vault.Entries)
        {
            var age = (DateTime.Now - entry.LastPasswordChange).TotalDays;
            if (age >= _vault.Settings.PasswordExpiryDays)
            {
                expired.Add(entry.Title);
            }
        }

        if (expired.Count > 0)
        {
            var names = expired.Count <= 5
                ? string.Join("、", expired)
                : string.Join("、", expired.Take(5)) + $" 等 {expired.Count} 个";
            Announce($"提醒：{names}平台的密码已超过 {_vault.Settings.PasswordExpiryDays} 天未修改，建议更换。");
        }
    }

    private void MasterPasswordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AttemptUnlock();
        }
    }

    private void OnUnlockButtonClick(object sender, RoutedEventArgs e) => AttemptUnlock();

    // =========================================================================
    // 分类树与列表刷新
    // =========================================================================

    private void RefreshTree()
    {
        categoryTree.Items.Clear();
        if (_currentModule == Module.Url)
        {
            categoryTree.Items.Add(MakeTreeNode("全部网址", null));
            foreach (var category in _urlData.Categories)
                categoryTree.Items.Add(MakeTreeNode(category, category));
        }
        else if (_currentModule == Module.Snippet)
        {
            categoryTree.Items.Add(MakeTreeNode("全部笔记", null));
            foreach (var category in _snippetData.Categories)
                categoryTree.Items.Add(MakeTreeNode(category, category));
        }
        else if (_currentModule == Module.Password)
        {
            categoryTree.Items.Add(MakeTreeNode("全部密码", null));
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_vault is not null)
            {
                foreach (var entry in _vault.Entries)
                {
                    if (string.IsNullOrWhiteSpace(entry.Tags)) continue;
                    foreach (var tag in entry.Tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        tags.Add(tag);
                }
            }
            foreach (var tag in tags.OrderBy(t => t))
                categoryTree.Items.Add(MakeTreeNode(tag, tag));
        }
        else if (_currentModule == Module.Note)
        {
            categoryTree.Items.Add(MakeTreeNode("全部日记", null));
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_vault is not null)
            {
                foreach (var note in _vault.Notes)
                {
                    if (!string.IsNullOrWhiteSpace(note.Category))
                        cats.Add(note.Category);
                }
            }
            foreach (var cat in cats.OrderBy(c => c))
                categoryTree.Items.Add(MakeTreeNode(cat, cat));
        }
        else if (_currentModule == Module.IdDocument)
        {
            categoryTree.Items.Add(MakeTreeNode("全部证件", null));
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_vault is not null)
            {
                foreach (var doc in _vault.IdDocuments)
                {
                    if (!string.IsNullOrWhiteSpace(doc.Category))
                        cats.Add(doc.Category);
                }
            }
            foreach (var cat in cats.OrderBy(c => c))
                categoryTree.Items.Add(MakeTreeNode(cat, cat));
        }
        else if (_currentModule == Module.Accounting)
        {
            categoryTree.Items.Clear();
            categoryTree.Items.Add(MakeTreeNode("全部账目", null));
            var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_vault is not null)
            {
                foreach (var acc in _vault.Accountings)
                {
                    if (!string.IsNullOrWhiteSpace(acc.Category))
                        cats.Add(acc.Category);
                }
            }
            foreach (var cat in cats.OrderBy(c => c))
                categoryTree.Items.Add(MakeTreeNode(cat, cat));
        }

        if (categoryTree.Items.Count > 0 && categoryTree.Items[0] is TreeViewItem first)
            first.IsSelected = true;
    }

    private static TreeViewItem MakeTreeNode(string header, string? tag)
    {
        var node = new TreeViewItem { Header = header, Tag = tag };
        AutomationProperties.SetName(node, header);
        return node;
    }

    private void OnCategorySelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _currentFilter = categoryTree.SelectedItem is TreeViewItem node ? node.Tag as string : null;
        RefreshList();
        UpdateStatus();
    }

    private void CategoryTree_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FocusList();
            Announce("已切换到列表区。");
        }
    }

    private void RefreshList()
    {
        var search = (searchBox.Text ?? string.Empty).Trim();

        if (_currentModule == Module.Url)
        {
            urlListBox.Items.Clear();
            _filteredUrls = _urlData.Entries
                .Where(u => _currentFilter is null || u.Category == _currentFilter)
                .Where(u => string.IsNullOrEmpty(search)
                            || u.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || u.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || u.Account.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || u.Notes.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || u.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _filteredUrls = SortEntries(_filteredUrls, u => u.IsFavorite, u => u.Title, u => u.CreatedTime, u => u.ModifiedTime);

            foreach (var entry in _filteredUrls)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildUrlContextMenu();
                urlListBox.Items.Add(item);
            }

            if (urlListBox.Items.Count > 0) urlListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }
        else if (_currentModule == Module.Snippet)
        {
            snippetListBox.Items.Clear();
            _filteredSnippets = _snippetData.Entries
                .Where(s => _currentFilter is null || s.Category == _currentFilter)
                .Where(s => string.IsNullOrEmpty(search)
                            || s.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || s.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || s.Category.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _filteredSnippets = SortEntries(_filteredSnippets, s => s.IsFavorite, s => s.Title, s => s.CreatedTime, s => s.ModifiedTime);

            foreach (var entry in _filteredSnippets)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildSnippetContextMenu();
                snippetListBox.Items.Add(item);
            }

            if (snippetListBox.Items.Count > 0) snippetListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }
        else if (_currentModule == Module.Password)
        {
            passwordListBox.Items.Clear();
            _filteredPasswords = (_vault?.Entries ?? new List<PasswordEntry>())
                .Where(p => _currentFilter is null || ContainsTag(p.Tags, _currentFilter))
                .Where(p => string.IsNullOrEmpty(search)
                            || p.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Url.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Email.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.PhoneNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Notes.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || p.Tags.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || (p.CustomFields != null && p.CustomFields.Any(cf =>
                                cf.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                cf.Value.Contains(search, StringComparison.OrdinalIgnoreCase))))
                .ToList();
            _filteredPasswords = SortEntries(_filteredPasswords, p => p.IsFavorite, p => p.Title, p => p.LastPasswordChange, p => p.ModifiedTime);

            foreach (var entry in _filteredPasswords)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildPasswordContextMenu();
                passwordListBox.Items.Add(item);
            }

            if (passwordListBox.Items.Count > 0) passwordListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }
        else if (_currentModule == Module.Note)
        {
            noteListBox.Items.Clear();
            _filteredNotes = (_vault?.Notes ?? new List<NoteEntry>())
                .Where(n => _currentFilter is null || n.Category == _currentFilter)
                .Where(n => string.IsNullOrEmpty(search)
                            || n.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || n.Content.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || n.Weather.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || n.Mood.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _filteredNotes = SortEntries(_filteredNotes, n => n.IsFavorite, n => n.Title, n => n.CreatedTime, n => n.ModifiedTime);

            foreach (var entry in _filteredNotes)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildNoteContextMenu();
                noteListBox.Items.Add(item);
            }

            if (noteListBox.Items.Count > 0) noteListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }
        else if (_currentModule == Module.IdDocument)
        {
            idDocumentListBox.Items.Clear();
            _filteredIdDocuments = (_vault?.IdDocuments ?? new List<IdDocumentEntry>())
                .Where(d => _currentFilter is null || d.Category == _currentFilter)
                .Where(d => string.IsNullOrEmpty(search)
                            || d.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.HolderName.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.DocNumber.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.DocType.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.IssueAuthority.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || d.Notes.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _filteredIdDocuments = SortEntries(_filteredIdDocuments, d => d.IsFavorite, d => d.Title, d => d.CreatedTime, d => d.ModifiedTime);

            foreach (var entry in _filteredIdDocuments)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildIdDocumentContextMenu();
                idDocumentListBox.Items.Add(item);
            }

            if (idDocumentListBox.Items.Count > 0) idDocumentListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }
        else if (_currentModule == Module.Accounting)
        {
            accountingListBox.Items.Clear();
            _filteredAccountings = (_vault?.Accountings ?? new List<AccountingEntry>())
                .Where(a => _currentFilter is null || a.Category == _currentFilter)
                .Where(a => string.IsNullOrEmpty(search)
                            || a.Title.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || a.Category.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || a.Note.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || a.PaymentMethod.Contains(search, StringComparison.OrdinalIgnoreCase)
                            || a.Type.Contains(search, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _filteredAccountings = SortEntries(_filteredAccountings, a => a.IsFavorite, a => a.Title, a => a.Date, a => a.ModifiedTime);

            foreach (var entry in _filteredAccountings)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var typeIcon = entry.IsIncome ? "[收]" : "[支]";
                var display = $"{prefix}{typeIcon} ¥{entry.Amount:F2} {entry.Category} - {entry.Title}";
                var item = new ListBoxItem { Content = display, Tag = entry };
                AutomationProperties.SetName(item, $"{(entry.IsFavorite ? "收藏 " : "")}{entry.Type} {entry.Amount:F2}元 {entry.Category} {entry.Title} {entry.Date:yyyy-MM-dd}");
                item.ContextMenu = BuildAccountingContextMenu();
                accountingListBox.Items.Add(item);
            }

            if (accountingListBox.Items.Count > 0) accountingListBox.SelectedIndex = 0;
            else UpdateDetailForEmpty();
        }

        UpdateStatus();
    }

    private static bool ContainsTag(string? tags, string? tag)
    {
        if (string.IsNullOrWhiteSpace(tags) || string.IsNullOrEmpty(tag)) return false;
        return tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                   .Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
    }

    // =========================================================================
    // 上下文菜单
    // =========================================================================

    private static MenuItem MakeMenuItem(string header, string automationName, RoutedEventHandler handler, string? gesture = null)
    {
        var item = new MenuItem { Header = header };
        AutomationProperties.SetName(item, automationName);
        if (gesture is not null) item.InputGestureText = gesture;
        item.Click += handler;
        return item;
    }

    private ContextMenu BuildUrlContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("打开网址", "用默认浏览器打开网址", (_, _) => DoOpenUrl(), "Enter"));
        menu.Items.Add(MakeMenuItem("复制网址", "复制网址到剪贴板", (_, _) => DoCopyUrl(), "Ctrl+Enter"));
        menu.Items.Add(MakeMenuItem("复制名称", "复制站点名称到剪贴板", (_, _) => DoCopyName()));
        menu.Items.Add(MakeMenuItem("复制账号", "复制账号到剪贴板", (_, _) => DoCopyAccount()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("切换收藏", "切换收藏置顶", (_, _) => DoToggleFavorite(), "Ctrl+Shift+F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("编辑", "编辑网址条目", (_, _) => DoEdit(), "Ctrl+E"));
        menu.Items.Add(MakeMenuItem("删除", "删除网址条目", (_, _) => DoDelete(), "Del"));
        return menu;
    }

    private ContextMenu BuildPasswordContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("复制密码", "复制密码到剪贴板，自动清除", (_, _) => DoCopyPassword(), "Ctrl+Shift+C"));
        menu.Items.Add(MakeMenuItem("复制用户名", "复制用户名到剪贴板", (_, _) => DoCopyUserName(), "Ctrl+Shift+B"));
        menu.Items.Add(MakeMenuItem("复制手机号", "复制手机号到剪贴板", (_, _) => DoCopyPhone(), "Ctrl+Shift+P"));
        menu.Items.Add(MakeMenuItem("复制动态验证码", "复制 TOTP 动态验证码", (_, _) => DoCopyTotp(), "Ctrl+Shift+T"));
        menu.Items.Add(MakeMenuItem("字段选择器", "打开字段选择器复制任意字段", (_, _) => DoFieldSelector(), "Ctrl+Shift+V"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("切换收藏", "切换收藏置顶", (_, _) => DoToggleFavorite(), "Ctrl+Shift+F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("编辑", "编辑密码条目", (_, _) => DoEdit(), "Ctrl+E"));
        menu.Items.Add(MakeMenuItem("删除", "删除密码条目", (_, _) => DoDelete(), "Del"));
        return menu;
    }

    private ContextMenu BuildNoteContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("编辑", "编辑日记", (_, _) => DoEdit(), "Ctrl+E"));
        menu.Items.Add(MakeMenuItem("按月浏览", "按月份筛选日记", (_, _) => DoBrowseByMonth(), "Ctrl+Shift+M"));
        menu.Items.Add(MakeMenuItem("总结", "总结日记内容并提取标题标签", (_, _) => DoSummary()));
        menu.Items.Add(BuildExportSubMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("切换收藏", "切换收藏置顶", (_, _) => DoToggleFavorite(), "Ctrl+Shift+F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("删除", "删除日记", (_, _) => DoDelete(), "Del"));
        return menu;
    }

    // =========================================================================
    // 选中项与详情
    // =========================================================================

    private UrlEntry? GetSelectedUrl() => (urlListBox.SelectedItem as ListBoxItem)?.Tag as UrlEntry;
    private SnippetEntry? GetSelectedSnippet() => (snippetListBox.SelectedItem as ListBoxItem)?.Tag as SnippetEntry;
    private PasswordEntry? GetSelectedPassword() => (passwordListBox.SelectedItem as ListBoxItem)?.Tag as PasswordEntry;
    private NoteEntry? GetSelectedNote() => (noteListBox.SelectedItem as ListBoxItem)?.Tag as NoteEntry;
    private IdDocumentEntry? GetSelectedIdDocument() => (idDocumentListBox.SelectedItem as ListBoxItem)?.Tag as IdDocumentEntry;
    private AccountingEntry? GetSelectedAccounting() => (accountingListBox.SelectedItem as ListBoxItem)?.Tag as AccountingEntry;

    private void OnUrlSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.Url) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnSnippetSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.Snippet) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnPasswordSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.Password) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnNoteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.Note) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnIdDocumentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.IdDocument) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnAccountingSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_currentModule == Module.Accounting) { UpdateDetail(); UpdateStatus(); }
    }

    private void OnUrlDoubleClick(object sender, MouseButtonEventArgs e) => DoOpenUrl();
    private void OnSnippetDoubleClick(object sender, MouseButtonEventArgs e) => DoEdit();
    private void OnNoteDoubleClick(object sender, MouseButtonEventArgs e) => DoEdit();
    private void OnIdDocumentDoubleClick(object sender, MouseButtonEventArgs e) => DoEdit();
    private void OnAccountingDoubleClick(object sender, MouseButtonEventArgs e) => DoEdit();

    private void UpdateDetail()
    {
        detailText.Text = _currentModule switch
        {
            Module.Url => GetSelectedUrl() is { } url ? BuildUrlDetail(url) : "（未选中网址条目）",
            Module.Snippet => GetSelectedSnippet() is { } snip ? BuildSnippetDetail(snip) : "（未选中文案）",
            Module.Password => GetSelectedPassword() is { } pwd ? BuildPasswordDetail(pwd) : "（未选中密码条目）",
            Module.Note => GetSelectedNote() is { } note ? BuildNoteDetail(note) : "（未选中日记）",
            Module.IdDocument => GetSelectedIdDocument() is { } doc ? BuildIdDocumentDetail(doc) : "（未选中证件）",
            Module.Accounting => GetSelectedAccounting() is { } acc ? BuildAccountingDetail(acc) : "（未选中账目）",
            _ => ""
        };
    }

    private void UpdateDetailForEmpty()
    {
        var needUnlock = (_currentModule == Module.Password || _currentModule == Module.Note || _currentModule == Module.IdDocument || _currentModule == Module.Accounting) && !_isUnlocked;
        detailText.Text = needUnlock
            ? "（密码库未解锁，请输入主密码）"
            : "（当前没有条目，按 Ctrl+N 新建）\n\n提示：右键列表项可打开上下文菜单";
    }

    private string BuildUrlDetail(UrlEntry entry)
    {
        var linked = "无";
        if (!string.IsNullOrEmpty(entry.LinkedPasswordId) && _vault is not null)
        {
            var linkedEntry = _vault.Entries.FirstOrDefault(p => p.Id == entry.LinkedPasswordId);
            if (linkedEntry is not null) linked = linkedEntry.Title;
        }

        var health = entry.LastCheckedTime.HasValue
            ? $"{entry.LastCheckStatus}（{entry.LastCheckedTime:yyyy-MM-dd HH:mm}）"
            : "未检查";

        return $"站点名称：{entry.Title}\n"
               + $"网址：{entry.Url}\n"
               + $"账号：{entry.Account}\n"
               + $"分类：{entry.Category}\n"
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"健康状态：{health}\n"
               + $"备注：{entry.Notes}\n"
               + $"关联密码：{linked}\n\n"
               + "提示：Enter 打开 | Ctrl+Enter 复制网址 | F2 编辑 | Del 删除 | Ctrl+Shift+F 收藏";
    }

    private string BuildPasswordDetail(PasswordEntry entry)
    {
        var totp = string.IsNullOrEmpty(entry.TotpSecret)
            ? "未设置" : "已设置（按 Ctrl+Shift+T 复制当前验证码）";

        var questions = entry.SecurityQuestions.Count == 0
            ? "无" : string.Join("；", entry.SecurityQuestions.Select(q => $"{q.Question}（答案已隐藏）"));

        var customFields = entry.CustomFields.Count == 0
            ? "无" : string.Join("；", entry.CustomFields.Select(f => f.Sensitive ? $"{f.Name}（已隐藏）" : $"{f.Name}：{f.Value}"));

        var age = (DateTime.Now - entry.LastPasswordChange).Days;
        var expiryInfo = _vault?.Settings.PasswordExpiryDays > 0
            ? $"上次修改：{entry.LastPasswordChange:yyyy-MM-dd}（{age} 天前，{_vault.Settings.PasswordExpiryDays} 天到期）"
            : $"上次修改：{entry.LastPasswordChange:yyyy-MM-dd}（{age} 天前）";

        return $"平台名称：{entry.Title}\n"
               + $"用户名：{entry.UserName}\n"
               + $"密码：已隐藏（按 Ctrl+Shift+C 复制）\n"
               + $"网址：{entry.Url}\n"
               + $"手机号：{entry.PhoneNumber}\n"
               + $"邮箱：{entry.Email}\n"
               + $"TOTP：{totp}\n"
               + $"标签：{entry.Tags}\n"
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"{expiryInfo}\n"
               + $"密保问题：{questions}\n"
               + $"自定义字段：{customFields}\n"
               + $"备注：{entry.Notes}";
    }

    private string BuildNoteDetail(NoteEntry entry)
    {
        var preview = entry.Content.Length > 200
            ? entry.Content[..200] + "..."
            : entry.Content;

        var weatherInfo = string.IsNullOrEmpty(entry.Weather) ? "" : $"天气：{entry.Weather}\n";
        var moodInfo = string.IsNullOrEmpty(entry.Mood) ? "" : $"心情：{entry.Mood}\n";

        return $"标题：{entry.Title}\n"
               + $"分类：{entry.Category}\n"
               + weatherInfo
               + moodInfo
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"创建时间：{entry.CreatedTime:yyyy-MM-dd HH:mm}\n"
               + $"修改时间：{entry.ModifiedTime:yyyy-MM-dd HH:mm}\n\n"
               + $"内容预览：\n{preview}";
    }

    private string BuildSnippetDetail(SnippetEntry entry)
    {
        var preview = entry.Content.Length > 500
            ? entry.Content[..500] + "..."
            : entry.Content;

        return $"标题：{entry.Title}\n"
               + $"分类：{entry.Category}\n"
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"创建时间：{entry.CreatedTime:yyyy-MM-dd HH:mm}\n"
               + $"修改时间：{entry.ModifiedTime:yyyy-MM-dd HH:mm}\n\n"
               + $"内容：\n{preview}";
    }

    private string BuildIdDocumentDetail(IdDocumentEntry entry)
    {
        var hasImage = !string.IsNullOrEmpty(entry.ImageData) ? "已上传" : "无";

        return $"证件名称：{entry.Title}\n"
               + $"证件类型：{entry.DocType}\n"
               + $"证件号码：{entry.DocNumber}\n"
               + $"持有人：{entry.HolderName}\n"
               + $"签发日期：{(entry.IssueDate?.ToString("yyyy-MM-dd") ?? "未设置")}\n"
               + $"有效期至：{(entry.ExpiryDate?.ToString("yyyy-MM-dd") ?? "未设置")}\n"
               + $"签发机关：{entry.IssueAuthority}\n"
               + $"分类：{entry.Category}\n"
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"证件图片：{hasImage}\n"
               + $"创建时间：{entry.CreatedTime:yyyy-MM-dd HH:mm}\n"
               + $"修改时间：{entry.ModifiedTime:yyyy-MM-dd HH:mm}\n\n"
               + $"备注：{entry.Notes}";
    }

    private string BuildAccountingDetail(AccountingEntry entry)
    {
        var monthlySummary = GetMonthlySummary(entry.Date);

        return $"说明：{entry.Title}\n"
               + $"类型：{entry.Type}\n"
               + $"金额：¥{entry.Amount:F2}\n"
               + $"分类：{entry.Category}\n"
               + $"支付方式：{entry.PaymentMethod}\n"
               + $"日期：{entry.Date:yyyy-MM-dd}\n"
               + $"收藏：{(entry.IsFavorite ? "是" : "否")}\n"
               + $"创建时间：{entry.CreatedTime:yyyy-MM-dd HH:mm}\n"
               + $"修改时间：{entry.ModifiedTime:yyyy-MM-dd HH:mm}\n"
               + $"备注：{entry.Note}\n\n"
               + $"{monthlySummary}\n\n"
               + "提示：Enter 编辑 | Del 删除 | F2 编辑 | Ctrl+Shift+S 播报统计 | Ctrl+Shift+F 收藏";
    }

    /// <summary>计算指定月份的收支汇总文本。</summary>
    private string GetMonthlySummary(DateTime monthDate)
    {
        if (_vault is null) return "";

        var monthAccountings = _vault.Accountings
            .Where(a => a.Date.Year == monthDate.Year && a.Date.Month == monthDate.Month)
            .ToList();

        var income = monthAccountings.Where(a => a.IsIncome).Sum(a => a.Amount);
        var expense = monthAccountings.Where(a => !a.IsIncome).Sum(a => a.Amount);
        var balance = income - expense;

        return $"{monthDate:yyyy年MM月}统计：收入 ¥{income:F2}，支出 ¥{expense:F2}，结余 ¥{balance:F2}（共 {monthAccountings.Count} 笔）";
    }

    /// <summary>语音播报当月收支统计。</summary>
    private void AnnounceMonthlySummary()
    {
        if (_vault is null || _vault.Accountings.Count == 0)
        {
            Announce("暂无记账数据。");
            return;
        }

        var now = DateTime.Now;
        var monthAccountings = _vault.Accountings
            .Where(a => a.Date.Year == now.Year && a.Date.Month == now.Month)
            .ToList();

        if (monthAccountings.Count == 0)
        {
            Announce($"{now:yyyy年MM月}暂无记账记录。");
            return;
        }

        var income = monthAccountings.Where(a => a.IsIncome).Sum(a => a.Amount);
        var expense = monthAccountings.Where(a => !a.IsIncome).Sum(a => a.Amount);
        var balance = income - expense;

        Announce($"{now:yyyy年MM月}统计：共 {monthAccountings.Count} 笔，收入 {income:F2} 元，支出 {expense:F2} 元，结余 {balance:F2} 元。");
    }

    // =========================================================================
    // 状态栏与播报
    // =========================================================================

    private void UpdateStatus()
    {
        // 活动区内容暂时全部清空，等用户后续指示再添加
        statusPositionText.Text = "";
        statusHintText.Text = "";
    }

    private void Announce(string message)
    {
        if (string.IsNullOrEmpty(message)) return;
        statusMessageText.Text = string.Empty;
        Dispatcher.BeginInvoke(new Action(() => _a11y.Announce(statusMessageText, message)), DispatcherPriority.DataBind);
    }

    // =========================================================================
    // 全局键盘处理
    // =========================================================================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        ResetActivityTimer();

        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // ALT 单键（不组合 Ctrl/Shift）：切换主菜单栏显示/隐藏
        // WPF 中按 ALT 时 e.Key == Key.System，e.SystemKey == Key.LeftAlt/RightAlt
        if (!ctrl && !shift && e.Key == Key.System &&
            (e.SystemKey == Key.LeftAlt || e.SystemKey == Key.RightAlt))
        {
            e.Handled = true;
            ToggleMainMenu();
            return;
        }

        // ESC：如果菜单开着先关掉菜单，其他场景交给后续逻辑
        if (!ctrl && !shift && e.Key == Key.Escape)
        {
            if (mainMenu != null && mainMenu.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                HideMainMenu();
                return;
            }
        }

        try
        {
            // 模块切换 Ctrl+1~6
            if (ctrl && !shift && e.Key == Key.D1) { e.Handled = true; SafeSet(() => urlModuleRadio.IsChecked = true); return; }
            if (ctrl && !shift && e.Key == Key.D2) { e.Handled = true; SafeSet(() => snippetModuleRadio.IsChecked = true); return; }
            if (ctrl && !shift && e.Key == Key.D3) { e.Handled = true; SafeSet(() => passwordModuleRadio.IsChecked = true); return; }
            if (ctrl && !shift && e.Key == Key.D4) { e.Handled = true; SafeSet(() => noteModuleRadio.IsChecked = true); return; }
            if (ctrl && !shift && e.Key == Key.D5) { e.Handled = true; SafeSet(() => idDocumentModuleRadio.IsChecked = true); return; }
            if (ctrl && !shift && e.Key == Key.D6) { e.Handled = true; SafeSet(() => accountingModuleRadio.IsChecked = true); return; }
        }
        catch (Exception ex)
        {
            HandleModuleSwitchException(ex);
            e.Handled = true;
            return;
        }

        // F6 切换焦点区域
        if (e.Key == Key.F6 && !ctrl && !shift) { e.Handled = true; CycleFocus(); return; }

        // F5 密码生成器
        if (e.Key == Key.F5 && !ctrl && !shift) { e.Handled = true; DoPasswordGenerator(); return; }

        // 通用快捷键
        if (ctrl && !shift && e.Key == Key.F) { e.Handled = true; DoSearch(); return; }
        if (ctrl && !shift && e.Key == Key.S) { e.Handled = true; DoSave(); return; }
        if (ctrl && !shift && e.Key == Key.N) { e.Handled = true; DoNew(); return; }
        if (ctrl && !shift && e.Key == Key.E) { e.Handled = true; DoEdit(); return; }
        if (ctrl && !shift && e.Key == Key.D) { e.Handled = true; DoDelete(); return; }

        // 切换收藏 Ctrl+Shift+F
        if (ctrl && shift && e.Key == Key.F) { e.Handled = true; DoToggleFavorite(); return; }

        // Ctrl+Shift+O 切换排序模式
        if (ctrl && shift && e.Key == Key.O) { e.Handled = true; DoToggleSort(); return; }

        // Ctrl+Shift+X 导出TXT
        if (ctrl && shift && e.Key == Key.X) { e.Handled = true; DoExportTxt(); return; }

        // Ctrl+Shift+G 全局搜索
        if (ctrl && shift && e.Key == Key.G) { e.Handled = true; DoGlobalSearch(); return; }

        // Ctrl+Shift+A 全量备份
        if (ctrl && shift && e.Key == Key.A) { e.Handled = true; DoFullBackup(); return; }

        // Ctrl+Shift+R 全量恢复
        if (ctrl && shift && e.Key == Key.R) { e.Handled = true; DoFullRestore(); return; }

        // F2 编辑
        if (e.Key == Key.F2 && !ctrl && !shift && IsListFocused())
        {
            e.Handled = true; DoEdit(); return;
        }

        // Delete 删除
        if (e.Key == Key.Delete && !ctrl && !shift && IsListFocused())
        {
            e.Handled = true; DoDelete(); return;
        }

        // 密码模块专用快捷键
        if (_currentModule == Module.Password && _isUnlocked)
        {
            if (ctrl && shift && e.Key == Key.C) { e.Handled = true; DoCopyPassword(); return; }
            if (ctrl && shift && e.Key == Key.B) { e.Handled = true; DoCopyUserName(); return; }
            if (ctrl && shift && e.Key == Key.P) { e.Handled = true; DoCopyPhone(); return; }
            if (ctrl && shift && e.Key == Key.T) { e.Handled = true; DoCopyTotp(); return; }
            if (ctrl && shift && e.Key == Key.V) { e.Handled = true; DoFieldSelector(); return; }
            if (ctrl && shift && e.Key == Key.L) { e.Handled = true; DoLock(); return; }
        }

        // 网址列表：Enter 打开，Ctrl+Enter 复制网址
        if (_currentModule == Module.Url && urlListBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            if (ctrl && !shift) DoCopyUrl();
            else if (!ctrl && !shift) DoOpenUrl();
            return;
        }

        // 日记列表：Enter 编辑
        if (_currentModule == Module.Note && noteListBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            DoEdit();
            return;
        }

        // 文案列表：Enter 编辑
        if (_currentModule == Module.Snippet && snippetListBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            DoEdit();
            return;
        }

        // 证件列表：Enter 编辑
        if (_currentModule == Module.IdDocument && idDocumentListBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            DoEdit();
            return;
        }

        // 记事本模块：Ctrl+Shift+C 复制笔记内容
        if (_currentModule == Module.Snippet && ctrl && shift && e.Key == Key.C)
        {
            e.Handled = true;
            DoCopySnippet();
            return;
        }

        // 记事本模块：Ctrl+Shift+I 插入模板
        if (_currentModule == Module.Snippet && ctrl && shift && e.Key == Key.I)
        {
            e.Handled = true;
            DoInsertTemplate();
            return;
        }

        // 日记模块：Ctrl+Shift+M 按月浏览
        if (_currentModule == Module.Note && ctrl && shift && e.Key == Key.M)
        {
            e.Handled = true;
            DoBrowseByMonth();
            return;
        }

        // 记账模块：Ctrl+Shift+S 播报当月统计
        if (_currentModule == Module.Accounting && ctrl && shift && e.Key == Key.S)
        {
            e.Handled = true;
            AnnounceMonthlySummary();
            return;
        }

        // 记账列表：Enter 编辑
        if (_currentModule == Module.Accounting && accountingListBox.IsKeyboardFocusWithin && e.Key == Key.Enter)
        {
            e.Handled = true;
            DoEdit();
            return;
        }

        // Esc 清除搜索
        if (e.Key == Key.Escape && !string.IsNullOrEmpty(searchBox.Text))
        {
            searchBox.Clear();
            Announce("已清除搜索。");
            e.Handled = true;
        }
    }

    private void CycleFocus()
    {
        if (categoryTree.IsKeyboardFocusWithin)
        {
            FocusList();
            Announce("已切换到列表区。");
        }
        else if (IsListFocused())
        {
            detailText.Focus();
            Announce("已切换到详情区。");
        }
        else
        {
            categoryTree.Focus();
            Announce("已切换到分类树。");
        }
    }

    private bool IsListFocused()
    {
        return (_currentModule == Module.Url && urlListBox.IsKeyboardFocusWithin)
               || (_currentModule == Module.Snippet && snippetListBox.IsKeyboardFocusWithin)
               || (_currentModule == Module.Password && passwordListBox.IsKeyboardFocusWithin)
               || (_currentModule == Module.Note && noteListBox.IsKeyboardFocusWithin)
               || (_currentModule == Module.IdDocument && idDocumentListBox.IsKeyboardFocusWithin)
               || (_currentModule == Module.Accounting && accountingListBox.IsKeyboardFocusWithin);
    }

    private void FocusList()
    {
        if (_currentModule == Module.Url) urlListBox.Focus();
        else if (_currentModule == Module.Snippet) snippetListBox.Focus();
        else if (_currentModule == Module.Password && _isUnlocked) passwordListBox.Focus();
        else if (_currentModule == Module.Note && _isUnlocked) noteListBox.Focus();
        else if (_currentModule == Module.IdDocument && _isUnlocked) idDocumentListBox.Focus();
        else if (_currentModule == Module.Accounting && _isUnlocked) accountingListBox.Focus();
        else if ((_currentModule == Module.Password || _currentModule == Module.Note || _currentModule == Module.IdDocument || _currentModule == Module.Accounting) && unlockPanel.Visibility == Visibility.Visible)
            masterPasswordBox.Focus();
    }

    // =========================================================================
    // 菜单事件路由
    // =========================================================================

    private void OnNew(object sender, RoutedEventArgs e) => DoNew();
    private void OnSave(object sender, RoutedEventArgs e) => DoSave();
    private void OnExit(object sender, RoutedEventArgs e) => Close();
    private void OnEdit(object sender, RoutedEventArgs e) => DoEdit();
    private void OnDelete(object sender, RoutedEventArgs e) => DoDelete();
    private void OnSearch(object sender, RoutedEventArgs e) => DoSearch();
    private void OnGlobalSearch(object sender, RoutedEventArgs e) => DoGlobalSearch();
    private void OnSwitchUrl(object sender, RoutedEventArgs e) => urlModuleRadio.IsChecked = true;
    private void OnSwitchSnippet(object sender, RoutedEventArgs e) => snippetModuleRadio.IsChecked = true;
    private void OnSwitchPassword(object sender, RoutedEventArgs e) => passwordModuleRadio.IsChecked = true;
    private void OnSwitchNote(object sender, RoutedEventArgs e) => noteModuleRadio.IsChecked = true;
    private void OnSwitchIdDocument(object sender, RoutedEventArgs e) => idDocumentModuleRadio.IsChecked = true;
    private void OnSwitchAccounting(object sender, RoutedEventArgs e) => accountingModuleRadio.IsChecked = true;
    private void OnCopySnippet(object sender, RoutedEventArgs e) => DoCopySnippet();
    private void OnCycleFocus(object sender, RoutedEventArgs e) => CycleFocus();
    private void OnOpenUrl(object sender, RoutedEventArgs e) => DoOpenUrl();
    private void OnCopyUrl(object sender, RoutedEventArgs e) => DoCopyUrl();
    private void OnCopyPassword(object sender, RoutedEventArgs e) => DoCopyPassword();
    private void OnCopyUserName(object sender, RoutedEventArgs e) => DoCopyUserName();
    private void OnCopyPhone(object sender, RoutedEventArgs e) => DoCopyPhone();
    private void OnCopyTotp(object sender, RoutedEventArgs e) => DoCopyTotp();
    private void OnFieldSelector(object sender, RoutedEventArgs e) => DoFieldSelector();
    private void OnLock(object sender, RoutedEventArgs e) => DoLock();
    private void OnPasswordGenerator(object sender, RoutedEventArgs e) => DoPasswordGenerator();
    private void OnToggleFavorite(object sender, RoutedEventArgs e) => DoToggleFavorite();
    private void OnImport(object sender, RoutedEventArgs e) => DoImport();
    private void OnBackup(object sender, RoutedEventArgs e) => DoBackup();
    private void OnRestore(object sender, RoutedEventArgs e) => DoRestore();
    private void OnFullBackup(object sender, RoutedEventArgs e) => DoFullBackup();
    private void OnFullRestore(object sender, RoutedEventArgs e) => DoFullRestore();
    private void OnChangeMasterPassword(object sender, RoutedEventArgs e) => DoChangeMasterPassword();
    private void OnSettings(object sender, RoutedEventArgs e) => DoSettings();
    private void OnDuplicateCheck(object sender, RoutedEventArgs e) => DoDuplicateCheck();
    private void OnUrlHealthCheck(object sender, RoutedEventArgs e) => DoUrlHealthCheck();
    private void OnCategoryManager(object sender, RoutedEventArgs e) => DoCategoryManager();
    private void OnViewAuditLog(object sender, RoutedEventArgs e) => DoViewAuditLog();
    private void OnShortcutConfig(object sender, RoutedEventArgs e) => DoShortcutConfig();
    private void OnBatchDelete(object sender, RoutedEventArgs e) => DoBatchDelete();
    private void OnBatchMoveCategory(object sender, RoutedEventArgs e) => DoBatchMoveCategory();
    private void OnToggleSort(object sender, RoutedEventArgs e) => DoToggleSort();
    private void OnExportTxt(object sender, RoutedEventArgs e) => DoExportTxt();
    private void OnInsertTemplate(object sender, RoutedEventArgs e) => DoInsertTemplate();
    private void OnBrowseByMonth(object sender, RoutedEventArgs e) => DoBrowseByMonth();
    private void OnCheckUpdate(object sender, RoutedEventArgs e) => _ = CheckForUpdateAsync(silent: false);
    private void OnViewChangelog(object sender, RoutedEventArgs e) => ShowChangelog();
    private void OnAddButtonClick(object sender, RoutedEventArgs e) => DoNew();

    private void OnHelpShortcuts(object sender, RoutedEventArgs e)
    {
        var text = "快捷键说明：\n\n"
                   + "Ctrl+1 网址 / Ctrl+2 记事本 / Ctrl+3 密码 / Ctrl+4 日记 / Ctrl+5 证件 / Ctrl+6 记账\n"
                   + "F6  在分类树/列表/详情间切换焦点\n"
                   + "Ctrl+N 新建  Ctrl+E 或 F2 编辑  Ctrl+D 或 Del 删除\n"
                   + "Ctrl+F 搜索  Ctrl+Shift+G 全局搜索（F3 跳转分组）  Ctrl+S 保存  Esc 清除搜索\n"
                   + "Ctrl+Shift+F 切换收藏  Ctrl+Shift+O 切换排序\n"
                   + "Ctrl+Shift+X 导出TXT  右键或 Shift+F10 上下文菜单\n\n"
                   + "网址模块：\n  Enter 打开  Ctrl+Enter 复制网址\n\n"
                   + "密码模块：\n  Ctrl+Shift+C 复制密码（自动清除）\n"
                   + "  Ctrl+Shift+B 用户名  Ctrl+Shift+P 手机号\n"
                   + "  Ctrl+Shift+T 动态码  Ctrl+Shift+V 字段选择器\n"
                   + "  Ctrl+Shift+L 锁定  F5 密码生成器\n\n"
                   + "记事本模块：\n  Enter 编辑  Ctrl+Shift+C 复制内容\n"
                   + "  Ctrl+Shift+I 插入模板  Ctrl+Shift+X 导出TXT\n\n"
                   + "日记模块：\n  Enter 编辑  Ctrl+Shift+M 按月浏览\n"
                   + "  Ctrl+Shift+X 导出TXT\n\n"
                   + "证件模块：\n  Enter 编辑（需解锁密码库）\n\n"
                   + "记账模块：\n  Enter 编辑  Ctrl+Shift+S 播报当月统计\n"
                   + "  （需解锁密码库）\n\n"
                   + "可在 工具→快捷键设置 中自定义快捷键。";
        MessageBox.Show(this, text, "快捷键说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this,
            "随心记 v2.0\n\n"
            + "面向盲人用户的网址收藏、记事本、密码本、日记、证件保存与记账应用。\n"
            + "支持纯键盘操作、争渡读屏适配、自动锁定、审计日志、\n"
            + "防截屏、备份恢复、导入、重复检测、网址健康检查、\n"
            + "排序导出、模板插入、按月浏览等。\n\n"
            + "加密：AES-256-CBC + HMAC-SHA256\n"
            + "密钥派生：PBKDF2-SHA256 (600,000 iterations)",
            "关于", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =========================================================================
    // 操作实现
    // =========================================================================

    private void DoSearch()
    {
        searchBox.Focus();
        searchBox.SelectAll();
        Announce("搜索框，输入关键词过滤列表。");
    }

    /// <summary>打开全局搜索对话框，跨所有模块全文搜索并跳转到匹配项。</summary>
    private void DoGlobalSearch()
    {
        var previous = CaptureFocus();
        var dialog = new GlobalSearchDialog(_urlData, _snippetData, _vault, _isUnlocked)
        { Owner = this };

        if (dialog.ShowDialog() == true
            && !string.IsNullOrEmpty(dialog.ResultModule)
            && !string.IsNullOrEmpty(dialog.ResultEntryId))
        {
            NavigateToEntry(dialog.ResultModule, dialog.ResultEntryId);
        }

        RestoreFocus(previous);
    }

    /// <summary>切换到指定模块并选中指定 ID 的条目。</summary>
    private void NavigateToEntry(string module, string entryId)
    {
        // 清除当前模块的搜索和分类过滤，确保目标条目可见
        searchBox.Clear();
        _currentFilter = null;

        switch (module)
        {
            case "Url":
                urlModuleRadio.IsChecked = true;
                SwitchToModule(Module.Url);
                RefreshTree(); RefreshList();
                SelectUrlById(entryId);
                Announce("已跳转到网址收藏。");
                break;
            case "Snippet":
                snippetModuleRadio.IsChecked = true;
                SwitchToModule(Module.Snippet);
                RefreshTree(); RefreshList();
                SelectSnippetById(entryId);
                Announce("已跳转到记事本。");
                break;
            case "Password":
                if (!EnsureUnlocked()) return;
                passwordModuleRadio.IsChecked = true;
                SwitchToModule(Module.Password);
                RefreshTree(); RefreshList();
                SelectPasswordById(entryId);
                Announce("已跳转到密码收藏。");
                break;
            case "Note":
                if (!EnsureUnlocked()) return;
                noteModuleRadio.IsChecked = true;
                SwitchToModule(Module.Note);
                RefreshTree(); RefreshList();
                SelectNoteById(entryId);
                Announce("已跳转到日记。");
                break;
            case "IdDocument":
                if (!EnsureUnlocked()) return;
                idDocumentModuleRadio.IsChecked = true;
                SwitchToModule(Module.IdDocument);
                RefreshTree(); RefreshList();
                SelectIdDocumentById(entryId);
                Announce("已跳转到证件保存。");
                break;
            case "Accounting":
                if (!EnsureUnlocked()) return;
                accountingModuleRadio.IsChecked = true;
                SwitchToModule(Module.Accounting);
                RefreshTree(); RefreshList();
                SelectAccountingById(entryId);
                Announce("已跳转到记账。");
                break;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoaded) RefreshList();
    }

    private void OnClearSearch(object sender, RoutedEventArgs e)
    {
        searchBox.Clear();
        FocusList();
        Announce("已清除搜索。");
    }

    private void DoSave()
    {
        if (_currentModule == Module.Url)
        {
            StorageService.SaveUrls(_urlData);
            Announce("网址数据已保存。");
        }
        else if (_currentModule == Module.Snippet)
        {
            StorageService.SaveSnippets(_snippetData);
            Announce("记事本数据已保存。");
        }
        else if (_isUnlocked && _vault is not null)
        {
            SavePasswordVault();
            Announce("数据已保存。");
        }
        else
        {
            Announce("密码库未解锁，无法保存。");
        }
    }

    private bool EnsureUnlocked()
    {
        if (_currentModule == Module.Url || _currentModule == Module.Snippet) return true;
        if (_currentModule == Module.Accounting && _isUnlocked) return true;
        if (!_isUnlocked) { Announce("请先解锁密码库。"); return false; }
        return true;
    }

    private void DoNew()
    {
        if (_currentModule == Module.Url) NewUrl();
        else if (_currentModule == Module.Snippet) NewSnippet();
        else if (_currentModule == Module.Password && EnsureUnlocked()) NewPassword();
        else if (_currentModule == Module.Note && EnsureUnlocked()) NewNote();
        else if (_currentModule == Module.IdDocument && EnsureUnlocked()) NewIdDocument();
        else if (_currentModule == Module.Accounting && EnsureUnlocked()) NewAccounting();
    }

    private void DoEdit()
    {
        if (_currentModule == Module.Url) EditUrl();
        else if (_currentModule == Module.Snippet) EditSnippet();
        else if (_currentModule == Module.Password && EnsureUnlocked()) EditPassword();
        else if (_currentModule == Module.Note && EnsureUnlocked()) EditNote();
        else if (_currentModule == Module.IdDocument && EnsureUnlocked()) EditIdDocument();
        else if (_currentModule == Module.Accounting && EnsureUnlocked()) EditAccounting();
    }

    private void DoDelete()
    {
        if (_currentModule == Module.Url) DeleteUrl();
        else if (_currentModule == Module.Snippet) DeleteSnippet();
        else if (_currentModule == Module.Password && EnsureUnlocked()) DeletePassword();
        else if (_currentModule == Module.Note && EnsureUnlocked()) DeleteNote();
        else if (_currentModule == Module.IdDocument && EnsureUnlocked()) DeleteIdDocument();
        else if (_currentModule == Module.Accounting && EnsureUnlocked()) DeleteAccounting();
    }

    // ---- 网址增删改 ----

    private void NewUrl()
    {
        var previous = CaptureFocus();
        var dialog = new UrlEditDialog(null, _urlData.Categories, _vault?.Entries ?? new List<PasswordEntry>())
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _urlData.Entries.Add(result);
            EnsureCategoryExists(result.Category);
            StorageService.SaveUrls(_urlData);
            RefreshTree(); RefreshList(); SelectUrlById(result.Id);
            Announce($"已新建网址：{result.Title}。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditUrl()
    {
        var entry = GetSelectedUrl();
        if (entry is null) { Announce("请先选中要编辑的网址。"); return; }

        var previous = CaptureFocus();
        var dialog = new UrlEditDialog(entry, _urlData.Categories, _vault?.Entries ?? new List<PasswordEntry>())
        { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            var index = _urlData.Entries.IndexOf(entry);
            if (index >= 0) _urlData.Entries[index] = result;
            EnsureCategoryExists(result.Category);
            StorageService.SaveUrls(_urlData);
            RefreshTree(); RefreshList(); SelectUrlById(result.Id);
            Announce($"已更新网址：{result.Title}。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeleteUrl()
    {
        var entry = GetSelectedUrl();
        if (entry is null) { Announce("请先选中要删除的网址。"); return; }

        if (ConfirmDelete($"确认删除网址\"{entry.Title}\"吗？"))
        {
            _urlData.Entries.Remove(entry);
            StorageService.SaveUrls(_urlData);
            RefreshTree(); RefreshList();
            Announce($"已删除网址：{entry.Title}。");
        }
        else Announce("已取消删除。");
    }

    private void EnsureCategoryExists(string? category)
    {
        if (!string.IsNullOrWhiteSpace(category) && !_urlData.Categories.Contains(category))
            _urlData.Categories.Add(category);
    }

    private void SelectUrlById(string id)
    {
        for (var i = 0; i < urlListBox.Items.Count; i++)
        {
            if (urlListBox.Items[i] is ListBoxItem item && item.Tag is UrlEntry entry && entry.Id == id)
            { urlListBox.SelectedIndex = i; urlListBox.Focus(); return; }
        }
        if (urlListBox.Items.Count > 0) { urlListBox.SelectedIndex = 0; urlListBox.Focus(); }
    }

    // ---- 文案增删改 ----

    private void NewSnippet()
    {
        var previous = CaptureFocus();
        var dialog = new SnippetEditDialog(null, _snippetData.Categories) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _snippetData.Entries.Add(result);
            EnsureSnippetCategoryExists(result.Category);
            StorageService.SaveSnippets(_snippetData);
            RefreshTree(); RefreshList(); SelectSnippetById(result.Id);
            Announce($"已新建笔记：{result.Title}。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditSnippet()
    {
        var entry = GetSelectedSnippet();
        if (entry is null) { Announce("请先选中要编辑的笔记。"); return; }

        var previous = CaptureFocus();
        var dialog = new SnippetEditDialog(entry, _snippetData.Categories) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            var index = _snippetData.Entries.IndexOf(entry);
            if (index >= 0) _snippetData.Entries[index] = result;
            EnsureSnippetCategoryExists(result.Category);
            StorageService.SaveSnippets(_snippetData);
            RefreshTree(); RefreshList(); SelectSnippetById(result.Id);
            Announce($"已更新笔记：{result.Title}。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeleteSnippet()
    {
        var entry = GetSelectedSnippet();
        if (entry is null) { Announce("请先选中要删除的笔记。"); return; }

        if (ConfirmDelete($"确认删除笔记\"{entry.Title}\"吗？"))
        {
            _snippetData.Entries.Remove(entry);
            StorageService.SaveSnippets(_snippetData);
            RefreshTree(); RefreshList();
            Announce($"已删除笔记：{entry.Title}。");
        }
    }

    private void DoCopySnippet()
    {
        var entry = GetSelectedSnippet();
        if (entry is null) { Announce("请先选中要复制的笔记。"); return; }

        _clipboard.CopyToClipboard(entry.Content);
        Announce($"已复制笔记内容到剪贴板。");
    }

    private void EnsureSnippetCategoryExists(string category)
    {
        if (!string.IsNullOrWhiteSpace(category) && !_snippetData.Categories.Contains(category))
        {
            _snippetData.Categories.Add(category);
        }
    }

    private void SelectSnippetById(string id)
    {
        for (var i = 0; i < snippetListBox.Items.Count; i++)
        {
            if (snippetListBox.Items[i] is ListBoxItem item && item.Tag is SnippetEntry s && s.Id == id)
            {
                snippetListBox.SelectedIndex = i;
                return;
            }
        }
    }

    // ---- 证件增删改 ----

    private void NewIdDocument()
    {
        var previous = CaptureFocus();
        var categories = new List<string> { "默认" };
        if (_vault?.IdDocuments.Count > 0)
            categories = _vault.IdDocuments.Select(d => d.Category).Distinct().Where(c => !string.IsNullOrEmpty(c)).ToList();
        var dialog = new IdDocumentEditDialog(null, categories) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _vault!.IdDocuments.Add(result);
            SavePasswordVault();
            AuditLogService.Log(_vault, "新建", $"新建证件条目：{result.Title}");
            RefreshTree(); RefreshList(); SelectIdDocumentById(result.Id);
            Announce($"已新建证件：{result.Title}。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditIdDocument()
    {
        var entry = GetSelectedIdDocument();
        if (entry is null) { Announce("请先选中要编辑的证件。"); return; }

        var categories = new List<string> { "默认" };
        if (_vault?.IdDocuments.Count > 0)
            categories = _vault.IdDocuments.Select(d => d.Category).Distinct().Where(c => !string.IsNullOrEmpty(c)).ToList();
        var previous = CaptureFocus();
        var dialog = new IdDocumentEditDialog(entry, categories) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            var index = _vault!.IdDocuments.IndexOf(entry);
            if (index >= 0) _vault.IdDocuments[index] = result;
            SavePasswordVault();
            AuditLogService.Log(_vault, "编辑", $"编辑证件条目：{result.Title}");
            RefreshTree(); RefreshList(); SelectIdDocumentById(result.Id);
            Announce($"已更新证件：{result.Title}。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeleteIdDocument()
    {
        var entry = GetSelectedIdDocument();
        if (entry is null) { Announce("请先选中要删除的证件。"); return; }

        if (ConfirmDelete($"确认删除证件\"{entry.Title}\"吗？"))
        {
            _vault!.IdDocuments.Remove(entry);
            SavePasswordVault();
            AuditLogService.Log(_vault, "删除", $"删除证件条目：{entry.Title}");
            RefreshTree(); RefreshList();
            Announce($"已删除证件：{entry.Title}。");
        }
    }

    private void SelectIdDocumentById(string id)
    {
        for (var i = 0; i < idDocumentListBox.Items.Count; i++)
        {
            if (idDocumentListBox.Items[i] is ListBoxItem item && item.Tag is IdDocumentEntry d && d.Id == id)
            {
                idDocumentListBox.SelectedIndex = i;
                return;
            }
        }
    }

    // ---- 记账增删改 ----

    private void NewAccounting()
    {
        var previous = CaptureFocus();
        var dialog = new AccountingEditDialog(null) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _vault!.Accountings.Add(result);
            SavePasswordVault();
            RefreshTree(); RefreshList(); SelectAccountingById(result.Id);
            Announce($"已新建{result.Type}：{result.Title}，{result.Amount:F2}元。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditAccounting()
    {
        var entry = GetSelectedAccounting();
        if (entry is null) { Announce("请先选中要编辑的账目。"); return; }

        var previous = CaptureFocus();
        var dialog = new AccountingEditDialog(entry) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            var index = _vault!.Accountings.IndexOf(entry);
            if (index >= 0) _vault.Accountings[index] = result;
            SavePasswordVault();
            RefreshTree(); RefreshList(); SelectAccountingById(result.Id);
            Announce($"已更新{result.Type}：{result.Title}，{result.Amount:F2}元。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeleteAccounting()
    {
        var entry = GetSelectedAccounting();
        if (entry is null) { Announce("请先选中要删除的账目。"); return; }

        if (ConfirmDelete($"确认删除账目\"{entry.Title}\"（{entry.Amount:F2}元）吗？"))
        {
            _vault!.Accountings.Remove(entry);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            Announce($"已删除账目：{entry.Title}。");
        }
        else Announce("已取消删除。");
    }

    private void SelectAccountingById(string id)
    {
        for (var i = 0; i < accountingListBox.Items.Count; i++)
        {
            if (accountingListBox.Items[i] is ListBoxItem item && item.Tag is AccountingEntry a && a.Id == id)
            { accountingListBox.SelectedIndex = i; accountingListBox.Focus(); return; }
        }
        if (accountingListBox.Items.Count > 0) { accountingListBox.SelectedIndex = 0; accountingListBox.Focus(); }
    }

    private void DoCopyDocNumber()
    {
        var entry = GetSelectedIdDocument();
        if (entry is null) { Announce("请先选中一个证件。"); return; }
        if (string.IsNullOrEmpty(entry.DocNumber)) { Announce("该证件没有号码。"); return; }
        _clipboard.CopyToClipboard(entry.DocNumber);
        Announce($"已复制证件号码：{entry.DocNumber}。");
    }

    // ---- 上下文菜单构建 ----

    /// <summary>构建导出子菜单，列出所有可导出的格式预设。</summary>
    private MenuItem BuildExportSubMenu()
    {
        var exportMenu = new MenuItem { Header = "导出" };
        AutomationProperties.SetName(exportMenu, "导出菜单，展开选择导出格式");

        foreach (var preset in TextFormatService.Presets)
        {
            var presetName = preset.Name; // 闭包捕获
            var item = new MenuItem
            {
                Header = $"{presetName} (.{preset.FileExtension})",
            };
            AutomationProperties.SetName(item, $"导出为{presetName}，{preset.Description}");
            item.Click += (_, _) => DoExportWithPreset(presetName);
            exportMenu.Items.Add(item);
        }

        return exportMenu;
    }

    /// <summary>使用指定排版预设导出当前模块的选中条目。</summary>
    private void DoExportWithPreset(string presetName)
    {
        var preset = TextFormatService.FindPreset(presetName);
        if (preset is null)
        {
            Announce($"找不到排版预设：{presetName}");
            return;
        }

        if (_currentModule == Module.Snippet)
        {
            var entries = _filteredSnippets.Select(e => (Title: e.Title, Content: e.Content,
                Extra: $"分类：{e.Category}\n创建：{e.CreatedTime:yyyy-MM-dd HH:mm}"));
            ExportEntriesWithPreset(entries, "记事本导出", preset);
        }
        else if (_currentModule == Module.Note && EnsureUnlocked())
        {
            var entries = _filteredNotes.Select(e => (Title: e.Title, Content: e.Content,
                Extra: $"分类：{e.Category}\n天气：{e.Weather}\n心情：{e.Mood}\n创建：{e.CreatedTime:yyyy-MM-dd HH:mm}"));
            ExportEntriesWithPreset(entries, "日记导出", preset);
        }
        else
        {
            Announce("导出仅在记事本和日记模块可用。");
        }
    }

    /// <summary>使用排版预设导出条目到文件，弹出保存位置选择对话框。</summary>
    private void ExportEntriesWithPreset(
        IEnumerable<(string Title, string Content, string Extra)> entries,
        string defaultName,
        TextFormatService.FormatPreset preset)
    {
        var ext = preset.FileExtension;
        var filterDesc = ext switch
        {
            "md" => "Markdown 文件",
            "html" => "网页文件",
            "rtf" => "RTF 富文本",
            "docx" => "Word 文档",
            "xml" => "XML 文件",
            "csv" => "CSV 表格",
            "tex" => "LaTeX 文件",
            "json" => "JSON 文件",
            _ => "文本文件",
        };

        var dlg = new SaveFileDialog
        {
            Filter = $"{filterDesc}|*.{ext}",
            FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd}.{ext}"
        };
        if (dlg.ShowDialog() != true) { Announce("已取消导出。"); return; }

        try
        {
            var count = 0;
            var entryList = entries.ToList();

            // 二进制格式（如 DOCX）
            if (preset.IsBinary && preset.BinaryContent is not null)
            {
                if (entryList.Count == 1)
                {
                    var entry = entryList[0];
                    var content = preset.Options != TextFormatService.FormatOptions.None
                        ? TextFormatService.Format(entry.Content, preset.Options)
                        : entry.Content;
                    var bytes = preset.BinaryContent(entry.Title, content);
                    System.IO.File.WriteAllBytes(dlg.FileName, bytes);
                    count = 1;
                }
                else
                {
                    // 多条目合并为一个文档
                    var combinedContent = new System.Text.StringBuilder();
                    foreach (var entry in entryList)
                    {
                        combinedContent.AppendLine(new string('=', 50));
                        combinedContent.AppendLine($"标题：{entry.Title}");
                        combinedContent.AppendLine(entry.Extra);
                        combinedContent.AppendLine(new string('-', 50));
                        var content = preset.Options != TextFormatService.FormatOptions.None
                            ? TextFormatService.Format(entry.Content, preset.Options)
                            : entry.Content;
                        combinedContent.AppendLine(content);
                        combinedContent.AppendLine();
                        count++;
                    }
                    var bytes = preset.BinaryContent(defaultName, combinedContent.ToString());
                    System.IO.File.WriteAllBytes(dlg.FileName, bytes);
                }
                Announce($"已导出 {count} 条到 {System.IO.Path.GetFileName(dlg.FileName)}，格式：{preset.Name}。");
                return;
            }

            // 文本格式
            var sb = new System.Text.StringBuilder();
            if (entryList.Count == 1 && preset.WrapContent is not null)
            {
                var entry = entryList[0];
                var content = preset.Options != TextFormatService.FormatOptions.None
                    ? TextFormatService.Format(entry.Content, preset.Options)
                    : entry.Content;
                sb.Append(preset.WrapContent(entry.Title, content));
                count = 1;
            }
            else
            {
                foreach (var entry in entryList)
                {
                    var content = preset.Options != TextFormatService.FormatOptions.None
                        ? TextFormatService.Format(entry.Content, preset.Options)
                        : entry.Content;

                    if (preset.WrapContent is not null)
                    {
                        sb.Append(preset.WrapContent(entry.Title, content));
                    }
                    else
                    {
                        sb.AppendLine(new string('=', 50));
                        sb.AppendLine($"标题：{entry.Title}");
                        sb.AppendLine(entry.Extra);
                        sb.AppendLine(new string('-', 50));
                        sb.AppendLine(content);
                        sb.AppendLine();
                    }
                    count++;
                }
            }

            using var writer = new System.IO.StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            writer.Write(sb.ToString());
            Announce($"已导出 {count} 条到 {System.IO.Path.GetFileName(dlg.FileName)}，格式：{preset.Name}。");
        }
        catch (Exception ex)
        {
            Announce($"导出失败：{ex.Message}");
        }
    }

    private ContextMenu BuildSnippetContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("编辑", "编辑笔记", (s, e) => DoEdit(), "F2"));
        menu.Items.Add(MakeMenuItem("复制笔记内容", "复制笔记内容到剪贴板", (s, e) => DoCopySnippet()));
        menu.Items.Add(MakeMenuItem("插入模板", "选择模板复制到剪贴板", (s, e) => DoInsertTemplate(), "Ctrl+Shift+I"));
        menu.Items.Add(MakeMenuItem("总结", "总结文章内容并提取标题标签", (s, e) => DoSummary()));
        menu.Items.Add(BuildExportSubMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("删除", "删除笔记", (s, e) => DoDelete(), "Del"));
        return menu;
    }

    private ContextMenu BuildIdDocumentContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("编辑", "编辑证件", (s, e) => DoEdit(), "F2"));
        menu.Items.Add(MakeMenuItem("复制证件号码", "复制证件号码到剪贴板", (s, e) => DoCopyDocNumber()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("删除", "删除证件", (s, e) => DoDelete(), "Del"));
        return menu;
    }

    private ContextMenu BuildAccountingContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MakeMenuItem("编辑", "编辑账目", (s, e) => DoEdit(), "F2"));
        menu.Items.Add(MakeMenuItem("播报当月统计", "播报当月收支统计", (s, e) => AnnounceMonthlySummary(), "Ctrl+Shift+S"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("切换收藏", "切换收藏置顶", (s, e) => DoToggleFavorite(), "Ctrl+Shift+F"));
        menu.Items.Add(new Separator());
        menu.Items.Add(MakeMenuItem("删除", "删除账目", (s, e) => DoDelete(), "Del"));
        return menu;
    }

    // ---- 密码增删改 ----

    private void SavePasswordVault()
    {
        if (_vault is null || _masterPassword is null) return;
        var encrypted = CryptoService.EncryptVault(_vault, _masterPassword);
        StorageService.SaveVault(encrypted);
    }

    private void NewPassword()
    {
        var previous = CaptureFocus();
        var dialog = new PasswordEditDialog(null) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _vault!.Entries.Add(result);
            SavePasswordVault();
            RefreshTree(); RefreshList(); SelectPasswordById(result.Id);
            AuditLogService.Log(_vault, "新建", $"新建密码条目：{result.Title}");
            Announce($"已新建密码条目：{result.Title}。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditPassword()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中要编辑的密码条目。"); return; }

        var previous = CaptureFocus();
        var dialog = new PasswordEditDialog(entry) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            // 如果密码改变了，更新 LastPasswordChange
            if (result.Password != entry.Password)
                result.LastPasswordChange = DateTime.Now;
            else
                result.LastPasswordChange = entry.LastPasswordChange;

            var index = _vault!.Entries.IndexOf(entry);
            if (index >= 0) _vault.Entries[index] = result;
            SavePasswordVault();
            RefreshTree(); RefreshList(); SelectPasswordById(result.Id);
            AuditLogService.Log(_vault, "编辑", $"编辑密码条目：{result.Title}");
            Announce($"已更新密码条目：{result.Title}。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeletePassword()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中要删除的密码条目。"); return; }

        if (ConfirmDelete($"确认删除密码条目\"{entry.Title}\"吗？"))
        {
            _vault!.Entries.Remove(entry);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            AuditLogService.Log(_vault, "删除", $"删除密码条目：{entry.Title}");
            Announce($"已删除密码条目：{entry.Title}。");
        }
        else Announce("已取消删除。");
    }

    private void SelectPasswordById(string id)
    {
        for (var i = 0; i < passwordListBox.Items.Count; i++)
        {
            if (passwordListBox.Items[i] is ListBoxItem item && item.Tag is PasswordEntry entry && entry.Id == id)
            { passwordListBox.SelectedIndex = i; passwordListBox.Focus(); return; }
        }
        if (passwordListBox.Items.Count > 0) { passwordListBox.SelectedIndex = 0; passwordListBox.Focus(); }
    }

    // ---- 日记增删改 ----

    private void NewNote()
    {
        var previous = CaptureFocus();
        var dialog = new NoteEditDialog(null) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            _vault!.Notes.Add(result);
            SavePasswordVault();
            RefreshTree(); RefreshList(); SelectNoteById(result.Id);
            Announce($"已新建日记：{result.Title}。");
        }
        else Announce("已取消新建。");
        RestoreFocus(previous);
    }

    private void EditNote()
    {
        var entry = GetSelectedNote();
        if (entry is null) { Announce("请先选中要编辑的日记。"); return; }

        var previous = CaptureFocus();
        var dialog = new NoteEditDialog(entry) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.Result is not null)
        {
            var result = dialog.Result;
            var index = _vault!.Notes.IndexOf(entry);
            if (index >= 0) _vault.Notes[index] = result;
            SavePasswordVault();
            RefreshList(); SelectNoteById(result.Id);
            Announce($"已更新日记：{result.Title}。");
        }
        else Announce("已取消编辑。");
        RestoreFocus(previous);
    }

    private void DeleteNote()
    {
        var entry = GetSelectedNote();
        if (entry is null) { Announce("请先选中要删除的日记。"); return; }

        if (ConfirmDelete($"确认删除日记\"{entry.Title}\"吗？"))
        {
            _vault!.Notes.Remove(entry);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            Announce($"已删除日记：{entry.Title}。");
        }
        else Announce("已取消删除。");
    }

    private void SelectNoteById(string id)
    {
        for (var i = 0; i < noteListBox.Items.Count; i++)
        {
            if (noteListBox.Items[i] is ListBoxItem item && item.Tag is NoteEntry entry && entry.Id == id)
            { noteListBox.SelectedIndex = i; noteListBox.Focus(); return; }
        }
        if (noteListBox.Items.Count > 0) { noteListBox.SelectedIndex = 0; noteListBox.Focus(); }
    }

    // ---- 收藏切换 ----

    private void DoToggleFavorite()
    {
        if (_currentModule == Module.Url)
        {
            var entry = GetSelectedUrl();
            if (entry is null) { Announce("请先选中一个网址。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            entry.ModifiedTime = DateTime.Now;
            StorageService.SaveUrls(_urlData);
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
        else if (_currentModule == Module.Snippet)
        {
            var entry = GetSelectedSnippet();
            if (entry is null) { Announce("请先选中一个笔记。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            entry.ModifiedTime = DateTime.Now;
            StorageService.SaveSnippets(_snippetData);
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
        else if (_currentModule == Module.Password && EnsureUnlocked())
        {
            var entry = GetSelectedPassword();
            if (entry is null) { Announce("请先选中一个密码条目。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            SavePasswordVault();
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
        else if (_currentModule == Module.Note && EnsureUnlocked())
        {
            var entry = GetSelectedNote();
            if (entry is null) { Announce("请先选中一个日记。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            entry.ModifiedTime = DateTime.Now;
            SavePasswordVault();
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
        else if (_currentModule == Module.IdDocument && EnsureUnlocked())
        {
            var entry = GetSelectedIdDocument();
            if (entry is null) { Announce("请先选中一个证件。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            entry.ModifiedTime = DateTime.Now;
            SavePasswordVault();
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
        else if (_currentModule == Module.Accounting && EnsureUnlocked())
        {
            var entry = GetSelectedAccounting();
            if (entry is null) { Announce("请先选中一个账目。"); return; }
            entry.IsFavorite = !entry.IsFavorite;
            entry.ModifiedTime = DateTime.Now;
            SavePasswordVault();
            RefreshList();
            Announce(entry.IsFavorite ? $"已收藏：{entry.Title}。" : $"已取消收藏：{entry.Title}。");
        }
    }

    // ---- 网址操作 ----

    private void DoOpenUrl()
    {
        var entry = GetSelectedUrl();
        if (entry is null) { Announce("请先选中一个网址。"); return; }
        if (string.IsNullOrWhiteSpace(entry.Url)) { Announce("该条目没有网址。"); return; }

        try
        {
            var url = entry.Url.Trim();
            if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Announce($"正在打开：{entry.Title}。");
        }
        catch (Exception ex) { Announce("打开网址失败：" + ex.Message); }
    }

    private void DoCopyUrl()
    {
        var entry = GetSelectedUrl();
        if (entry is null) return;
        if (string.IsNullOrWhiteSpace(entry.Url)) return;
        _clipboard.CopyToClipboard(entry.Url);
    }

    private void DoCopyName()
    {
        var entry = GetSelectedUrl();
        if (entry is null) return;
        if (string.IsNullOrEmpty(entry.Title)) return;
        _clipboard.CopyToClipboard(entry.Title);
    }

    private void DoCopyAccount()
    {
        var entry = GetSelectedUrl();
        if (entry is null) return;
        if (string.IsNullOrEmpty(entry.Account)) return;
        _clipboard.CopyToClipboard(entry.Account);
    }

    // ---- 密码复制操作 ----

    private void DoCopyPassword()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中一个密码条目。"); return; }
        if (string.IsNullOrEmpty(entry.Password)) { Announce("该条目未设置密码。"); return; }

        var seconds = _vault?.Settings.ClipboardClearSeconds ?? 30;
        _clipboard.CopyWithAutoClear(entry.Password, seconds, () => Announce("剪贴板已自动清除。"));
        AuditLogService.Log(_vault, "复制", $"复制密码：{entry.Title}");
        Announce($"已复制密码，{seconds} 秒后自动清除。");
    }

    private void DoCopyUserName()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中一个密码条目。"); return; }
        if (string.IsNullOrEmpty(entry.UserName)) { Announce("该条目未设置用户名。"); return; }
        _clipboard.CopyToClipboard(entry.UserName);
        Announce("已复制用户名到剪贴板。");
    }

    private void DoCopyPhone()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中一个密码条目。"); return; }
        if (string.IsNullOrEmpty(entry.PhoneNumber)) { Announce("该条目未设置手机号。"); return; }
        _clipboard.CopyToClipboard(entry.PhoneNumber);
        Announce("已复制手机号到剪贴板。");
    }

    private void DoCopyTotp()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中一个密码条目。"); return; }
        if (string.IsNullOrEmpty(entry.TotpSecret)) { Announce("该条目未设置 TOTP 密钥。"); return; }

        var code = StorageService.GenerateTotp(entry.TotpSecret);
        if (string.IsNullOrEmpty(code)) { Announce("TOTP 密钥无效，无法生成验证码。"); return; }

        var seconds = _vault?.Settings.ClipboardClearSeconds ?? 30;
        _clipboard.CopyWithAutoClear(code, seconds, () => Announce("剪贴板已自动清除。"));
        Announce($"已复制动态验证码 {code}，{seconds} 秒后自动清除。");
    }

    private void DoFieldSelector()
    {
        var entry = GetSelectedPassword();
        if (entry is null) { Announce("请先选中一个密码条目。"); return; }

        var previous = CaptureFocus();
        var dialog = new FieldSelectorDialog(entry) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedFieldIndex >= 0)
        {
            var value = dialog.SelectedValue;
            if (string.IsNullOrEmpty(value)) { Announce("所选字段为空。"); }
            else if (dialog.SelectedSensitive)
            {
                var seconds = _vault?.Settings.ClipboardClearSeconds ?? 30;
                _clipboard.CopyWithAutoClear(value, seconds, () => Announce("剪贴板已自动清除。"));
                Announce($"已复制{dialog.SelectedFieldName}，{seconds} 秒后自动清除。");
            }
            else
            {
                _clipboard.CopyToClipboard(value);
                Announce($"已复制{dialog.SelectedFieldName}到剪贴板。");
            }
        }
        RestoreFocus(previous);
    }

    private void DoLock()
    {
        _clipboard.StopAutoClearTimer();
        _clipboard.ClearClipboard();
        DisableAntiScreenshot();

        _vault = null;
        _isUnlocked = false;
        _masterPassword = null;
        _filteredPasswords.Clear();
        _filteredNotes.Clear();
        passwordListBox.Items.Clear();
        noteListBox.Items.Clear();

        Announce("密码库已锁定。");
        ShowUnlockPanel();
    }

    private void DoPasswordGenerator()
    {
        var previous = CaptureFocus();
        var dialog = new PasswordGeneratorDialog { Owner = this };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.GeneratedPassword))
        {
            var seconds = _vault?.Settings.ClipboardClearSeconds ?? 30;
            _clipboard.CopyWithAutoClear(dialog.GeneratedPassword, seconds, () => Announce("剪贴板已自动清除。"));
            Announce($"已生成并复制新密码，{seconds} 秒后自动清除。");
        }
        RestoreFocus(previous);
    }

    // =========================================================================
    // 安全增强功能
    // =========================================================================

    private void DoChangeMasterPassword()
    {
        if (!EnsureUnlocked() || _masterPassword is null) return;

        var previous = CaptureFocus();
        var dialog = new ChangeMasterPasswordDialog(_masterPassword) { Owner = this };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.NewPassword))
        {
            _masterPassword = dialog.NewPassword;
            SavePasswordVault();
            AuditLogService.Log(_vault, "修改主密码", "主密码已修改");
            Announce("主密码已修改，后续需使用新密码解锁。");
        }
        RestoreFocus(previous);
    }

    private void DoSettings()
    {
        if (_vault is null)
        {
            _vault = new VaultData();
        }

        var previous = CaptureFocus();
        var dialog = new SettingsDialog(_vault.Settings) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            _vault.Settings = dialog.Result;
            if (_isUnlocked) SavePasswordVault();
            SetupAutoLockTimer();
            if (_vault.Settings.AntiScreenshot && _isUnlocked) EnableAntiScreenshot();
            else DisableAntiScreenshot();
            Announce("设置已保存。");
        }
        RestoreFocus(previous);
    }

    private void DoViewAuditLog()
    {
        if (_vault is null) { Announce("请先解锁密码库。"); return; }
        var summary = AuditLogService.GetSummary(_vault);
        MessageBox.Show(this, summary, "审计日志", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // =========================================================================
    // 功能补全：备份恢复、导入、分类管理
    // =========================================================================

    private void DoBackup()
    {
        if (_currentModule == Module.Url)
        {
            var dlg = new SaveFileDialog { Filter = "网址备份|*.json", FileName = "urls_backup.json" };
            if (dlg.ShowDialog() == true)
            {
                if (BackupService.ExportUrlBackup(_urlData, dlg.FileName))
                    Announce("网址数据已导出备份。");
                else
                    Announce("导出失败。");
            }
        }
        else if (EnsureUnlocked())
        {
            var dlg = new SaveFileDialog { Filter = "密码库备份|*.bnbackup", FileName = "vault_backup.bnbackup" };
            if (dlg.ShowDialog() == true)
            {
                if (BackupService.ExportVaultBackup(_vault!, _masterPassword!, dlg.FileName))
                {
                    AuditLogService.Log(_vault, "备份", "导出密码库备份");
                    Announce("密码库已导出加密备份。");
                }
                else
                    Announce("导出失败。");
            }
        }
    }

    private void DoRestore()
    {
        if (_currentModule == Module.Url)
        {
            var dlg = new OpenFileDialog { Filter = "网址备份|*.json" };
            if (dlg.ShowDialog() == true)
            {
                var restored = BackupService.ImportUrlBackup(dlg.FileName);
                if (restored is not null)
                {
                    if (ConfirmDelete("确认恢复网址数据吗？当前数据将被覆盖。"))
                    {
                        _urlData = restored;
                        StorageService.SaveUrls(_urlData);
                        RefreshTree(); RefreshList();
                        Announce($"已恢复 {restored.Entries.Count} 条网址。");
                    }
                }
                else Announce("恢复失败，文件无效。");
            }
        }
        else if (EnsureUnlocked())
        {
            var dlg = new OpenFileDialog { Filter = "密码库备份|*.bnbackup" };
            if (dlg.ShowDialog() == true)
            {
                var restored = BackupService.ImportVaultBackup(dlg.FileName, _masterPassword!);
                if (restored is not null)
                {
                    if (ConfirmDelete("确认恢复密码库吗？当前数据将被覆盖。"))
                    {
                        _vault = restored;
                        SavePasswordVault();
                        RefreshTree(); RefreshList();
                        AuditLogService.Log(_vault, "恢复", "从备份恢复密码库");
                        Announce($"已恢复 {restored.Entries.Count} 条密码、{restored.Notes.Count} 条日记。");
                    }
                }
                else Announce("恢复失败，文件无效或主密码不匹配。");
            }
        }
    }

    // ---- 全量备份与恢复 ----

    private void DoFullBackup()
    {
        // 判断是否包含密码库
        var includeVault = _isUnlocked && _vault is not null && !string.IsNullOrEmpty(_masterPassword);

        if (!includeVault)
        {
            // 未解锁时提示：将仅备份网址和记事本
            var choice = MessageBox.Show(this,
                "密码库未解锁，全量备份将仅包含网址收藏和记事本数据（不含密码/日记/证件）。\n\n" +
                "如需包含全部数据，请先解锁密码库后再执行全量备份。\n\n是否继续？",
                "全量备份", MessageBoxButton.OKCancel, MessageBoxImage.Information);

            if (choice != MessageBoxResult.OK) return;
        }

        var dlg = new SaveFileDialog
        {
            Filter = "全量备份|*.bnfull",
            FileName = $"SuixinJi_全量备份_{DateTime.Now:yyyyMMdd_HHmmss}.bnfull"
        };

        if (dlg.ShowDialog() != true) { Announce("已取消全量备份。"); return; }

        if (BackupService.ExportFullBackup(_urlData, _snippetData, _vault, _masterPassword, dlg.FileName))
        {
            var urlCount = _urlData.Entries.Count;
            var snippetCount = _snippetData.Entries.Count;
            var vaultInfo = includeVault
                ? $"密码 {_vault!.Entries.Count} 条、日记 {_vault.Notes.Count} 篇、证件 {_vault.IdDocuments.Count} 条、记账 {_vault.Accountings.Count} 笔"
                : "密码库未包含";

            if (includeVault)
                AuditLogService.Log(_vault, "全量备份", $"导出全量备份：网址 {urlCount}、记事本 {snippetCount}、密码库");

            Announce($"全量备份成功。网址 {urlCount} 条、记事本 {snippetCount} 条、{vaultInfo}。");
        }
        else
        {
            Announce("全量备份失败。");
        }
    }

    private void DoFullRestore()
    {
        var dlg = new OpenFileDialog { Filter = "全量备份|*.bnfull" };
        if (dlg.ShowDialog() != true) { Announce("已取消全量恢复。"); return; }

        // 尝试读取备份文件，判断是否包含密码库
        // 先尝试用当前主密码解密（如果已解锁）
        string? masterPassword = _masterPassword;
        bool wasUnlocked = _isUnlocked;

        if (!wasUnlocked)
        {
            // 未解锁时需要用户输入主密码
            masterPassword = PromptForMasterPassword();
            if (string.IsNullOrEmpty(masterPassword))
            {
                Announce("已取消全量恢复。");
                return;
            }
        }

        var (urlData, snippetData, vault, hasVault) = BackupService.ImportFullBackup(dlg.FileName, masterPassword!);

        if (urlData is null && snippetData is null && vault is null)
        {
            // 可能是密码不匹配
            Announce(hasVault
                ? "恢复失败，主密码不匹配或文件已损坏。"
                : "恢复失败，文件无效。");
            return;
        }

        // 确认覆盖
        var parts = new List<string>();
        if (urlData is not null) parts.Add($"网址 {urlData.Entries.Count} 条");
        if (snippetData is not null) parts.Add($"记事本 {snippetData.Entries.Count} 条");
        if (vault is not null) parts.Add($"密码 {vault.Entries.Count} 条、日记 {vault.Notes.Count} 篇、证件 {vault.IdDocuments.Count} 条、记账 {vault.Accountings.Count} 笔");
        else if (hasVault) parts.Add("密码库（解密失败，将跳过）");

        var summary = string.Join("、", parts);
        if (!ConfirmDelete($"确认从备份文件恢复吗？\n\n备份内容：{summary}\n\n当前所有数据将被覆盖，此操作不可撤销。"))
        {
            Announce("已取消全量恢复。");
            return;
        }

        // 恢复网址
        if (urlData is not null)
        {
            _urlData = urlData;
            if (_urlData.Categories.Count == 0) _urlData.Categories.Add("默认");
            StorageService.SaveUrls(_urlData);
        }

        // 恢复记事本
        if (snippetData is not null)
        {
            _snippetData = snippetData;
            if (_snippetData.Categories.Count == 0) _snippetData.Categories.Add("默认");
            StorageService.SaveSnippets(_snippetData);
        }

        // 恢复密码库
        if (vault is not null)
        {
            _vault = vault;
            _masterPassword = masterPassword;
            _isUnlocked = true;
            SavePasswordVault();
            SetupAutoLockTimer();
            EnableAntiScreenshot();
            AuditLogService.Log(_vault, "全量恢复", $"从全量备份恢复：网址 {(urlData?.Entries.Count ?? 0)}、记事本 {(snippetData?.Entries.Count ?? 0)}、密码库");
        }

        RefreshTree();
        RefreshList();
        SwitchToModule(Module.Url);

        Announce($"全量恢复成功。{summary}。");
    }

    /// <summary>弹出一个简单的密码输入对话框，用于全量恢复时输入主密码。</summary>
    private string? PromptForMasterPassword()
    {
        var dialog = new FullRestorePasswordDialog { Owner = this };
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    private void DoImport()
    {
        var previous = CaptureFocus();
        var dialog = new ImportDialog { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            if (dialog.ImportedUrls.Count > 0)
            {
                if (_urlData.Categories.Contains("导入") == false)
                    _urlData.Categories.Add("导入");

                foreach (var url in dialog.ImportedUrls)
                    _urlData.Entries.Add(url);

                StorageService.SaveUrls(_urlData);
                RefreshTree(); RefreshList();
                Announce($"已导入 {dialog.ImportedUrls.Count} 条网址。");
            }

            if (dialog.ImportedPasswords.Count > 0 && EnsureUnlocked())
            {
                foreach (var pwd in dialog.ImportedPasswords)
                    _vault!.Entries.Add(pwd);

                SavePasswordVault();
                RefreshTree(); RefreshList();
                AuditLogService.Log(_vault, "导入", $"导入 {dialog.ImportedPasswords.Count} 条密码");
                Announce($"已导入 {dialog.ImportedPasswords.Count} 条密码。");
            }
        }
        RestoreFocus(previous);
    }

    private void DoCategoryManager()
    {
        if (_currentModule == Module.Url)
        {
            var previous = CaptureFocus();
            var dialog = new CategoryManagerDialog(_urlData.Categories) { Owner = this };

            if (dialog.ShowDialog() == true)
            {
                _urlData.Categories = dialog.Result;
                if (_urlData.Categories.Count == 0)
                    _urlData.Categories.Add("默认");
                StorageService.SaveUrls(_urlData);
                RefreshTree(); RefreshList();
                Announce("分类已更新。");
            }
            RestoreFocus(previous);
        }
        else
        {
            Announce("分类管理仅在网址模块可用。密码模块使用标签（在编辑界面设置）。");
        }
    }

    // =========================================================================
    // 效率提升：批量操作、网址健康检查
    // =========================================================================

    private void DoBatchDelete()
    {
        var count = _currentModule switch
        {
            Module.Url => urlListBox.SelectedItems.Count,
            Module.Snippet => snippetListBox.SelectedItems.Count,
            Module.Password => passwordListBox.SelectedItems.Count,
            Module.Note => noteListBox.SelectedItems.Count,
            Module.IdDocument => idDocumentListBox.SelectedItems.Count,
            _ => 0
        };

        if (count == 0) { Announce("请先选中要删除的条目（按住 Ctrl 或 Shift 多选）。"); return; }

        if (!ConfirmDelete($"确认删除选中的 {count} 个条目吗？")) { Announce("已取消批量删除。"); return; }

        if (_currentModule == Module.Url)
        {
            var entries = urlListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(i => i.Tag as UrlEntry)
                .Where(e => e is not null)
                .Cast<UrlEntry>()
                .ToList();
            foreach (var e in entries) _urlData.Entries.Remove(e);
            StorageService.SaveUrls(_urlData);
            RefreshTree(); RefreshList();
            Announce($"已批量删除 {entries.Count} 条网址。");
        }
        else if (_currentModule == Module.Snippet)
        {
            var entries = snippetListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(i => i.Tag as SnippetEntry)
                .Where(e => e is not null)
                .Cast<SnippetEntry>()
                .ToList();
            foreach (var e in entries) _snippetData.Entries.Remove(e);
            StorageService.SaveSnippets(_snippetData);
            RefreshTree(); RefreshList();
            Announce($"已批量删除 {entries.Count} 条文案。");
        }
        else if (_currentModule == Module.Password && EnsureUnlocked())
        {
            var entries = passwordListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(i => i.Tag as PasswordEntry)
                .Where(e => e is not null)
                .Cast<PasswordEntry>()
                .ToList();
            foreach (var e in entries) _vault!.Entries.Remove(e);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            AuditLogService.Log(_vault, "批量删除", $"删除 {entries.Count} 条密码");
            Announce($"已批量删除 {entries.Count} 条密码。");
        }
        else if (_currentModule == Module.Note && EnsureUnlocked())
        {
            var entries = noteListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(i => i.Tag as NoteEntry)
                .Where(e => e is not null)
                .Cast<NoteEntry>()
                .ToList();
            foreach (var e in entries) _vault!.Notes.Remove(e);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            Announce($"已批量删除 {entries.Count} 条日记。");
        }
        else if (_currentModule == Module.IdDocument && EnsureUnlocked())
        {
            var entries = idDocumentListBox.SelectedItems
                .OfType<ListBoxItem>()
                .Select(i => i.Tag as IdDocumentEntry)
                .Where(e => e is not null)
                .Cast<IdDocumentEntry>()
                .ToList();
            foreach (var e in entries) _vault!.IdDocuments.Remove(e);
            SavePasswordVault();
            RefreshTree(); RefreshList();
            AuditLogService.Log(_vault, "批量删除", $"删除 {entries.Count} 条证件");
            Announce($"已批量删除 {entries.Count} 条证件。");
        }
    }

    private void DoBatchMoveCategory()
    {
        if (_currentModule != Module.Url) { Announce("批量移动分类仅在网址模块可用。"); return; }

        var selected = urlListBox.SelectedItems
            .OfType<ListBoxItem>()
            .Select(i => i.Tag as UrlEntry)
            .Where(e => e is not null)
            .Cast<UrlEntry>()
            .ToList();

        if (selected.Count == 0) { Announce("请先选中要移动的条目。"); return; }

        var dialog = new CategoryManagerDialog(_urlData.Categories) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            var cats = dialog.Result;
            if (cats.Count > 0)
            {
                var target = cats[^1]; // 使用最后添加或选中的分类
                foreach (var entry in selected)
                {
                    entry.Category = target;
                    entry.ModifiedTime = DateTime.Now;
                }
                _urlData.Categories = cats;
                StorageService.SaveUrls(_urlData);
                RefreshTree(); RefreshList();
                Announce($"已将 {selected.Count} 条网址移动到\"{target}\"分类。");
            }
        }
    }

    private async void DoUrlHealthCheck()
    {
        if (_filteredUrls.Count == 0) { Announce("没有可检查的网址。"); return; }

        Announce($"开始检查 {_filteredUrls.Count} 个网址的可访问性，请稍候...");
        var entries = _filteredUrls.ToList();

        await Task.Run(async () =>
        {
            await UrlHealthChecker.CheckAllAsync(entries);
        });

        StorageService.SaveUrls(_urlData);
        RefreshList();
        var okCount = entries.Count(e => e.LastCheckStatus == "OK");
        var failCount = entries.Count(e => e.LastCheckStatus != "OK" && e.LastCheckStatus != "Unknown");
        Announce($"检查完成：{okCount} 个正常，{failCount} 个异常。");
    }

    // =========================================================================
    // 重复检测与快捷键配置
    // =========================================================================

    private void DoDuplicateCheck()
    {
        var urlGroups = DuplicateDetector.DetectDuplicateUrls(_urlData.Entries);
        var pwdGroups = _vault is not null
            ? DuplicateDetector.DetectDuplicatePasswords(_vault.Entries)
            : new List<DuplicateDetector.DuplicatePasswordGroup>();

        var dialog = new DuplicateDialog(urlGroups, pwdGroups) { Owner = this };
        dialog.ShowDialog();

        var total = urlGroups.Count + pwdGroups.Count;
        if (total == 0)
            Announce("未检测到重复条目。");
        else
            Announce($"检测到 {urlGroups.Count} 组重复网址、{pwdGroups.Count} 组重复密码。");
    }

    private void DoShortcutConfig()
    {
        var previous = CaptureFocus();
        var dialog = new ShortcutConfigDialog(_shortcuts) { Owner = this };

        if (dialog.ShowDialog() == true)
        {
            _shortcuts = ShortcutConfigService.Load();
            Announce("快捷键配置已保存。");
        }
        RestoreFocus(previous);
    }

    // =========================================================================
    // 排序功能
    // =========================================================================

    private void DoToggleSort()
    {
        _sortMode = (_sortMode + 1) % 3;
        var modeName = _sortMode switch
        {
            0 => "按名称排序",
            1 => "按创建时间排序",
            2 => "按修改时间排序",
            _ => ""
        };
        RefreshList();
        Announce($"已切换为{modeName}。");
    }

    // =========================================================================
    // 文章总结与标题提取
    // =========================================================================

    /// <summary>总结当前选中条目的内容，弹出总结对话框。</summary>
    private void DoSummary()
    {
        string? title = null;
        string? content = null;

        if (_currentModule == Module.Snippet)
        {
            var entry = GetSelectedSnippet();
            if (entry is null) { Announce("请先选中一篇笔记。"); return; }
            title = entry.Title;
            content = entry.Content;
        }
        else if (_currentModule == Module.Note && EnsureUnlocked())
        {
            var entry = GetSelectedNote();
            if (entry is null) { Announce("请先选中一篇日记。"); return; }
            title = entry.Title;
            content = entry.Content;
        }
        else
        {
            Announce("总结功能仅在记事本和日记模块可用。");
            return;
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            Announce("内容为空，无法生成总结。");
            return;
        }

        var previous = CaptureFocus();
        var dialog = new SummaryDialog(title!, content!) { Owner = this };
        dialog.ShowDialog();
        RestoreFocus(previous);
    }

    // =========================================================================
    // 导出TXT
    // =========================================================================

    private void DoExportTxt()
    {
        if (_currentModule == Module.Snippet)
        {
            ExportEntriesToTxt(
                _filteredSnippets.Select(e => (Title: e.Title, Content: e.Content, Extra: $"分类：{e.Category}\n创建：{e.CreatedTime:yyyy-MM-dd HH:mm}")),
                "记事本导出");
        }
        else if (_currentModule == Module.Note && EnsureUnlocked())
        {
            ExportEntriesToTxt(
                _filteredNotes.Select(e => (Title: e.Title, Content: e.Content, Extra: $"分类：{e.Category}\n天气：{e.Weather}\n心情：{e.Mood}\n创建：{e.CreatedTime:yyyy-MM-dd HH:mm}")),
                "日记导出");
        }
        else
        {
            Announce("导出TXT仅在记事本和日记模块可用。");
        }
    }

    private void ExportEntriesToTxt(IEnumerable<(string Title, string Content, string Extra)> entries, string defaultName)
    {
        var dlg = new SaveFileDialog { Filter = "文本文件|*.txt", FileName = $"{defaultName}_{DateTime.Now:yyyyMMdd}.txt" };
        if (dlg.ShowDialog() != true) { Announce("已取消导出。"); return; }

        try
        {
            using var writer = new System.IO.StreamWriter(dlg.FileName, false, System.Text.Encoding.UTF8);
            var count = 0;
            foreach (var entry in entries)
            {
                writer.WriteLine($"{'='}{'=',50}");
                writer.WriteLine($"标题：{entry.Title}");
                writer.WriteLine(entry.Extra);
                writer.WriteLine($"{'-',50}");
                writer.WriteLine(entry.Content);
                writer.WriteLine();
                count++;
            }
            Announce($"已导出 {count} 条到 {System.IO.Path.GetFileName(dlg.FileName)}。");
        }
        catch (Exception ex)
        {
            Announce($"导出失败：{ex.Message}");
        }
    }

    // =========================================================================
    // 模板/快捷短语功能（记事本模块）
    // =========================================================================

    private void DoInsertTemplate()
    {
        if (_currentModule != Module.Snippet)
        {
            Announce("模板功能仅在记事本模块可用。");
            return;
        }

        // 预定义模板列表
        var templates = new[]
        {
            ("日期模板", DateTime.Now.ToString("yyyy年MM月dd日 dddd")),
            ("时间模板", DateTime.Now.ToString("HH:mm:ss")),
            ("日期时间", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")),
            ("问候语-早上", "早上好！今天是" + DateTime.Now.ToString("yyyy年MM月dd日") + "。"),
            ("问候语-下午", "下午好！今天是" + DateTime.Now.ToString("yyyy年MM月dd日") + "。"),
            ("问候语-晚上", "晚上好！今天是" + DateTime.Now.ToString("yyyy年MM月dd日") + "。"),
            ("待办清单", "【待办事项】\n1. \n2. \n3. \n"),
            ("会议记录", "【会议记录】\n时间：\n地点：\n参会人员：\n议题：\n\n内容：\n"),
            ("联系信息", "姓名：\n电话：\n邮箱：\n地址：\n"),
        };

        var previous = CaptureFocus();
        var dialog = new TemplatePickerDialog(templates) { Owner = this };

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(dialog.SelectedContent))
        {
            _clipboard.CopyToClipboard(dialog.SelectedContent);
            Announce($"已复制模板「{dialog.SelectedTitle}」到剪贴板，可粘贴使用。");
        }
        RestoreFocus(previous);
    }

    // =========================================================================
    // 草稿自动保存
    // =========================================================================

    private static readonly string DraftFilePath = Path.Combine(StorageService.AppDataDir, "draft.json");

    private void LoadDraft()
    {
        try
        {
            if (!File.Exists(DraftFilePath)) return;
            var json = File.ReadAllText(DraftFilePath);
            if (!string.IsNullOrWhiteSpace(json))
            {
                _draftKey = json;
            }
        }
        catch { }
    }

    private void SaveDraft(string content)
    {
        try
        {
            var dir = Path.GetDirectoryName(DraftFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(DraftFilePath, content ?? "");
        }
        catch { }
    }

    private void ClearDraft()
    {
        try
        {
            if (File.Exists(DraftFilePath))
                File.Delete(DraftFilePath);
            _draftKey = null;
        }
        catch { }
    }

    // =========================================================================
    // 日记连续记录提醒与按月浏览
    // =========================================================================

    private void CheckDiaryStreak()
    {
        if (_vault is null || _vault.Notes.Count == 0) return;

        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        var hasToday = _vault.Notes.Any(n => n.CreatedTime.Date == today);
        var hasYesterday = _vault.Notes.Any(n => n.CreatedTime.Date == yesterday);

        if (!hasToday && hasYesterday)
        {
            // 计算连续天数
            var streak = 0;
            var checkDate = yesterday;
            while (_vault.Notes.Any(n => n.CreatedTime.Date == checkDate))
            {
                streak++;
                checkDate = checkDate.AddDays(-1);
            }

            if (streak >= 2)
            {
                Announce($"提醒：已连续记录日记 {streak} 天，今天还没有写日记哦！按 Ctrl+4 切换到日记模块，Ctrl+N 新建。");
            }
        }
    }

    private void DoBrowseByMonth()
    {
        if (_currentModule != Module.Note || !EnsureUnlocked())
        {
            Announce("按月浏览仅在日记模块可用。");
            return;
        }

        if (_vault is null || _vault.Notes.Count == 0)
        {
            Announce("暂无日记记录。");
            return;
        }

        // 按月分组
        var months = _vault.Notes
            .GroupBy(n => new { n.CreatedTime.Year, n.CreatedTime.Month })
            .OrderByDescending(g => new DateTime(g.Key.Year, g.Key.Month, 1))
            .ToList();

        var monthList = months.Select(g => $"{g.Key.Year}年{g.Key.Month:D2}月（{g.Count()}篇）").ToList();

        var previous = CaptureFocus();
        var dialog = new MonthPickerDialog(monthList) { Owner = this };

        if (dialog.ShowDialog() == true && dialog.SelectedIndex >= 0 && dialog.SelectedIndex < months.Count)
        {
            var selectedGroup = months[dialog.SelectedIndex];

            noteListBox.Items.Clear();
            _filteredNotes = selectedGroup
                .OrderByDescending(n => n.IsFavorite)
                .ThenByDescending(n => n.CreatedTime)
                .ToList();

            foreach (var entry in _filteredNotes)
            {
                var prefix = entry.IsFavorite ? "★ " : "";
                var item = new ListBoxItem { Content = prefix + entry.Title, Tag = entry };
                AutomationProperties.SetName(item, (entry.IsFavorite ? "收藏 " : "") + entry.Title);
                item.ContextMenu = BuildNoteContextMenu();
                noteListBox.Items.Add(item);
            }

            if (noteListBox.Items.Count > 0) noteListBox.SelectedIndex = 0;
            UpdateDetail();
            Announce($"已筛选 {selectedGroup.Key.Year}年{selectedGroup.Key.Month:D2}月，共 {selectedGroup.Count()} 篇日记。");
        }
        RestoreFocus(previous);
    }

    // =========================================================================
    // 辅助方法
    // =========================================================================

    private bool ConfirmDelete(string message)
    {
        return MessageBox.Show(this, message, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question)
               == MessageBoxResult.Yes;
    }

    /// <summary>
    /// 通用排序辅助方法。按收藏置顶优先，然后根据 _sortMode 按名称/创建时间/修改时间排序。
    /// </summary>
    private List<T> SortEntries<T>(
        List<T> entries,
        Func<T, bool> isFavoriteSelector,
        Func<T, string> titleSelector,
        Func<T, DateTime> createdTimeSelector,
        Func<T, DateTime> modifiedTimeSelector)
    {
        return _sortMode switch
        {
            1 => entries.OrderByDescending(isFavoriteSelector).ThenByDescending(createdTimeSelector).ToList(),
            2 => entries.OrderByDescending(isFavoriteSelector).ThenByDescending(modifiedTimeSelector).ToList(),
            _ => entries.OrderByDescending(isFavoriteSelector).ThenBy(titleSelector, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private FrameworkElement? CaptureFocus() => FocusManager.GetFocusedElement(this) as FrameworkElement;

    private void RestoreFocus(FrameworkElement? previous)
    {
        if (previous is not null && previous.IsVisible) previous.Focus();
        else FocusList();
    }

    // =========================================================================
    // 自动更新检查
    // =========================================================================

    /// <summary>检查应用更新。silent=true 时静默检查，仅在有更新时提示。</summary>
    private async Task CheckForUpdateAsync(bool silent)
    {
        try
        {
            if (!silent)
                Announce("正在检查更新...");

            var release = await UpdateService.FetchLatestReleaseAsync(
                UpdateService.RepoOwner, UpdateService.RepoName);

            if (release is null)
            {
                if (!silent) Announce("无法连接更新服务器，请稍后重试。");
                return;
            }

            var currentVer = UpdateService.CurrentVersion;
            var hasUpdate = UpdateService.IsNewerVersion(currentVer, release.TagName);

            if (!hasUpdate)
            {
                if (!silent) Announce($"当前已是最新版本 v{currentVer}。");
                return;
            }

            // 查找 win-x64 安装包
            var asset = UpdateService.FindWinX64Asset(release);
            if (asset is null)
            {
                // 没有可用的安装包，降级为打开网页
                var body0 = string.IsNullOrEmpty(release.Body) ? "无详细信息" : release.Body;
                if (body0.Length > 1000) body0 = body0[..1000] + "...";

                var msg0 = $"发现新版本 {release.TagName}！\n\n" +
                           $"当前版本：v{currentVer}\n" +
                           $"发布日期：{release.PublishedAt:yyyy-MM-dd}\n\n" +
                           $"更新内容：\n{body0}\n\n" +
                           $"未能找到自动安装包，是否前往下载页面？";

                var r0 = MessageBox.Show(this, msg0, "发现新版本",
                    MessageBoxButton.YesNo, MessageBoxImage.Information);

                if (r0 == MessageBoxResult.Yes && !string.IsNullOrEmpty(release.HtmlUrl))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo(release.HtmlUrl) { UseShellExecute = true });
                        Announce("正在打开下载页面。");
                    }
                    catch { Announce("无法打开浏览器，请手动访问下载页面。"); }
                }
                return;
            }

            // 有新版本且找到安装包：提示用户是否自动更新
            var body = string.IsNullOrEmpty(release.Body) ? "无详细信息" : release.Body;
            if (body.Length > 800) body = body[..800] + "...";

            var sizeStr = asset.Size > 0
                ? $"（安装包大小：{asset.Size / (1024.0 * 1024):F1} MB）"
                : "";

            var msg = $"发现新版本 {release.TagName}！{sizeStr}\n\n" +
                      $"当前版本：v{currentVer}\n" +
                      $"发布日期：{release.PublishedAt:yyyy-MM-dd}\n\n" +
                      $"更新内容：\n{body}\n\n" +
                      $"是否立即下载并自动更新？\n" +
                      $"更新过程中应用将自动关闭并重启。";

            var result = MessageBox.Show(this, msg, "发现新版本",
                MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
            {
                Announce("已跳过本次更新。");
                return;
            }

            // 启动自动更新对话框
            Announce("正在下载更新，请稍候。");
            var dialog = new UpdateProgressDialog(release, asset) { Owner = this };
            dialog.Loaded += async (_, _) => await dialog.StartUpdateAsync();
            dialog.ShowDialog();

            if (dialog.UpdateCompleted)
            {
                // 更新脚本已启动，关闭应用让脚本完成替换和重启
                Announce("更新下载完成，应用即将重启。");
                Application.Current.Shutdown();
            }
            else if (!string.IsNullOrEmpty(dialog.ErrorMessage))
            {
                Announce($"更新失败：{dialog.ErrorMessage}");
                MessageBox.Show(this, $"更新失败：{dialog.ErrorMessage}\n\n" +
                    "您可以稍后重试，或前往官网手动下载。",
                    "更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch
        {
            if (!silent) Announce("检查更新失败，请稍后重试。");
        }
    }

    // =========================================================================
    // 更新日志
    // =========================================================================

    /// <summary>显示更新日志对话框。</summary>
    private void ShowChangelog()
    {
        var changelog = ChangelogService.GetFullChangelog();
        var currentVer = UpdateService.CurrentVersion;

        var msg = $"当前版本：v{currentVer}\n\n{changelog}";

        MessageBox.Show(this, msg, "更新日志",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
