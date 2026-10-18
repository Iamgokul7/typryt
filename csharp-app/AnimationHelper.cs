using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Typryt;

/// <summary>
/// Builds the splash animation: the logo "Typryt" is typed, one step at a time, with a
/// backspace correction on the way, and the cursor finishes as a full stop.
///
/// Timeline (milliseconds):
///   0 - 1500   the cursor blinks, about every 500 ms
///   1500       t         (typed)
///   1580       ty
///   1660       t         (y deleted)
///   1720       (empty)   (t deleted)
///   1800       T         (corrected capital)
///   1880 - 2200  Ty, Typ, Typr, Typry, Typryt
///   2500       the cursor becomes a full stop and stays
/// </summary>
public static class AnimationHelper
{
    public const int TotalMilliseconds = 2500;

    private static readonly (int At, string Text)[] WordSteps =
    [
        (1500, "t"),
        (1580, "ty"),
        (1660, "t"),
        (1720, ""),
        (1800, "T"),
        (1880, "Ty"),
        (1960, "Typ"),
        (2040, "Typr"),
        (2120, "Typry"),
        (2200, "Typryt"),
    ];

    /// <summary>Builds the storyboard for a word and its cursor. The caller starts it.</summary>
    public static Storyboard Build(TextBlock word, TextBlock cursor)
    {
        var storyboard = new Storyboard();

        // The word is typed and corrected.
        var typing = new StringAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        typing.KeyFrames.Add(new DiscreteStringKeyFrame("", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        foreach (var (at, text) in WordSteps)
        {
            typing.KeyFrames.Add(new DiscreteStringKeyFrame(text, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
        }
        Add(storyboard, typing, word, TextBlock.TextProperty);

        // The cursor blinks until typing starts, stays solid while typing, then becomes a full stop.
        var blink = new DoubleAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        for (int at = 0; at < 1500; at += 500)
        {
            double visible = (at / 500) % 2 == 0 ? 1 : 0;
            blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(visible, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(at))));
        }
        blink.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1500))));
        Add(storyboard, blink, cursor, UIElement.OpacityProperty);

        var cursorText = new StringAnimationUsingKeyFrames { Duration = Duration(TotalMilliseconds) };
        cursorText.KeyFrames.Add(new DiscreteStringKeyFrame("|", KeyTime.FromTimeSpan(TimeSpan.Zero)));
        cursorText.KeyFrames.Add(new DiscreteStringKeyFrame(".", KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(TotalMilliseconds))));
        Add(storyboard, cursorText, cursor, TextBlock.TextProperty);

        return storyboard;
    }

    private static Duration Duration(int milliseconds) => new(TimeSpan.FromMilliseconds(milliseconds));

    private static void Add(Storyboard storyboard, Timeline animation, DependencyObject target, DependencyProperty property)
    {
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, new PropertyPath(property));
        storyboard.Children.Add(animation);
    }
}
