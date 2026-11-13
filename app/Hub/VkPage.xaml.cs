using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CoolPlayer;

enum VkMode { My, Recs, Playlists, Search }

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
    }

    public void OnShown()
    {
        if (!AppHost.Vk.LoggedIn)
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
        ShuffleAllBtn.Visibility = !login && !playlists && items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
                    SetView(status: "Загрузка…");
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
                    SetView(status: "Подбираем…");
                    Fill(await AppHost.Vk.RecommendationsAsync(), ver);
                    break;
                case VkMode.Playlists:
                    SetView(playlists: true, status: "Загрузка…");
                    var lists = await AppHost.Vk.PlaylistsAsync();
                    if (ver != loadVersion) return;
                    Playlists.ItemsSource = lists;
                    SubTitle.Text = $"{lists.Count} плейлистов";
                    SetView(playlists: true, status: lists.Count == 0 ? "Плейлистов пока нет" : "");
                    break;
                case VkMode.Search:
                    SetView(status: "Найдите что угодно — нажмите Enter");
                    SubTitle.Text = "";
                    break;
            }
        }
        catch (Exception ex)
        {
            if (ver != loadVersion) return;
            loaded = false;
            SetView(playlists: mode == VkMode.Playlists, status: ex is VkException ? ex.Message : "Нет связи с VK");
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
        SubTitle.Text = mode == VkMode.Search ? $"Найдено: {n}" : $"{n} {Plural(n)}";
        string status = n == 0 ? "Здесь пока пусто"
            : playable == 0 ? "VK не отдаёт ссылки на треки этому входу. Попробуйте выйти и войти заново."
            : "";
        SetView(status: status);
    }

    static string Plural(int n) =>
        (n % 10 == 1 && n % 100 != 11) ? "трек" : (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "трека" : "треков";

    // ------------------------------------------------------------------ действия

    void PlaySelected()
    {
        if (Tracks.SelectedItem is not Track t) return;
        if (!t.Playable) { AppHost.Toast("Этот трек недоступен для прослушивания"); return; }
        AppHost.Player.Play(items, t);
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
        SetView(status: "Ищу…");
        try { Fill(await AppHost.Vk.SearchAsync(q), ver); }
        catch (Exception ex) { SetView(status: ex is VkException ? ex.Message : "Нет связи с VK"); }
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
        SetView(status: "Загрузка…");
        try
        {
            Fill(await AppHost.Vk.PlaylistTracksAsync(p), ver);
            SubTitle.Text = $"{p.Title} · {items.Count} {Plural(items.Count)}";
        }
        catch (Exception ex) { SetView(status: ex is VkException ? ex.Message : "Нет связи с VK"); }
    }

    void LoginClick(object s, RoutedEventArgs e)
    {
        var w = new VkLoginWindow { Owner = Window.GetWindow(this) };
        w.ShowDialog();
    }
}
