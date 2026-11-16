using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CoolPlayer;

enum VkMode { My, Recs, Playlists, Search, Queue, History, Local }

internal partial class VkPage : UserControl
{
    readonly VkMode mode;
    readonly ObservableCollection<Track> items = [];
    bool loaded;
    int loadVersion;

    public VkPage(VkMode mode)
    {
        this.mode = mode;
        InitializeComponent();
        Tracks.ItemsSource = items;
        SearchBox.Visibility = mode == VkMode.Search ? Visibility.Visible : Visibility.Collapsed;
        AppHost.Vk.AuthChanged += () => Dispatcher.InvokeAsync(() =>
        {
            loaded = false;
            items.Clear();
            if (IsVisible) OnShown();
        });
        IsVisibleChanged += (_, _) => { if (IsVisible) OnShown(); };
        switch (mode)
        {
            case VkMode.Queue:
                AppHost.Player.QueueChanged += Reload;
                AppHost.Player.Changed += () => { if (AppHost.Player.Current != shownCurrent) Reload(); };
                ExtraBtn.Content = L.T("Очистить очередь");
                break;
            case VkMode.History:
                History.Changed += Reload;
                ExtraBtn.Content = L.T("Очистить историю");
                break;
            case VkMode.Local:
                ExtraBtn.Content = L.T("Добавить папку…");
                break;
            case VkMode.Playlists:
                ExtraBtn.Content = L.T("Перенести из Spotify / Яндекса…");
                break;
        }
        ExtraBtn.Visibility = mode is VkMode.Queue or VkMode.History or VkMode.Local or VkMode.Playlists ? Visibility.Visible : Visibility.Collapsed;
    }

    Track? shownCurrent;
    bool NeedsVk => mode is VkMode.My or VkMode.Recs or VkMode.Playlists or VkMode.Search;

    /// <summary>Перерисовать, если страница открыта (иначе — при следующем показе).</summary>
    void Reload() => Dispatcher.InvokeAsync(() =>
    {
        loaded = false;
        if (IsVisible) _ = LoadAsync();
    });

