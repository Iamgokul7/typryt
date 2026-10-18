using System.Windows;

namespace Typryt;

/// <summary>The startup splash: the logo is typed out, then the window closes by itself.</summary>
public partial class SplashScreen : Window
{
    public SplashScreen()
    {
        InitializeComponent();
        Loaded += (_, _) => Play();
    }

    private void Play()
    {
        var storyboard = AnimationHelper.Build(WordText, CursorText);
        storyboard.Completed += (_, _) => Close();
        storyboard.Begin(this, isControllable: false);
    }
}
