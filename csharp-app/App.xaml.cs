using System.Windows;

namespace Typryt;

public partial class App : Application
{
    private KeyboardHook? _keyboardHook;
    private WordDetector _wordDetector = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _keyboardHook = new KeyboardHook();
        _keyboardHook.KeyPressed += OnKeyPressed;
        _keyboardHook.Install();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _keyboardHook?.Dispose();
        base.OnExit(e);
    }

    private void OnKeyPressed(KeyInput key)
    {
        var completed = _wordDetector.OnKey(key);
        if (completed is not null)
        {
            // Phase 5b sends this word to the engine and replaces it.
        }
    }
}
