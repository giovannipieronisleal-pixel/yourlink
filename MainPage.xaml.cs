namespace appmaui.blazor;

public partial class MainPage : ContentPage
{
    public const string AndroidCloudPanelModePrefKey = "youlink_android_cloud_panel_mode_v1";

    public MainPage()
    {
        InitializeComponent();

        // Limpa qualquer preferência legada do modo WebView nuvem no Android.
        if (OperatingSystem.IsAndroid())
        {
            try
            {
                Preferences.Default.Set(AndroidCloudPanelModePrefKey, false);
            }
            catch
            {
            }
        }
    }
}
