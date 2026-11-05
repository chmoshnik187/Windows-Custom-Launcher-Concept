namespace DotShell.Island.Wpf;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotShell.Core;
using DotShell.Core.Audio;
using DotShell.Core.Clipboard;
using DotShell.Core.Input;
using DotShell.Core.Media;
using DotShell.Core.Native;
using DotShell.Core.Time;
using DotShell.Core.Settings;
using DotShell.Core.Launcher;
using DotShell.Island.Wpf.Widgets;

public partial class IslandWindow
{
    // ================================================================
    //  Быстрый чат с Qwen (вкладка QWEN)
    // ================================================================

    /// <summary>
    /// Подтянуть список моделей из Ollama (родной /api/tags, а не
    /// OpenAI-совместимый /v1/models: он отдаёт имена и размеры, а главное —
    /// не притворяется пустым массивом, когда моделей нет).
    ///
    /// Побочно решает давнюю боль: имя модели в настройках могло устареть
    /// (модель удалили), и человек видел «ошибку API» вместо объяснения.
    /// Теперь мёртвое имя само заменяется на существующее.
    /// </summary>
    private async Task RefreshModelsAsync(bool manual = false)
    {
        var models = await DotShell.Core.Ai.OllamaClient.ListAsync();
        var names = models.Select(m => m.Name).ToList();

        _ = Dispatcher.BeginInvoke(() =>
        {
            AiModelBox.ItemsSource = names;

            var configured = _services.Settings.Current.AiModel;
            var configuredAlive = names.Any(n => string.Equals(n, configured, StringComparison.OrdinalIgnoreCase));

            if (names.Count > 0 && !configuredAlive)
            {
                var pick = names.FirstOrDefault(n => n.Contains("qwen", StringComparison.OrdinalIgnoreCase))
                           ?? names[0];
                AiModelBox.Text = pick;
                _services.Settings.Update(st => st.AiModel = pick);
                _vm.StatusLine = $"модель чата: {pick}";
            }
            else if (string.IsNullOrWhiteSpace(AiModelBox.Text) && names.Count > 0)
            {
                AiModelBox.Text = names[0];
            }

            // Кнопка «скачать» нужна ровно тогда, когда скачивать нечего.
            AiPullBtn.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            if (names.Count > 0)
            {
                AiHint.Visibility = Visibility.Collapsed;
                if (manual) _vm.StatusLine = $"моделей найдено: {names.Count}";
                return;
            }

            ShowAiHint("В Ollama пока нет ни одной модели. Нажми СКАЧАТЬ — остров сам поставит " +
                       DotShell.Core.Ai.OllamaClient.Suggested +
                       " (1.4 ГБ, один раз), и чат заработает без интернета.");
        });
    }

    /// <summary>Скачать рекомендованную модель прямо из острова, с прогрессом.</summary>
    private async void OnPullModel(object sender, RoutedEventArgs e)
    {
        if (AiPullBtn is null) return;

        if (!await DotShell.Core.Ai.OllamaClient.IsRunningAsync())
        {
            ShowAiHint("Сервер Ollama не отвечает. Открой приложение Ollama (оно поднимает сервер " +
                       "на localhost:11434), подожди пару секунд и нажми СКАЧАТЬ снова.");
            return;
        }

        AiPullBtn.IsEnabled = false;
        AiPullBar.Visibility = Visibility.Visible;
        AiPullBar.Value = 0;
        ShowAiHint($"скачиваю {DotShell.Core.Ai.OllamaClient.Suggested} — это один раз, дальше всё офлайн");

        var model = DotShell.Core.Ai.OllamaClient.Suggested;
        var progress = new Progress<(string Status, double Fraction)>(p =>
        {
            if (p.Fraction >= 0) AiPullBar.Value = p.Fraction * 100;
            AiHint.Text = p.Fraction >= 0
                ? $"{p.Status} {p.Fraction * 100:F0}%"
                : p.Status;
        });

        var ok = await Task.Run(() => DotShell.Core.Ai.OllamaClient.PullAsync(
            model,
            (status, fraction) => ((IProgress<(string, double)>)progress).Report((status, fraction))));

        AiPullBtn.IsEnabled = true;
        AiPullBar.Visibility = Visibility.Collapsed;

        if (!ok)
        {
            ShowAiHint("Не получилось скачать модель. Проверь, запущена ли Ollama, и попробуй ещё раз.");
            return;
        }

        _services.Settings.Update(st =>
        {
            st.AiModel = model;
            st.AiProvider = "Ollama";
            st.AiBaseUrl = DotShell.Core.Ai.OllamaClient.Root + "/v1";
        });

        ShowAiHint($"готово: {model} на месте. Пиши вопрос в поле внизу и жми Enter.");
        await RefreshModelsAsync(manual: true);
        ChatInput.Focus();
    }

    private void ShowAiHint(string text)
    {
        AiHint.Text = text;
        AiHint.Visibility = Visibility.Visible;
    }

    private void OnModelDropDownOpened(object? sender, EventArgs e) => _ = RefreshModelsAsync();

    private void OnRefreshModels(object sender, RoutedEventArgs e) => _ = RefreshModelsAsync(manual: true);

    private void OnSaveAiConfig(object sender, RoutedEventArgs e)
    {
        var model = AiModelBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(model))
        {
            ShowAiHint("Выбери модель из списка (кнопка ⟳) или впиши имя вручную — его подскажет команда ollama list.");
            return;
        }

        _services.Settings.Update(st => st.AiModel = model);
        AiHint.Visibility = Visibility.Collapsed;
        _vm.StatusLine = $"модель чата: {model}";
        ChatInput.Focus();
    }

    private async void OnChatSend(object sender, RoutedEventArgs e) => await SendChat();

    private async void OnChatInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true; // без этого Enter вставил бы перенос строки
        await SendChat();
    }

    private async Task SendChat()
    {
        var text = ChatInput.Text;
        if (string.IsNullOrWhiteSpace(text) || _vm.ChatBusy) return;
        ChatInput.Clear();
        await _vm.ChatSendAsync(text);
    }
}
