using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CoolPlayer;

/// <summary>Меню трека — как в приложении VK: микс, мои треки, плейлисты, скачать, поделиться и т.д.</summary>
static class TrackMenu
{
    public static ContextMenu Build(Track t, Action? removedFromList = null)
    {
        // шрифт задаём явно: иначе меню наследует шрифт значков от кнопки «⋯» и вместо текста — квадраты
        var m = new ContextMenu
        {
            MinWidth = 260, FontSize = 13.5,
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        };
        var vk = AppHost.Vk;
        bool login = vk.LoggedIn;

        if (LocalLibrary.PathOf(t) is { } path)
        {
            m.Items.Add(Item("", L.T("Играть следующим"), () => { AppHost.Player.PlayNext(t); AppHost.Toast(L.T("Будет следующим")); return Task.CompletedTask; }));
            m.Items.Add(Item("\uE838", L.T("Показать в папке"), () =>
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
                return Task.CompletedTask;
            }));
            m.Items.Add(Item("", L.T("Перейти к исполнителю"), () => { AppHost.Hub?.OpenSearch(t.Artist); return Task.CompletedTask; }, login && t.Artist.Length > 0));
            m.Items.Add(Item("", L.T("Текст песни"), () => { AppHost.Hub?.ShowLyrics(t); return Task.CompletedTask; }));
            return m;
        }

        m.Items.Add(Item("E8B1", L.T("Микс по треку"), async () =>
        {
            try
            {
                var mix = await vk.MixAsync(t);
                var list = new List<Track> { t };
                list.AddRange(mix.Where(x => x.Id != t.Id));
                AppHost.Player.Play(list, t.Playable ? t : list.First(x => x.Playable));
                AppHost.Hub?.ShowTracks($"Микс по треку «{t.Title}»", list);
            }
            catch (Exception ex) { Fail(ex); }
        }, login));
        m.Items.Add(Item("E893", L.T("Играть следующим"), () => { AppHost.Player.PlayNext(t); AppHost.Toast(L.T("Будет следующим")); return Task.CompletedTask; }, t.Playable));
        m.Items.Add(new Separator());

        if (vk.IsMine(t))
            m.Items.Add(Item("E74D", L.T("Удалить из моих треков"), async () =>
            {
                try { await vk.DeleteAsync(t); AppHost.Toast(L.T("Удалено из моих треков")); removedFromList?.Invoke(); }
                catch (Exception ex) { Fail(ex); }
            }, login, danger: true));
        else
            m.Items.Add(Item("E710", L.T("Добавить в мои треки"), async () =>
            {
                try { await vk.AddAsync(t); AppHost.Toast(L.T("Добавлено в мои треки")); }
                catch (Exception ex) { Fail(ex); }
            }, login));

        var toList = Item("E90B", L.T("Добавить в плейлист"), null, login);
        toList.Items.Add(new MenuItem { Header = L.T("Загрузка…"), IsEnabled = false });
        bool filled = false;
        toList.SubmenuOpened += async (_, _) =>
        {
            if (filled) return;
            filled = true;
            try
            {
                var lists = (await vk.PlaylistsAsync()).Where(p => p.OwnerId == vk.UserId).ToList();
                toList.Items.Clear();
                if (lists.Count == 0) toList.Items.Add(new MenuItem { Header = L.T("Нет своих плейлистов"), IsEnabled = false });
                foreach (var p in lists)
                {
                    var pi = new MenuItem { Header = p.Title };
                    pi.Click += async (_, _) =>
                    {
                        try { await vk.AddToPlaylistAsync(p, t); AppHost.Toast($"Добавлено в «{p.Title}»"); }
                        catch (Exception ex) { Fail(ex); }
                    };
                    toList.Items.Add(pi);
                }
            }
            catch (Exception ex) { filled = false; toList.Items.Clear(); toList.Items.Add(new MenuItem { Header = L.T("Не удалось загрузить"), IsEnabled = false }); Fail(ex); }
        };
        m.Items.Add(toList);

