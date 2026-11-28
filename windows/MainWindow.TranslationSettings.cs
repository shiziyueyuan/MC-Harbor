using MCHarbor.Windows.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MCHarbor.Windows;

public sealed partial class MainWindow
{
    private void TranslationProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_appearanceReady) return;
        SaveSetting("translationProvider", Math.Max(0, TranslationProvider.SelectedIndex).ToString());
        UpdateTranslationSettings();
    }

    private void UpdateTranslationSettings()
    {
        TranslationQwenSettings.Visibility = VisibleWhen(TranslationProvider.SelectedIndex == 1);
        TranslationForeignNotice.Visibility = VisibleWhen(TranslationProvider.SelectedIndex >= 2);
    }

    private void SaveQwenSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = OnlineTranslationClient.QwenAddress(TranslationQwenEndpoint.Text.Trim());
            if (endpoint.Query.Length > 0) throw new IOException("接口地址不应包含密钥参数，请使用下方的密钥框。");
            var key = TranslationQwenKey.Password.Trim();
            if (key.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))) throw new IOException("请输入有效的 API 密钥。");
            if (key.Length > 0) AppSecrets.Save("online-translation-qwen", key);
            TranslationQwenKey.Password = "";
            SaveSetting("translationQwenEndpoint", endpoint.AbsoluteUri);
            TranslationSettingsStatus.Text = "设置已保存，密钥由当前 Windows 用户保护。";
        }
        catch (Exception error) { TranslationSettingsStatus.Text = error.Message; }
    }

    private void RemoveQwenKey_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSecrets.Remove("online-translation-qwen"); TranslationQwenKey.Password = "";
            TranslationSettingsStatus.Text = "密钥已删除。";
        }
        catch (Exception error) { TranslationSettingsStatus.Text = error.Message; }
    }

    private async void TestTranslation_Click(object sender, RoutedEventArgs e)
    {
        if (TranslationTestButton.IsEnabled == false) return;
        TranslationTestButton.IsEnabled = false;
        TranslationSettingsStatus.Text = "检测中…";
        try
        {
            var provider = (OnlineTranslator)Math.Max(0, TranslationProvider.SelectedIndex);
            var key = provider == OnlineTranslator.Qwen ? AppSecrets.Read("online-translation-qwen") : null;
            if (provider == OnlineTranslator.Qwen && string.IsNullOrWhiteSpace(key)) throw new IOException("请先保存阿里云 API 密钥。");
            var client = new OnlineTranslationClient(provider, key, AppPaths.GetPreference("translationQwenEndpoint"));
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = await client.TranslateAsync(["Improves performance and reduces memory usage."], cancellation.Token);
            TranslationSettingsStatus.Text = output.FirstOrDefault() ?? "该服务没有返回译文。";
        }
        catch (Exception error) { TranslationSettingsStatus.Text = error.Message; }
        finally { TranslationTestButton.IsEnabled = true; }
    }
}
