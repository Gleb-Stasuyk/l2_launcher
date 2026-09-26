using System.ComponentModel;

namespace L2Launcher;

/// <summary>Tiny EN/RU string table. XAML binds to Loc[Key]; switching language raises Item[].</summary>
public sealed class Loc : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool Russian { get; private set; }

    public void SetRussian(bool ru)
    {
        Russian = ru;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
    }

    public string this[string key] => T.TryGetValue(key, out var v) ? (Russian ? v.ru : v.en) : key;
    public string F(string key, params object[] args) => string.Format(this[key], args);

    private static readonly Dictionary<string, (string en, string ru)> T = new()
    {
        ["Language"] = ("Language:", "Язык:"),
        ["English"] = ("English", "English"),
        ["Russian"] = ("Русский", "Русский"),
        ["LangHint"] = ("The game language is applied when you press Play (in game: Options → Game → Korean = Russian, English = English).",
                        "Язык игры применяется при нажатии «Играть» (в игре: Опции → Игра → Korean = русский, English = английский)."),
        ["Play"] = ("PLAY", "ИГРАТЬ"),
        ["Check"] = ("Check files", "Проверить файлы"),
        ["OpenFolder"] = ("Game folder", "Папка игры"),
        ["Website"] = ("Website", "Сайт"),
        ["ShowLog"] = ("Log", "Журнал"),
        ["Cancel"] = ("Cancel", "Отмена"),
        ["S_Start"] = ("Starting…", "Запуск…"),
        ["S_Fetch"] = ("Contacting update server…", "Связь с сервером обновлений…"),
        ["S_Verify"] = ("Checking files… {0}/{1}", "Проверка файлов… {0}/{1}"),
        ["S_Download"] = ("Downloading… {0}%  ({1} / {2})  {3}/s", "Загрузка… {0}%  ({1} / {2})  {3}/с"),
        ["S_Ready"] = ("Ready to play. Client version {0}.", "Готово к игре. Версия клиента {0}."),
        ["S_UpToDate"] = ("All {0} files are up to date.", "Все файлы ({0}) в порядке."),
        ["S_Repaired"] = ("Updated/repaired {0} file(s).", "Обновлено/восстановлено файлов: {0}."),
        ["S_Offline"] = ("Update server unreachable. Playing without a file check.", "Сервер обновлений недоступен. Игра без проверки файлов."),
        ["S_SelfUpdate"] = ("Updating the launcher to {0}…", "Обновление лаунчера до {0}…"),
        ["S_Launching"] = ("Starting the game…", "Запуск игры…"),
        ["S_Gate"] = ("Checking access…", "Проверка доступа…"),
        ["S_Cancelled"] = ("Cancelled.", "Отменено."),
        ["S_Error"] = ("Error: {0}", "Ошибка: {0}"),
        ["E_server_unreachable"] = ("cannot reach the update server. Check your internet connection and try again.",
                                    "нет связи с сервером обновлений. Проверьте интернет и попробуйте ещё раз."),
        ["E_bad_signature"] = ("the update manifest signature is invalid. Update stopped for safety; contact the administrator.",
                               "подпись манифеста обновлений неверна. Обновление остановлено; сообщите администратору."),
        ["E_bad_manifest"] = ("the update manifest is damaged or too new. Update the launcher.",
                              "манифест обновлений повреждён или требует новый лаунчер."),
        ["E_hash_mismatch"] = ("a downloaded file is damaged on the server. Contact the administrator.",
                               "файл на сервере повреждён. Сообщите администратору."),
        ["E_access_denied"] = ("cannot write to the game folder. Close the game or move it out of Program Files.",
                               "нет доступа к папке игры. Закройте игру или перенесите её из Program Files."),
        ["E_ru_files_missing"] = ("Russian language files are missing. Press “Check files”.",
                                  "Не найдены файлы русского языка. Нажмите «Проверить файлы»."),
        ["E_no_game_exe"] = ("system\\l2.exe not found. Press “Check files”.", "Не найден system\\l2.exe. Нажмите «Проверить файлы»."),
        ["E_gate_unreachable"] = ("the login check server is unreachable.", "сервер проверки входа недоступен."),
        ["GateDenied"] = ("Access denied: {0}", "Доступ запрещён: {0}"),
    };
}
