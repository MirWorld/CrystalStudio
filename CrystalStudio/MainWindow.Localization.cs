namespace CrystalStudio;

public partial class MainWindow
{
    private MenuItem chineseLanguage = null!, englishLanguage = null!;

    private void InitializeLanguageMenu()
    {
        var menu = new MenuItem { Name = "LanguageMenu", Header = LocalText.T("Language (restart required)") };
        chineseLanguage = new MenuItem { Name = "ChineseLanguage", Header = "简体中文", ToggleType = MenuItemToggleType.CheckBox };
        englishLanguage = new MenuItem { Name = "EnglishLanguage", Header = "English", ToggleType = MenuItemToggleType.CheckBox };
        chineseLanguage.Click += (_, _) => ChooseLanguage("zh-CN");
        englishLanguage.Click += (_, _) => ChooseLanguage("en-US");
        menu.Items.Add(chineseLanguage); menu.Items.Add(englishLanguage);
        ViewMenu.Items.Add(new Separator()); ViewMenu.Items.Add(menu);
        UpdateLanguageChecks();
    }

    private void ChooseLanguage(string language)
    {
        try
        {
            LocalText.SaveLanguage(language, settingsStore);
            Status.Text = LocalText.T("Language saved. Restart Crystal Studio to apply.");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { MessageBox.Show(this, LocalText.Format("Could not save language preference: {0}", ex.Message)); }
        UpdateLanguageChecks();
    }

    private void UpdateLanguageChecks()
    {
        string selected = LocalText.PreferredLanguage(settingsStore);
        chineseLanguage.IsChecked = selected == "zh-CN";
        englishLanguage.IsChecked = selected == "en-US";
    }
}