        m.Items.Add(Item("E896", L.T("Скачать"), async () =>
        {
            // куда сохранить: выбор папки, по умолчанию — прошлая
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Куда сохранить трек"), InitialDirectory = Downloader.Folder };
            try { System.IO.Directory.CreateDirectory(Downloader.Folder); } catch { }
            if ((AppHost.Hub is { IsVisible: true } owner ? dlg.ShowDialog(owner) : dlg.ShowDialog()) != true) return;
            if (!string.Equals(dlg.FolderName, Downloader.Folder, StringComparison.OrdinalIgnoreCase))
            {
                SettingsStore.Current.DownloadDir = dlg.FolderName;
                SettingsStore.Save();
            }
            AppHost.Toast($"Скачиваю «{t.Title}»…");
            var progress = Live.TrackDownload(t.Title);
            try
            {
                var path = await Task.Run(() => Downloader.DownloadAsync(t, progress));
                progress.Report(1);
                AppHost.Toast(L.T("Сохранено в ") + System.IO.Path.GetDirectoryName(path));
            }
            catch (Exception ex) { Live.DownloadFailed(progress); Program.Log(ex); AppHost.Toast(L.T("Не удалось скачать трек")); }
        }, t.Playable));
        m.Items.Add(Item("E72D", L.T("Поделиться"), () =>
        {
            try { Clipboard.SetText($"https://vk.com/audio{t.Id}"); AppHost.Toast("Ссылка скопирована"); } catch { }
            return Task.CompletedTask;
        }));
        m.Items.Add(Item("E8E0", L.T("Не нравится"), () =>
        {
            AppHost.Player.Dislike(t);
            AppHost.Toast(L.T("Больше не будет играть"));
            removedFromList?.Invoke();
            return Task.CompletedTask;
        }));
        m.Items.Add(new Separator());

        m.Items.Add(Item("E77B", L.T("Перейти к исполнителю"), () => { AppHost.Hub?.OpenSearch(t.Artist); return Task.CompletedTask; }, login));
        var album = VkApi.AlbumOf(t);
        m.Items.Add(Item("E93C", L.T("Перейти к альбому"), () => { if (album != null) AppHost.Hub?.OpenPlaylist(album); return Task.CompletedTask; }, login && album != null));
        m.Items.Add(Item("E8A5", L.T("Текст песни"), () => { AppHost.Hub?.ShowLyrics(t); return Task.CompletedTask; }));
        return m;
    }

    public static MenuItem Item(string glyph, string text, Func<Task>? click, bool enabled = true, bool danger = false)
    {
        if (glyph.Length == 0) glyph = Glyph(text);
        // код значка в виде «E8B1» → сам символ (иначе шрифт значков рисует буквы квадратами)
        if (glyph.Length == 4 && int.TryParse(glyph, System.Globalization.NumberStyles.HexNumber, null, out int code)) glyph = ((char)code).ToString();
        // значок кладём прямо в заголовок: колонка Icon в шаблоне меню Fluent подменяет шрифт,
        // и вместо значков выходили квадраты
        var icon = new TextBlock { Text = glyph, FontSize = 14, Width = 22, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "Icons");
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(icon);
        head.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        var mi = new MenuItem { Header = head, IsEnabled = enabled };
        if (danger) mi.Foreground = new SolidColorBrush(Color.FromRgb(255, 107, 107));
        if (click != null) mi.Click += async (_, _) => await click();
        return mi;
    }

    static string Glyph(string text) => text switch
    {
        _ when text.StartsWith(L.T("Микс")) => "",
        _ when text.StartsWith(L.T("Играть")) => "",
        _ when text.StartsWith(L.T("Удалить")) => "",
        _ when text.StartsWith(L.T("Добавить в мои")) => "",
        _ when text.StartsWith(L.T("Добавить в плейлист")) => "",
        _ when text.StartsWith(L.T("Скачать")) => "",
        _ when text.StartsWith(L.T("Поделиться")) => "",
        _ when text.StartsWith(L.T("Не нравится")) => "",
        _ when text.StartsWith(L.T("Перейти к исп")) => "",
        _ when text.StartsWith(L.T("Перейти к альб")) => "",
        _ when text.StartsWith(L.T("Текст")) => "",
        _ => "",
    };

    static void Fail(Exception ex)
    {
        if (ex is not VkException) Program.Log(ex);
        AppHost.Toast(ex is VkException ? ex.Message : L.T("Нет связи с VK"));
    }
}