    async void ExtraClick(object s, RoutedEventArgs e)
    {
        switch (mode)
        {
            case VkMode.Queue: AppHost.Player.ClearUpcoming(); break;
            case VkMode.Playlists: ImportWindow.Open(); break;
            case VkMode.History:
                if (MessageBox.Show(L.T("Очистить историю прослушивания?"), "Taskbeat", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    History.Clear();
                break;
            case VkMode.Local:
                var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Папка с музыкой"), Multiselect = true };
                if (dlg.ShowDialog() != true) return;
                foreach (var f in dlg.FolderNames)
                    if (!SettingsStore.Current.LocalFolders.Contains(f, StringComparer.OrdinalIgnoreCase)) SettingsStore.Current.LocalFolders.Add(f);
                SettingsStore.Save();
                await LoadAsync();
                break;
        }
    }

    public void OnShown()
    {
        if (NeedsVk && !AppHost.Vk.LoggedIn)
        {
            SetView(login: true);
            return;
        }
        LoginCard.Visibility = Visibility.Collapsed;
        Header.Visibility = Visibility.Visible;
        if (!loaded) _ = LoadAsync();
        if (mode == VkMode.Search) Dispatcher.BeginInvoke(() => SearchBox.Focus());
    }

    void SetView(bool login = false, bool playlists = false, string status = "")
    {
        LoginCard.Visibility = login ? Visibility.Visible : Visibility.Collapsed;
        Header.Visibility = login ? Visibility.Collapsed : Visibility.Visible;
        PlaylistScroll.Visibility = !login && playlists ? Visibility.Visible : Visibility.Collapsed;
        Tracks.Visibility = !login && !playlists ? Visibility.Visible : Visibility.Collapsed;
        ShuffleAllBtn.Visibility = !login && !playlists && items.Count > 0 && mode != VkMode.Queue ? Visibility.Visible : Visibility.Collapsed;
        BackBtn.Visibility = mode == VkMode.Playlists && !playlists && !login ? Visibility.Visible : Visibility.Collapsed;
        Status.Text = status;
    }

    async Task LoadAsync()
    {
        loaded = true;
        int ver = ++loadVersion;
        items.Clear();
        try
        {
            switch (mode)
            {
                case VkMode.My:
                    SetView(status: L.T("Загрузка…"));
                    const int page = 500;
                    for (int offset = 0; offset < 10000; offset += page)
                    {
                        var batch = await AppHost.Vk.MyAudioAsync(offset, page);
                        if (ver != loadVersion) return;
                        foreach (var t in batch) items.Add(t);
                        Done();
                        if (batch.Count < page) break;
                    }
                    break;
                case VkMode.Recs:
                    SetView(status: L.T("Подбираем…"));
                    Fill(await AppHost.Vk.RecommendationsAsync(), ver);
                    break;
                case VkMode.Playlists:
                    SetView(playlists: true, status: L.T("Загрузка…"));
                    var lists = await AppHost.Vk.PlaylistsAsync();
                    if (ver != loadVersion) return;
                    Playlists.ItemsSource = lists;
                    SubTitle.Text = $"{lists.Count} плейлистов";
                    SetView(playlists: true, status: lists.Count == 0 ? L.T("Плейлистов пока нет") : "");
                    break;
                case VkMode.Search:
                    SetView(status: L.T("Найдите что угодно — нажмите Enter"));
                    SubTitle.Text = "";
                    break;
                case VkMode.Queue:
                    shownCurrent = AppHost.Player.Current;
                    var q = new List<Track>();
                    if (shownCurrent != null) q.Add(shownCurrent);
                    q.AddRange(AppHost.Player.Upcoming());
                    Fill(q, ver);
                    break;
                case VkMode.History:
                    Fill([.. History.Items.Select(h => h.Track)], ver);
                    break;
                case VkMode.Local:
                    SetView(status: L.T("Ищу музыку на компьютере…"));
                    var found = await LocalLibrary.ScanAsync(new Progress<int>(n => { if (ver == loadVersion) Status.Text = L.F("Найдено файлов: {0}…", n); }));
                    Fill(found, ver);
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ver != loadVersion) return;
            loaded = false;
            SetView(playlists: mode == VkMode.Playlists, status: ex is VkException ? ex.Message : L.T("Нет связи с VK"));
            if (ex is not VkException) Program.Log(ex);
        }
    }

    void Fill(List<Track> list, int ver)
    {
        if (ver != loadVersion) return;
        items.Clear();
        var bad = SettingsStore.Current.Disliked;
        foreach (var t in list)
            if (mode != VkMode.Recs || !bad.Contains(t.Id)) items.Add(t);
        Done();
    }

    void Done()
    {
        int n = items.Count, playable = items.Count(t => t.Playable);
        SubTitle.Text = mode switch
        {
            VkMode.Search => $"Найдено: {n}",
            VkMode.Queue => n <= 1 ? L.T("Дальше ничего нет") : L.F("Сейчас играет и ещё {0} {1}", n - 1, Plural(n - 1)),
            VkMode.Local => $"{n} {Plural(n)} · " + string.Join(", ", LocalLibrary.Folders().Select(System.IO.Path.GetFileName)),
            _ => $"{n} {Plural(n)}",
        };
        string status = n == 0 ? mode switch
            {
                VkMode.Queue => L.T("Очередь пуста — включите любой трек или плейлист"),
                VkMode.History => L.T("Здесь появятся треки, которые вы слушали"),
                VkMode.Local => L.T("Музыки не нашлось. Скачанные треки и папка «Музыка» попадают сюда сами, другие папки можно добавить кнопкой справа."),
                _ => L.T("Здесь пока пусто"),
            }
            : playable == 0 && mode != VkMode.History ? L.T("VK не отдаёт ссылки на треки этому входу. Попробуйте выйти и войти заново.")
            : "";
        SetView(status: status);
    }

    static string Plural(int n) => L.En ? (n == 1 ? "track" : "tracks") :
        (n % 10 == 1 && n % 100 != 11) ? L.T("трек") : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? L.T("трека") : L.T("треков");

    // ------------------------------------------------------------------ действия

    async void PlaySelected()
    {
        if (Tracks.SelectedItem is not Track t) return;
        if (mode == VkMode.Queue) { if (t != AppHost.Player.Current) AppHost.Player.JumpTo(t); return; }
        if (mode == VkMode.History) { await PlayHistoryAsync(t); return; }
        if (!t.Playable) { AppHost.Toast(L.T("Этот трек недоступен для прослушивания")); return; }
        AppHost.Player.Play(items, t);
    }

    /// <summary>Ссылки VK в истории устаревают — перед игрой берём свежие.</summary>
    async Task PlayHistoryAsync(Track start)
    {
        var list = items.ToList();
        var fresh = new Dictionary<string, Track>();
        if (list.Any(x => x.Source != "Local"))
        {
            if (!AppHost.Vk.LoggedIn) { AppHost.Toast(L.T("Войдите в VK, чтобы слушать историю")); return; }
            try
            {
                // выбранный и следующие за ним — до 200 треков
                int at = Math.Max(0, list.IndexOf(start));
                var near = list.Skip(at).Where(x => x.Source != "Local" && x.OwnerId != 0).DistinctBy(x => x.Id).Take(200).ToList();
                foreach (var f in await AppHost.Vk.ByIdAsync(near)) fresh[f.Id] = f;
            }
            catch (Exception ex) { AppHost.Toast(ex is VkException ? ex.Message : L.T("Нет связи с VK")); return; }
        }
        var play = list.Select(x => x.Source == "Local" ? x : fresh.GetValueOrDefault(x.Id))
            .OfType<Track>().Where(x => x.Playable).DistinctBy(x => x.Id).ToList();
        var first = start.Source == "Local" ? start : fresh.GetValueOrDefault(start.Id);
        if (first is not { Playable: true }) { AppHost.Toast(L.T("Этот трек недоступен для прослушивания")); return; }
        AppHost.Player.Play(play, play.First(x => x.Id == first.Id));
    }

    /// <summary>Один клик по строке — играть (клик по «⋯» открывает меню, а не трек).</summary>
    void TrackClick(object s, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListBoxItem)
        {
            if (el is System.Windows.Controls.Primitives.ButtonBase) return;
            el = el is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(el)
                : LogicalTreeHelper.GetParent(el);
        }
        if (el is not ListBoxItem { DataContext: Track t }) return;
        Tracks.SelectedItem = t;
        if (AppHost.Player.Current == t && AppHost.Player.IsPlaying) return; // уже играет
        PlaySelected();
    }

    void TrackKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PlaySelected();
        if (mode == VkMode.Queue && Tracks.SelectedItem is Track t)
        {
            if (e.Key == Key.Delete) { AppHost.Player.RemoveFromQueue(t); e.Handled = true; }
            else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey is Key.Up or Key.Down)
            {
                AppHost.Player.MoveInQueue(t, e.SystemKey == Key.Up ? -1 : 1);
                Dispatcher.BeginInvoke(() => Tracks.SelectedItem = items.FirstOrDefault(x => x == t), System.Windows.Threading.DispatcherPriority.Background);
                e.Handled = true;
            }
        }
    }

    void ShuffleAllClick(object s, RoutedEventArgs e)
    {
        var playable = items.Where(t => t.Playable).ToList();
        if (playable.Count == 0) return;
        AppHost.Player.Shuffle = true;
        AppHost.Player.Play(items, playable[Random.Shared.Next(playable.Count)]);
    }

    void RefreshClick(object s, RoutedEventArgs e)
    {
        if (mode == VkMode.Search) { _ = SearchAsync(); return; }
        _ = LoadAsync();
    }

    async void SearchKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await SearchAsync();
    }

    async Task SearchAsync()
    {
        string q = SearchBox.Text.Trim();
        if (q.Length == 0) return;
        int ver = ++loadVersion;
        SetView(status: L.T("Ищу…"));
        try { Fill(await AppHost.Vk.SearchAsync(q), ver); }
        catch (Exception ex) { SetView(status: ex is VkException ? ex.Message : L.T("Нет связи с VK")); }
    }

    async void PlaylistClick(object s, MouseButtonEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not VkPlaylist p) return;
        await OpenPlaylistAsync(p);
    }

    void BackClick(object s, RoutedEventArgs e)
    {
        ++loadVersion;
        items.Clear();
        SubTitle.Text = Playlists.ItemsSource is List<VkPlaylist> l ? $"{l.Count} плейлистов" : "";
        SetView(playlists: true);
    }

    // ------------------------------------------------------------------ меню трека и переходы

    void OpenMenu(Track t, UIElement target)
    {
        var m = TrackMenu.Build(t, () => { items.Remove(t); Done(); });
        if (mode == VkMode.Queue && t != AppHost.Player.Current)
        {
            int i = 0;
            m.Items.Insert(i++, TrackMenu.Item("\uE74A", L.T("Выше в очереди"), () => { AppHost.Player.MoveInQueue(t, -1); return Task.CompletedTask; }));
            m.Items.Insert(i++, TrackMenu.Item("\uE74B", L.T("Ниже в очереди"), () => { AppHost.Player.MoveInQueue(t, 1); return Task.CompletedTask; }));
            m.Items.Insert(i++, TrackMenu.Item("\uE711", L.T("Убрать из очереди"), () => { AppHost.Player.RemoveFromQueue(t); return Task.CompletedTask; }));
            m.Items.Insert(i, new Separator());
        }
        m.PlacementTarget = target;
        m.IsOpen = true;
    }

    void MoreClick(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.DataContext is not Track t) return;
        Tracks.SelectedItem = t;
        OpenMenu(t, (UIElement)s);
        e.Handled = true;
    }

    void TrackRightClick(object s, MouseButtonEventArgs e)
    {
        var el = e.OriginalSource as DependencyObject;
        while (el != null && el is not ListBoxItem) el = System.Windows.Media.VisualTreeHelper.GetParent(el);
        if (el is ListBoxItem { DataContext: Track t } row)
        {
            Tracks.SelectedItem = t;
            OpenMenu(t, row);
            e.Handled = true;
        }
    }

    /// <summary>Показать произвольный список (микс по треку и т.п.).</summary>
    public void ShowList(string title, List<Track> list)
    {
        ++loadVersion;
        loaded = true;
        items.Clear();
        foreach (var t in list) items.Add(t);
        Done();
        SubTitle.Text = $"{title} · {items.Count} {Plural(items.Count)}";
    }

    public async void Search(string q)
    {
        SearchBox.Text = q;
        loaded = true;
        await SearchAsync();
    }

    public void OpenPlaylist(VkPlaylist p)
    {
        loaded = true;
        _ = OpenPlaylistAsync(p);
    }

    async Task OpenPlaylistAsync(VkPlaylist p)
    {
        int ver = ++loadVersion;
        items.Clear();
        SubTitle.Text = p.Title;
        SetView(status: L.T("Загрузка…"));
        try
        {
            Fill(await AppHost.Vk.PlaylistTracksAsync(p), ver);
            SubTitle.Text = $"{p.Title} · {items.Count} {Plural(items.Count)}";
        }
        catch (Exception ex) { SetView(status: ex is VkException ? ex.Message : L.T("Нет связи с VK")); }
    }

    void LoginClick(object s, RoutedEventArgs e)
    {
        var w = new VkLoginWindow { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }
}
