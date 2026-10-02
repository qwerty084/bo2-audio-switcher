using System.Windows;

public partial class App : Application
{
    Mutex single;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        single = new Mutex(true, "bo2audio", out bool first);
        if (!first)
        {
            MessageBox.Show("BO2 Audio is already running.", "BO2 Audio");
            Shutdown();
            return;
        }
        new MainWindow().Show();
    }
}
