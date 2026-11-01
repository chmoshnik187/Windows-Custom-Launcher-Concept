namespace DotShell.Core.Clipboard;

using System.ComponentModel;
using System.Runtime.CompilerServices;

/// <summary>Что именно скопировали.</summary>
public enum ClipboardKind
{
    Text = 0,
    Link = 1,
    Image = 2,
    Files = 3,
    Other = 99
}

/// <summary>
/// Одна запись истории буфера обмена.
/// INotifyPropertyChanged нужен, чтобы подпись "5 мин назад" обновлялась в списке
/// без полной перерисовки.
/// </summary>
public sealed class ClipboardItem : INotifyPropertyChanged
{
    public long Id { get; set; }
    public ClipboardKind Kind { get; set; } = ClipboardKind.Text;

    /// <summary>Основной текст (для ссылок — URL, для файлов — список через перевод строки).</summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>Короткое превью для списка (одна строка).</summary>
    public string Preview
    {
        get
        {
            var t = Text.Replace("\r", " ").Replace("\n", "  ·  ").Trim();
            if (t.Length == 0) return Kind == ClipboardKind.Image ? "(изображение)" : "(пусто)";
            return t.Length > 90 ? t[..90] + "…" : t;
        }
    }

    /// <summary>Путь к сохранённому изображению (png), если Kind == Image.</summary>
    public string? ImagePath { get; set; }

    public DateTimeOffset CopiedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>Закреплено — такие записи не удаляются при очистке и не вытесняются лимитом.</summary>
    public bool Pinned { get; set; }

    /// <summary>Сколько раз эту запись вставляли (нужно для сортировки "частое сверху").</summary>
    public int UseCount { get; set; }

    /// <summary>Хеш содержимого — чтобы не плодить дубликаты подряд.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Буква-иконка типа записи (в Nothing-стиле иконки не нужны, нужна буква).</summary>
    public string KindLabel => Kind switch
    {
        ClipboardKind.Text => "T",
        ClipboardKind.Link => "L",
        ClipboardKind.Image => "I",
        ClipboardKind.Files => "F",
        _ => "?"
    };

    /// <summary>Человеческий возраст записи.</summary>
    public string AgeLabel
    {
        get
        {
            var span = DateTimeOffset.Now - CopiedAt;
            string age = span.TotalSeconds < 60 ? "только что"
                : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} мин назад"
                : span.TotalHours < 24 ? $"{(int)span.TotalHours} ч назад"
                : $"{(int)span.TotalDays} дн назад";

            string extras = (Pinned ? " · PIN" : "") + (UseCount > 1 ? $" · ×{UseCount}" : "");
            return $"{Kind.ToString().ToUpperInvariant()} · {age}{extras}";
        }
    }

    /// <summary>Позвать, когда прошло достаточно времени и подписи "N мин назад" пора обновиться.</summary>
    public void RefreshAge() => RaisePropertyChanged(nameof(AgeLabel));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaisePropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
